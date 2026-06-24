using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AkerMcp.StrideLauncher
{
    /// <summary>
    /// Launches Stride Game Studio with the AkerMcp plugin injected for this run
    /// only. Lives in <c>&lt;GameStudio&gt;/AkerMcpPlugins</c>; Game Studio is the
    /// parent folder, the hook DLL is a sibling.
    /// </summary>
    internal static class Program
    {
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
                var hookDll = Path.Combine(pluginDir, "AkerMcp.Stride.StartupHook.dll");
                if (File.Exists(hookDll))
                    psi.Environment["DOTNET_STARTUP_HOOKS"] = hookDll;

                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                Warn("Failed to launch Stride Game Studio:\n" + ex.Message);
                return 1;
            }
        }

        private static void Warn(string message)
            => MessageBox(IntPtr.Zero, message, "AkerMcp — Stride Game Studio", 0x00000030 /* MB_ICONWARNING */);
    }
}
