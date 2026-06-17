#if TOOLS
#nullable enable
using Godot;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Editor-only entry point. Godot instantiates this when the plugin is
    /// enabled. It owns the AkerMcp lifecycle and, crucially, pumps the
    /// main-thread dispatcher queue every editor frame from <see cref="_Process"/>
    /// (Godot's scene tree may only be touched from the main thread).
    /// </summary>
    [Tool]
    public partial class AkerMcpEditorPlugin : EditorPlugin
    {
        public override void _EnterTree()
        {
            SetProcess(true);
            GodotMcpPlugin.Instance.Start();
            GD.Print("[AkerMcp] Editor plugin enabled.");
        }

        public override void _Process(double delta)
        {
            // Drain queued main-thread actions (scene ops, screenshot, code exec).
            GodotMcpPlugin.Instance.Tick();
        }

        public override void _ExitTree()
        {
            // Called on disable and before every C# assembly reload (rebuild).
            GodotMcpPlugin.Instance.Stop();
            GD.Print("[AkerMcp] Editor plugin disabled.");
        }
    }
}
#endif
