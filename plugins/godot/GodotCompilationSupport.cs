#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Godot exposes no public C# API to drive the .NET build pipeline, so this
    /// shells out to `dotnet build` and parses MSBuild diagnostics. On success
    /// Godot hot-reloads the rebuilt assembly when the editor regains focus
    /// (or immediately, with EnableDynamicLoading). Unlike Unity there is no
    /// domain reload that drops the IPC connection mid-call.
    /// </summary>
    public class GodotCompilationSupport : ICompilationSupport
    {
        private const int BuildTimeoutMs = 120_000;
        // After the process exits, how long to wait for the last output to drain. Bounded, because
        // a build node or compiler server that inherited the pipe can hold it open for minutes.
        private const int OutputDrainMs = 3_000;
        // Raw lines reported when the build failed and no line parsed as a diagnostic.
        private const int MaxRawLines = 20;

        private readonly object _lock = new();
        private readonly List<CompileMessage> _messages = new();
        private volatile bool _isCompiling;
        private bool _lastSucceeded = true;
        private DateTime _lastTime = DateTime.MinValue;

        private readonly string _projectDir;

        // path(line[,col[,endLine,endCol]]): error|warning [CODE]: message [project]
        private static readonly Regex MsBuildLine = new(
            @"^(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\):\s+(?<sev>error|warning)(?:\s+(?<code>[A-Za-z]+\d+))?\s*:\s+(?<msg>.+?)(?:\s+\[[^\]]*\])?\s*$",
            RegexOptions.Compiled);

        // Project- and tool-level diagnostics carry no line: "X.csproj : error NU1301: …",
        // "MSBUILD : error MSB1009: …", "CSC : error CS2001: …". The first pattern alone missed them,
        // so a failed restore came back as "FAILED: 0 errors" with nothing to act on.
        private static readonly Regex MsBuildOriginLine = new(
            @"^(?<file>[^(]+?)\s*:\s+(?<sev>error|warning)(?:\s+(?<code>[A-Za-z]+\d+))?\s*:\s+(?<msg>.+?)(?:\s+\[[^\]]*\])?\s*$",
            RegexOptions.Compiled);

        public GodotCompilationSupport()
        {
            _projectDir = ProjectSettings.GlobalizePath("res://").TrimEnd('/');
        }

        public void RequestRecompile()
        {
            // Resolved on every build (here, on the main thread, where ProjectSettings may be read):
            // the project file can be renamed or added while the plugin runs, and the first *.csproj
            // in the folder is not necessarily the one Godot builds.
            var csprojPath = FindCsproj(_projectDir);
            if (csprojPath == null)
            {
                lock (_lock)
                {
                    _messages.Clear();
                    _messages.Add(new CompileMessage
                    {
                        Type = CompileMessageType.Error,
                        Message = "No .csproj found in the project directory; cannot build.",
                        File = "", Line = 0, Column = 0
                    });
                    _lastSucceeded = false;
                    _lastTime = DateTime.Now;
                }
                return;
            }

            if (_isCompiling) return;
            _isCompiling = true;
            lock (_lock) _messages.Clear();

            // Run off the editor main thread; the refresh_scripts flow polls status.
            System.Threading.Tasks.Task.Run(() => RunBuild(csprojPath));
        }

        public CompilationStatus GetCompilationStatus()
        {
            lock (_lock)
            {
                return new CompilationStatus
                {
                    IsCompiling = _isCompiling,
                    IsImporting = false,
                    LastCompileSucceeded = _lastSucceeded,
                    ErrorCount = _messages.Count(m => m.Type == CompileMessageType.Error),
                    WarningCount = _messages.Count(m => m.Type == CompileMessageType.Warning),
                    LastCompileTime = _lastTime == DateTime.MinValue ? "never" : _lastTime.ToString("HH:mm:ss")
                };
            }
        }

        public IEnumerable<CompileMessage> GetCompileMessages()
        {
            lock (_lock) return _messages.ToList();
        }

        public void ClearCompileMessages()
        {
            lock (_lock) _messages.Clear();
        }

        private void RunBuild(string csprojPath)
        {
            var parsed = new List<CompileMessage>();
            bool success = false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{csprojPath}\" -c Debug --nologo -v quiet",
                    WorkingDirectory = _projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                // Set when each stream reaches its end (the null line).
                // Not disposed: a build node that inherited a pipe can deliver the end of a stream
                // after this method has returned, and the handler must still find the event alive.
                var stdoutDone = new System.Threading.ManualResetEventSlim();
                var stderrDone = new System.Threading.ManualResetEventSlim();
                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => Collect(e.Data, stdout, stdoutDone);
                proc.ErrorDataReceived += (_, e) => Collect(e.Data, stderr, stderrDone);

                if (!proc.Start())
                    throw new InvalidOperationException("Failed to start dotnet.");

                // Both streams are drained as they fill. Reading them to the end first (as before)
                // blocked until dotnet closed them, so the timeout passed to WaitForExit afterwards
                // never applied, and a hung build hung the "compiling" state forever.
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                bool exited = proc.WaitForExit(BuildTimeoutMs);
                if (!exited)
                {
                    // The whole tree: dotnet build hands the work to MSBuild nodes, and killing only
                    // the dotnet process leaves them building (and holding the project's files).
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    proc.WaitForExit(OutputDrainMs);
                    parsed.Add(new CompileMessage
                    {
                        Type = CompileMessageType.Error,
                        Message = $"'dotnet build' did not finish within {BuildTimeoutMs / 1000} s and was stopped.",
                        File = csprojPath, Line = 0, Column = 0
                    });
                }

                // WaitForExit(int) returns before the async readers have seen end-of-stream; give
                // the last lines a bounded moment to arrive rather than waiting on the pipes, which
                // a lingering build node can keep open.
                var drain = Stopwatch.StartNew();
                stdoutDone.Wait(OutputDrainMs);
                stderrDone.Wait(Math.Max(0, OutputDrainMs - (int)drain.ElapsedMilliseconds));

                string outText, errText;
                lock (stdout) outText = stdout.ToString();
                lock (stderr) errText = stderr.ToString();

                ParseDiagnostics(outText, parsed);
                ParseDiagnostics(errText, parsed);

                int exitCode = exited ? proc.ExitCode : -1;
                if (exited && exitCode != 0 && !parsed.Any(m => m.Type == CompileMessageType.Error))
                {
                    // The build failed and said why in a form neither pattern knows: report its own
                    // words instead of "FAILED: 0 errors".
                    var raw = RawLines(errText).Concat(RawLines(outText)).Take(MaxRawLines).ToList();
                    if (raw.Count == 0) raw.Add($"'dotnet build' exited with code {exitCode} and printed nothing.");
                    foreach (var line in raw)
                        parsed.Add(new CompileMessage
                        {
                            Type = CompileMessageType.Error,
                            Message = line,
                            File = csprojPath, Line = 0, Column = 0
                        });
                }

                success = exited && exitCode == 0
                          && !parsed.Any(m => m.Type == CompileMessageType.Error);
            }
            catch (Exception ex)
            {
                parsed.Add(new CompileMessage
                {
                    Type = CompileMessageType.Error,
                    Message = $"Failed to run 'dotnet build': {ex.Message}. Is the .NET SDK on PATH?",
                    File = "", Line = 0, Column = 0
                });
            }
            finally
            {
                lock (_lock)
                {
                    _messages.Clear();
                    _messages.AddRange(parsed);
                    _lastSucceeded = success;
                    _lastTime = DateTime.Now;
                }
                _isCompiling = false;
            }
        }

        private static void Collect(string? line, StringBuilder into, System.Threading.ManualResetEventSlim done)
        {
            if (line == null) { done.Set(); return; }
            lock (into) into.AppendLine(line);
        }

        private static void ParseDiagnostics(string output, List<CompileMessage> into)
        {
            if (string.IsNullOrEmpty(output)) return;
            var seen = new HashSet<string>(into.Select(Key));

            foreach (var raw in output.Split('\n'))
            {
                var text = raw.Trim();
                CompileMessage msg;

                var m = MsBuildLine.Match(text);
                if (m.Success)
                {
                    msg = new CompileMessage
                    {
                        Type = m.Groups["sev"].Value == "error" ? CompileMessageType.Error : CompileMessageType.Warning,
                        Message = WithCode(m),
                        File = m.Groups["file"].Value.Trim(),
                        Line = int.Parse(m.Groups["line"].Value),
                        Column = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 0
                    };
                }
                else
                {
                    m = MsBuildOriginLine.Match(text);
                    if (!m.Success) continue;
                    msg = new CompileMessage
                    {
                        Type = m.Groups["sev"].Value == "error" ? CompileMessageType.Error : CompileMessageType.Warning,
                        Message = WithCode(m),
                        File = m.Groups["file"].Value.Trim(),
                        Line = 0,
                        Column = 0
                    };
                }

                if (seen.Add(Key(msg))) into.Add(msg);
            }
        }

        // The code leads the message ("CS0103: The name …"), as in the Unity adapter's messages:
        // it is what a search for the fix starts from.
        private static string WithCode(Match m)
        {
            var msg = m.Groups["msg"].Value.Trim();
            return m.Groups["code"].Success ? $"{m.Groups["code"].Value}: {msg}" : msg;
        }

        private static IEnumerable<string> RawLines(string output)
            => output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

        private static string Key(CompileMessage m) => $"{m.File}|{m.Line}|{m.Column}|{m.Message}";

        // The project Godot itself builds: res://<dotnet/project/assembly_name>.csproj (GodotTools'
        // DetermineProjectLocation). Falls back to the project's sanitized name, then to any .csproj.
        private static string? FindCsproj(string dir)
        {
            try
            {
                foreach (var name in CandidateProjectNames())
                {
                    var path = Path.Combine(dir, name + ".csproj");
                    if (File.Exists(path)) return path;
                }
                return Directory.GetFiles(dir, "*.csproj").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }
            catch { return null; }
        }

        private static IEnumerable<string> CandidateProjectNames()
        {
            var assemblyName = ProjectSettings.GetSetting("dotnet/project/assembly_name", "").AsString();
            if (!string.IsNullOrWhiteSpace(assemblyName)) yield return assemblyName.Trim();

            // Godot's own fallback (Path::get_csharp_project_name): the project name with the
            // characters that break file names or assembly loading replaced by '-'.
            var projectName = ProjectSettings.GetSetting("application/config/name", "").AsString().Trim();
            if (projectName.Length > 0)
            {
                foreach (var c in new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\', ';', '\'', '=', ',' })
                    projectName = projectName.Replace(c, '-');
                yield return projectName;
            }
        }
    }
}
#endif
