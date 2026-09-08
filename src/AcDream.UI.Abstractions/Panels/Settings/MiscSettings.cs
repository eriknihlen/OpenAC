namespace AcDream.UI.Abstractions.Panels.Settings;

public sealed record MiscSettings(
    bool TooltipEnable,
    float TooltipDelaySeconds)
{
    public static MiscSettings Default { get; } = new(
        TooltipEnable: true,
        TooltipDelaySeconds: 0.25f);
}
