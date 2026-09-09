using System;
using System.Collections.Generic;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.App.UI.Layout;

public sealed class ImportedLayout
{
    /// <summary>Root widget of the imported tree.</summary>
    public UiElement Root { get; }

    private readonly Dictionary<uint, UiElement> _byId;

    public ImportedLayout(UiElement root, Dictionary<uint, UiElement> byId)
    {
        Root  = root;
        _byId = byId;
    }

    public UiElement? FindElement(uint id)
        => _byId.TryGetValue(id, out var e) ? e : null;
}

public static class LayoutImporter
{
    // ── Pure layer ────────────────────────────────────────────────────────────

    public static ImportedLayout BuildFromInfos(
        ElementInfo rootInfo,
        IEnumerable<ElementInfo> children,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? datFont,
        Func<uint, UiDatFont?>? fontResolve = null,
        Func<UiStringInfoValue, string?>? stringResolve = null)
    {
        rootInfo.Children = new List<ElementInfo>(children);
        return Build(rootInfo, resolve, datFont, fontResolve, stringResolve);
    }

    public static ImportedLayout Build(
        ElementInfo rootInfo,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? datFont,
        Func<uint, UiDatFont?>? fontResolve = null,
        Func<UiStringInfoValue, string?>? stringResolve = null,
        uint sourceLayoutDid = 0u)
    {
        var byId = new Dictionary<uint, UiElement>();
        var root = BuildWidget(rootInfo, resolve, datFont, fontResolve, stringResolve, byId, sourceLayoutDid);
        if (root is null)
        {
            Console.WriteLine($"[UI] LayoutImporter: root element 0x{rootInfo.Id:X8} (type {rootInfo.Type}) produced no widget — using empty container fallback.");
            root = new UiDatElement(rootInfo, resolve);
        }
        return new ImportedLayout(root, byId);
    }

    private static UiElement? BuildWidget(
        ElementInfo info,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? datFont,
        Func<uint, UiDatFont?>? fontResolve,
        Func<UiStringInfoValue, string?>? stringResolve,
        Dictionary<uint, UiElement> byId,
        uint sourceLayoutDid)
    {
        var w = DatWidgetFactory.Create(info, resolve, datFont, fontResolve, stringResolve);
        if (w is null) return null;               // Type-12 style prototype — skip

        w.SourceLayoutDid = sourceLayoutDid;

        w.AuthoredInvisible = info.Invisible;
        if (info.Invisible)
            w.Visible = false;

        w.AuthoredTooltipEnabled = info.TooltipEnabled;
        w.AuthoredTooltipText = DatWidgetFactory.ResolveTooltipText(info, stringResolve);
        w.AuthoredTooltipRootElementId = info.TooltipRootElementId;
        w.AuthoredTooltipLayoutDid = info.TooltipLayoutDid;
        w.AuthoredTooltipTextChildElementId = info.TooltipTextChildElementId;
        w.AuthoredTooltipDelaySeconds = info.TooltipDelaySeconds;

        w.AuthoredResizeMaxWidth = info.MaxWidth;
        w.AuthoredResizeMinWidth = info.MinWidth;
        w.AuthoredResizeMaxHeight = info.MaxHeight;
        w.AuthoredResizeMinHeight = info.MinHeight;

        if (info.Id != 0) byId[info.Id] = w;

        if (!w.ConsumesDatChildren)
        {
            foreach (var child in info.Children)
            {
                var cw = BuildWidget(child, resolve, datFont, fontResolve, stringResolve, byId, sourceLayoutDid);
                if (cw is not null) w.AddChild(cw);
            }
        }
        else if (w is UiMeter)
        {
            foreach (var child in info.Children)
            {
                if (child.Type == 3) continue;
                var cw = BuildWidget(child, resolve, datFont, fontResolve, stringResolve, byId, sourceLayoutDid);
                if (cw is not null) w.AddChild(cw);
            }
        }
        else if (w is UiText or UiField)
        {
            foreach (var child in info.Children)
            {
                if (child.StateMedia.Count == 0) continue;
                var cw = BuildWidget(child, resolve, datFont, fontResolve, stringResolve, byId, sourceLayoutDid);
                if (cw is null) continue;
                w.AddChild(cw);
            }
        }

        if (w is IUiDatStateful stateful)
            stateful.TrySetRetailState(stateful.ActiveRetailStateId);

        if (w is IUiChildrenAttachedListener childrenAttached)
            childrenAttached.OnChildrenAttached();

        return w;
    }

