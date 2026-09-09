using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcDream.Core.Spells;

namespace AcDream.App.UI.Layout;

public sealed class EffectsUiController : IRetainedPanelController
{
    public const uint LayoutId = 0x2100001Bu;
    public const uint PositiveRootId = 0x1000011Fu;
    public const uint NegativeRootId = 0x10000121u;
    public const uint CloseId = 0x100000FCu;
    public const uint ListId = 0x10000123u;
    public const uint ListScrollbarId = 0x10000124u;
    public const uint InfoTextId = 0x10000126u;
    public const uint InfoScrollbarId = 0x10000127u;
    public const uint RowTemplateId = 0x10000128u;
    public const uint RowIconId = 0x10000129u;
    public const uint RowLabelId = 0x1000012Au;
    public const uint RowDurationId = 0x1000012Bu;

    private readonly Spellbook _spellbook;
    private readonly bool _positive;
    private readonly Func<double> _serverTime;
    private readonly Func<uint, uint> _resolveSpellIcon;
    private readonly EffectRowTemplateFactory _templates;
    private readonly string _selectPrompt;
    private readonly UiItemList _list;
    private readonly UiText? _info;
    private readonly UiTextLayoutCache<string>? _infoLayout;
    private readonly UiButton? _close;
    private readonly UiScrollbar? _listScrollbar;
    private readonly UiScrollbar? _infoScrollbar;
    private readonly Dictionary<uint, EffectRowTemplateFactory.EffectRow> _rows = new();
    private uint? _selectedSpellId;
    private double _lastDurationUpdate = double.NaN;
    private bool _disposed;

    internal uint? SelectedSpellId => _selectedSpellId;

    private EffectsUiController(
        ImportedLayout layout,
        Spellbook spellbook,
        bool positive,
        Func<double> serverTime,
        Func<uint, uint> resolveSpellIcon,
        EffectRowTemplateFactory templates,
        string selectPrompt,
        UiItemList list,
        Action? close)
    {
        _spellbook = spellbook;
        _positive = positive;
        _serverTime = serverTime;
        _resolveSpellIcon = resolveSpellIcon;
        _templates = templates;
        _selectPrompt = selectPrompt;
        _list = list;
        _info = layout.FindElement(InfoTextId) as UiText;
        _close = layout.FindElement(CloseId) as UiButton;
        _listScrollbar = layout.FindElement(ListScrollbarId) as UiScrollbar;
        _infoScrollbar = layout.FindElement(InfoScrollbarId) as UiScrollbar;
        if (_close is not null) _close.OnClick = close;
        _list.Columns = 1;
        _list.CellWidth = templates.Width;
        _list.CellHeight = templates.Height;
        if (_listScrollbar is not null)
            _listScrollbar.Model = _list.Scroll;
        _infoLayout = ConfigureInfo();
        _spellbook.EnchantmentsChanged += Rebuild;
        Rebuild();
    }

    public static EffectsUiController? Bind(
        ImportedLayout layout,
        Spellbook spellbook,
        bool positive,
        Func<double> serverTime,
        Func<uint, (uint Texture, int Width, int Height)> spriteResolve,
        Func<uint, uint> resolveSpellIcon,
        EffectRowTemplateFactory templates,
        string selectPrompt,
        Action? close = null)
    {
        UiElement? host = layout.FindElement(ListId);
        if (host is null) return null;
        UiItemList list;
        if (host is UiItemList itemList)
            list = itemList;
        else
        {
            list = new UiItemList(spriteResolve)
            {
                Width = host.Width,
                Height = host.Height,
                Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right | AnchorEdges.Bottom,
            };
            host.AddChild(list);
            list.CaptureCurrentAnchorBaseline();
        }
        return new EffectsUiController(
            layout,
            spellbook,
            positive,
            serverTime,
            resolveSpellIcon,
            templates,
            selectPrompt,
            list,
            close);
    }

    public void Tick()
    {
        double now = _serverTime();
        if (!double.IsFinite(now)) return;
        if (double.IsFinite(_lastDurationUpdate)
            && now >= _lastDurationUpdate
            && now - _lastDurationUpdate < 1.0)
            return;
        _lastDurationUpdate = now;
        foreach (ActiveEnchantmentRecord enchantment in VisibleEnchantments())
            if (_rows.TryGetValue(
                    enchantment.Identity,
                    out EffectRowTemplateFactory.EffectRow? row))
                row.Remaining = FormatRemaining(enchantment, now);
    }

