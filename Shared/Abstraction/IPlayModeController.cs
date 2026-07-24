namespace AkerMcp.Shared.Abstraction
{
    /// <summary>
    /// Optional. Engine adapters that implement this let the AI run the project and
    /// observe it live — start/stop play, pause/step, read play state — in an
    /// engine-neutral way. For a game engine "play" means entering Play Mode / running
    /// the scene; for an animation editor it means playing the timeline. Engines that
    /// don't implement it cause the related tools (enter_play, exit_play, set_play_pause,
    /// play_step, get_play_state) to report NOT_SUPPORTED rather than failing.
    /// </summary>
    public interface IPlayModeController
    {
        /// <summary>Current play state (does not change anything).</summary>
        PlayState GetState();

        /// <summary>
        /// Start play / animation playback. On some engines (Unity) this triggers a
        /// domain reload that drops the IPC connection mid-call — the server handles
        /// that like refresh_scripts (disconnect = success, wait for reconnect).
        /// </summary>
        PlayState EnterPlay();

        /// <summary>Stop play and return to edit / reset the playhead. May also reload.</summary>
        PlayState ExitPlay();

        /// <summary>Pause (true) or resume (false) an in-progress play/playback.</summary>
        PlayState SetPaused(bool paused);

        /// <summary>Advance <paramref name="frames"/> frames; only meaningful while paused.</summary>
        PlayState Step(int frames);
    }

    public class PlayState
    {
        /// <summary>False when the operation isn't applicable on this engine (reported, not fatal).</summary>
        public bool Supported { get; set; } = true;
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        /// <summary>Seconds since play started, or the animation playhead position.</summary>
        public double Time { get; set; }
        /// <summary>Total length in seconds if known (e.g. an animation clip); 0 otherwise.</summary>
        public double Duration { get; set; }
        /// <summary>
        /// Frames rendered since play started (0 if unknown). Diff it across two get_play_state
        /// reads to prove the game loop is LIVE — a frozen/soft-locked game keeps a stale value
        /// while IsPlaying stays true.
        /// </summary>
        public long FrameCount { get; set; }
        /// <summary>Current frames-per-second (0 if unknown).</summary>
        public double Fps { get; set; }
        /// <summary>
        /// For engines that run the game in a SEPARATE window (Godot), its window-title
        /// substring — so capture_window / send_input can target it without guessing. The
        /// server also uses it to auto-route capture_sequence/send_input after enter_play.
        /// </summary>
        public string? WindowTitle { get; set; }
        /// <summary>OS process id of a separately-launched game, if known (0 otherwise).</summary>
        public int ProcessId { get; set; }
        /// <summary>
        /// True when entering/exiting play triggers a domain reload that drops the IPC
        /// connection (Unity). Informational for the AI; the server tolerates the drop.
        /// </summary>
        public bool WillReload { get; set; }
        /// <summary>Human-readable note, e.g. "runs in a separate window; capture via capture_window".</summary>
        public string? Note { get; set; }
        public string? Error { get; set; }
    }
}
