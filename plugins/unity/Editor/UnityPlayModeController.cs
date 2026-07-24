#nullable enable

using System;
using UnityEditor;
using AkerMcp.Shared.Abstraction;

namespace AkerMcp.Unity
{
    /// <summary>
    /// Unity play control via EditorApplication. Entering/exiting Play Mode reloads the
    /// script domain (by default), which briefly drops the IPC connection — the server
    /// handles that like refresh_scripts (waits for the plugin to auto-restart). The
    /// running game shows in the Game View, so take_screenshot("game") / capture_sequence
    /// capture it directly. Called on the main thread (dispatched by the IPC handler).
    /// </summary>
    public sealed class UnityPlayModeController : IPlayModeController
    {
        public PlayState GetState() => Snapshot();

        public PlayState EnterPlay()
        {
            if (!EditorApplication.isPlaying)
                EditorApplication.EnterPlaymode();

            // EnterPlaymode is DEFERRED — it applies after this call returns (and after the
            // domain reload). Report the real, not-yet-settled state (do NOT fake IsPlaying=true);
            // the server's settle-poll on get_play_state confirms the transition honestly.
            var s = Snapshot();
            s.Note = "Entering Play Mode (deferred). A domain reload may briefly drop the connection; it auto-reconnects.";
            return s;
        }

        public PlayState ExitPlay()
        {
            if (EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();

            // ExitPlaymode is deferred too; report the real state and let the server settle-poll.
            var s = Snapshot();
            s.Note = "Exiting Play Mode (deferred).";
            return s;
        }

        public PlayState SetPaused(bool paused)
        {
            EditorApplication.isPaused = paused;
            return Snapshot();
        }

        public PlayState Step(int frames)
        {
            if (!EditorApplication.isPlaying)
                return new PlayState { IsPlaying = false, Error = "Not playing — call enter_play first." };

            if (!EditorApplication.isPaused)
                EditorApplication.isPaused = true;

            for (int i = 0; i < Math.Max(1, frames); i++)
                EditorApplication.Step();

            return Snapshot();
        }

        private static PlayState Snapshot()
        {
            bool playing = EditorApplication.isPlaying;
            float dt = UnityEngine.Time.smoothDeltaTime;
            return new PlayState
            {
                IsPlaying = playing,
                IsPaused = EditorApplication.isPaused,
                // Seconds since the current scene started playing (0 in edit mode).
                Time = playing ? UnityEngine.Time.timeSinceLevelLoad : 0,
                // Diff FrameCount across reads to prove the loop is live (not soft-locked).
                FrameCount = playing ? UnityEngine.Time.frameCount : 0,
                Fps = playing && dt > 0.0001f ? 1f / dt : 0,
                // Entering/exiting Play Mode reloads the domain by default (drops the connection).
                WillReload = true,
            };
        }
    }
}
