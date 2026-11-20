#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using AkerMcp.Shared.Abstraction;
using LogLevel = AkerMcp.Shared.Abstraction.LogLevel;
using LogEntry = AkerMcp.Shared.Abstraction.LogEntry;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Editor integration: selection, scene I/O, project path and a log buffer.
    /// Note: up to Godot 4.4 there is no global log-capture hook (unlike Unity's
    /// <c>Application.logMessageReceived</c>; <c>OS.AddLogger</c> only arrives in 4.5), so the
    /// console buffer only holds messages routed through <see cref="Log"/> / <see cref="Append"/>:
    /// the plugin's own output (<see cref="GodotMcpPlugin"/>'s Log/LogError) and <c>Log()</c>
    /// calls from `execute` scripts. Engine errors and a bare <c>GD.Print</c> never reach it.
    /// </summary>
    public class GodotEditorContext : IEditorContext
    {
        private readonly List<LogEntry> _logBuffer = new();
        // Godot appends on its main thread, get_console_logs reads on the pipe thread: both go through this lock.
        private readonly object _logLock = new();
        private const int MaxLogEntries = 200;

        public bool IsEditorMode => Engine.IsEditorHint();

        public string? GetSelectedObjectPath()
        {
            var nodes = EditorInterface.Singleton.GetSelection().GetSelectedNodes();
            return nodes.Count == 0 ? null : new GodotSceneNode(nodes[0]).Path;
        }

        public void SetSelection(string objectPath)
        {
            var node = new GodotSceneGraph().GetNode(objectPath);
            if (node?.UnderlyingObject is Node n)
            {
                var selection = EditorInterface.Singleton.GetSelection();
                selection.Clear();
                selection.AddNode(n);
            }
        }

        public string? GetCurrentScenePath()
        {
            var path = EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        public void OpenScene(string path) => EditorInterface.Singleton.OpenSceneFromPath(path);

        public void SaveScene() => EditorInterface.Singleton.SaveScene();

        public string GetProjectPath() => ProjectSettings.GlobalizePath("res://");

        public IEnumerable<LogEntry> GetRecentLogs(int count = 50)
        {
            // A copy, not a lazy view: the pipe thread enumerates it while the editor may still be appending.
            lock (_logLock) return _logBuffer.Skip(Math.Max(0, _logBuffer.Count - count)).ToList();
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            switch (level)
            {
                case LogLevel.Warning: GD.PushWarning(message); break;
                case LogLevel.Error: GD.PushError(message); break;
                default: GD.Print($"[AkerMcp] {message}"); break;
            }
            Append(level, message);
        }

        public void Append(LogLevel level, string message)
        {
            lock (_logLock)
            {
                _logBuffer.Add(new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message });
                if (_logBuffer.Count > MaxLogEntries) _logBuffer.RemoveAt(0);
            }
        }
    }
}
#endif
