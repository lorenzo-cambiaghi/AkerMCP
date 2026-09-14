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

        /// <summary>
        /// The last <paramref name="count"/> console entries, oldest first, as a copy. get_console_logs reads it on the
        /// pipe thread while the engine keeps logging on its own, so an implementation copies its buffer under a lock
        /// instead of handing out a live view: enumerating a list that the engine is appending to throws "Collection
        /// was modified", or returns an entry twice.
        /// </summary>
        IEnumerable<LogEntry> GetRecentLogs(int count = 50);
        void Log(string message, LogLevel level = LogLevel.Info);
    }
}
