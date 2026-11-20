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
            _logHandler = new CapturingLogHandler(current, this);
            Debug.unityLogger.logHandler = _logHandler;
        }

        private CapturingLogHandler? _logHandler;

        /// <summary>
        /// Stops recording: called when the plugin stops. Stop/Start from the AkerMcp window does not reload the
        /// domain, and the old context stayed subscribed to <c>logMessageReceived</c> — the static event kept it
        /// alive, so the finalizer meant to unsubscribe it never ran — recording every log into a buffer nobody read.
        /// </summary>
        public void Detach()
        {
            Application.logMessageReceived -= OnLogMessage;
            if (_logHandler == null) return;
            _logHandler.Detached = true;
            // Unwrapped only if nobody wrapped it since; otherwise it stays in the chain as a plain pass-through.
            if (ReferenceEquals(Debug.unityLogger.logHandler, _logHandler))
                Debug.unityLogger.logHandler = _logHandler.Inner;
            _logHandler = null;
        }

        public string? GetSelectedObjectPath()
        {
            var selected = Selection.activeGameObject;
            if (selected == null) return null;
            // A prefab ASSET selected in the Project window is a GameObject too, with no scene: its name path was then
            // looked up in the open scene, and get_selection described whichever scene object shared that path.
            if (EditorUtility.IsPersistent(selected)) return null;
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
            // Only the trailing "/Assets": Replace("/Assets", "") also cut it out of the middle, and a project under
            // D:/Assets/Game resolved to D:/Game — where write_script then wrote its files.
            var dataPath = Application.dataPath;
            const string assets = "/Assets";
            return dataPath.EndsWith(assets, StringComparison.Ordinal)
                ? dataPath.Substring(0, dataPath.Length - assets.Length)
                : System.IO.Directory.GetParent(dataPath)?.FullName ?? dataPath;
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

        /// <summary>Records each managed log, then hands it to the handler it wraps.</summary>
        private sealed class CapturingLogHandler : ILogHandler
        {
            private readonly UnityEditorContext _owner;

            public ILogHandler Inner { get; }

            /// <summary>Set when the owning context stops: from then on the handler only forwards.</summary>
            public volatile bool Detached;

            public CapturingLogHandler(ILogHandler inner, UnityEditorContext owner)
            {
                Inner = inner;
                _owner = owner;
            }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                if (Detached) { Inner.LogFormat(logType, context, format, args); return; }
                _owner.Record(Format(format, args), StackFor(logType), logType);
                t_forwarding = true;
                try { Inner.LogFormat(logType, context, format, args); }
                finally { t_forwarding = false; }
            }

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                if (Detached) { Inner.LogException(exception, context); return; }
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
                logType == LogType.Log ? string.Empty : WithoutLoggingFrames(StackTraceUtility.ExtractStackTrace());

            // The stack is taken from INSIDE the log call, so it opened with this handler and Unity's Debug/Logger
            // plumbing: since the handler went in, the "Stack:" of every Debug.LogError in get_console_logs pointed at
            // AkerMcp instead of the code that logged. Only the leading run goes: deeper down the same names are real
            // callers.
            private static string WithoutLoggingFrames(string stack)
            {
                var lines = stack.Split('\n');
                int first = 0;
                while (first < lines.Length && IsLoggingFrame(lines[first])) first++;
                return first == 0 ? stack : string.Join("\n", lines, first, lines.Length - first);
            }

            private static readonly string[] LoggingFramePrefixes =
            {
                "UnityEngine.StackTraceUtility:",
                "UnityEngine.Debug:",
                "UnityEngine.Logger:",
                "UnityEngine.DebugLogHandler:",
                // Unity writes a nested type as Outer/Inner; .NET as Outer+Inner.
                typeof(CapturingLogHandler).FullName!.Replace('+', '/') + ":",
                typeof(CapturingLogHandler).FullName + ":",
            };

            private static bool IsLoggingFrame(string line)
            {
                foreach (var prefix in LoggingFramePrefixes)
                    if (line.StartsWith(prefix, StringComparison.Ordinal)) return true;
                return false;
            }
        }
    }
}
