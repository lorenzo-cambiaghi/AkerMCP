#nullable enable
using System;
using System.IO;
using Stride.Core.Diagnostics;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// The adapter's one diagnostics log, <c>%TEMP%\akermcp-stride.log</c>. It used to
    /// be three copy-pasted <c>File.AppendAllText</c> helpers: unsynchronised (lines from
    /// the WPF and pipe threads could interleave or throw on a sharing violation) and
    /// unbounded (the scene bridge wrote a line on every scene read, so a long session
    /// grew the file without limit).
    ///
    /// Thread-safe, and capped: past <see cref="MaxBytes"/> the file is copied over
    /// <c>akermcp-stride.old.log</c> and emptied, so at most two files exist. Copy and
    /// truncate rather than move, because the startup hook may hold it open for a moment.
    /// The startup hook appends to the same file with its own writer, since it cannot
    /// reference this assembly.
    ///
    /// <see cref="Error"/> also goes to Stride's <see cref="GlobalLogger"/>, which puts
    /// it in Game Studio's output and in get_console_logs: a failure that happens after
    /// a tool call has already answered (an asynchronous open or save) is otherwise
    /// invisible to the AI.
    /// </summary>
    internal static class StrideDiag
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new();
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "akermcp-stride.log");
        private static readonly string OldLogPath = Path.Combine(Path.GetTempPath(), "akermcp-stride.old.log");
        private static Logger? _editorLog;

        public static void Log(string category, string message)
        {
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}{Environment.NewLine}";
                lock (Gate)
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.Copy(LogPath, OldLogPath, overwrite: true);
                        File.WriteAllText(LogPath, string.Empty);
                    }
                    File.AppendAllText(LogPath, line);
                }
            }
            catch
            {
                // Diagnostics must never throw.
            }
        }

        public static void Error(string category, string message)
        {
            Log(category, "ERROR " + message);
            try
            {
                _editorLog ??= GlobalLogger.GetLogger("AkerMcp");
                _editorLog.Error($"[{category}] {message}");
            }
            catch
            {
                // Same rule: the log file already has it.
            }
        }
    }
}
