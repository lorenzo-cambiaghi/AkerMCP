using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AkerMcp.Shared.Ipc;

namespace AkerMcp.Client
{
    public class PluginDiscovery : IDisposable
    {
        private readonly string _lockFilePath;
        private bool _disposed;

        public string PipeName { get; }

        public PluginDiscovery(string engineName, string engineVersion)
        {
            var current = Process.GetCurrentProcess();
            var pid = current.Id;
            PipeName = $"{IpcConstants.PipePrefix}{engineName.ToLowerInvariant()}-{pid}";

            var discoveryDir = Path.Combine(Path.GetTempPath(), IpcConstants.DiscoveryDirectory);
            Directory.CreateDirectory(discoveryDir);

            // Purge stale lock files from dead processes
            PurgeStaleLockFiles(discoveryDir);

            _lockFilePath = Path.Combine(discoveryDir, $"{engineName.ToLowerInvariant()}-{pid}.lock");

            // startTime must be the *process* start time (not "now") so that the
            // PID-recycling guard in IsLockOwnerAlive works on subsequent reads.
            var info = new
            {
                pipe = PipeName,
                engine = engineName,
                version = engineVersion,
                protocolVersion = IpcConstants.ProtocolVersion,
                pid = pid,
                startTime = current.StartTime.ToUniversalTime().ToString("o")
            };

            File.WriteAllText(_lockFilePath, JsonSerializer.Serialize(info));
        }

        private static void PurgeStaleLockFiles(string discoveryDir)
        {
            try
            {
                foreach (var lockFile in Directory.GetFiles(discoveryDir, "*.lock"))
                {
                    try
                    {
                        var content = File.ReadAllText(lockFile);
                        var info = JsonSerializer.Deserialize<JsonElement>(content);
                        if (!IsLockOwnerAlive(info))
                            File.Delete(lockFile);
                    }
                    catch
                    {
                        try { File.Delete(lockFile); } catch { }
                    }
                }
            }
            catch { /* Discovery dir access error, skip */ }
        }

        private static bool IsLockOwnerAlive(JsonElement info)
        {
            if (!info.TryGetProperty("pid", out var pidElem)) return false;
            var pid = pidElem.GetInt32();

            Process process;
            try { process = Process.GetProcessById(pid); }
            catch { return false; }

            try
            {
                if (process.HasExited) return false;

                // Guard against PID recycling: compare process start time with the
                // one recorded in the lock file. Tolerate small clock-skew (2s).
                if (info.TryGetProperty("startTime", out var startElem) &&
                    DateTime.TryParse(startElem.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var lockStart))
                {
                    var processStart = process.StartTime.ToUniversalTime();
                    if (Math.Abs((lockStart - processStart).TotalSeconds) > 2)
                        return false;
                }
                return true;
            }
            finally
            {
                process.Dispose();
            }
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
