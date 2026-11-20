using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AkerMcp.StrideLauncher
{
    /// <summary>
    /// Launches Stride Game Studio with the AkerMcp plugin injected for this run
    /// only. Lives in <c>&lt;GameStudio&gt;/AkerMcpPlugins</c>; Game Studio is the
    /// parent folder, the hook DLL is a sibling.
    /// </summary>
    internal static class Program
    {
        private const string HookFileName = "AkerMcp.Stride.StartupHook.dll";
        private const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";

        // Read by the hook: it runs from a shadow copy, so it cannot find the adapter
        // from its own location.
        private const string PluginDirVariable = "AKERMCP_STRIDE_PLUGIN_DIR";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var pluginDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var gameStudioDir = Directory.GetParent(pluginDir)?.FullName;
                if (gameStudioDir == null)
                {
                    Warn("Could not locate the Game Studio folder. Re-run install-stride-wrapper.ps1.");
                    return 2;
                }

                var gameStudioExe = Path.Combine(gameStudioDir, "Stride.GameStudio.exe");
                if (!File.Exists(gameStudioExe))
                {
                    Warn($"Stride.GameStudio.exe not found next to the plugin folder:\n{gameStudioExe}\n\n" +
                         "Re-run install-stride-wrapper.ps1 against your Game Studio install.");
                    return 2;
                }

                var psi = new ProcessStartInfo(gameStudioExe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = gameStudioDir,
                };

                // Pass through any arguments (e.g. a project/solution path).
                foreach (var arg in args)
                    psi.ArgumentList.Add(arg);

                // Inject the plugin ONLY into this child process. The variable never
                // persists to the user/system environment, so a missing or moved DLL
                // can never break other .NET apps — at worst Game Studio starts
                // without AkerMcp.
                var installedHook = Path.Combine(pluginDir, HookFileName);
                var hookFound = File.Exists(installedHook);
                if (hookFound)
                {
                    var hookToLoad = ShadowCopy(installedHook) ?? installedHook;
                    psi.Environment.TryGetValue(StartupHooksVariable, out var existing);
                    psi.Environment[StartupHooksVariable] = AppendHook(existing, hookToLoad);
                    psi.Environment[PluginDirVariable] = pluginDir;
                }

                Process.Start(psi);

                // After the start, so a broken install still opens Game Studio at once.
                if (!hookFound)
                    Warn($"{HookFileName} is missing from\n{pluginDir}\n\nGame Studio was started WITHOUT AkerMcp. " +
                         "Re-run install-stride-wrapper.ps1 to restore it.");
                return 0;
            }
            catch (Exception ex)
            {
                Warn("Failed to launch Stride Game Studio:\n" + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Adds <paramref name="hook"/> to an existing DOTNET_STARTUP_HOOKS value instead
        /// of replacing it: a hook the user set for their own reasons keeps running. An
        /// entry that is already ours (inherited from a shell that saw a previous launch)
        /// is dropped, so the hook never runs twice.
        /// </summary>
        private static string AppendHook(string? existing, string hook)
        {
            var entries = (existing ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(e => !string.Equals(Path.GetFileName(e), HookFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            entries.Add(hook);
            return string.Join(Path.PathSeparator, entries);
        }

        /// <summary>
        /// Copies the hook to <c>%TEMP%\AkerMcp\stride-hook\&lt;content hash&gt;\</c> and
        /// returns that path, or null if the copy could not be made.
        ///
        /// Why a copy: every .NET process Game Studio starts inherits the variable and
        /// maps the hook DLL before its Main runs, and MSBuild/VBCSCompiler nodes outlive
        /// Game Studio by minutes. Pointed at the installed file, they kept it locked and
        /// re-running the installer failed. Why one folder per content hash rather than
        /// one per launch: launches of the same build share the copy, so %TEMP% does not
        /// fill up, and a rebuilt hook gets a fresh folder instead of having to overwrite
        /// a copy that a lingering node may still hold.
        /// </summary>
        private static string? ShadowCopy(string installedHook)
        {
            try
            {
                var bytes = File.ReadAllBytes(installedHook);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 16);
                var root = Path.Combine(Path.GetTempPath(), "AkerMcp", "stride-hook");
                var target = Path.Combine(root, hash, HookFileName);

                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // Write aside and move into place, so a concurrent launch never loads
                    // a half-written DLL.
                    var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temp, bytes);
                    try { File.Move(temp, target); }
                    catch (IOException) when (File.Exists(target)) { TryDelete(temp); } // another launch won the race
                }

                PruneOtherCopies(root, hash);
                return target;
            }
            catch
            {
                return null;
            }
        }

        // Older builds' copies. One still mapped by a running process fails to delete
        // and is retried at the next launch.
        private static void PruneOtherCopies(string root, string keep)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(dir, recursive: true); } catch { /* in use */ }
                }
            }
            catch
            {
                // Housekeeping only.
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private static void Warn(string message)
            => MessageBox(IntPtr.Zero, message, "AkerMcp — Stride Game Studio", 0x00000030 /* MB_ICONWARNING */);
    }
}
