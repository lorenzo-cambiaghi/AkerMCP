using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AkerMcp.Shared.Ipc
{
    /// <summary>What a running plugin writes about itself in the discovery directory.</summary>
    public sealed class EngineLockInfo
    {
        [JsonPropertyName("pipe")] public string Pipe { get; set; } = "";
        [JsonPropertyName("engine")] public string Engine { get; set; } = "Unknown";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; set; } = "";
        [JsonPropertyName("pid")] public int Pid { get; set; }

        /// <summary>The process start time, not the time of writing: it is what tells a live owner from a recycled pid.</summary>
        [JsonPropertyName("startTime")] public string? StartTime { get; set; }

        /// <summary>
        /// The project the editor has open, so two editors of the same engine can be told apart. Absent in lock
        /// files written by plugins older than 1.6.
        /// </summary>
        [JsonPropertyName("project")] public string? Project { get; set; }
        [JsonPropertyName("projectPath")] public string? ProjectPath { get; set; }
    }

    /// <summary>
    /// The discovery directory, shared by the plugin that announces itself and the server that looks for it. The two
    /// halves used to keep a copy each of the liveness check, and the copies had already drifted.
    /// </summary>
    public static class LockFileStore
    {
        // A file younger than this that does not parse may be one being written by an older plugin (which did not
        // write atomically): it is left alone, not deleted, or the plugin would never be found until it restarted.
        private static readonly TimeSpan UnreadableGrace = TimeSpan.FromSeconds(10);

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string Directory => Path.Combine(Path.GetTempPath(), IpcConstants.DiscoveryDirectory);

        /// <summary>
        /// Writes the file whole or not at all: to a temporary name first, then moved over. A reader can no longer see
        /// half a file and, taking it for a corrupt one, delete a live plugin's lock.
        /// </summary>
        public static void Write(string path, EngineLockInfo info)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(info, Options));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        /// <summary>Every live plugin's lock, after removing the ones whose owner is gone.</summary>
        public static List<EngineLockInfo> ReadLive(Action<string>? log = null)
        {
            var found = new List<EngineLockInfo>();
            var dir = Directory;
            if (!System.IO.Directory.Exists(dir)) return found;

            string[] files;
            try { files = System.IO.Directory.GetFiles(dir, "*.lock"); }
            catch { return found; }

            foreach (var file in files)
            {
                EngineLockInfo? info;
                try
                {
                    info = JsonSerializer.Deserialize<EngineLockInfo>(File.ReadAllText(file));
                }
                catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (ex is JsonException && IsOlderThan(file, UnreadableGrace))
                        Delete(file, "unreadable", log);
                    continue;
                }

                if (info == null || info.Pid <= 0)
                {
                    if (IsOlderThan(file, UnreadableGrace)) Delete(file, "no pid", log);
                    continue;
                }

                if (!IsOwnerAlive(info, out var reason))
                {
                    Delete(file, reason, log);
                    continue;
                }

                if (!string.IsNullOrEmpty(info.Pipe)) found.Add(info);
            }

            PurgeOrphanedTemporaries(dir);
            return found;
        }

        /// <summary>
        /// Whether the process that wrote the lock is still the one running under its pid. A process that exists but
        /// cannot be looked at (another user's, an elevated editor) counts as alive: deleting its lock would make a
        /// running editor undiscoverable, while keeping a dead one costs one failed connection attempt.
        /// </summary>
        public static bool IsOwnerAlive(EngineLockInfo info, out string reason)
        {
            Process process;
            try
            {
                process = Process.GetProcessById(info.Pid);
            }
            catch (ArgumentException)
            {
                reason = $"PID {info.Pid} is dead";
                return false;
            }
            catch (Exception)
            {
                reason = "alive (not inspectable)";
                return true;
            }

            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        reason = $"PID {info.Pid} has exited";
                        return false;
                    }

                    // Guard against PID recycling: compare the process start time with the one recorded in the lock
                    // file. Tolerate small clock skew (2 s).
                    if (info.StartTime != null &&
                        DateTime.TryParse(info.StartTime, null, DateTimeStyles.RoundtripKind, out var lockStart))
                    {
                        var processStart = process.StartTime.ToUniversalTime();
                        if (Math.Abs((lockStart - processStart).TotalSeconds) > 2)
                        {
                            reason = $"PID {info.Pid} was recycled (start time mismatch)";
                            return false;
                        }
                    }
                }
                catch (Exception)
                {
                    // Win32Exception / InvalidOperationException: access denied to a live process.
                    reason = "alive (not inspectable)";
                    return true;
                }
            }

            reason = "alive";
            return true;
        }

        private static bool IsOlderThan(string file, TimeSpan age)
        {
            try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > age; }
            catch { return false; }
        }

        private static void Delete(string file, string reason, Action<string>? log)
        {
            try
            {
                File.Delete(file);
                log?.Invoke($"Removing stale lock file: {Path.GetFileName(file)} ({reason})");
            }
            catch { /* someone else removed it, or it is still open: next scan */ }
        }

        private static void PurgeOrphanedTemporaries(string dir)
        {
            try
            {
                foreach (var temp in System.IO.Directory.GetFiles(dir, "*.tmp"))
                    if (IsOlderThan(temp, TimeSpan.FromMinutes(1)))
                        try { File.Delete(temp); } catch { }
            }
            catch { }
        }
    }
}
