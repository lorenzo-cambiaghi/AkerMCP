#nullable enable
using Godot;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.GodotAdapter
{
    /// <summary>
    /// Godot play control via EditorInterface. Running a scene launches the game in a
    /// SEPARATE OS process/window, so pause/step from the editor aren't available, and
    /// the live game must be screenshotted with capture_window (the editor viewport
    /// won't show it). Called on the main thread (dispatched by the IPC handler).
    /// </summary>
    public sealed class GodotPlayModeController : IPlayModeController
    {
        public PlayState GetState()
        {
            bool playing = EditorInterface.Singleton.IsPlayingScene();
            return new PlayState
            {
                IsPlaying = playing,
                Note = playing
                    ? "The game runs in a separate window — screenshot it with capture_window (by its title)."
                    : null
            };
        }

        public PlayState EnterPlay()
        {
            EditorInterface.Singleton.PlayCurrentScene();
            // Report the REAL state: PlayCurrentScene runs the currently edited scene, and starts
            // nothing if no runnable scene is open — IsPlayingScene() reflects that synchronously.
            bool playing = EditorInterface.Singleton.IsPlayingScene();
            return new PlayState
            {
                IsPlaying = playing,
                Error = playing ? null : "Nothing started — is a runnable scene currently open/edited?",
                Note = playing
                    ? "Running the current scene in a SEPARATE window. Use capture_window (by the game window title) " +
                      "to see it, and send_input with window_title to drive it — take_screenshot shows the editor, not the game."
                    : null
            };
        }

        public PlayState ExitPlay()
        {
            EditorInterface.Singleton.StopPlayingScene();
            return new PlayState { IsPlaying = false };
        }

        public PlayState SetPaused(bool paused) => new PlayState
        {
            Supported = false,
            IsPlaying = EditorInterface.Singleton.IsPlayingScene(),
            Error = "Godot runs the game in a separate process; pause/resume from the editor isn't available."
        };

        public PlayState Step(int frames) => new PlayState
        {
            Supported = false,
            IsPlaying = EditorInterface.Singleton.IsPlayingScene(),
            Error = "Frame stepping isn't available for Godot's separate game process."
        };
    }
}
