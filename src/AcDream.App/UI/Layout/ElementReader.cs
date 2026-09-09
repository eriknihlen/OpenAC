using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;

namespace AcDream.App.UI.Layout;

public enum HJustify : byte { Left = 0, Center = 1, Right = 2 }

public enum VJustify : byte { Top = 0, Center = 1, Bottom = 2 }

public readonly record struct UiTabTableEntry(uint ButtonElementId, uint PageElementId, bool IsDefault);

public readonly record struct UiTemplateListEntry(uint TemplateLayoutId, uint TemplateElementId);

public sealed class ElementInfo
{
    public uint Id;

    public uint Type;

    public float X, Y, Width, Height;

    public float OriginalParentWidth, OriginalParentHeight;
    public bool HasOriginalParentSize;

    public uint Left, Top, Right, Bottom;

    /// <summary>Draw order within the parent (lower = drawn first / behind).</summary>
    public uint ReadOrder;

    public uint ZLevel;

    public Dictionary<uint, UiStateInfo> States = new();

    public uint DefaultStateId;

    /// <summary>
    /// Font dat object id inherited from the base element's <c>Properties[0x1A]</c>
    /// (<c>ArrayBaseProperty → DataIdBaseProperty</c>). 0 = none / not inherited.
    /// </summary>
    public uint FontDid;

    public HJustify HJustify = HJustify.Left;

    public VJustify VJustify = VJustify.Top;

    public Vector4? FontColor;

    public Vector4? TagFontColor;

    public bool Outline;

    public Vector4? OutlineColor;

    /// <summary>
    /// Sprite per state: state name → (RenderSurface file id, DrawMode int).
    /// The <c>""</c> key represents the unnamed DirectState (<c>ElementDesc.StateDesc</c>).
    /// Named states use the <c>UIStateId.ToString()</c> value as the key
    /// (e.g. <c>"HideDetail"</c>, <c>"ShowDetail"</c>).
    /// </summary>
    public Dictionary<string, (uint File, int DrawMode)> StateMedia = new();

    public Dictionary<string, UiCursorMedia> StateCursors = new();

    public string DefaultStateName = "";

    public List<ElementInfo> Children = new();

    public List<UiTabTableEntry> TabTable = new();

    public List<UiTemplateListEntry> TemplateList = new();

    public uint LedCheckedSprite;

    public uint LedUncheckedSprite;

    public uint ScrollbarElementId;

    public bool Invisible;

    public int MarginLeft, MarginRight, MarginTop, MarginBottom;

    public bool TooltipEnabled;

    public UiStringInfoValue? TooltipText;

    public uint TooltipRootElementId;

    public uint TooltipLayoutDid;

    public uint TooltipTextChildElementId;

    public float? TooltipDelaySeconds;

    public int? MaxWidth;
    public int? MinWidth;

    public int? MaxHeight;
    public int? MinHeight;

    public bool TryGetEffectiveProperty(uint propertyId, out UiPropertyValue value, uint? stateId = null)
    {
        uint effectiveState = stateId ?? EffectiveDefaultStateId();

        UiPropertyValue? resolved = null;
        if (States.TryGetValue(UiStateInfo.DirectStateId, out var direct)
            && direct.Properties.TryGetValue(propertyId, out var directValue))
        {
            resolved = directValue;
        }

        if (effectiveState != UiStateInfo.DirectStateId
            && States.TryGetValue(effectiveState, out var state)
            && state.Properties.TryGetValue(propertyId, out var stateValue))
        {
            resolved = stateValue;
        }

        value = resolved!;
        return resolved is not null;
    }

    public bool TryGetEffectiveBool(uint propertyId, out bool value, uint? stateId = null)
    {
        if (TryGetEffectiveProperty(propertyId, out var property, stateId)
            && property.Kind == UiPropertyKind.Bool)
        {
            value = property.BoolValue;
            return true;
        }

        value = false;
        return false;
    }

