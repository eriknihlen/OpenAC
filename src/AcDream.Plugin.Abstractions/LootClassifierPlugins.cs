namespace AcDream.Plugin.Abstractions;

/// <summary>VTank's public loot-plugin action vocabulary.</summary>
public enum PluginLootAction
{
    NoLoot = 0,
    Keep = 1,
    Salvage = 2,
    Sell = 3,
    Read = 4,
    User1 = 5,
    User2 = 6,
    User3 = 7,
    User4 = 8,
    User5 = 9,
    KeepUpTo = 10,
}

public readonly record struct PluginLootClassificationContext(
    PluginInventoryItem Item,
    PluginItemProperties Properties,
    IReadOnlyList<PluginInventoryItem> OwnedItems);

public readonly record struct PluginLootClassification(
    bool Matched,
    PluginLootAction Action,
    string RuleName = "",
    int Priority = 0,
    int KeepCount = 0);

public readonly record struct PluginLootedItem(
    PluginInventoryItem Item,
    PluginLootAction Action);

public interface IPluginLootClassifier
{
    PluginLootClassification Classify(
        in PluginLootClassificationContext context);

    void OnLooted(in PluginLootedItem item) { }

    void OnItemRemoved(uint objectId) { }
}

public readonly record struct PluginLootClassifierInfo(
    string Id,
    string DisplayName);

public interface IPluginLootClassifierRegistry
{
    IReadOnlyList<PluginLootClassifierInfo> Available =>
        Array.Empty<PluginLootClassifierInfo>();

    IDisposable Register(
        string classifierId,
        string displayName,
        IPluginLootClassifier classifier) =>
        throw new NotSupportedException("Loot classifiers are unavailable.");

    bool TryClassify(
        string classifierId,
        in PluginLootClassificationContext context,
        out PluginLootClassification classification)
    {
        classification = default;
        return false;
    }

    bool TryNotifyLooted(
        string classifierId,
        in PluginLootedItem item) => false;

    bool TryNotifyItemRemoved(
        string classifierId,
        uint objectId) => false;
}

public sealed class NoOpPluginLootClassifierRegistry
    : IPluginLootClassifierRegistry
{
    public static NoOpPluginLootClassifierRegistry Instance { get; } = new();
    private NoOpPluginLootClassifierRegistry() { }
}
