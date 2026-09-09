using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Expressions;

internal sealed class SalvageStagingManager(IPluginHost host)
{
    private readonly HashSet<uint> _staged = [];

    public int Count => _staged.Count;

    public bool Add(uint objectId)
    {
        if (!host.Automation.Items.CaptureOwnedItems()
            .Any(item => item.ObjectId == objectId && !item.IsEquipped))
        {
            return false;
        }
        _staged.Add(objectId);
        return true;
    }

    public bool Open()
    {
        PluginInventoryItem? ust = host.Automation.Items.CaptureOwnedItems()
            .Where(static item => item.Name.Equals("Ust", StringComparison.Ordinal))
            .OrderBy(static item => item.ObjectId)
            .Cast<PluginInventoryItem?>()
            .FirstOrDefault();
        return ust is { } found
            && host.Automation.Items.Use(found.ObjectId).Accepted;
    }

    public bool Salvage()
    {
        IReadOnlyList<PluginInventoryItem> inventory =
            host.Automation.Items.CaptureOwnedItems();
        PluginInventoryItem? ust = inventory
            .Where(static item => item.Name.Equals("Ust", StringComparison.Ordinal))
            .OrderBy(static item => item.ObjectId)
            .Cast<PluginInventoryItem?>()
            .FirstOrDefault();
        if (ust is not { } tool)
            return false;
        uint[] items = inventory
            .Where(item => item.ObjectId != tool.ObjectId && _staged.Contains(item.ObjectId))
            .Select(static item => item.ObjectId)
            .ToArray();
        if (items.Length == 0
            || !host.Automation.Items.Salvage(tool.ObjectId, items).Accepted)
        {
            return false;
        }
        _staged.Clear();
        return true;
    }

    public void Clear() => _staged.Clear();
}