    // ── Dat shell ─────────────────────────────────────────────────────────────

    public static ElementInfo? ImportInfos(IDatReaderWriter dats, uint layoutId)
    {
        var ld = dats.Get<LayoutDesc>(layoutId);
        if (ld is null) return null;

        var referencedAsBase = new HashSet<uint>();
        foreach (var kv in ld.Elements)
            CollectBaseRefsInDesc(kv.Value, layoutId, referencedAsBase);

        var tops = new List<ElementInfo>();
        foreach (var kv in ld.Elements)
        {
            var d = kv.Value;
            if (referencedAsBase.Contains(d.ElementId) && HasNoOwnMedia(d))
            {
                Console.WriteLine($"[UI] LayoutImporter: skipping prototype element 0x{d.ElementId:X8} in layout 0x{layoutId:X8} (no own media, referenced as BaseElement).");
                continue;
            }

            tops.Add(Resolve(dats, d, new HashSet<(uint, uint)>()));
        }

        var referencedAsTemplate = new HashSet<uint>();
        foreach (ElementInfo top in tops)
            CollectTemplateRefs(top, layoutId, referencedAsTemplate);
        if (referencedAsTemplate.Count > 0)
        {
            for (int i = tops.Count - 1; i >= 0; i--)
            {
                if (!referencedAsTemplate.Contains(tops[i].Id)) continue;
                Console.WriteLine(
                    $"[UI] LayoutImporter: skipping row-template element 0x{tops[i].Id:X8} "
                    + $"in layout 0x{layoutId:X8} (referenced by a same-layout template list).");
                tops.RemoveAt(i);
            }
        }

        if (tops.Count == 1)
            return tops[0];

        foreach (var top in tops)
            SetOriginalParentSize(top, ld.Width, ld.Height);
        return new ElementInfo
        {
            Id = 0,
            Type = 3,
            Width = ld.Width,
            Height = ld.Height,
            Children = tops,
        };
    }

    private static void CollectTemplateRefs(
        ElementInfo info, uint layoutId, HashSet<uint> referenced)
    {
        foreach (UiTemplateListEntry entry in info.TemplateList)
        {
            if (entry.TemplateLayoutId == layoutId)
                referenced.Add(entry.TemplateElementId);
        }
        foreach (ElementInfo child in info.Children)
            CollectTemplateRefs(child, layoutId, referenced);
    }

    public static ElementInfo? ImportInfos(
        IDatReaderWriter dats,
        uint layoutId,
        uint rootElementId)
    {
        var ld = dats.Get<LayoutDesc>(layoutId);
        if (ld is null) return null;
        ElementDesc? root = FindDesc(ld, rootElementId);
        return root is null
            ? null
            : Resolve(dats, root, new HashSet<(uint, uint)>());
    }

    public static ImportedLayout? Import(
        IDatReaderWriter dats,
        uint layoutId,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? datFont,
        Func<uint, UiDatFont?>? fontResolve = null)
    {
        var rootInfo = ImportInfos(dats, layoutId);
        if (rootInfo is null) return null;
        var strings = new DatStringResolver(dats);
        return Build(rootInfo, resolve, datFont, fontResolve, strings.Resolve, layoutId);
    }

    public static ImportedLayout? Import(
        IDatReaderWriter dats,
        uint layoutId,
        uint rootElementId,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? datFont,
        Func<uint, UiDatFont?>? fontResolve = null)
    {
        var rootInfo = ImportInfos(dats, layoutId, rootElementId);
        if (rootInfo is null) return null;
        var strings = new DatStringResolver(dats);
        return Build(rootInfo, resolve, datFont, fontResolve, strings.Resolve, layoutId);
    }

    // ── Inheritance resolution ────────────────────────────────────────────────

    internal static bool ShouldMountBaseChildren(int derivedChildCount, int derivedMediaCount, int baseChildCount)
        => derivedChildCount == 0 && derivedMediaCount == 0 && baseChildCount > 0;

