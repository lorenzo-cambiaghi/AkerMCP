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
                WindowTitle = playing ? GameWindowTitle() : null,
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
                // The game window title (= project name) so the server can auto-route
                // capture_sequence/send_input to it — no manual window_title needed.
                WindowTitle = playing ? GameWindowTitle() : null,
                Error = playing ? null : "Nothing started — is a runnable scene currently open/edited?",
                Note = playing
                    ? "Running the current scene in a SEPARATE window. capture_sequence/send_input auto-route to it now."
                    : null
            };
        }

        // The running game's default window title is the project name.
        private static string? GameWindowTitle()
        {
            try
            {
                var name = ProjectSettings.GetSetting("application/config/name").AsString();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
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
