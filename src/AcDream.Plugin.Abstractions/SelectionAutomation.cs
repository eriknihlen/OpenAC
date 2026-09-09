namespace AcDream.Plugin.Abstractions;

public enum PluginSelectionAction
{
    PreviousSelection = 0,
    PreviousPlayer,
    NextPlayer,
}

public interface ISelectionAutomation
{
    bool Execute(PluginSelectionAction action) => false;
}
