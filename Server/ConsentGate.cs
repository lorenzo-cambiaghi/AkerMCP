using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AkerMcp.Shared.Ipc;
using AkerMcp.Shared.Protocol;

namespace AkerMcp.Server
{
    /// <summary>
    /// Asks the connected engine whether the agent may act, and remembers the answer.
    ///
    /// It guards the calls that reach around the engine to the operating system — the
    /// screenshot fallback, and the window tools that click and type. Those are the ones a
    /// per-surface gate inside an adapter cannot see: they never enter the adapter at all,
    /// so an editor that had revoked the session still got photographed, and now still gets
    /// clicked. This is where that stops, on the server side of the pipe, because that is
    /// the side those calls are made from.
    ///
    /// It is built to cost nothing when there is nothing to enforce, which is the normal
    /// case. An engine without <c>IAgentConsent</c> answers NOT_SUPPORTED once and is never
    /// asked again for the life of the connection: Unity, Godot and Stride pay a single IPC
    /// round trip per session and no latency thereafter. An engine that does have a policy is
    /// asked at most once every few seconds, and only when the server is about to act outside
    /// it — never on the ordinary inspect/modify/verify path.
    ///
    /// When the engine cannot answer, the last answer stands. That is deliberate rather than
    /// lenient: the moment the OS-level tools matter most is when a modal dialog has frozen
    /// the editor's main thread, and refusing to unblock the editor because the editor is
    /// blocked would be a trap with no way out. A revocation already observed survives that
    /// silence; an engine that never answered was never claiming a policy to begin with.
    ///
    /// One limit, worth stating because it is a design choice and not an oversight: the policy
    /// is the CONNECTED engine's, and it governs every OS-level call regardless of which window
    /// that call names. So a second editor's window is refused while this one has revoked — the
    /// safe direction — but it is not protected by ITS own policy, which this server never asked
    /// for and cannot see. A server talks to one engine at a time; a window belonging to someone
    /// else is, from here, just a window. Pin the engine whose rules should apply
    /// (engine_status, or AKER_MCP_ENGINE).
    /// </summary>
    public sealed class ConsentGate
    {
        private readonly EngineConnection _engine;
        private readonly object _lock = new object();

        private bool _policyKnownAbsent;      // the engine answered NOT_SUPPORTED: stop asking
        private bool? _granted;               // the last answer, if any
        private string? _reason;
        private DateTime _askedAtUtc = DateTime.MinValue;

        /// <summary>
        /// How long an answer is trusted before asking again. Short enough that a revocation
        /// takes effect while the user is still watching for it, long enough that a burst of
        /// ui_action steps does not turn into a burst of IPC calls.
        /// </summary>
        private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long to wait for the engine to answer. Short on purpose: this question is asked
        /// on the way to an action the user is waiting for, and the fallback — the previous
        /// answer — is always available.
        /// </summary>
        private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(2);

        public ConsentGate(EngineConnection engine) => _engine = engine;

        /// <summary>
        /// Forget everything on (re)connection. A new session is a new policy: the engine that
        /// answered before may not be the engine answering now, and an absent policy must not
        /// be inherited by a successor that has one.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _policyKnownAbsent = false;
                _granted = null;
                _reason = null;
                _askedAtUtc = DateTime.MinValue;
            }
        }

        /// <summary>
        /// The refusal to hand back, or null when the action may proceed. Written as the
        /// engine's own sentence, so a user reading it recognises the policy that produced it.
        /// </summary>
        public async Task<string?> Refuse(string whatFor, CancellationToken ct)
        {
            var state = await Evaluate(ct);
            if (state.granted) return null;

            var because = string.IsNullOrWhiteSpace(state.reason)
                ? "the engine reports the agent's access is not currently granted"
                : state.reason;
            return $"{whatFor} refused: {because}. This is the engine's consent policy, and it " +
                   "covers the OS-level tools too — the server will not take a picture of a window, " +
                   "or click in one, that the editor itself would decline to show or change.";
        }

        /// <summary>
        /// Does this error mean "there is no policy here", as opposed to "not right now"?
        ///
        /// Three sentences say it, and only one of them was being read. An engine that
        /// implements the contract but not the interface answers NOT_SUPPORTED and names
        /// IAgentConsent. An engine older than the method — every plugin still shipping the
        /// 1.6.0 contract — has never heard of it and answers "Unknown method: get_consent",
        /// which is the same information in a different voice. Missing that one cost a round
        /// trip on every OS-level call for the life of the session, quietly, because the
        /// fallback is permissive: it worked, it was just never cached.
        ///
        /// Anything else — a timeout, a disconnect, an adapter that threw — is deliberately
        /// NOT final. A transient failure must not switch the policy off for the session.
        /// </summary>
        public static bool IsNoPolicyAnswer(string errorText)
            => errorText.Contains(IpcConstants.ErrorCodes.NotSupported, StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("IAgentConsent", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("Unknown method", StringComparison.OrdinalIgnoreCase);

        private async Task<(bool granted, string? reason)> Evaluate(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_policyKnownAbsent) return (true, null);
                if (_granted.HasValue && DateTime.UtcNow - _askedAtUtc < Freshness)
                    return (_granted.Value, _reason);
            }

            // Nobody to ask. The window tools are documented to work with no engine connected
            // at all, and no policy can be in force when there is no engine to hold one — but
            // a revocation already heard still stands, so this reads the last answer rather
            // than inventing a fresh yes. Skipping the call is not an optimisation: asking
            // would queue behind the 20-second reconnect grace period, and three drags in one
            // ui_action measured 22 seconds before this check existed.
            if (!_engine.IsConnected)
            {
                lock (_lock) { return (_granted ?? true, _reason); }
            }

            // A consent read is a cheap state read by contract, so it gets a short leash of
            // its own rather than the 30-second default: an engine too busy to answer must
            // slow nothing down, it just does not update the answer.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AskTimeout);

            ToolResult result;
            try
            {
                result = await _engine.ForwardToolCall(IpcConstants.Methods.GetConsent, null, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lock (_lock) { return (_granted ?? true, _reason); }
            }

            if (result.IsError)
            {
                var text = result.Content.Count > 0 ? result.Content[0].Text ?? "" : "";
                if (IsNoPolicyAnswer(text))
                {
                    lock (_lock) { _policyKnownAbsent = true; }
                    return (true, null);
                }

                // Anything else — a timeout, a disconnect, an engine that is busy — is not an
                // answer. The last one stands; if there has never been one, nothing is claiming
                // to govern this session.
                lock (_lock) { return (_granted ?? true, _reason); }
            }

            var payload = result.Content.Count > 0 ? result.Content[0].Text : null;
            bool granted = true;
            string? reason = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(payload!);
                    if (json.ValueKind == JsonValueKind.Object)
                    {
                        if (json.TryGetProperty("granted", out var g)
                            && (g.ValueKind == JsonValueKind.True || g.ValueKind == JsonValueKind.False))
                            granted = g.GetBoolean();
                        if (json.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                            reason = r.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // An engine that answers something unreadable is not refusing; treat it as the
                // absent policy it effectively is, but keep asking in case it recovers.
                return (_granted ?? true, _reason);
            }

            lock (_lock)
            {
                _granted = granted;
                _reason = reason;
                _askedAtUtc = DateTime.UtcNow;
                return (granted, reason);
            }
        }
    }
}
