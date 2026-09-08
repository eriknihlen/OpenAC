using AcDream.Core.Items;
using AcDream.Core.Spells;

namespace AcDream.App.UI.Layout;

public sealed class ItemCooldownUiController
{
    private readonly Spellbook _spellbook;
    private readonly ClientObjectTable _objects;
    private readonly Func<double> _currentTime;
    private readonly List<UiItemList> _lists = new();
    private readonly Dictionary<uint, int> _stepByItemId = new();

    private ItemCooldownUiController(
        Spellbook spellbook,
        ClientObjectTable objects,
        Func<double> currentTime)
    {
        _spellbook = spellbook;
        _objects = objects;
        _currentTime = currentTime;
    }

    public static ItemCooldownUiController Bind(
        UiElement root,
        Spellbook spellbook,
        ClientObjectTable objects,
        Func<double> currentTime,
        ItemCooldownAssets assets)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(spellbook);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(currentTime);

        var controller = new ItemCooldownUiController(
            spellbook,
            objects,
            currentTime);
        controller.Configure(root, assets.Sprites);
        controller.Tick();
        return controller;
    }

    public void Tick()
    {
        _stepByItemId.Clear();
        if (!_spellbook.HasCooldownEnchantments)
            return;

        double now = _currentTime();

        foreach (UiItemList list in _lists)
        {
            if (!IsEffectivelyVisible(list))
                continue;

            for (int index = 0; index < list.GetNumUIItems(); index++)
            {
                UiItemSlot? cell = list.GetItem(index);
                if (cell is null)
                    continue;
                uint itemId = cell.ItemId;
                if (itemId == 0u || _stepByItemId.ContainsKey(itemId))
                    continue;

                int step = 0;
                ClientObject? item = _objects.Get(itemId);
                if (item?.CooldownId is { } cooldownId
                    && item.CooldownDuration is { } duration
                    && _spellbook.OnCooldown(
                        cooldownId,
                        now,
                        out double remaining))
                    step = ItemCooldownDisplay.GetOverlayStep(
                        duration,
                        remaining);

                _stepByItemId.Add(itemId, step);
            }
        }
    }

    internal static bool IsEffectivelyVisible(UiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        for (UiElement? current = element; current is not null; current = current.Parent)
        {
            if (!current.Visible)
                return false;
        }
        return true;
    }

    internal int GetOverlayStep(uint itemId)
        => _stepByItemId.TryGetValue(itemId, out int step) ? step : 0;

    private void Configure(UiElement element, IReadOnlyList<uint> sprites)
    {
        if (element is UiItemList list)
        {
            _lists.Add(list);
            list.CooldownSprites = sprites;
            list.CooldownStepProvider = GetOverlayStep;
        }

        foreach (UiElement child in element.Children)
            Configure(child, sprites);
    }
}
