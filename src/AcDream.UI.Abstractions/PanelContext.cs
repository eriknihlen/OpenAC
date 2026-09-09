namespace AcDream.UI.Abstractions;

public readonly record struct PanelContext(
    float       DeltaSeconds,
    ICommandBus Commands);
