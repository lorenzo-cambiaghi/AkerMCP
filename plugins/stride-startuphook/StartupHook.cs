using System;
using System.IO;
using System.Reflection;

// IMPORTANT: The .NET runtime startup-hook contract requires a type named
// "StartupHook" in the GLOBAL namespace (no namespace) exposing a parameterless
// "static void Initialize()" method. Do not rename or move into a namespace.
//
// This runs before Game Studio's Main, in EVERY .NET process that inherits the
// DOTNET_STARTUP_HOOKS variable — but our launcher only sets it for the Game
// Studio process it spawns. Everything is wrapped in try/catch and the only work
// done eagerly is wiring two event handlers, so a non-Stride process (or a broken
// install) is a no-op and can never prevent the host from starting.
internal static class StartupHook
{
    private static string _pluginDir = string.Empty;
    private static bool _registered;

    public static void Initialize()
    {
        try
        {
            _pluginDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location) ?? string.Empty;

            // Our bundled dependencies (AkerMcp.*, MessagePack, ...) live next to
            // this hook, not next to Game Studio; resolve them from here. Stride
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

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (IsEditorAssembly(args.LoadedAssembly))
            TryRegister();
    }

    private static bool IsEditorAssembly(Assembly asm)
        => string.Equals(asm.GetName().Name, "Stride.Core.Assets.Editor", StringComparison.OrdinalIgnoreCase);

    private static void TryRegister()
    {
        if (_registered) return;
        _registered = true;
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

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "akermcp-stride.log"),
                $"{DateTime.Now:HH:mm:ss} [startup-hook] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics are best-effort.
        }
    }
}
