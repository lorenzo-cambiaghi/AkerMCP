#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private readonly SessionViewModel _session;

        private static readonly Regex MsBuildLine = new(
            @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+\w+:\s+(?<msg>.+?)(?:\s+\[[^\]]*\])?\s*$",
            RegexOptions.Compiled);

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
            var args = $"build \"{csproj}\" -c {config} --nologo";
            if (!string.IsNullOrEmpty(request.OutputPath))
                args += $" -o \"{request.OutputPath}\"";

            var sw = Stopwatch.StartNew();
            int exit;
            string output;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(csproj)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start dotnet.");
                output = proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd();
                proc.WaitForExit(600_000);
                exit = proc.HasExited ? proc.ExitCode : -1;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new BuildResult { Success = false, Error = $"Failed to run 'dotnet build': {ex.Message}. Is the .NET SDK on PATH?" };
            }
            sw.Stop();

            var (errors, warnings, errorText) = ParseDiagnostics(output);
            bool ok = exit == 0 && errors == 0;
            string outPath = !string.IsNullOrEmpty(request.OutputPath)
                ? request.OutputPath
                : Path.Combine(Path.GetDirectoryName(csproj)!, "bin", config);

            return new BuildResult
            {
                Success = ok,
                OutputPath = ok ? outPath : null,
                Errors = errors,
                Warnings = warnings,
                DurationSeconds = sw.Elapsed.TotalSeconds,
                Summary = ok ? $"Build succeeded ({config}) → {outPath}" : $"Build failed (exit {exit}): {errors} error(s)",
                Error = ok ? null : errorText
            };
        }

        private ProjectViewModel? FindProject(string platform)
            => ExecutableProjects().FirstOrDefault(p =>
                   string.Equals(p.Platform.ToString(), platform, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(p.Name, platform, StringComparison.OrdinalIgnoreCase));

        private static (int errors, int warnings, string? errorText) ParseDiagnostics(string output)
        {
            int errors = 0, warnings = 0;
            var firstErrors = new List<string>();
            foreach (var raw in output.Split('\n'))
            {
                var m = MsBuildLine.Match(raw.Trim());
                if (!m.Success) continue;
                if (m.Groups["sev"].Value == "error")
                {
                    errors++;
                    if (firstErrors.Count < 5)
                        firstErrors.Add($"{m.Groups["file"].Value}({m.Groups["line"].Value}): {m.Groups["msg"].Value.Trim()}");
                }
                else warnings++;
            }
            return (errors, warnings, firstErrors.Count > 0 ? string.Join("\n", firstErrors) : null);
        }

        private static BuildSettingsResult NotFound(string platform) => new BuildSettingsResult
        {
            Success = false,
            Platform = platform,
            Error = $"Unknown platform '{platform}'. Call list_platforms for valid names."
        };
    }
}