    private static ElementInfo Resolve(
        IDatReaderWriter dats,
        ElementDesc d,
        HashSet<(uint layoutId, uint elementId)> baseChain)
    {
        var self = ToInfo(d);
        var result = self;
        ElementInfo? baseInfo = null;

        // Apply BaseElement / BaseLayoutId inheritance if present.
        if (d.BaseElement != 0 && d.BaseLayoutId != 0
            && baseChain.Add((d.BaseLayoutId, d.BaseElement)))
        {
            var baseLd   = dats.Get<LayoutDesc>(d.BaseLayoutId);
            var baseDesc = baseLd is null ? null : FindDesc(baseLd, d.BaseElement);
            if (baseDesc is not null)
            {
                baseInfo = Resolve(dats, baseDesc, baseChain);
                result = ElementReader.Merge(baseInfo, self);
            }
        }

        IncorporateChildren(dats, result, baseInfo?.Children, d);

        if (baseInfo is not null
            && ShouldMountBaseChildren(d.Children.Count, self.StateMedia.Count, baseInfo.Children.Count))
        {
            result.ZLevel = self.ZLevel;

        }

        return result;
    }

    private static void IncorporateChildren(
        IDatReaderWriter dats,
        ElementInfo result,
        IReadOnlyList<ElementInfo>? baseChildren,
        ElementDesc derived)
    {
        baseChildren ??= Array.Empty<ElementInfo>();
        var baseById = baseChildren.ToDictionary(child => child.Id);
        int retainedBaseCount = baseChildren.Count(child =>
            !derived.Children.ContainsKey(child.Id));

        foreach (ElementInfo baseChild in baseChildren)
        {
            bool hasOverlay = derived.Children.TryGetValue(
                baseChild.Id, out ElementDesc? overlay);
            ElementInfo child = hasOverlay
                ? IncorporateResolvedChild(dats, baseChild, overlay!)
                : baseChild;
            if (hasOverlay)
                SetOriginalParentSize(child, result.Width, result.Height);
            result.Children.Add(child);
        }

        foreach (var pair in derived.Children)
        {
            if (baseById.ContainsKey(pair.Key)) continue;
            ElementInfo child = Resolve(dats, pair.Value, new HashSet<(uint, uint)>());
            child.ReadOrder += checked((uint)retainedBaseCount);
            SetOriginalParentSize(child, result.Width, result.Height);
            result.Children.Add(child);
        }
    }

    private static ElementInfo IncorporateResolvedChild(
        IDatReaderWriter dats,
        ElementInfo baseChild,
        ElementDesc derivedChild)
    {
        ElementInfo self = ToInfo(derivedChild);
        ElementInfo result = ElementReader.Merge(baseChild, self);
        IncorporateChildren(dats, result, baseChild.Children, derivedChild);
        return result;
    }

    private static ElementInfo ToInfo(ElementDesc d)
    {
        // Normalize DefaultState: UIStateId.ToString() gives "Undef"/"Undefined" or "0" when
        // no default is set; map those to "" so UiDatElement treats them as "no preference".
        var defState = d.DefaultState.ToString();
        var info = new ElementInfo
        {
            Id               = d.ElementId,
            Type             = d.Type,
            X                = (float)d.X,
            Y                = (float)d.Y,
            Width            = (float)d.Width,
            Height           = (float)d.Height,
            Left             = d.LeftEdge,
            Top              = d.TopEdge,
            Right            = d.RightEdge,
            Bottom           = d.BottomEdge,
            ReadOrder        = d.ReadOrder,
            ZLevel           = d.ZLevel,
            DefaultStateId   = (uint)d.DefaultState,
            DefaultStateName = (defState is "Undef" or "Undefined" or "0") ? "" : defState,
        };

        // DirectState (unnamed, key "").
        if (d.StateDesc is not null)
            ReadState(d.StateDesc, UiStateInfo.DirectStateId, "", info);

        // Named states (e.g. UIStateId.HideDetail → "HideDetail").
        foreach (var s in d.States)
            ReadState(s.Value, (uint)s.Key, s.Key.ToString(), info);

        ElementReader.ApplyCanonicalLegacyProjection(info);
        return info;
    }

