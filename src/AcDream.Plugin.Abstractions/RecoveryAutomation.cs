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
}
