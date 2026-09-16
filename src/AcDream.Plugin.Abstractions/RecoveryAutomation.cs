namespace AcDream.Plugin.Abstractions;

/// <summary>Result of one explicit operator recovery operation.</summary>
public readonly record struct PluginRecoveryResult(
    bool Accepted,
    int PreviousCount = 0,
    int CurrentCount = 0,
    string Message = "");

public interface IRecoveryAutomation
{
    PluginRecoveryResult ClearOneBusyReference() => new(
        Accepted: false,
        Message: "Action recovery is unavailable on this host.");

    /// <summary>
    /// Gives up an inventory request the server never answered - a use,
    /// a wield, a move - so another can begin. The count fields carry
    /// one while a request was pending, zero when there was none.
    /// </summary>
    PluginRecoveryResult AbandonPendingInventoryRequest() => new(
        Accepted: false,
        Message: "Action recovery is unavailable on this host.");
}
