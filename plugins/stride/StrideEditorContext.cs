#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AkerMcp.Shared.Abstraction;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Diagnostics;
using Stride.Engine;
using LogLevel = AkerMcp.Shared.Abstraction.LogLevel;
using LogEntry = AkerMcp.Shared.Abstraction.LogEntry;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Editor integration for Stride: selection (read/write against the active scene
    /// editor's SelectedContent), current scene + project paths, and a console-log
    /// buffer fed from Stride's <see cref="GlobalLogger.GlobalMessageLogged"/> so
    /// get_console_logs surfaces real engine output (like Unity's log hook).
    /// </summary>
    public class StrideEditorContext : IEditorContext
    {
        private readonly SessionViewModel _session;
        private readonly List<LogEntry> _logBuffer = new();
        private readonly object _lock = new();
        private const int MaxLogEntries = 300;

        public StrideEditorContext(SessionViewModel session)
        {
            _session = session;
            GlobalLogger.GlobalMessageLogged += OnGlobalLog;
        }

        public bool IsEditorMode => true;

        public string GetProjectPath()
            => _session.SolutionPath != null ? _session.SolutionPath.ToOSPath() : "";

        public string? GetCurrentScenePath()
            => StrideSceneBridge.FindActiveEntityEditor()?.Asset.Url;

        public string? GetSelectedObjectPath()
        {
            var editor = StrideSceneBridge.FindActiveEntityEditor();
            var vm = editor?.SelectedContent.OfType<EntityViewModel>().FirstOrDefault();
            return vm != null ? new StrideSceneNode(vm.AssetSideEntity).Path : null;
        }

        public void SetSelection(string objectPath)
        {
            var editor = StrideSceneBridge.FindActiveEntityEditor()
                ?? throw new InvalidOperationException("No scene editor is open.");
            if (new StrideSceneGraph(_session).GetNode(objectPath)?.UnderlyingObject is not Entity e)
                throw new InvalidOperationException($"Object not found: {objectPath}");

            var vm = FindViewModel(editor.HierarchyRoot, e.Id);
            if (vm == null)
                throw new InvalidOperationException($"No editor item found for {objectPath}.");

            editor.SelectedContent.Clear();
            editor.SelectedContent.Add(vm);
        }

        // Not wired to any MCP tool; kept as explicit no-ops/stubs.
        public void OpenScene(string path)
            => throw new NotSupportedException("OpenScene is not supported by the Stride adapter.");
        public void SaveScene() { /* no tool drives this yet */ }

        public IEnumerable<LogEntry> GetRecentLogs(int count = 50)
        {
            lock (_lock) return _logBuffer.Skip(Math.Max(0, _logBuffer.Count - count)).ToList();
        }

        public void Log(string message, LogLevel level = LogLevel.Info) => Append(level, message);

        private void OnGlobalLog(ILogMessage m)
        {
            var level = m.Type switch
            {
                LogMessageType.Error or LogMessageType.Fatal => LogLevel.Error,
                LogMessageType.Warning => LogLevel.Warning,
                _ => LogLevel.Info
            };
            Append(level, m.Text);
        }

        private void Append(LogLevel level, string message)
        {
            lock (_lock)
            {
                _logBuffer.Add(new LogEntry { Timestamp = DateTime.Now, Level = level, Message = message });
                if (_logBuffer.Count > MaxLogEntries) _logBuffer.RemoveAt(0);
            }
        }

        private static EntityViewModel? FindViewModel(EntityHierarchyItemViewModel root, Guid entityId)
        {
            foreach (var item in Traverse(root))
                if (item is EntityViewModel ev && ev.Id.ObjectId == entityId)
                    return ev;
            return null;
        }

        private static IEnumerable<EntityHierarchyItemViewModel> Traverse(EntityHierarchyItemViewModel node)
        {
            yield return node;
            foreach (var child in node.Children)
                foreach (var d in Traverse(child))
                    yield return d;
        }
    }
}
