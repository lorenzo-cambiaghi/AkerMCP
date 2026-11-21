using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AkerMcp.Shared.Setup
{
    /// <summary>
    /// The standalone server installed for this user: one folder per version, filled from the release zip for this OS.
    ///
    /// The editor plugins point the AI client at the copy that matches their own version, so a user needs neither Node
    /// nor the .NET SDK, and the server never drifts from the plugin it talks to. The npm launcher (packages/npm) keeps
    /// its downloads in the same place, laid out the same way: <c>&lt;Root&gt;/v1.7.0/win-x64/AkerMcp.Server.exe</c>.
    ///
    /// The folder is a data folder, not a cache: an AI client config names a file inside it, and a cache cleaner that
    /// emptied it would leave the client starting nothing.
    /// </summary>
    /// <summary>Downloads <paramref name="url"/> (following redirects) to <paramref name="destinationPath"/>; throws on any failure.</summary>
    public delegate Task DownloadFile(string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellation);

    public static class ServerInstall
    {
        public const string Repository = "lorenzo-cambiaghi/AkerMCP";

        /// <summary>Points the install somewhere else (a portable setup, a test). Read by the npm launcher as well.</summary>
        public const string RootVariable = "AKER_MCP_HOME";

        private static readonly Regex VersionFolder = new Regex(@"^v(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>%LOCALAPPDATA%\AkerMcp\server, ~/Library/Application Support/AkerMcp/server, or $XDG_DATA_HOME/AkerMcp/server.</summary>
        public static string Root
        {
            get
            {
                var overridden = Environment.GetEnvironmentVariable(RootVariable);
                if (!string.IsNullOrWhiteSpace(overridden))
                    return Path.GetFullPath(overridden);

                // Spelled out per OS: SpecialFolder.LocalApplicationData means ~/.local/share on Mono for macOS and
                // ~/Library/Application Support on .NET 8, and the Unity editor and the server must agree.
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (IsWindows)
                {
                    var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                    if (string.IsNullOrEmpty(local)) local = Path.Combine(home, "AppData", "Local");
                    return Path.Combine(local, "AkerMcp", "server");
                }
                if (IsMac)
                    return Path.Combine(home, "Library", "Application Support", "AkerMcp", "server");

                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "AkerMcp", "server");
            }
        }

        /// <summary>
        /// The release build that runs here, or null when there is none. Apple Silicon runs the x64 build under Rosetta,
        /// and Windows on Arm runs it under emulation: those are the only builds the release publishes.
        /// </summary>
        public static string? RuntimeId
        {
            get
            {
                var arch = RuntimeInformation.OSArchitecture;
                if (IsWindows && (arch == Architecture.X64 || arch == Architecture.Arm64)) return "win-x64";
                if (IsMac && (arch == Architecture.X64 || arch == Architecture.Arm64)) return "osx-x64";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && arch == Architecture.X64) return "linux-x64";
                return null;
            }
        }

        public static string ExecutableName => IsWindows ? "AkerMcp.Server.exe" : "AkerMcp.Server";

        public static string AssetName(string runtimeId) => $"AkerMcp.Server-{runtimeId}.zip";

        public static string DownloadUrl(string version, string runtimeId) =>
            $"https://github.com/{Repository}/releases/download/v{Bare(version)}/{AssetName(runtimeId)}";

        public static string ExecutablePath(string version) => ExecutablePath(version, Root, RequireRuntimeId());

        public static string ExecutablePath(string version, string root, string runtimeId) =>
            Path.Combine(root, "v" + Bare(version), runtimeId, ExecutableName);

        public static bool IsInstalled(string version) => RuntimeId != null && File.Exists(ExecutablePath(version));

        /// <summary>The version of the installed server a client config launches, or null when the command is anything else.</summary>
        public static string? VersionOf(string? command) => VersionOf(command, Root);

        public static string? VersionOf(string? command, string root)
        {
            if (string.IsNullOrWhiteSpace(command) || !Path.IsPathRooted(command)) return null;
            string full, fullRoot;
            try
            {
                full = Path.GetFullPath(command);
                fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception) { return null; }

            var comparison = IsWindows || IsMac ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) return null;

            var rest = full.Substring(fullRoot.Length + 1);
            var cut = rest.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (cut <= 0) return null;
            var match = VersionFolder.Match(rest.Substring(0, cut));
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// The installed server of this version, downloaded first when it is missing. Safe to call from two processes at
        /// once: the files land in a private folder and are moved into place whole.
        /// </summary>
        public static Task<string> EnsureAsync(string version, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
            EnsureAsync(version, HttpDownload, progress, cancellation);

        /// <summary>
        /// The same, with the caller's way of downloading. The Unity editor passes UnityWebRequest: the HttpClient of
        /// Unity's Mono fails GitHub's certificate chain ("TrustFailure: Chain instance is empty", Unity 6000.6.5,
        /// 2026-11-21), where the .NET 8 of the server, Godot and Stride does not.
        /// </summary>
        public static async Task<string> EnsureAsync(string version, DownloadFile download, IProgress<double>? progress = null, CancellationToken cancellation = default)
        {
            var runtimeId = RequireRuntimeId();
            var target = ExecutablePath(version);
            if (File.Exists(target)) return target;

            var root = Root;
            Directory.CreateDirectory(root);
            var zip = Path.Combine(root, $".download-{Guid.NewGuid():N}.zip");
            try
            {
                await download(DownloadUrl(version, runtimeId), zip, progress, cancellation).ConfigureAwait(false);
                // Unpacking 70 MB is not for the thread that called (an editor's main thread): it runs on the pool.
                return await Task.Run(() => InstallFromZip(zip, version, root, runtimeId), cancellation).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(zip);
            }
        }

        /// <summary>The default download: HttpClient, streamed to the file.</summary>
        public static async Task HttpDownload(string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellation)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AkerMcp-Setup");
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"GitHub answered {(int)response.StatusCode} for {url}.");

                    var total = response.Content.Headers.ContentLength;
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var file = File.Create(destinationPath))
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int n;
                        while ((n = await source.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                        {
                            await file.WriteAsync(buffer, 0, n, cancellation).ConfigureAwait(false);
                            read += n;
                            if (total > 0) progress?.Report((double)read / total.Value);
                        }
                    }
                }
            }
        }

        /// <summary>Unpacks a release zip into its version folder and returns the executable's path.</summary>
        public static string InstallFromZip(string zipPath, string version, string root, string runtimeId)
        {
            var target = ExecutablePath(version, root, runtimeId);
            var staging = Path.Combine(root, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // The release zips hold their files under "./": keep the file name, and never a path that could
                        // climb out of the folder.
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        entry.ExtractToFile(Path.Combine(staging, entry.Name), overwrite: true);
                    }
                }

                var staged = Path.Combine(staging, ExecutableName);
                if (!File.Exists(staged))
                    throw new InvalidDataException($"{Path.GetFileName(zipPath)} has no {ExecutableName}.");
                MarkExecutable(staged);

                return MoveIntoPlace(staging, target);
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        /// <summary>
        /// Copies a server that is running from somewhere else (a release zip unpacked in Downloads) into its version
        /// folder, so the client config does not point at a file the user is about to delete.
        /// </summary>
        public static string InstallCopyOf(string executable, string version)
        {
            var target = ExecutablePath(version);
            if (string.Equals(Path.GetFullPath(executable), Path.GetFullPath(target),
                    IsWindows || IsMac ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return target;
            if (File.Exists(target)) return target;

            var root = Root;
            Directory.CreateDirectory(root);
            var staging = Path.Combine(root, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                var staged = Path.Combine(staging, ExecutableName);
                File.Copy(executable, staged);
                MarkExecutable(staged);
                return MoveIntoPlace(staging, target);
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        private static string MoveIntoPlace(string staging, string target)
        {
            var dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            try
            {
                Directory.Move(staging, dir);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another process (a second client starting the npm launcher, another editor) got there first.
            }
            if (!File.Exists(target))
                throw new IOException($"Could not place the server at {target}.");
            return target;
        }

        private static void MarkExecutable(string path)
        {
            if (IsWindows) return;
            // netstandard2.1 has no File.SetUnixFileMode; chmod is on every macOS and Linux.
            using (var chmod = Process.Start(new ProcessStartInfo("/bin/chmod", $"755 \"{path}\"") { UseShellExecute = false, CreateNoWindow = true }))
            {
                chmod?.WaitForExit(10000);
            }
        }

        private static string RequireRuntimeId() =>
            RuntimeId ?? throw new PlatformNotSupportedException(
                $"No AkerMcp.Server build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}). " +
                $"Build it from source with the .NET 8 SDK: https://github.com/{Repository}");

        private static string Bare(string version) => version.Trim().TrimStart('v', 'V');

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* a leftover temp file is harmless */ }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception) { /* idem */ }
        }
    }
}