    public bool TryGetEffectiveInteger(uint propertyId, out int value, uint? stateId = null)
    {
        if (TryGetEffectiveProperty(propertyId, out var property, stateId)
            && property.Kind == UiPropertyKind.Integer)
        {
            value = property.IntegerValue;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryGetEffectiveFloat(uint propertyId, out float value, uint? stateId = null)
    {
        if (TryGetEffectiveProperty(propertyId, out var property, stateId)
            && property.Kind == UiPropertyKind.Float)
        {
            value = property.FloatValue;
            return true;
        }

        value = 0f;
        return false;
    }

    public uint EffectiveDefaultStateId()
    {
        if (DefaultStateId != 0 && States.ContainsKey(DefaultStateId))
            return DefaultStateId;
        if (States.ContainsKey(1u)) // UIStateId.Normal
            return 1u;
        return UiStateInfo.DirectStateId;
    }
}

public static class ElementReader
{
    public static AnchorEdges ToAnchors(uint left, uint top, uint right, uint bottom)
    {
        var a = AnchorEdges.None;
        if (left == 1 || left == 4)                 a |= AnchorEdges.Left;
        if (right == 1 || right == 4 || left == 2)  a |= AnchorEdges.Right;
        if (top == 1 || top == 4)                   a |= AnchorEdges.Top;
        if (bottom == 1 || bottom == 4 || top == 2) a |= AnchorEdges.Bottom;
        if (a == AnchorEdges.None) a = AnchorEdges.Left | AnchorEdges.Top; // default: pin top-left
        return a;
    }

    public static ElementInfo Merge(ElementInfo base_, ElementInfo derived)
    {
        var m = new ElementInfo
        {
            Id        = derived.Id        != 0 ? derived.Id        : base_.Id,
            Type      = derived.Type      != 0 ? derived.Type      : base_.Type,
            X         = derived.X,
            Y         = derived.Y,
            // NOTE: 0 is the "not set, inherit from base" sentinel for Width/Height. This
            // diverges from the format doc §12 rule 2 ("derived W/H win even if zero") but is
            // indistinguishable for Plan 1 (all base elements are zero-size Type-12 prototypes).
            // If a real zero-size derived element ever needs to override a non-zero base in
            // switch Width/Height to nullable values and use presence-aware merging.
            Width     = derived.Width     != 0 ? derived.Width     : base_.Width,
            Height    = derived.Height    != 0 ? derived.Height    : base_.Height,
            Left      = derived.Left,
            Top       = derived.Top,
            Right     = derived.Right,
            Bottom    = derived.Bottom,
            ReadOrder = derived.ReadOrder,
            ZLevel    = derived.ZLevel != 0 ? derived.ZLevel : base_.ZLevel,
            DefaultStateId = derived.DefaultStateId != 0 ? derived.DefaultStateId : base_.DefaultStateId,
            FontDid          = derived.FontDid   != 0 ? derived.FontDid   : base_.FontDid,
            HJustify         = HasEffectiveEnum(derived, 0x14u) ? derived.HJustify : base_.HJustify,
            VJustify         = HasEffectiveEnum(derived, 0x15u) ? derived.VJustify : base_.VJustify,
            FontColor        = derived.FontColor ?? base_.FontColor,
            TagFontColor     = derived.TagFontColor ?? base_.TagFontColor,
            Outline          = derived.Outline || base_.Outline,
            // OutlineColor: same "non-null derived wins" rule as FontColor.
            OutlineColor     = derived.OutlineColor ?? base_.OutlineColor,
            MarginLeft       = derived.MarginLeft   != 0 ? derived.MarginLeft   : base_.MarginLeft,
            MarginRight      = derived.MarginRight  != 0 ? derived.MarginRight  : base_.MarginRight,
            MarginTop        = derived.MarginTop    != 0 ? derived.MarginTop    : base_.MarginTop,
            MarginBottom     = derived.MarginBottom != 0 ? derived.MarginBottom : base_.MarginBottom,
            // DefaultStateName: derived wins if set; otherwise inherit the base's default.
            DefaultStateName = !string.IsNullOrEmpty(derived.DefaultStateName) ? derived.DefaultStateName : base_.DefaultStateName,
            Children  = new List<ElementInfo>(derived.Children),
        };
        // Start with base StateMedia as defaults, then let derived entries override.
        m.StateMedia = new Dictionary<string, (uint, int)>(base_.StateMedia);
        foreach (var kv in derived.StateMedia)
            m.StateMedia[kv.Key] = kv.Value;
        m.StateCursors = new Dictionary<string, UiCursorMedia>(base_.StateCursors);
        foreach (var kv in derived.StateCursors)
            m.StateCursors[kv.Key] = kv.Value;

        m.States = new Dictionary<uint, UiStateInfo>();
        foreach (var (id, state) in base_.States)
            m.States[id] = state.Clone();
        foreach (var (id, state) in derived.States)
        {
            m.States[id] = m.States.TryGetValue(id, out var baseState)
                ? UiStateInfo.Merge(baseState, state)
                : state.Clone();
        }

        ApplyCanonicalLegacyProjection(m);
        return m;
    }

    internal static void ApplyCanonicalLegacyProjection(ElementInfo info)
    {
        if (info.TryGetEffectiveProperty(0x1Au, out var font)
            && font.Kind == UiPropertyKind.Array
            && font.ArrayValue.Count > 0
            && font.ArrayValue[0].Kind == UiPropertyKind.DataId)
        {
            info.FontDid = checked((uint)font.ArrayValue[0].UnsignedValue);
        }

        if (info.TryGetEffectiveProperty(0x14u, out var horizontal)
            && horizontal.Kind == UiPropertyKind.Enum)
        {
            info.HJustify = MapHorizontalJustification(horizontal.UnsignedValue);
        }

        if (info.TryGetEffectiveProperty(0x15u, out var vertical)
            && vertical.Kind == UiPropertyKind.Enum)
        {
            info.VJustify = MapVerticalJustification(vertical.UnsignedValue);
        }

        if (info.TryGetEffectiveProperty(0x1Bu, out var color))
        {
            UiPropertyValue? colorValue = color.Kind == UiPropertyKind.Color
                ? color
                : color.Kind == UiPropertyKind.Array
                    && color.ArrayValue.Count > 0
                    && color.ArrayValue[0].Kind == UiPropertyKind.Color
                        ? color.ArrayValue[0]
                        : null;
            if (colorValue is not null)
            {
                var c = colorValue.ColorValue;
                float alpha = c.Alpha == 0 ? 1f : c.Alpha / 255f;
                info.FontColor = new Vector4(c.Red / 255f, c.Green / 255f, c.Blue / 255f, alpha);
            }
        }

        if (info.TryGetEffectiveProperty(0x1Du, out var tagColor))
        {
            UiPropertyValue? tagValue = tagColor.Kind == UiPropertyKind.Color
                ? tagColor
                : tagColor.Kind == UiPropertyKind.Array
                    && tagColor.ArrayValue.Count > 0
                    && tagColor.ArrayValue[0].Kind == UiPropertyKind.Color
                        ? tagColor.ArrayValue[0]
                        : null;
            if (tagValue is not null)
            {
                var t = tagValue.ColorValue;
                float alpha = t.Alpha == 0 ? 1f : t.Alpha / 255f;
                info.TagFontColor =
                    new Vector4(t.Red / 255f, t.Green / 255f, t.Blue / 255f, alpha);
            }
        }

        if (info.TryGetEffectiveProperty(0x21u, out var outline)
            && outline.Kind == UiPropertyKind.Bool)
        {
            info.Outline = outline.BoolValue;
        }

        if (info.TryGetEffectiveProperty(0x22u, out var outlineColor))
        {
            UiPropertyValue? outlineColorValue = outlineColor.Kind == UiPropertyKind.Color
                ? outlineColor
                : outlineColor.Kind == UiPropertyKind.Array
                    && outlineColor.ArrayValue.Count > 0
                    && outlineColor.ArrayValue[0].Kind == UiPropertyKind.Color
                        ? outlineColor.ArrayValue[0]
                        : null;
            if (outlineColorValue is not null)
            {
                var c = outlineColorValue.ColorValue;
                float alpha = c.Alpha == 0 ? 1f : c.Alpha / 255f;
                info.OutlineColor = new Vector4(c.Red / 255f, c.Green / 255f, c.Blue / 255f, alpha);
            }
        }

        if (info.TryGetEffectiveInteger(0x23u, out int marginLeft))
            info.MarginLeft = marginLeft;
        if (info.TryGetEffectiveInteger(0x24u, out int marginRight))
            info.MarginRight = marginRight;
        if (info.TryGetEffectiveInteger(0x25u, out int marginTop))
            info.MarginTop = marginTop;
        if (info.TryGetEffectiveInteger(0x26u, out int marginBottom))
            info.MarginBottom = marginBottom;

        info.TabTable = ReadTabTable(info);

        info.TemplateList = ReadTemplateList(info);

        info.ScrollbarElementId = ReadReferencedElementId(info, 0x72u);

        info.LedCheckedSprite = ReadReferencedElementId(info, 0x10000082u);
        info.LedUncheckedSprite = ReadReferencedElementId(info, 0x10000083u);

        if (info.TryGetEffectiveBool(0x3Bu, out bool invisible))
        {
            info.Invisible = invisible;
        }

        if (info.TryGetEffectiveBool(0x4Bu, out bool tooltipOn))
        {
            info.TooltipEnabled = tooltipOn;
        }

        if (info.TryGetEffectiveProperty(0x49u, out var tooltipText)
            && tooltipText.Kind == UiPropertyKind.StringInfo)
        {
            info.TooltipText = tooltipText.StringInfoValue;
        }

        info.TooltipRootElementId = ReadReferencedElementId(info, 0x47u);
        info.TooltipLayoutDid = ReadReferencedElementId(info, 0x48u);
        info.TooltipTextChildElementId = ReadReferencedElementId(info, 0x4Au);

        if (info.TryGetEffectiveFloat(0x50u, out float tooltipDelay))
        {
            info.TooltipDelaySeconds = tooltipDelay;
        }

        if (info.TryGetEffectiveInteger(0x3Du, out int maxWidth))
            info.MaxWidth = maxWidth;
        if (info.TryGetEffectiveInteger(0x3Fu, out int minWidth))
            info.MinWidth = minWidth;
        if (info.TryGetEffectiveInteger(0x3Cu, out int maxHeight))
            info.MaxHeight = maxHeight;
        if (info.TryGetEffectiveInteger(0x3Eu, out int minHeight))
            info.MinHeight = minHeight;
    }

    internal static HJustify MapHorizontalJustification(ulong raw) => raw switch
    {
        1UL => HJustify.Center,
        3UL or 5UL => HJustify.Right,
        _ => HJustify.Left,
    };

    internal static VJustify MapVerticalJustification(ulong raw) => raw switch
    {
        1UL => VJustify.Center,
        3UL or 5UL => VJustify.Bottom,
        _ => VJustify.Top,
    };

    private static bool HasEffectiveEnum(ElementInfo info, uint propertyId) =>
        info.TryGetEffectiveProperty(propertyId, out UiPropertyValue value)
        && value.Kind == UiPropertyKind.Enum;

    private static List<UiTabTableEntry> ReadTabTable(ElementInfo info)
    {
        var entries = new List<UiTabTableEntry>();
        if (!info.TryGetEffectiveProperty(0x2Eu, out var property)
            || property.Kind != UiPropertyKind.Array)
            return entries;

        foreach (UiPropertyValue item in property.ArrayValue)
        {
            if (item.Kind != UiPropertyKind.Struct) continue;
            uint buttonId = ReadStructMemberId(item.StructValue, 0x30u);
            uint pageId = ReadStructMemberId(item.StructValue, 0x31u);
            if (buttonId == 0u || pageId == 0u) continue;
            bool isDefault = item.StructValue.TryGetValue(0x32u, out var flag)
                && flag.Kind == UiPropertyKind.Bool
                && flag.BoolValue;
            entries.Add(new UiTabTableEntry(buttonId, pageId, isDefault));
        }

        return entries;
    }

    private static List<UiTemplateListEntry> ReadTemplateList(ElementInfo info)
    {
        var entries = new List<UiTemplateListEntry>();
        if (!info.TryGetEffectiveProperty(0x64u, out var property)
            || property.Kind != UiPropertyKind.Array)
            return entries;

        foreach (UiPropertyValue item in property.ArrayValue)
        {
            if (item.Kind != UiPropertyKind.Struct) continue;
            uint layoutDid = ReadStructMemberId(item.StructValue, 0x63u);
            uint elementId = ReadStructMemberId(item.StructValue, 0x62u);
            entries.Add(new UiTemplateListEntry(layoutDid, elementId));
        }

        return entries;
    }

    private static uint ReadStructMemberId(IReadOnlyDictionary<uint, UiPropertyValue> members, uint key)
    {
        if (!members.TryGetValue(key, out var value))
            return 0u;
        return value.Kind switch
        {
            UiPropertyKind.Enum or UiPropertyKind.DataId => (uint)value.UnsignedValue,
            UiPropertyKind.Integer when value.IntegerValue >= 0 => (uint)value.IntegerValue,
            _ => 0u,
        };
    }

    private static uint ReadReferencedElementId(ElementInfo info, uint propertyId)
    {
        if (!info.TryGetEffectiveProperty(propertyId, out var property))
            return 0u;
        return property.Kind switch
        {
            UiPropertyKind.Enum or UiPropertyKind.DataId => (uint)property.UnsignedValue,
            UiPropertyKind.Integer when property.IntegerValue >= 0 => (uint)property.IntegerValue,
            _ => 0u,
        };
    }

    internal static Vector4[] ReadEffectiveColorPalette(
        ElementInfo info,
        uint propertyId)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.TryGetEffectiveProperty(propertyId, out UiPropertyValue value))
            return [];

        IEnumerable<UiPropertyValue> entries = value.Kind switch
        {
            UiPropertyKind.Color => [value],
            UiPropertyKind.Array => value.ArrayValue,
            _ => [],
        };

        return entries
            .Where(entry => entry.Kind == UiPropertyKind.Color)
            .Select(entry =>
            {
                UiColorValue color = entry.ColorValue;
                float alpha = color.Alpha == 0 ? 1f : color.Alpha / 255f;
                return new Vector4(
                    color.Red / 255f,
                    color.Green / 255f,
                    color.Blue / 255f,
                    alpha);
            })
            .ToArray();
    }

