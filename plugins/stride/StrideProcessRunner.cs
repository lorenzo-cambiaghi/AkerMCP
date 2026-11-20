#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AkerMcp.StrideAdapter
{
    /// <summary>What a finished (or killed) external process left behind.</summary>
    internal sealed class ProcessRunResult
    {
        public int ExitCode { get; init; } = -1;
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";

        /// <summary>The process outlived its timeout and its whole tree was killed.</summary>
        public bool TimedOut { get; init; }
    }

    /// <summary>
    /// Runs one external process (here always <c>dotnet build</c>) for
    /// <see cref="StrideCompilationSupport"/> and <see cref="StrideBuildManager"/>.
    ///
    /// The two classes used to call <c>ReadToEnd</c> on stdout, then on stderr, then
    /// <c>WaitForExit(timeout)</c>. The reads block until the stream closes, so the
    /// timeout never got a chance: a hung build blocked forever (and pinned
    /// refresh_scripts' "compiling" flag), a build that filled the stderr pipe while we
    /// sat on stdout deadlocked, and nothing ever killed the process. Here both
    /// streams are drained concurrently, the timeout is real, and on expiry the whole
    /// tree (MSBuild nodes included) is killed.
    ///
    /// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>: a quoted
    /// command line breaks on a path ending in '\', which escapes the closing quote.
    /// </summary>
    internal static class StrideProcessRunner
    {
        private const string HookFileName = "AkerMcp.Stride.StartupHook.dll";

        // After the process exits, how long to wait for the pipes to close. A grandchild
        // that inherited them (a build server) can keep them open long after the build.
        private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(3);

        public static async Task<ProcessRunResult> RunAsync(
            string fileName, IEnumerable<string> arguments, string workingDirectory,
            TimeSpan timeout, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            DropOurStartupHook(psi);

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            proc.Exited += (_, _) => exited.TrySetResult(true);

            if (!proc.Start())
                throw new InvalidOperationException($"Failed to start '{fileName}'.");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var pumps = Task.WhenAll(Pump(proc.StandardOutput, stdout), Pump(proc.StandardError, stderr));

            bool timedOut = false;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(timeout);
                // Wait on the exit itself, not on the pipes closing (see DrainGrace).
                var cancelled = Task.Delay(Timeout.Infinite, limit.Token);
                if (proc.HasExited) exited.TrySetResult(true);
                if (await Task.WhenAny(exited.Task, cancelled).ConfigureAwait(false) != exited.Task)
                {
                    timedOut = !ct.IsCancellationRequested;
                    KillTree(proc);
                }
            }

            await Task.WhenAny(pumps, Task.Delay(DrainGrace)).ConfigureAwait(false);

            int exit = -1;
            try { if (proc.HasExited) exit = proc.ExitCode; } catch { /* killed before it started */ }

            ct.ThrowIfCancellationRequested();
            return new ProcessRunResult
            {
                ExitCode = exit,
                StdOut = Snapshot(stdout),
                StdErr = Snapshot(stderr),
                TimedOut = timedOut,
            };
        }

        // Read in chunks into a buffer we own, so whatever arrived is still usable when a
        // pipe never closes (ReadToEndAsync would give nothing at all).
        private static async Task Pump(StreamReader reader, StringBuilder sink)
        {
            var buffer = new char[4096];
            try
            {
                int n;
                while ((n = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    lock (sink) sink.Append(buffer, 0, n);
            }
            catch
            {
                // Pipe torn down by the kill: keep what was read.
            }
        }

        private static string Snapshot(StringBuilder sb)
        {
            lock (sb) return sb.ToString();
        }

        private static void KillTree(Process proc)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { proc.WaitForExit(5000); } catch { }
        }

        // Game Studio runs with our startup hook in DOTNET_STARTUP_HOOKS and every child
        // inherits it. The hook leaves non-Game Studio processes at once, but a build we
        // start ourselves need not even load it (and map the DLL for the life of the
        // MSBuild nodes). Only our entry goes: someone else's hooks are not ours to drop.
        private static void DropOurStartupHook(ProcessStartInfo psi)
        {
            if (!psi.Environment.TryGetValue("DOTNET_STARTUP_HOOKS", out var hooks) || string.IsNullOrEmpty(hooks))
                return;
            var kept = hooks.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(h => !string.Equals(Path.GetFileName(h.Trim()), HookFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (kept.Count == 0) psi.Environment.Remove("DOTNET_STARTUP_HOOKS");
            else psi.Environment["DOTNET_STARTUP_HOOKS"] = string.Join(Path.PathSeparator, kept);
            psi.Environment.Remove("AKERMCP_STRIDE_PLUGIN_DIR");
        }
    }

    /// <summary>One error or warning from MSBuild/dotnet output.</summary>
    internal sealed class MsBuildDiagnostic
    {
        public bool IsError { get; init; }
        /// <summary>Source file, or the project / tool that raised it ("MSBUILD", "X.csproj").</summary>
        public string Origin { get; init; } = "";
        public int Line { get; init; }
        public int Column { get; init; }
        public string Code { get; init; } = "";
        public string Message { get; init; } = "";

        /// <summary>The message with its code in front ("CS1002: ; expected"), as the AI wants to read it.</summary>
        public string Text => Code.Length > 0 ? $"{Code}: {Message}" : Message;
    }

    /// <summary>
    /// Parses MSBuild's canonical error format. The old regex required a
    /// <c>file(line,col)</c> origin, so project-level failures (<c>X.csproj : error
    /// NU1301</c> when a feed is unreachable, <c>MSBUILD : error MSB1009</c>) matched
    /// nothing and a failed build reported "0 errors". Those now parse, and when a
    /// build fails with nothing recognisable the raw output is kept instead.
    /// </summary>
    internal static class MsBuildDiagnostics
    {
        //   C:\p\Foo.cs(12,5): error CS1002: ; expected [C:\p\Game.csproj]
        //   C:\p\Game.csproj : error NU1301: Unable to load the service index ...
        //   MSBUILD : error MSB1009: Project file does not exist.
        // The origin is lazy, so it stops at the first "(pos): error|warning" that fits;
        // a drive letter's ':' never matches, it is not followed by whitespace.
        private static readonly Regex CanonicalLine = new(
            @"^(?<origin>.+?)(?:\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\))?\s*:\s+(?<sev>(?:fatal\s+)?error|warning)(?:\s+(?<code>[A-Za-z]+\d+))?\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]*\])?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>All diagnostics in <paramref name="outputs"/>, each once (MSBuild repeats them in its summary).</summary>
        public static List<MsBuildDiagnostic> Parse(params string[] outputs)
        {
            var found = new List<MsBuildDiagnostic>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var output in outputs)
            {
                if (string.IsNullOrEmpty(output)) continue;
                foreach (var raw in output.Split('\n'))
                {
                    var m = CanonicalLine.Match(raw.Trim());
                    if (!m.Success) continue;

                    var d = new MsBuildDiagnostic
                    {
                        IsError = m.Groups["sev"].Value.EndsWith("error", StringComparison.OrdinalIgnoreCase),
                        Origin = m.Groups["origin"].Value.Trim(),
                        Line = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : 0,
                        Column = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 0,
                        Code = m.Groups["code"].Value,
                        Message = m.Groups["msg"].Value.Trim(),
                    };
                    if (seen.Add($"{d.IsError}|{d.Origin}|{d.Line}|{d.Column}|{d.Code}|{d.Message}"))
                        found.Add(d);
                }
            }
            return found;
        }

        /// <summary>
        /// What to show when a run failed but <see cref="Parse"/> found no error: the
        /// non-empty stderr lines, else the tail of stdout (dotnet's own CLI errors, an
        /// SDK resolution failure, a crash). Capped at <paramref name="max"/> lines.
        /// </summary>
        public static List<string> FallbackLines(ProcessRunResult run, int max = 15)
        {
            static List<string> Lines(string s) => s.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
            var err = Lines(run.StdErr);
            if (err.Count > 0) return err.Take(max).ToList();
            var outLines = Lines(run.StdOut);
            return outLines.Skip(Math.Max(0, outLines.Count - max)).ToList();
        }
    }
}