    private static void ReadState(StateDesc sd, uint stateId, string name, ElementInfo info)
    {
        var state = new UiStateInfo
        {
            Id = stateId,
            Name = name,
            PassToChildren = sd.PassToChildren,
            IncorporationFlags = (uint)sd.IncorporationFlags,
            MediaCount = sd.Media.Count,
        };

        // Keep the WHOLE sequence, in order. A state's media is a small
        // program — images interleaved with pauses and jumps — and taking only
        // the first image (below) reduces a blinking element to a still frame.
        // Unrecognised entries are kept as Other so a jump's index still lands
        // on the authored entry.
        var steps = new List<UiMediaStep>(sd.Media.Count);
        foreach (var m in sd.Media)
        {
            steps.Add(m switch
            {
                MediaDescImage i => new UiMediaStep(
                    UiMediaStepKind.Image, i.File, (int)i.DrawMode, 0f, 0f, 0u, 0f),
                MediaDescPause p => new UiMediaStep(
                    UiMediaStepKind.Pause, 0u, 0, p.MinDuration, p.MaxDuration, 0u, 0f),
                MediaDescState st => new UiMediaStep(
                    UiMediaStepKind.State, 0u, 0, 0f, 0f,
                    (uint)st.StateId, st.Probability),
                MediaDescJump j => new UiMediaStep(
                    UiMediaStepKind.Jump, 0u, 0, 0f, 0f, j.JumpItemIndex, j.Probability),
                _ => new UiMediaStep(
                    UiMediaStepKind.Other, 0u, 0, 0f, 0f, 0u, 0f, (int)m.MediaType),
            });
        }
        state.MediaSteps = steps;

        if (sd.Media.Count == 2
            && sd.Media[0] is MediaDescAnimation animation
            && sd.Media[1] is MediaDescJump { JumpItemIndex: 0, Probability: 1f }
            && animation.Frames.Count > 0
            && float.IsFinite(animation.Duration) && animation.Duration > 0f)
        {
            state.LoopingAnimation = new UiLoopingImageAnimation(
                animation.Frames.ToArray(), animation.Duration, (int)animation.DrawMode);
        }

        bool imageRead = false;
        foreach (var m in sd.Media)
        {
            if (m is MediaDescImage img)
            {
                state.ImageMediaCount++;
                if (!imageRead && img.File != 0)
                {
                    info.StateMedia[name] = (img.File, (int)img.DrawMode);
                    state.Image = new UiImageMedia(img.File, (int)img.DrawMode);
                    imageRead = true;
                }
            }

            if (m is MediaDescCursor cursor && cursor.File != 0)
            {
                info.StateCursors[name] = new UiCursorMedia(
                    cursor.File,
                    checked((int)cursor.XHotspot),
                    checked((int)cursor.YHotspot));
                state.Cursor = info.StateCursors[name];
            }
        }

        if (sd.Properties is not null)
        {
            foreach (var (propertyId, property) in sd.Properties)
                state.Properties.Values[propertyId] = ConvertProperty(property);
        }
        info.States[stateId] = state;

        if (info.FontDid == 0 && sd.Properties is not null
            && sd.Properties.TryGetValue(0x1Au, out var raw)
            && raw is ArrayBaseProperty arr && arr.Value.Count > 0
            && arr.Value[0] is DataIdBaseProperty did)
        {
            info.FontDid = did.Value;
        }

        if (sd.Properties is not null)
        {
            if (info.FontColor is null
                && sd.Properties.TryGetValue(0x1Bu, out var cRaw)
                && cRaw is ColorBaseProperty cProp)
            {
                var c = cProp.Value;
                float a = c.Alpha == 0 ? 1f : c.Alpha / 255f;
                info.FontColor = new System.Numerics.Vector4(c.Red / 255f, c.Green / 255f, c.Blue / 255f, a);
            }


            if (info.OutlineColor is null
                && sd.Properties.TryGetValue(0x22u, out var outlineColorRaw)
                && outlineColorRaw is ColorBaseProperty outlineColorProp)
            {
                var oc = outlineColorProp.Value;
                float oa = oc.Alpha == 0 ? 1f : oc.Alpha / 255f;
                info.OutlineColor = new System.Numerics.Vector4(oc.Red / 255f, oc.Green / 255f, oc.Blue / 255f, oa);
            }
        }
    }

