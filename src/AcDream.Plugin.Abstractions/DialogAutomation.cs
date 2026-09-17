namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One outstanding confirmation dialog, as reported by
/// <see cref="IEvents.ConfirmationRequested"/>.
/// </summary>
public readonly record struct PluginConfirmation(
    uint ContextId,
    int Type,
    string Text);

/// <summary>
/// Answers the client's own yes/no confirmation dialog. Known
/// <see cref="PluginConfirmation.Type"/> values: 5 is the crafting-percent
/// confirmation ("this has a chance to fail, continue?"); other values are
/// server-defined and only distinguishable by their text.
/// </summary>
public interface IDialogAutomation
{
    /// <summary>
    /// Answers the dialog identified by <paramref name="contextId"/> exactly
    /// as the client's own Yes/No buttons would. Returns false when there is
    /// no outstanding dialog with that context id.
    /// </summary>
    bool Answer(uint contextId, bool accept) => false;
}
