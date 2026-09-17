namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One of the client's own built-in selection-cycling commands, the same
/// ones the user can trigger from the keyboard.
/// </summary>
public enum PluginSelectionAction
{
    /// <summary>Re-selects whatever was selected before the current target.</summary>
    PreviousSelection = 0,

    /// <summary>Steps the selection to the previous nearby player.</summary>
    PreviousPlayer,

    /// <summary>Steps the selection to the next nearby player.</summary>
    NextPlayer,
}

/// <summary>
/// Lets a plugin trigger the client's built-in selection-cycling commands
/// instead of naming a target by object id.
/// </summary>
public interface ISelectionAutomation
{
    /// <summary>
    /// Runs one selection-cycling command exactly as the matching key press
    /// would. Returns false when the host does not provide this surface (a
    /// host with no 3D view, for example) or when the command found nothing
    /// to select.
    /// </summary>
    bool Execute(PluginSelectionAction action) => false;
}