    internal static UiPropertyValue ConvertProperty(BaseProperty property)
    {
        var value = new UiPropertyValue { MasterPropertyId = property.MasterPropertyId };
        switch (property)
        {
            case EnumBaseProperty p:
                value.Kind = UiPropertyKind.Enum;
                value.UnsignedValue = p.Value;
                break;
            case BoolBaseProperty p:
                value.Kind = UiPropertyKind.Bool;
                value.BoolValue = p.Value;
                break;
            case DataIdBaseProperty p:
                value.Kind = UiPropertyKind.DataId;
                value.UnsignedValue = p.Value;
                break;
            case FloatBaseProperty p:
                value.Kind = UiPropertyKind.Float;
                value.FloatValue = p.Value;
                break;
            case IntegerBaseProperty p:
                value.Kind = UiPropertyKind.Integer;
                value.IntegerValue = p.Value;
                break;
            case StringInfoBaseProperty p:
                value.Kind = UiPropertyKind.StringInfo;
                value.StringInfoValue = new UiStringInfoValue(
                    p.Value.Token,
                    p.Value.StringId,
                    p.Value.TableId.DataId,
                    (byte)p.Value.Override,
                    p.Value.English,
                    p.Value.Comment);
                break;
            case ColorBaseProperty p:
                value.Kind = UiPropertyKind.Color;
                value.ColorValue = new UiColorValue(
                    p.Value.Blue,
                    p.Value.Green,
                    p.Value.Red,
                    p.Value.Alpha);
                break;
            case ArrayBaseProperty p:
                value.Kind = UiPropertyKind.Array;
                foreach (var item in p.Value)
                    value.ArrayValue.Add(ConvertProperty(item));
                break;
            case StructBaseProperty p:
                value.Kind = UiPropertyKind.Struct;
                foreach (var (key, item) in p.Value)
                    value.StructValue[key] = ConvertProperty(item);
                break;
            case VectorBaseProperty p:
                value.Kind = UiPropertyKind.Vector;
                value.VectorValue = p.Value;
                break;
            case Bitfield32BaseProperty p:
                value.Kind = UiPropertyKind.Bitfield32;
                value.UnsignedValue = p.Value;
                break;
            case Bitfield64BaseProperty p:
                value.Kind = UiPropertyKind.Bitfield64;
                value.UnsignedValue = p.Value;
                break;
            case InstanceIdBaseProperty p:
                value.Kind = UiPropertyKind.InstanceId;
                value.UnsignedValue = p.Value;
                break;
            default:
                throw new NotSupportedException($"Unsupported UI base-property type {property.GetType().FullName}.");
        }

        return value;
    }

    // ── Prototype detection helpers ───────────────────────────────────────────

    private static void CollectBaseRefsInDesc(ElementDesc d, uint layoutId, HashSet<uint> result)
    {
        if (d.BaseElement != 0 && d.BaseLayoutId == layoutId)
            result.Add(d.BaseElement);
        foreach (var kv in d.Children)
            CollectBaseRefsInDesc(kv.Value, layoutId, result);
    }

    private static bool HasNoOwnMedia(ElementDesc d)
    {
        // Re-use ToInfo's media extraction: if the resulting StateMedia is empty the
        // element has no renderable image in any state.
        var info = ToInfo(d);
        return info.StateMedia.Count == 0;
    }

    // ── Element tree search ───────────────────────────────────────────────────

    private static ElementDesc? FindDesc(LayoutDesc ld, uint id)
    {
        foreach (var kv in ld.Elements)
        {
            var f = FindDescIn(kv.Value, id);
            if (f is not null) return f;
        }
        return null;
    }

    private static ElementDesc? FindDescIn(ElementDesc d, uint id)
    {
        if (d.ElementId == id) return d;
        foreach (var kv in d.Children)
        {
            var f = FindDescIn(kv.Value, id);
            if (f is not null) return f;
        }
        return null;
    }

    // ── Raw-edge layout provenance ────────────────────────────────────────────

    private static void SetOriginalParentSize(ElementInfo child, float width, float height)
    {
        child.OriginalParentWidth = width;
        child.OriginalParentHeight = height;
        child.HasOriginalParentSize = true;
    }
}
