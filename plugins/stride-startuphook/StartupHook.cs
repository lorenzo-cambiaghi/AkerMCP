using System;
using System.IO;
using System.Reflection;
using System.Threading;

// IMPORTANT: The .NET runtime startup-hook contract requires a type named
// "StartupHook" in the GLOBAL namespace (no namespace) exposing a parameterless
// "static void Initialize()" method. Do not rename or move into a namespace.
//
// This runs before Main in EVERY .NET process that inherits DOTNET_STARTUP_HOOKS.
// The launcher sets it only for the Game Studio it spawns, but Game Studio passes
// its environment on to everything it starts: dotnet build, MSBuild nodes,
// VBCSCompiler, the asset compiler, the game itself. So the first thing done here is
// to leave any process that is not Game Studio. Everything is wrapped in try/catch,
// so a broken install can never prevent the host from starting.
internal static class StartupHook
{
    // The entry assembly of Game Studio itself; every other host is left alone.
    private const string EditorEntryAssembly = "Stride.GameStudio";

    // Set by AkerMcp.Stride.Launcher. The hook runs from a shadow copy under %TEMP%
    // (so the installed DLL is never locked), which means its own location is not
    // where the adapter and its dependencies live.
    private const string PluginDirVariable = "AKERMCP_STRIDE_PLUGIN_DIR";

    private static string _pluginDir = string.Empty;
    private static int _registered;

    public static void Initialize()
    {
        try
        {
            // The variable is deliberately NOT cleared here: Game Studio restarts itself
            // through Process.Start (File > Open / Reload project), and that new Game
            // Studio must get the hook too. Leaving early is what keeps the children clean.
            var entry = Assembly.GetEntryAssembly()?.GetName().Name;
            if (!string.Equals(entry, EditorEntryAssembly, StringComparison.OrdinalIgnoreCase))
                return;

            _pluginDir = ResolvePluginDir();

            // Our bundled dependencies (AkerMcp.*, MessagePack, ...) live next to
            // the adapter, not next to Game Studio; resolve them from there. Stride
            // and Roslyn are already loaded by Game Studio, so those never reach us.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginDir;

            // Register once the editor assembly is available. Subscribe first to
            // avoid a race, then handle the case where it is already loaded.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IsEditorAssembly(asm))
                {
                    TryRegister();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log("Initialize failed: " + ex);
        }
    }

    private static string ResolvePluginDir()
    {
        var fromLauncher = Environment.GetEnvironmentVariable(PluginDirVariable);
        if (!string.IsNullOrEmpty(fromLauncher) && File.Exists(Path.Combine(fromLauncher, "AkerMcp.Stride.dll")))
            return fromLauncher!;

        // Loaded straight from the install folder (an older launcher, or a hand-set variable).
        return Path.GetDirectoryName(typeof(StartupHook).Assembly.Location) ?? string.Empty;
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (IsEditorAssembly(args.LoadedAssembly))
            TryRegister();
    }

    private static bool IsEditorAssembly(Assembly asm)
        => string.Equals(asm.GetName().Name, "Stride.Core.Assets.Editor", StringComparison.OrdinalIgnoreCase);

    private static void TryRegister()
    {
        // AssemblyLoad fires on whichever thread loads the assembly, so two loads can
        // race here: only the first caller registers.
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;

        // Every later assembly load would otherwise pay for this handler for the whole
        // session, for nothing.
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        try
        {
            var adapterPath = Path.Combine(_pluginDir, "AkerMcp.Stride.dll");
            var adapter = Assembly.LoadFrom(adapterPath);
            var bootstrap = adapter.GetType("AkerMcp.StrideAdapter.StrideBootstrap", throwOnError: true);
            var register = bootstrap!.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
            register!.Invoke(null, null);
            Log("AkerMcp.Stride registered via startup hook.");
        }
        catch (Exception ex)
        {
            Log("Registration failed: " + ex);
        }
    }

    private static Assembly? ResolveFromPluginDir(object? sender, ResolveEventArgs args)
    {
        try
        {
            var simpleName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(simpleName)) return null;
            var candidate = Path.Combine(_pluginDir, simpleName + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }
        catch
        {
            return null;
        }
    }

    // Kept separate from the adapter's StrideDiag on purpose: this assembly cannot
    // reference the adapter (it is what loads it). Same file, same line shape.
    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "akermcp-stride.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [startup-hook] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics are best-effort.
        }
    }
}
