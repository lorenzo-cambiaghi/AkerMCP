using System;
using System.Diagnostics;
using System.IO;
using AkerMcp.Shared.Ipc;

namespace AkerMcp.Client
{
    public class PluginDiscovery : IDisposable
    {
        private readonly string _lockFilePath;
        private bool _disposed;

        public string PipeName { get; }

        /// <param name="projectPath">
        /// The open project (a folder, or a solution file on Stride), written in the lock so that the server can tell
        /// two editors of the same engine apart and be pinned to one of them. Null when the engine cannot say.
        /// </param>
        public PluginDiscovery(string engineName, string engineVersion, string? projectPath = null)
        {
            using var current = Process.GetCurrentProcess();
            var pid = current.Id;
            PipeName = $"{IpcConstants.PipePrefix}{engineName.ToLowerInvariant()}-{pid}";

            var discoveryDir = LockFileStore.Directory;
            Directory.CreateDirectory(discoveryDir);

            // Purge stale lock files from dead processes.
            LockFileStore.ReadLive();

            _lockFilePath = Path.Combine(discoveryDir, $"{engineName.ToLowerInvariant()}-{pid}.lock");

            LockFileStore.Write(_lockFilePath, new EngineLockInfo
            {
                Pipe = PipeName,
                Engine = engineName,
                Version = engineVersion,
                ProtocolVersion = IpcConstants.ProtocolVersion,
                Pid = pid,
                // The *process* start time (not "now"), so that the PID-recycling guard works on later reads.
                StartTime = current.StartTime.ToUniversalTime().ToString("o"),
                Project = ProjectName(projectPath),
                ProjectPath = string.IsNullOrWhiteSpace(projectPath) ? null : projectPath,
            });
        }

        /// <summary>"MyGame" for "C:/Projects/MyGame", "C:/Projects/MyGame/" and "C:/Projects/MyGame/MyGame.sln".</summary>
        public static string? ProjectName(string? projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return null;
            var trimmed = projectPath!.TrimEnd('/', '\\');
            var name = Path.HasExtension(trimmed) && !Directory.Exists(trimmed)
                ? Path.GetFileNameWithoutExtension(trimmed)
                : Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? null : name;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (File.Exists(_lockFilePath))
                    File.Delete(_lockFilePath);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
