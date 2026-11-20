#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Abstraction;
using Stride.Core.Assets.Editor.ViewModel;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Drives refresh_scripts / get_compile_errors for Stride by shelling out to
    /// `dotnet build` on the game's code project and parsing MSBuild diagnostics
    /// (same approach as the Godot adapter). Game Studio hot-reloads the rebuilt
    /// assembly on its own; there is no domain reload that drops the IPC connection.
    /// </summary>
    public class StrideCompilationSupport : ICompilationSupport
    {
        // A game project build that takes longer than this is hung (a locked file, a
        // prompt nobody sees): its tree is killed and the build reported as failed.
        private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);

        private readonly object _lock = new();
        private readonly List<CompileMessage> _messages = new();
        private int _isCompiling;
        private bool _lastSucceeded = true;
        private DateTime _lastTime = DateTime.MinValue;

        private readonly string? _csprojPath;
        private readonly string _projectDir;

        public StrideCompilationSupport(SessionViewModel session)
        {
            try
            {
                var proj = session.CurrentProject?.ProjectPath;
                if (proj != null)
                {
                    _csprojPath = proj.ToOSPath();
                    _projectDir = Path.GetDirectoryName(_csprojPath) ?? Directory.GetCurrentDirectory();
                }
                else
                {
                    _projectDir = session.SolutionPath != null
                        ? Path.GetDirectoryName(session.SolutionPath.ToOSPath()) ?? Directory.GetCurrentDirectory()
                        : Directory.GetCurrentDirectory();
                    _csprojPath = FindCsproj(_projectDir);
                }
            }
            catch
            {
                _projectDir = Directory.GetCurrentDirectory();
                _csprojPath = null;
            }
        }

        public void RequestRecompile()
        {
            if (_csprojPath == null)
            {
                Finish(new List<CompileMessage> { Error("No game .csproj found for the current project; cannot build.") }, false);
                return;
            }

            // Two refresh_scripts racing must not start two builds of the same project.
            if (Interlocked.CompareExchange(ref _isCompiling, 1, 0) != 0) return;
            lock (_lock) _messages.Clear();
            Task.Run(RunBuild);
        }

        public CompilationStatus GetCompilationStatus()
        {
            lock (_lock)
            {
                return new CompilationStatus
                {
                    IsCompiling = Volatile.Read(ref _isCompiling) != 0,
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

        private async Task RunBuild()
        {
            var parsed = new List<CompileMessage>();
            bool success = false;
            try
            {
                var run = await StrideProcessRunner.RunAsync(
                    "dotnet", new[] { "build", _csprojPath!, "-c", "Debug", "--nologo", "-v", "quiet" },
                    _projectDir, BuildTimeout).ConfigureAwait(false);

                foreach (var d in MsBuildDiagnostics.Parse(run.StdOut, run.StdErr))
                {
                    parsed.Add(new CompileMessage
                    {
                        Type = d.IsError ? CompileMessageType.Error : CompileMessageType.Warning,
                        Message = d.Text,
                        File = d.Origin,
                        Line = d.Line,
                        Column = d.Column
                    });
                }

                if (run.TimedOut)
                    parsed.Add(Error($"'dotnet build' did not finish within {BuildTimeout.TotalMinutes:0} minutes; its process tree was killed."));
                else if (run.ExitCode != 0 && !parsed.Any(m => m.Type == CompileMessageType.Error))
                {
                    // A failure MSBuild did not phrase as a diagnostic: show what it said
                    // rather than "failed with 0 errors".
                    var lines = MsBuildDiagnostics.FallbackLines(run);
                    parsed.Add(Error($"'dotnet build' failed (exit {run.ExitCode})" +
                                     (lines.Count > 0 ? ":\n" + string.Join("\n", lines) : ".")));
                }

                success = !run.TimedOut && run.ExitCode == 0
                          && !parsed.Any(m => m.Type == CompileMessageType.Error);
            }
            catch (Exception ex)
            {
                parsed.Add(Error($"Failed to run 'dotnet build': {ex.Message}. Is the .NET SDK on PATH?"));
            }
            finally
            {
                Finish(parsed, success);
                Volatile.Write(ref _isCompiling, 0);
            }
        }

        private void Finish(List<CompileMessage> messages, bool success)
        {
            lock (_lock)
            {
                _messages.Clear();
                _messages.AddRange(messages);
                _lastSucceeded = success;
                _lastTime = DateTime.Now;
            }
        }

        private static CompileMessage Error(string message)
            => new CompileMessage { Type = CompileMessageType.Error, Message = message, File = "", Line = 0, Column = 0 };

        private static string? FindCsproj(string dir)
        {
            try { return Directory.GetFiles(dir, "*.csproj").FirstOrDefault(); }
            catch { return null; }
        }
    }
}
