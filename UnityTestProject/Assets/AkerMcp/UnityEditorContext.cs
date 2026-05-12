#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using AkerMcp.Shared.Abstraction;
using LogLevel = AkerMcp.Shared.Abstraction.LogLevel;

namespace AkerMcp.Unity
{
    public class UnityEditorContext : IEditorContext
    {
        private readonly List<LogEntry> _logBuffer = new List<LogEntry>();
        private const int MaxLogEntries = 200;

        public bool IsEditorMode => !EditorApplication.isPlaying;

        public UnityEditorContext()
        {
            Application.logMessageReceived += OnLogMessage;
        }

        public string GetSelectedObjectPath()
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

        public string GetCurrentScenePath()
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
            return _logBuffer.TakeLast(count);
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
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Message = condition,
                StackTrace = stackTrace,
                Level = type switch
                {
                    LogType.Error => LogLevel.Error,
                    LogType.Exception => LogLevel.Error,
                    LogType.Warning => LogLevel.Warning,
                    _ => LogLevel.Info
                }
            };

            _logBuffer.Add(entry);
            if (_logBuffer.Count > MaxLogEntries)
                _logBuffer.RemoveAt(0);
        }

        ~UnityEditorContext()
        {
            Application.logMessageReceived -= OnLogMessage;
        }
    }
}
#endif
