using System.Collections.Generic;

namespace AkerMcp.Shared.Abstraction
{
    public interface IEditorContext
    {
        bool IsEditorMode { get; }
        string? GetSelectedObjectPath();
        void SetSelection(string objectPath);
        string? GetCurrentScenePath();
        void OpenScene(string path);
        void SaveScene();
        string GetProjectPath();
        IEnumerable<LogEntry> GetRecentLogs(int count = 50);
        void Log(string message, LogLevel level = LogLevel.Info);
    }
}