    internal static IReadOnlyDictionary<uint, Vector4>? BuildPerStateColorMap(
        ElementInfo info, uint propertyId)
    {
        Dictionary<uint, Vector4>? map = null;
        foreach (uint stateId in info.States.Keys)
        {
            if (!info.TryGetEffectiveProperty(propertyId, out UiPropertyValue value, stateId))
                continue;

            UiPropertyValue? colorValue = value.Kind == UiPropertyKind.Color
                ? value
                : value.Kind == UiPropertyKind.Array
                    && value.ArrayValue.Count > 0
                    && value.ArrayValue[0].Kind == UiPropertyKind.Color
                        ? value.ArrayValue[0]
                        : null;
            if (colorValue is null)
                continue;

            UiColorValue c = colorValue.ColorValue;
            float alpha = c.Alpha == 0 ? 1f : c.Alpha / 255f;
            (map ??= new Dictionary<uint, Vector4>())[stateId] =
                new Vector4(c.Red / 255f, c.Green / 255f, c.Blue / 255f, alpha);
        }

        return map is { Count: > 1 } && map.Values.Distinct().Count() > 1 ? map : null;
    }

    internal static IReadOnlyDictionary<uint, bool>? BuildPerStateBoolMap(
        ElementInfo info, uint propertyId)
    {
        Dictionary<uint, bool>? map = null;
        foreach (uint stateId in info.States.Keys)
        {
            if (!info.TryGetEffectiveProperty(propertyId, out UiPropertyValue value, stateId)
                || value.Kind != UiPropertyKind.Bool)
                continue;
            (map ??= new Dictionary<uint, bool>())[stateId] = value.BoolValue;
        }

        return map is { Count: > 1 } && map.Values.Distinct().Count() > 1 ? map : null;
    }
}
