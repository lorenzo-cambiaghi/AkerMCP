using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AkerMcp.Server
{
    /// <summary>
    /// Finds the SoundFont that <c>create_music</c> uses when asked for real instruments, and
    /// fetches it once if it is not there yet.
    ///
    /// <para>The file is not in the repository and not in the release package, and both of
    /// those are deliberate. It is 151 MB, which no git host wants inside a checkout — GitHub
    /// refuses a file over 100 MB outright — and bundling it would put a third party's asset
    /// in every download of a tool most of whose users will never ask for orchestral audio.
    /// So it lives as a release asset and arrives on first use, cached under the user's
    /// profile where a second project reuses it rather than fetching it again.</para>
    ///
    /// <para>Which SoundFont matters more than where it lives. FluidR3_GM is MIT: its terms
    /// govern redistributing derived sound libraries, not the recordings made with it, so a
    /// game shipping audio generated here inherits nothing. The obvious small alternative,
    /// TimGM6mb, is GPL — for a tool whose output lands in someone else's project that is the
    /// wrong thing to hand over quietly. Frank Wen's licence travels next to the file, which
    /// is the one condition it asks for; the other, that nothing lock users into this
    /// particular font, is met by AKER_MCP_SOUNDFONT.</para>
    /// </summary>
    public static class SoundFontLibrary
    {
        /// <summary>Point this at any .sf2 to use it instead — the author's own font, a smaller one, anything.</summary>
        public const string PathVariable = "AKER_MCP_SOUNDFONT";

        /// <summary>
        /// Override where the default is fetched from: a mirror, or a local file:// URL. Either
        /// goes through the same size and SHA-256 checks as the default download.
        /// </summary>
        public const string UrlVariable = "AKER_MCP_SOUNDFONT_URL";

        public const string FileName = "FluidR3_GM_GS.sf2";

        /// <summary>The licence notice, embedded from docs/ and written beside the cached font.</summary>
        public const string LicenceFileName = "FluidR3-LICENSE.txt";

        /// <summary>
        /// Where the default font is published. A release asset of this repository rather than
        /// a third-party link, so the URL cannot rot under us and the bytes are the ones this
        /// code was tested against.
        /// </summary>
        public const string DefaultUrl =
            "https://github.com/lorenzo-cambiaghi/AkerMCP/releases/download/soundfont-v1/FluidR3_GM_GS.sf2";

        /// <summary>
        /// SHA-256 of the file this code was tested against: FluidR3 GM+GS, 151,001,312 bytes,
        /// as published on archive.org by Frank Wen. A download that does not match is
        /// rejected, so a mirror serving something else fails loudly instead of producing
        /// music nobody can account for.
        /// </summary>
        public const string ExpectedSha256 =
            "545B2833936F15F04DF5F0C5C4096B3BA6CED46EC7031F61991CAE46F8681986";

        private const long MinimumPlausibleBytes = 4L * 1024 * 1024;

        /// <summary>
        /// How long one call waits for a first-time fetch before answering "still downloading".
        /// The fetch itself carries on in the background: the server answers one message at a
        /// time, so a call that sat through a 151 MB download held every other tool with it.
        /// </summary>
        internal static TimeSpan ForegroundWait { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The longest the body may go without a byte. HttpClient.Timeout stops counting once the
        /// headers arrive, so a server that went quiet mid-file held the fetch — and the claim on
        /// the cache — for ever.
        /// </summary>
        internal static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>The longest a whole fetch may take, however steadily it trickles.</summary>
        internal static TimeSpan OverallTimeout { get; set; } = TimeSpan.FromMinutes(30);

        private static readonly object Sync = new object();
        private static Task<(string? path, string? error)>? _fetch;
        private static long _received;

        /// <summary>Where a downloaded font is kept: per user, shared across projects.</summary>
        public static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                      Environment.SpecialFolderOption.Create),
            "AkerMcp", "soundfonts", FileName);

        /// <summary>
        /// The font to render with: the override if set, the cached download if present, or a
        /// fetch. Returns null with a reason the caller can print, never throws for the
        /// ordinary cases — a missing font is a normal state, not an error in the score.
        /// </summary>
        public static async Task<(string? path, string? error)> Resolve(CancellationToken ct)
        {
            var configured = Environment.GetEnvironmentVariable(PathVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                // A wrong path gets the same full answer as a failed download: naming the
                // problem without naming the way out leaves the caller to guess that chiptune
                // exists, and it is the option that always works.
                return File.Exists(configured)
                    ? (configured, null)
                    : (null, Unavailable($"{PathVariable} points at '{configured}', which does not exist"));
            }

            if (File.Exists(CachePath)) return (Cached(), null);

            var url = Environment.GetEnvironmentVariable(UrlVariable);
            if (string.IsNullOrWhiteSpace(url)) url = DefaultUrl;

            Task<(string? path, string? error)> fetch;
            lock (Sync)
            {
                // One fetch per process, whoever asks: a second caller joins the first. A fetch
                // that ended in failure is not kept, so the next call tries again.
                if (_fetch == null || _fetch.IsCompleted)
                {
                    if (File.Exists(CachePath)) return (Cached(), null);
                    Interlocked.Exchange(ref _received, 0);
                    _fetch = Task.Run(() => Download(url!));
                }
                fetch = _fetch;
            }

            // A fast connection finishes inside the wait and the caller never knows; a slow one
            // gets an answer now and the font on a later call.
            var first = await Task.WhenAny(fetch, Task.Delay(ForegroundWait, ct)).ConfigureAwait(false);
            if (first == fetch) return await fetch.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return (null, $"The SoundFont is still downloading in the background " +
                          $"({Interlocked.Read(ref _received) / 1_000_000} of about 151 MB so far) — this happens " +
                          "once. Try again in a minute, or use the chiptune engine (omit \"engine\") meanwhile.");
        }

        /// <summary>
        /// The cached font, with the licence notice beside it. Written on every hit rather than
        /// only after a download, so a font cached before the notice existed gets one too; a
        /// failure to write it never costs the caller the music.
        /// </summary>
        private static string Cached()
        {
            var notice = Path.Combine(Path.GetDirectoryName(CachePath)!, LicenceFileName);
            try
            {
                if (!File.Exists(notice))
                {
                    using var source = typeof(SoundFontLibrary).Assembly.GetManifestResourceStream(LicenceFileName);
                    if (source != null)
                    {
                        using var target = File.Create(notice);
                        source.CopyTo(target);
                    }
                }
            }
            catch (Exception ex) { StdioTransport.LogInfo($"Could not write {notice}: {ex.Message}"); }
            return CachePath;
        }

        /// <summary>True when a font is already available without going to the network.</summary>
        public static bool IsAvailableLocally()
        {
            var configured = Environment.GetEnvironmentVariable(PathVariable);
            if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured);
            return File.Exists(CachePath);
        }

        /// <summary>Where a fetch in progress writes, beside the target and moved over it at the end.</summary>
        internal static string PartPath => CachePath + ".part";

        /// <summary>
        /// Fetch the font into the cache. Never throws: every way it can end is an answer.
        /// </summary>
        private static async Task<(string? path, string? error)> Download(string url)
        {
            // Download beside the target and move at the end: an interrupted fetch must not
            // leave a half file that looks cached and then fails to parse for good.
            var temp = PartPath;
            FileStream? claim = null;
            bool moved = false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);

                // One process fetches at a time. Two servers — two editors open, say — share this
                // cache, and without a claim both wrote the same .part and the second failed with
                // a bare sharing violation. The claim is held until the file is in place and
                // deletes itself when released, a crash included.
                claim = await Claim().ConfigureAwait(false);
                if (claim == null)
                    return (null, Unavailable("another AkerMcp server on this machine is downloading it right now; " +
                                              "try again in a minute"));
                // The other one may have finished while this one was getting here.
                if (File.Exists(CachePath)) return (Cached(), null);

                StdioTransport.LogInfo($"Fetching the SoundFont for create_music from {url} (about 151 MB, once).");

                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                {
                    // HttpClient has no file scheme: a local copy is read directly, and then
                    // checked exactly like a download.
                    await using var source = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read,
                                                            FileShare.Read, 81920, useAsync: true);
                    await using var file = File.Create(temp);
                    await source.CopyToAsync(file).ConfigureAwait(false);
                }
                else
                {
                    var failure = await Fetch(url, temp).ConfigureAwait(false);
                    if (failure != null) return (null, Unavailable(failure));
                }

                var size = new FileInfo(temp).Length;
                if (size < MinimumPlausibleBytes)
                    return (null, Unavailable($"the download was only {size / 1024} KB, which is not a SoundFont " +
                                              "(an error page, most likely)"));

                var actual = Sha256(temp);
                if (!string.Equals(actual, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    return (null, Unavailable($"the download's SHA-256 is {actual}, not the expected {ExpectedSha256}"));

                File.Move(temp, CachePath, overwrite: true);
                moved = true;
                StdioTransport.LogInfo($"SoundFont cached at {CachePath} ({size / (1024 * 1024)} MB).");
                return (Cached(), null);
            }
            catch (Exception ex)
            {
                return (null, Unavailable($"{ex.GetType().Name}: {ex.Message}"));
            }
            finally
            {
                // However the fetch ended — a bad hash, a stall, a refused connection — no .part
                // is left to fill the disk. Only the holder of the claim may touch it: without the
                // claim, the .part belongs to the other process's fetch in progress.
                if (claim != null && !moved)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
                }
                claim?.Dispose();
            }
        }

        /// <summary>
        /// The cross-process claim on the cache, or null if another process keeps it. Waited for
        /// briefly: a fetch that fails, or is about to finish, lets go within moments, and giving
        /// up at once turned two servers starting together into a spurious error.
        /// </summary>
        private static async Task<FileStream?> Claim()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                try
                {
                    return new FileStream(CachePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                          FileShare.None, 1, FileOptions.DeleteOnClose);
                }
                // The other holder letting go is not one error but two. While it holds the file,
                // opening it is a sharing violation (IOException); in the instant it closes, a
                // DeleteOnClose file is delete-pending and Windows answers access denied
                // (UnauthorizedAccessException) instead — which escaped this loop and turned the
                // moment of release into a failed download.
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException)
                                           && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
                // Still denied after ten seconds is not a release in progress but a cache folder
                // this process may not write, so that one is left to propagate: the answer then
                // says access was denied instead of blaming another server's download.
                catch (IOException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Stream an HTTP body into <paramref name="temp"/>, with a deadline on every read and on
        /// the whole. Returns null when the body arrived, or what went wrong.
        /// </summary>
        private static async Task<string?> Fetch(string url, string temp)
        {
            using var overall = new CancellationTokenSource(OverallTimeout);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            // The deadlines below are the only ones: HttpClient's own would stop at the headers.
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                idle.CancelAfter(IdleTimeout);
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token)
                                               .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return $"the download returned {(int)response.StatusCode} {response.ReasonPhrase}";

                await using var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                await using var file = File.Create(temp);
                var buffer = new byte[81920];
                while (true)
                {
                    idle.CancelAfter(IdleTimeout);          // re-armed for every read
                    int n = await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (n == 0) return null;
                    await file.WriteAsync(buffer.AsMemory(0, n), idle.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _received, n);
                }
            }
            catch (OperationCanceledException)
            {
                return overall.IsCancellationRequested
                    ? $"the download did not finish within {OverallTimeout.TotalMinutes:0} minutes"
                    : $"the download stalled: no data for {IdleTimeout.TotalSeconds:0} seconds";
            }
        }

        private static string Unavailable(string because) =>
            $"No SoundFont available: {because}. " +
            $"Either set {PathVariable} to a .sf2 file you already have, or use the chiptune engine " +
            "(omit \"engine\" from the score), which needs nothing and is the default.";

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
    }
}
