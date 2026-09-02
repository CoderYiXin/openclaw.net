namespace OpenClaw.Core.Loops;

/// <summary>
/// Dispatches a loop prompt into a session and advances the agent turn.
/// Implemented by the Gateway host to bridge loop scheduling with AgentRuntime.
/// </summary>
public interface IAgentLoopDispatcher
{
    /// <summary>
    /// Injects the loop prompt as a user message into the session and runs one agent turn.
    /// Returns true if the turn was dispatched and completed (success or failure).
    /// Returns false if the session does not exist or is locked.
    /// </summary>
    Task<bool> DispatchAsync(string sessionId, string prompt, CancellationToken ct);
}
