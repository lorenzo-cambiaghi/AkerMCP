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
    /// Note: Godot exposes no global log-capture hook (unlike Unity's
    /// <c>Application.logMessageReceived</c>), so the console buffer only holds
    /// messages routed through <see cref="Log"/> / <see cref="Append"/> — chiefly
    /// the plugin's own output and <c>Log()</c> calls from `execute` scripts.
    /// </summary>
    public class GodotEditorContext : IEditorContext
    {
        private readonly List<LogEntry> _logBuffer = new();
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
            => _logBuffer.Skip(Math.Max(0, _logBuffer.Count - count));

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
            _logBuffer.Add(new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message });
            if (_logBuffer.Count > MaxLogEntries) _logBuffer.RemoveAt(0);
        }
    }
}
