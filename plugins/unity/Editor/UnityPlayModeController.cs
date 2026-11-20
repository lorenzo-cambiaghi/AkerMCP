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
        // How long a requested frame may take to run before the remaining steps are dropped (a breakpoint, a modal
        // dialog, an editor that stopped ticking): the queue must not fire into a later, unrelated pause.
        private const double StepTimeoutSeconds = 5.0;

        private int _stepsQueued;           // steps not requested from Unity yet
        private int _stepIssuedAtFrame = -1; // Time.frameCount when the outstanding step was requested; -1 = none
        private double _stepIssuedAt;
        private bool _pumping;

        public PlayState GetState() => Snapshot();

        public PlayState EnterPlay()
        {
            if (!EditorApplication.isPlaying)
            {
                // Unity refuses both cases with a notification in the editor and nothing in the API: the server then
                // polled for 4 s and reported isPlaying=false with no reason. Thrown, they come back as the error.
                if (EditorApplication.isCompiling)
                    throw new InvalidOperationException(
                        "Cannot enter Play Mode while scripts are compiling. Wait for get_compile_status to report " +
                        "the compile finished, then retry.");
                if (EditorUtility.scriptCompilationFailed)
                    throw new InvalidOperationException(
                        "Cannot enter Play Mode: the scripts have compile errors (Unity requires them fixed first). " +
                        "Call get_compile_errors.");

                EditorApplication.EnterPlaymode();
            }

            // EnterPlaymode is DEFERRED — it applies after this call returns (and after the
            // domain reload). Report the real, not-yet-settled state (do NOT fake IsPlaying=true);
            // the server's settle-poll on get_play_state confirms the transition honestly.
            var s = Snapshot();
            s.Note = "Entering Play Mode (deferred). A domain reload may briefly drop the connection; it auto-reconnects.";
            return s;
        }

        public PlayState ExitPlay()
        {
            CancelSteps();
            if (EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();

            // ExitPlaymode is deferred too; report the real state and let the server settle-poll.
            var s = Snapshot();
            s.Note = "Exiting Play Mode (deferred).";
            return s;
        }

        public PlayState SetPaused(bool paused)
        {
            if (!paused) CancelSteps();
            EditorApplication.isPaused = paused;
            return Snapshot();
        }

        /// <summary>
        /// Advances <paramref name="frames"/> frames, one per editor tick.
        /// <para>
        /// <c>EditorApplication.Step()</c> only asks for one frame on the next tick: called N times in a row it still
        /// advanced ONE frame, and play_step(10) reported success after a single frame. The steps are queued instead
        /// and requested one at a time, each once the previous frame has run. The interface is synchronous, so the
        /// reply comes back before the last of them: it says which frameCount the steps end on.
        /// </para>
        /// </summary>
        public PlayState Step(int frames)
        {
            if (!EditorApplication.isPlaying)
                return new PlayState { IsPlaying = false, Error = "Not playing — call enter_play first." };

            if (!EditorApplication.isPaused)
                EditorApplication.isPaused = true;

            int requested = Math.Max(1, frames);
            _stepsQueued += requested;
            // Counted before pumping: whether Unity runs a requested frame inside Step() or on the next tick, the
            // frames still to come are the outstanding one (if it has not run) plus the queue.
            int now = UnityEngine.Time.frameCount;
            long target = now + (_stepIssuedAtFrame == now ? 1 : 0) + _stepsQueued;
            PumpSteps();

            var s = Snapshot();
            s.Note = $"Stepping {requested} frame(s), one per editor tick: frameCount reaches {target} shortly after " +
                     "this reply. Confirm with get_play_state.";
            return s;
        }

        /// <summary>Drops queued steps and the editor hook: called when the plugin stops.</summary>
        public void Detach() => CancelSteps();

        private void PumpSteps()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
            {
                CancelSteps(); // play ended or resumed: the queued frames would land in a later, unrelated pause
                return;
            }

            if (_stepIssuedAtFrame >= 0)
            {
                if (UnityEngine.Time.frameCount == _stepIssuedAtFrame)
                {
                    if (EditorApplication.timeSinceStartup - _stepIssuedAt > StepTimeoutSeconds) CancelSteps();
                    else Hook();
                    return; // the requested frame has not run yet
                }
                _stepIssuedAtFrame = -1;
            }

            if (_stepsQueued <= 0)
            {
                Unhook();
                return;
            }

            _stepsQueued--;
            _stepIssuedAtFrame = UnityEngine.Time.frameCount;
            _stepIssuedAt = EditorApplication.timeSinceStartup;
            EditorApplication.Step();

            if (_stepsQueued > 0) Hook();
            else Unhook();
        }

        private void CancelSteps()
        {
            _stepsQueued = 0;
            _stepIssuedAtFrame = -1;
            Unhook();
        }

        private void Hook()
        {
            if (_pumping) return;
            _pumping = true;
            EditorApplication.update += PumpSteps;
        }

        private void Unhook()
        {
            if (!_pumping) return;
            _pumping = false;
            EditorApplication.update -= PumpSteps;
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
