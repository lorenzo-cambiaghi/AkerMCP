namespace AkerMcp.Shared.Abstraction
{
    /// <summary>
    /// Optional. An engine that lets the user grant and revoke an agent's access implements
    /// this, and the server stops working around the answer.
    ///
    /// It exists because refusal and inability used to look identical on the wire. An adapter
    /// that declined a screenshot returned null; so does an adapter that has no screen capture
    /// at all; and the server, seeing null, fell back to an OS-level window grab — which is
    /// exactly right for the second case and exactly wrong for the first. The user revoked the
    /// session and got a photograph of their screen anyway, taken by the half of the system
    /// their revocation never reached.
    ///
    /// Nothing here enforces anything by itself. It tells the server that a policy exists and
    /// what it currently says, so the server can decline in the same voice instead of routing
    /// around a refusal it could not recognise. An engine that does not implement it is not
    /// policed — the answer is NOT_SUPPORTED, the server asks once per connection and never
    /// again, and no call pays for a rule that is not there.
    /// </summary>
    public interface IAgentConsent
    {
        /// <summary>
        /// Whether the agent may act right now. Called rarely — once per connection when the
        /// engine has no policy, and otherwise at most every few seconds before the server
        /// reaches around the engine — so it should be a cheap read of current state, never a
        /// prompt or a blocking wait.
        /// </summary>
        ConsentState GetConsent();
    }

    /// <summary>What the engine's consent policy currently says.</summary>
    public class ConsentState
    {
        /// <summary>False when the user has revoked or not yet granted the session.</summary>
        public bool Granted { get; set; } = true;

        /// <summary>
        /// Why, in words a caller can act on and a user can recognise ("the session was
        /// revoked at 14:12"). Shown verbatim when a tool declines, so it is worth writing.
        /// </summary>
        public string? Reason { get; set; }
    }
}
