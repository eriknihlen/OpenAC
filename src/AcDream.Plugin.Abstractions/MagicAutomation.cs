namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginCastCompletion(
    long Revision,
    uint SpellId,
    uint TargetObjectId,
    uint WeenieError)
{
    public bool IsSuccess => Revision != 0 && WeenieError == 0u;
}
