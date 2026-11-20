#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AkerMcp.Shared.Abstraction;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.ViewModel;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Platform/build support for Stride. In Stride a "platform" is an executable
    /// project in the solution (each with a PlatformType); there is no global active
    /// build target (like Godot), and building = `dotnet build` of that project.
    ///
    /// Scope: list_platforms + build_player are functional; switch_build_target is
    /// reported N/A; get_platform_settings returns the project's basic info and
    /// set_platform_settings is not supported (configure in Game Studio / GameSettings).
    /// </summary>
    public class StrideBuildManager : IBuildManager
    {
        // A player build that takes longer than this is hung: its tree is killed.
        private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

        private readonly SessionViewModel _session;
        private int _building;

        public StrideBuildManager(SessionViewModel session) => _session = session;

        private IEnumerable<ProjectViewModel> ExecutableProjects()
            => _session.LocalPackages.OfType<ProjectViewModel>()
                       .Where(p => p.Type == ProjectType.Executable);

        public IReadOnlyList<PlatformInfo> GetPlatforms()
            => ExecutableProjects()
               .Select(p => new PlatformInfo { Name = p.Platform.ToString(), IsActive = false, IsSupported = true })
               .ToList();

        public BuildSettingsResult GetPlatformSettings(string platform)
        {
            var proj = FindProject(platform);
            if (proj == null) return NotFound(platform);

            return new BuildSettingsResult
            {
                Success = true,
                Platform = platform,
                Settings = new Dictionary<string, string>
                {
                    ["project"] = proj.Name,
                    ["csproj"] = proj.ProjectPath != null ? proj.ProjectPath.ToOSPath() : "",
                    ["platform"] = proj.Platform.ToString(),
                    ["type"] = proj.Type.ToString()
                }
            };
        }

        public BuildSettingsResult SetPlatformSettings(string platform, IDictionary<string, string> values)
            => new BuildSettingsResult
            {
                Success = false,
                Platform = platform,
                Error = "Editing platform settings is not supported by the Stride adapter — configure them in Game Studio (GameSettings asset)."
            };

        public PlatformSwitchResult SwitchPlatform(string platform)
            => new PlatformSwitchResult
            {
                Success = false,
                ActivePlatform = "",
                Error = "Stride has no global active build target to switch — pass the platform directly to build_player."
            };

        public BuildResult Build(BuildRequest request)
        {
            var proj = FindProject(request.Platform);
            if (proj == null)
                return new BuildResult { Success = false, Error = $"Unknown platform '{request.Platform}'. Call list_platforms." };

            var csproj = proj.ProjectPath?.ToOSPath();
            if (string.IsNullOrEmpty(csproj))
                return new BuildResult { Success = false, Error = "Project path unavailable for this platform." };

            var config = request.Development ? "Debug" : "Release";
            var args = new List<string> { "build", csproj!, "-c", config, "--nologo" };
            if (!string.IsNullOrEmpty(request.OutputPath))
                args.AddRange(new[] { "-o", request.OutputPath });

            // The wait below pumps the editor's dispatcher, so a second build_player can
            // arrive while this one runs: two builds of one project fight over obj/.
            if (Interlocked.CompareExchange(ref _building, 1, 0) != 0)
                return new BuildResult { Success = false, Error = "A build_player build is already running; wait for it to finish." };

            var sw = Stopwatch.StartNew();
            ProcessRunResult run;
            try
            {
                var task = StrideProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(csproj)!, BuildTimeout);
                // This runs on the WPF thread (the IPC handler dispatches it there), and a
                // build takes minutes: wait without freezing Game Studio. The runner owns the
                // timeout; the margin only covers the kill. If the dispatcher could not be
                // pumped, the blocking wait below is still safe: the runner never needs
                // this thread.
                StrideUiWait.Wait(task, BuildTimeout + TimeSpan.FromSeconds(30));
                run = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return new BuildResult { Success = false, Error = $"Failed to run 'dotnet build': {ex.Message}. Is the .NET SDK on PATH?" };
            }
            finally
            {
                sw.Stop();
                Volatile.Write(ref _building, 0);
            }

            var diagnostics = MsBuildDiagnostics.Parse(run.StdOut, run.StdErr);
            var errorList = diagnostics.Where(d => d.IsError).ToList();
            int warnings = diagnostics.Count(d => !d.IsError);
            bool ok = !run.TimedOut && run.ExitCode == 0 && errorList.Count == 0;
            string outPath = !string.IsNullOrEmpty(request.OutputPath)
                ? request.OutputPath
                : Path.Combine(Path.GetDirectoryName(csproj)!, "bin", config);

            string? errorText = null;
            if (!ok)
            {
                var lines = errorList.Take(5).Select(d => d.Line > 0 ? $"{d.Origin}({d.Line}): {d.Text}" : $"{d.Origin}: {d.Text}").ToList();
                if (run.TimedOut)
                    lines.Insert(0, $"'dotnet build' did not finish within {BuildTimeout.TotalMinutes:0} minutes; its process tree was killed.");
                else if (lines.Count == 0)
                    lines.AddRange(MsBuildDiagnostics.FallbackLines(run));   // failed without a recognisable diagnostic
                errorText = lines.Count > 0 ? string.Join("\n", lines) : $"'dotnet build' exited with code {run.ExitCode}.";
            }

            return new BuildResult
            {
                Success = ok,
                OutputPath = ok ? outPath : null,
                Errors = errorList.Count,
                Warnings = warnings,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                Summary = ok ? $"Build succeeded ({config}) → {outPath}"
                    : run.TimedOut ? "Build timed out and was killed"
                    : $"Build failed (exit {run.ExitCode}): {errorList.Count} error(s)",
                Error = errorText
            };
        }

        private ProjectViewModel? FindProject(string platform)
            => ExecutableProjects().FirstOrDefault(p =>
                   string.Equals(p.Platform.ToString(), platform, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(p.Name, platform, StringComparison.OrdinalIgnoreCase));

        private static BuildSettingsResult NotFound(string platform) => new BuildSettingsResult
        {
            Success = false,
            Platform = platform,
            Error = $"Unknown platform '{platform}'. Call list_platforms for valid names."
        };
    }
}
