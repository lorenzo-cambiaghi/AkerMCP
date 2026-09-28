#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using AkerMcp.Shared.Abstraction;
using LogLevel = AkerMcp.Shared.Abstraction.LogLevel;
using LogEntry = AkerMcp.Shared.Abstraction.LogEntry;

namespace AkerMcp.Unity
{
    public class UnityEditorContext : IEditorContext
    {
        private readonly List<LogEntry> _logBuffer = new List<LogEntry>();
        // Appends come from the main thread (and, through the log handler, from any thread that logs); get_console_logs
        // reads on the pipe thread: all of them go through this lock.
        private readonly object _logLock = new object();
        private const int MaxLogEntries = 200;

        public bool IsEditorMode => !EditorApplication.isPlaying;

        // Set while a log captured by CapturingLogHandler is being forwarded to Unity: the logMessageReceived callback
        // that Unity raises from inside that call is the same message, already in the buffer.
        [ThreadStatic] private static bool t_forwarding;

        public UnityEditorContext()
        {
            // Engine-side messages (import warnings, shader errors) only arrive through the callback.
            Application.logMessageReceived += OnLogMessage;
            InstallLogHandler();
        }

        /// <summary>
        /// Every managed log goes through <c>Debug.unityLogger.logHandler</c>: capturing there does not depend on Unity
        /// raising the log callbacks.
        /// <para>
        /// ⚠️ It does not always raise them. Leaving play mode, a <c>Debug.Log</c> written in <c>EnteredEditMode</c> — and in
        /// the editor ticks right after — reaches Editor.log and the Console, but neither <c>logMessageReceived</c> nor
        /// <c>logMessageReceivedThreaded</c> (measured with both subscribed, 2026-09-28). get_console_logs answered «No matching
        /// log entries» for exactly the warnings that tools write when play mode ends.
        /// </para>
        /// </summary>
        private void InstallLogHandler()
        {
            ILogHandler current = Debug.unityLogger.logHandler;
            // A context started again without a domain reload replaces the previous capture instead of stacking on it.
            if (current is CapturingLogHandler previous) current = previous.Inner;
            Debug.unityLogger.logHandler = new CapturingLogHandler(current, this);
        }

        public string? GetSelectedObjectPath()
        {
            var selected = Selection.activeGameObject;
            if (selected == null) return null;
            return new UnitySceneNode(selected).Path;
        }

        public void SetSelection(string objectPath)
        {
            var graph = new UnitySceneGraph();
            var node = graph.GetNode(objectPath);
            if (node != null)
                Selection.activeGameObject = node.UnderlyingObject as GameObject;
        }

        public string? GetCurrentScenePath()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return string.IsNullOrEmpty(scene.path) ? null : scene.path;
        }

        public void OpenScene(string path)
        {
            EditorSceneManager.OpenScene(path);
        }

        public void SaveScene()
        {
            EditorSceneManager.SaveOpenScenes();
        }

        public string GetProjectPath()
        {
            return Application.dataPath.Replace("/Assets", "");
        }

        public IEnumerable<LogEntry> GetRecentLogs(int count = 50)
        {
            // A copy, not `_logBuffer.TakeLast(count)`: that lazy view was enumerated on the pipe thread while Unity kept
            // logging, and under a stream of logs it threw "Collection was modified" or returned an entry twice.
            lock (_logLock)
                return _logBuffer.Skip(Math.Max(0, _logBuffer.Count - count)).ToList();
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            switch (level)
            {
                case LogLevel.Warning: Debug.LogWarning($"[AkerMcp] {message}"); break;
                case LogLevel.Error: Debug.LogError($"[AkerMcp] {message}"); break;
                default: Debug.Log($"[AkerMcp] {message}"); break;
            }
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (t_forwarding) return;
            Record(condition, stackTrace, type);
        }

        private void Record(string message, string stackTrace, LogType type)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Message = message,
                StackTrace = stackTrace,
                Level = type switch
                {
                    LogType.Error => LogLevel.Error,
                    LogType.Exception => LogLevel.Error,
                    // A failed Debug.Assert: an error, not the Info a level_filter "error" would never show.
                    LogType.Assert => LogLevel.Error,
                    LogType.Warning => LogLevel.Warning,
                    _ => LogLevel.Info
                }
            };

            lock (_logLock)
            {
                _logBuffer.Add(entry);
                if (_logBuffer.Count > MaxLogEntries)
                    _logBuffer.RemoveAt(0);
            }
        }

        ~UnityEditorContext()
        {
            Application.logMessageReceived -= OnLogMessage;
        }

        /// <summary>Records each managed log, then hands it to the handler it wraps.</summary>
        private sealed class CapturingLogHandler : ILogHandler
        {
            private readonly UnityEditorContext _owner;

            public ILogHandler Inner { get; }

            public CapturingLogHandler(ILogHandler inner, UnityEditorContext owner)
            {
                Inner = inner;
                _owner = owner;
            }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                _owner.Record(Format(format, args), StackFor(logType), logType);
                t_forwarding = true;
                try { Inner.LogFormat(logType, context, format, args); }
                finally { t_forwarding = false; }
            }

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                _owner.Record($"{exception.GetType().Name}: {exception.Message}", exception.StackTrace ?? string.Empty,
                    LogType.Exception);
                t_forwarding = true;
                try { Inner.LogException(exception, context); }
                finally { t_forwarding = false; }
            }

            private static string Format(string format, object[] args)
            {
                if (args == null || args.Length == 0) return format;
                try { return string.Format(format, args); }
                catch (FormatException) { return format; }
            }

            // The callback carries Unity's stack for every level; here it costs a walk, so only where it is read.
            private static string StackFor(LogType logType) =>
                logType == LogType.Log ? string.Empty : StackTraceUtility.ExtractStackTrace();
        }
    }
}
