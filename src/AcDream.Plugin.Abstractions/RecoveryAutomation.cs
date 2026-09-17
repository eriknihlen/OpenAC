namespace AcDream.Plugin.Abstractions;

/// <summary>Result of one explicit operator recovery operation.</summary>
/// <param name="Accepted">
/// True when the host carried the operation out; false when it could not, for
/// example with no session bound.
/// </param>
/// <param name="PreviousCount">
/// How many outstanding action references there were before the operation.
/// </param>
/// <param name="CurrentCount">
/// How many outstanding action references remain after the operation.
/// </param>
/// <param name="Message">A short explanation of what happened.</param>
public readonly record struct PluginRecoveryResult(
    bool Accepted,
    int PreviousCount = 0,
    int CurrentCount = 0,
    string Message = "");

/// <summary>
/// Manual escape hatches for a session that has got stuck, meant to be driven
/// by a person watching the client rather than automatically.
/// </summary>
public interface IRecoveryAutomation
{
    /// <summary>
    /// Release one of the references that mark the character as busy with an
    /// action. The client counts the actions it is still waiting on and refuses
    /// new ones while that count is above zero, so a reply that never arrives
    /// can leave the character stuck; this clears exactly one such reference.
    /// </summary>
    /// <returns>
    /// The counts before and after, or a result whose <c>Accepted</c> is false
    /// when no session is bound or the host offers no recovery, which is what
    /// the default implementation always returns.
    /// </returns>
    PluginRecoveryResult ClearOneBusyReference() => new(
        Accepted: false,
        Message: "Action recovery is unavailable on this host.");
}
