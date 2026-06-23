#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using AkerMcp.Shared.Abstraction;
using BuildResultStatus = UnityEditor.Build.Reporting.BuildResult;

namespace AkerMcp.Unity
{
    /// <summary>
    /// Maps the engine-neutral IBuildManager onto Unity's EditorUserBuildSettings /
    /// PlayerSettings / BuildPipeline. Platform names are neutral strings ("Android",
    /// "iOS", "Windows", …); the platform-specific settings keys are surfaced through
    /// the key-value map so nothing Unity-specific leaks into the shared abstraction.
    /// </summary>
    public class UnityBuildManager : IBuildManager
    {
        private readonly struct Target
        {
            public readonly string Name;
            public readonly BuildTargetGroup Group;
            public readonly BuildTarget BuildTarget;
            public Target(string name, BuildTargetGroup group, BuildTarget target)
            { Name = name; Group = group; BuildTarget = target; }
        }

        private static readonly Target[] Targets =
        {
            new Target("Android", BuildTargetGroup.Android,    BuildTarget.Android),
            new Target("iOS",     BuildTargetGroup.iOS,        BuildTarget.iOS),
            new Target("Windows", BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64),
            new Target("macOS",   BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX),
            new Target("Linux",   BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64),
            new Target("WebGL",   BuildTargetGroup.WebGL,      BuildTarget.WebGL),
        };

        public IReadOnlyList<PlatformInfo> GetPlatforms()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;
            return Targets.Select(t => new PlatformInfo
            {
                Name = t.Name,
                IsActive = t.BuildTarget == active,
                IsSupported = BuildPipeline.IsBuildTargetSupported(t.Group, t.BuildTarget)
            }).ToList();
        }

        public BuildSettingsResult GetPlatformSettings(string platform)
        {
            if (!TryResolve(platform, out var t))
                return NotFound(platform);

            var named = NamedBuildTarget.FromBuildTargetGroup(t.Group);
            var s = new Dictionary<string, string>
            {
                ["productName"] = PlayerSettings.productName,
                ["companyName"] = PlayerSettings.companyName,
                ["bundleVersion"] = PlayerSettings.bundleVersion,
                ["applicationIdentifier"] = PlayerSettings.GetApplicationIdentifier(named),
                ["scriptingBackend"] = PlayerSettings.GetScriptingBackend(named).ToString(),
            };

            if (t.Group == BuildTargetGroup.Android)
            {
                s["minSdkVersion"] = ((int)PlayerSettings.Android.minSdkVersion).ToString();
                s["targetSdkVersion"] = ((int)PlayerSettings.Android.targetSdkVersion).ToString();
                s["bundleVersionCode"] = PlayerSettings.Android.bundleVersionCode.ToString();
                s["targetArchitectures"] = PlayerSettings.Android.targetArchitectures.ToString();
            }
            else if (t.Group == BuildTargetGroup.iOS)
            {
                s["buildNumber"] = PlayerSettings.iOS.buildNumber;
                s["targetOSVersion"] = PlayerSettings.iOS.targetOSVersionString;
            }

            return new BuildSettingsResult { Success = true, Platform = t.Name, Settings = s };
        }

