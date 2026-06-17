#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly object _lock = new();
        private readonly List<CompileMessage> _messages = new();
        private volatile bool _isCompiling;
        private bool _lastSucceeded = true;
        private DateTime _lastTime = DateTime.MinValue;

        private readonly string _projectDir;
        private readonly string? _csprojPath;

        // path(line,col): error|warning CSxxxx: message [project]
        private static readonly Regex MsBuildLine = new(
            @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+\w+:\s+(?<msg>.+?)(?:\s+\[[^\]]*\])?\s*$",
            RegexOptions.Compiled);

        public GodotCompilationSupport()
        {
            _projectDir = ProjectSettings.GlobalizePath("res://").TrimEnd('/');
            _csprojPath = FindCsproj(_projectDir);
        }

        public void RequestRecompile()
        {
            if (_csprojPath == null)
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
                }
                _lastSucceeded = false;
                _lastTime = DateTime.Now;
                return;
            }

            if (_isCompiling) return;
            _isCompiling = true;
            lock (_lock) _messages.Clear();

            // Run off the editor main thread; the refresh_scripts flow polls status.
            System.Threading.Tasks.Task.Run(RunBuild);
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

        private void RunBuild()
        {
            var parsed = new List<CompileMessage>();
            bool success = false;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{_csprojPath}\" -c Debug --nologo -v quiet",
                    WorkingDirectory = _projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start dotnet.");

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120_000);

                ParseDiagnostics(stdout, parsed);
                ParseDiagnostics(stderr, parsed);
                success = proc.HasExited && proc.ExitCode == 0
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
                }
                _lastSucceeded = success;
                _lastTime = DateTime.Now;
                _isCompiling = false;
            }
        }

        private static void ParseDiagnostics(string output, List<CompileMessage> into)
        {
            if (string.IsNullOrEmpty(output)) return;
            var seen = new HashSet<string>(into.Select(Key));

            foreach (var raw in output.Split('\n'))
            {
                var m = MsBuildLine.Match(raw.Trim());
                if (!m.Success) continue;

                var msg = new CompileMessage
                {
                    Type = m.Groups["sev"].Value == "error" ? CompileMessageType.Error : CompileMessageType.Warning,
                    Message = m.Groups["msg"].Value.Trim(),
                    File = m.Groups["file"].Value.Trim(),
                    Line = int.Parse(m.Groups["line"].Value),
                    Column = int.Parse(m.Groups["col"].Value)
                };

                if (seen.Add(Key(msg))) into.Add(msg);
            }
        }

        private static string Key(CompileMessage m) => $"{m.File}|{m.Line}|{m.Column}|{m.Message}";

        private static string? FindCsproj(string dir)
        {
            try { return Directory.GetFiles(dir, "*.csproj").FirstOrDefault(); }
            catch { return null; }
        }
    }
}
