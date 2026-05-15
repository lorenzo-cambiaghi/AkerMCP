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
            var pid = Process.GetCurrentProcess().Id;
            PipeName = $"{IpcConstants.PipePrefix}{engineName.ToLowerInvariant()}-{pid}";

            var discoveryDir = Path.Combine(Path.GetTempPath(), IpcConstants.DiscoveryDirectory);
            Directory.CreateDirectory(discoveryDir);

            // Purge stale lock files from dead processes
            PurgeStaleLockFiles(discoveryDir);

            _lockFilePath = Path.Combine(discoveryDir, $"{engineName.ToLowerInvariant()}-{pid}.lock");

            var info = new
            {
                pipe = PipeName,
                engine = engineName,
                version = engineVersion,
                protocolVersion = IpcConstants.ProtocolVersion,
                pid = pid,
                startTime = DateTime.UtcNow.ToString("o")
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
                        if (info.TryGetProperty("pid", out var pidElem))
                        {
                            var pid = pidElem.GetInt32();
                            try
                            {
                                Process.GetProcessById(pid);
                                // Process alive, leave it
                            }
                            catch
                            {
                                // Process dead, remove stale file
                                File.Delete(lockFile);
                            }
                        }
                    }
                    catch
                    {
                        try { File.Delete(lockFile); } catch { }
                    }
                }
            }
            catch { /* Discovery dir access error, skip */ }
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