        public BuildSettingsResult SetPlatformSettings(string platform, IDictionary<string, string> values)
        {
            if (!TryResolve(platform, out var t))
                return NotFound(platform);

            var named = NamedBuildTarget.FromBuildTargetGroup(t.Group);
            var unknown = new List<string>();

            try
            {
                foreach (var kv in values)
                {
                    switch (kv.Key)
                    {
                        case "productName": PlayerSettings.productName = kv.Value; break;
                        case "companyName": PlayerSettings.companyName = kv.Value; break;
                        case "bundleVersion": PlayerSettings.bundleVersion = kv.Value; break;
                        case "applicationIdentifier": PlayerSettings.SetApplicationIdentifier(named, kv.Value); break;
                        case "scriptingBackend":
                            PlayerSettings.SetScriptingBackend(named,
                                (ScriptingImplementation)Enum.Parse(typeof(ScriptingImplementation), kv.Value, true));
                            break;

                        case "minSdkVersion" when t.Group == BuildTargetGroup.Android:
                            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)int.Parse(kv.Value); break;
                        case "targetSdkVersion" when t.Group == BuildTargetGroup.Android:
                            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)int.Parse(kv.Value); break;
                        case "bundleVersionCode" when t.Group == BuildTargetGroup.Android:
                            PlayerSettings.Android.bundleVersionCode = int.Parse(kv.Value); break;
                        case "targetArchitectures" when t.Group == BuildTargetGroup.Android:
                            PlayerSettings.Android.targetArchitectures =
                                (AndroidArchitecture)Enum.Parse(typeof(AndroidArchitecture), kv.Value, true);
                            break;

                        case "buildNumber" when t.Group == BuildTargetGroup.iOS:
                            PlayerSettings.iOS.buildNumber = kv.Value; break;
                        case "targetOSVersion" when t.Group == BuildTargetGroup.iOS:
                            PlayerSettings.iOS.targetOSVersionString = kv.Value; break;

                        default: unknown.Add(kv.Key); break;
                    }
                }
            }
            catch (Exception ex)
            {
                return new BuildSettingsResult
                {
                    Success = false,
                    Platform = t.Name,
                    Error = $"Failed to apply settings: {ex.Message}"
                };
            }

            AssetDatabase.SaveAssets();

            // Re-read so the caller sees the resulting state, and report unknown keys.
            var result = GetPlatformSettings(t.Name);
            result.UnknownKeys = unknown.Count > 0 ? unknown : null;
            return result;
        }

        public PlatformSwitchResult SwitchPlatform(string platform)
        {
            if (!TryResolve(platform, out var t))
                return new PlatformSwitchResult
                {
                    Success = false,
                    Error = $"Unknown platform '{platform}'.",
                    ActivePlatform = EditorUserBuildSettings.activeBuildTarget.ToString()
                };

            bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(t.Group, t.BuildTarget);
            return new PlatformSwitchResult
            {
                Success = ok,
                Error = ok ? null : $"Could not switch to {t.Name} (the platform module may not be installed).",
                ActivePlatform = EditorUserBuildSettings.activeBuildTarget.ToString()
            };
        }

        public BuildResult Build(BuildRequest request)
        {
            if (!TryResolve(request.Platform, out var t))
                return new BuildResult { Success = false, Error = $"Unknown platform '{request.Platform}'." };

            var scenes = request.Scenes != null && request.Scenes.Length > 0
                ? request.Scenes
                : EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

            if (scenes.Length == 0)
                return new BuildResult { Success = false, Error = "No scenes to build (enable scenes in Build Settings or pass 'scenes')." };

            var opts = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = request.OutputPath,
                target = t.BuildTarget,
                targetGroup = t.Group,
                options = request.Development ? BuildOptions.Development : BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(opts);
            var sum = report.summary;
            bool ok = sum.result == BuildResultStatus.Succeeded;

            return new BuildResult
            {
                Success = ok,
                OutputPath = sum.outputPath,
                Errors = sum.totalErrors,
                Warnings = sum.totalWarnings,
                SizeBytes = (long)sum.totalSize,
                DurationSeconds = sum.totalTime.TotalSeconds,
                Summary = $"{sum.result}: {sum.totalErrors} errors, {sum.totalWarnings} warnings",
                Error = ok ? null : $"Build {sum.result} with {sum.totalErrors} error(s)."
            };
        }

        private static bool TryResolve(string platform, out Target target)
        {
            foreach (var t in Targets)
            {
                if (string.Equals(t.Name, platform, StringComparison.OrdinalIgnoreCase))
                {
                    target = t;
                    return true;
                }
            }
            target = default;
            return false;
        }

        private static BuildSettingsResult NotFound(string platform) => new BuildSettingsResult
        {
            Success = false,
            Platform = platform,
            Error = $"Unknown platform '{platform}'. Call list_platforms for valid names."
        };
    }
}