    private void Rebuild()
    {
        _lastDurationUpdate = double.NaN;
        _rows.Clear();
        ActiveEnchantmentRecord[] enchantments = VisibleEnchantments().ToArray();
        using (_list.DeferLayout())
        {
            _list.Flush();
            foreach (ActiveEnchantmentRecord enchantment in enchantments)
            {
                _spellbook.TryGetMetadata(enchantment.SpellId, out SpellMetadata? metadata);
                uint identity = enchantment.Identity;
                EffectRowTemplateFactory.EffectRow row = _templates.Create(
                    enchantment.SpellId,
                    metadata is null ? 0u : _resolveSpellIcon(enchantment.SpellId),
                    metadata?.Name ?? $"Spell {enchantment.SpellId}",
                    FormatRemaining(enchantment, _serverTime()));
                row.Slot.Clicked = () => Select(enchantment.SpellId);
                _rows[identity] = row;
                _list.AddItem(row.Slot);
            }
        }
        if (_selectedSpellId is uint selected
            && !_rows.Values.Any(row => row.Slot.EntryId == selected))
            _selectedSpellId = null;
        SyncSelection();
        UpdateInfoText();
    }

    private IEnumerable<ActiveEnchantmentRecord> VisibleEnchantments()
        => _spellbook.EnchantmentsInEffectSnapshot
            .Where(record =>
            {
                if (!_spellbook.TryGetMetadata(record.SpellId, out SpellMetadata metadata))
                    return false;
                return metadata.IsBeneficial == _positive;
            })
            .OrderBy(record => _spellbook.TryGetMetadata(record.SpellId, out SpellMetadata metadata)
                ? metadata.Name : record.SpellId.ToString(CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);

    private static string FormatRemaining(ActiveEnchantmentRecord enchantment, double now)
    {
        if (enchantment.Duration < 0) return string.Empty;
        double remaining = Math.Max(0, enchantment.StartTime + enchantment.Duration - now);
        if (!double.IsFinite(remaining)) return "--:--";
        remaining = Math.Min(remaining, TimeSpan.MaxValue.TotalSeconds);
        TimeSpan time = TimeSpan.FromSeconds(remaining);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void Select(uint spellId)
    {
        _selectedSpellId = _selectedSpellId == spellId ? null : spellId;
        SyncSelection();
        UpdateInfoText();
    }

    private void SyncSelection()
    {
        foreach (EffectRowTemplateFactory.EffectRow row in _rows.Values)
            row.Slot.SetSelected(row.Slot.EntryId == _selectedSpellId);
    }

    private UiTextLayoutCache<string>? ConfigureInfo()
    {
        if (_info is null) return null;
        _info.PreserveEndOnLayout = false;
        _info.WheelScrollEnabled = true;
        _info.ClickThrough = false;
        if (_infoScrollbar is not null)
            _infoScrollbar.Model = _info.Scroll;
        var cache = new UiTextLayoutCache<string>(
            _info,
            static (target, value) => IndicatorDetailText.Shape(target, value),
            _selectPrompt,
            StringComparer.Ordinal);
        _info.LinesProvider = cache.Provider;
        return cache;
    }

    private void UpdateInfoText()
    {
        if (_infoLayout is null)
            return;

        string value = _selectPrompt;
        if (_selectedSpellId is uint spellId
            && _spellbook.ActiveEnchantmentSnapshot.Any(
                record => record.SpellId == spellId)
            && _spellbook.TryGetMetadata(spellId, out SpellMetadata metadata))
        {
            value = metadata.Name + "\n\n" + metadata.Description;
        }
        _infoLayout.SetValue(value);
    }

    public void OnShown() => Rebuild();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _spellbook.EnchantmentsChanged -= Rebuild;
        if (_close is not null) _close.OnClick = null;
        if (_listScrollbar is not null) _listScrollbar.Model = null;
        if (_infoScrollbar is not null) _infoScrollbar.Model = null;
    }
}
