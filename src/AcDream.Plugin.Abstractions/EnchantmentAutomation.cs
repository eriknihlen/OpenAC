namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginTrackedEnchantment(
    uint TargetObjectId,
    uint SpellId,
    uint Family,
    int Quality,
    bool IsUntargeted,
    double SecondsRemaining);

public interface IEnchantmentAutomation
{
    IReadOnlyList<PluginTrackedEnchantment> Capture(uint targetObjectId) =>
        Array.Empty<PluginTrackedEnchantment>();

    bool ReportCast(
        uint targetObjectId,
        uint spellId,
        double durationSeconds) => false;
}
