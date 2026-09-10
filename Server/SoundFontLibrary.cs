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
    /// those are deliberate. It is 148 MB, which no git host wants inside a checkout — GitHub
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

        /// <summary>Override where the default is fetched from (a mirror, or a local file:// URL).</summary>
        public const string UrlVariable = "AKER_MCP_SOUNDFONT_URL";

        public const string FileName = "FluidR3_GM_GS.sf2";

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

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

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

            if (File.Exists(CachePath)) return (CachePath, null);

            var url = Environment.GetEnvironmentVariable(UrlVariable);
            if (string.IsNullOrWhiteSpace(url)) url = DefaultUrl;

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Someone may have won the race while this call waited.
                if (File.Exists(CachePath)) return (CachePath, null);
                return await Download(url!, ct).ConfigureAwait(false);
            }
            finally { Gate.Release(); }
        }

        /// <summary>True when a font is already available without going to the network.</summary>
        public static bool IsAvailableLocally()
        {
            var configured = Environment.GetEnvironmentVariable(PathVariable);
            if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured);
            return File.Exists(CachePath);
        }

        private static async Task<(string? path, string? error)> Download(string url, CancellationToken ct)
        {
            var directory = Path.GetDirectoryName(CachePath)!;
            Directory.CreateDirectory(directory);
            // Download beside the target and move at the end: an interrupted fetch must not
            // leave a half file that looks cached and then fails to parse for good.
            var temp = CachePath + ".part";

            try
            {
                StdioTransport.LogInfo($"Fetching the SoundFont for create_music from {url} (about 148 MB, once).");

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                               .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return (null, Unavailable($"the download returned {(int)response.StatusCode} {response.ReasonPhrase}"));

                await using (var netStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var file = File.Create(temp))
                    await netStream.CopyToAsync(file, ct).ConfigureAwait(false);

                var size = new FileInfo(temp).Length;
                if (size < MinimumPlausibleBytes)
                {
                    File.Delete(temp);
                    return (null, Unavailable($"the download was only {size / 1024} KB, which is not a SoundFont " +
                                              "(an error page, most likely)"));
                }

                if (ExpectedSha256.Length > 0)
                {
                    var actual = Sha256(temp);
                    if (!string.Equals(actual, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(temp);
                        return (null, Unavailable($"the download's SHA-256 is {actual}, not the expected " +
                                                  $"{ExpectedSha256}"));
                    }
                }

                File.Move(temp, CachePath, overwrite: true);
                StdioTransport.LogInfo($"SoundFont cached at {CachePath} ({size / (1024 * 1024)} MB).");
                return (CachePath, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
                return (null, Unavailable($"{ex.GetType().Name}: {ex.Message}"));
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
