#nullable enable
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.StrideAdapter
{
    /// <summary>
    /// Stride play control. Game Studio has no in-editor Play Mode that a plugin can
    /// drive: the scene editor's embedded preview game only renders the scene, and
    /// "running the game" launches a separate built executable (via the toolbar), which
    /// isn't controllable from here. So this reports play control as not-applicable with
    /// clear guidance rather than pretending — build_player then run the produced exe, or
    /// use the Unity/Godot/SkelForge adapters for the interactive runtime loop.
    /// </summary>
    public sealed class StridePlayModeController : IPlayModeController
    {
        private const string NotApplicable =
            "Stride Game Studio has no plugin-controllable Play Mode (the scene editor's preview only renders " +
            "the scene; running the game launches a separate built executable). Use build_player and run the " +
            "produced exe to play, or send_input/capture_window against that game window.";

        public PlayState GetState() => new PlayState { Supported = false, IsPlaying = false, Note = NotApplicable };
        public PlayState EnterPlay() => new PlayState { Supported = false, Error = NotApplicable };
        public PlayState ExitPlay() => new PlayState { Supported = false, Error = NotApplicable };
        public PlayState SetPaused(bool paused) => new PlayState { Supported = false, Error = NotApplicable };
        public PlayState Step(int frames) => new PlayState { Supported = false, Error = NotApplicable };
    }
}
