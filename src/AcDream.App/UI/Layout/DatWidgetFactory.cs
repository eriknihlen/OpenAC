using System;
using System.Linq;
using AcDream.App.UI;

namespace AcDream.App.UI.Layout;

public static class DatWidgetFactory
{
    public static UiElement? Create(ElementInfo info,
        Func<uint, (uint, int, int)> resolve, UiDatFont? datFont,
        Func<uint, UiDatFont?>? fontResolve = null,
        Func<UiStringInfoValue, string?>? stringResolve = null)
    {
        UiDatFont? elementFont = datFont;
        if (fontResolve is not null && info.FontDid != 0)
            elementFont = fontResolve(info.FontDid) ?? datFont;

        UiElement e = info.Type switch
        {
            UiRadar.RetailClassId => new UiRadar(),
            UiVitalsRoot.GmVitalsClassId
                or UiVitalsRoot.GmFloatyVitalsClassId
                or UiVitalsRoot.GmFloatySideVitalsClassId
                => new UiVitalsRoot(info, resolve),
            1    => BuildButton(info, resolve, elementFont, fontResolve, stringResolve), // UIElement_Button
            2    => new UiDatElement(info, resolve)
                    {
                        WindowMoveHandle = true,
                        ClickThrough = false,
                    },
            IndicatorBarController.BurdenClassId
                or IndicatorBarController.EffectsClassId
                or IndicatorBarController.LinkClassId
                or IndicatorBarController.MiniGameClassId
                or IndicatorBarController.VitaeClassId => BuildButton(
                    info, resolve, elementFont, fontResolve, stringResolve),
            5    => new UiTemplateListBox(info, resolve, info.TemplateList, info.ScrollbarElementId),
            6    => BuildMenu(info, resolve, elementFont, fontResolve), // UIElement_Menu (reg :120163)
            7    => BuildMeter(info, resolve, elementFont, stringResolve),    // UIElement_Meter
            8    => new UiTabPanel(info, resolve, info.TabTable),
            9    => BuildResizeGrip(info, resolve),
            0xD  => new UiViewport(),                          // UIElement_Viewport — 3-D mini-scene blit leaf
            11   => BuildScrollbar(info, resolve),             // UIElement_Scrollbar (reg :124137)
            12   => BuildText(info, resolve, elementFont, stringResolve), // UIElement_Text
            0x13 => new UiDialogRoot(),
            0x14 => new UiDialogRoot(),
            0x15 => new UiDialogRoot(),
            0x17 => new UiDialogRoot(),                         // MessageDialog
            0x19 => new UiDialogRoot(),
            0x10000031u => new UiItemList(resolve),            // UIElement_ItemList — toolbar/inventory/paperdoll slots
            0x10000035u => BuildCheckbox(
                info, resolve, elementFont, fontResolve, stringResolve), // UIOption_Checkbox
            0x10000036u => new UiOptionToggleSlider(),
            0x10000037u => BuildScrollbar(info, resolve),
            0x10000038u => BuildMenu(info, resolve, elementFont, fontResolve),
            0x10000044u => new UiCheckboxBitfield64(
                info.TemplateList, info.LedCheckedSprite, info.LedUncheckedSprite),
            _    => new UiDatElement(info, resolve),
        };

        e.DatElementId = info.Id;
        e.SetStateCursors(info.StateCursors);

        // Propagate position + size (pixel-exact from the dat).
        e.Left   = info.X;
        e.Top    = info.Y;
        e.Width  = info.Width;
        e.Height = info.Height;

        e.ZOrder = (int)info.ReadOrder - (int)info.ZLevel * 10000;

        e.Anchors = ElementReader.ToAnchors(info.Left, info.Top, info.Right, info.Bottom);

        if (info.HasOriginalParentSize)
            e.LayoutPolicy = CreateLayoutPolicy(info);

        return e;
    }

    private static UiMenu BuildMenu(
        ElementInfo info,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? elementFont,
        Func<uint, UiDatFont?>? fontResolve)
    {
        ElementInfo? label = info.Children.FirstOrDefault(
            static child => child.Type == 12u);
        UiDatFont? labelFont = label is { FontDid: not 0u } && fontResolve is not null
            ? fontResolve(label.FontDid) ?? elementFont
            : elementFont;
        var menu = new UiMenu
        {
            SpriteResolve = resolve,
            DatFont = labelFont,
            ButtonDatFont = labelFont,
            NormalSprite = 0x06004D65u,
            PressedSprite = 0x06004D66u,
            PopupBgSprite = 0x0600124Cu,
            ItemNormalSprite = 0x0600124Eu,
            ItemHighlightSprite = 0x0600124Du,
            ButtonTextCentered = label?.HJustify == HJustify.Center,
        };
        if (label?.FontColor is { } color)
            menu.TextColor = color;
        return menu;
    }

    private static UiScrollbar BuildScrollbar(
        ElementInfo info,
        Func<uint, (uint tex, int w, int h)> resolve)
    {
        var bar = new UiScrollbar
        {
            SpriteResolve = resolve,
            TrackSprite = DefaultImage(info),
            Horizontal = info.Width > info.Height,
        };

        uint incrementId = ReferencedElementId(info, 0x77u);
        uint decrementId = ReferencedElementId(info, 0x78u);
        ElementInfo? increment = info.Children.FirstOrDefault(child => child.Id == incrementId);
        ElementInfo? decrement = info.Children.FirstOrDefault(child => child.Id == decrementId);

        ElementInfo? leadingButton = increment;
        ElementInfo? trailingButton = decrement;
        if (leadingButton is null && trailingButton is null)
        {
            ElementInfo[] typeOneChildren = info.Children
                .Where(child => child.Type == 1u && child.Id != 1u)
                .OrderBy(child => bar.Horizontal ? child.X : child.Y)
                .ThenBy(child => child.ReadOrder)
                .ToArray();
            leadingButton = typeOneChildren.FirstOrDefault();
            trailingButton = typeOneChildren.Length > 1 ? typeOneChildren[^1] : null;
        }
        bar.UpSprite = ButtonStateImage(leadingButton, "Normal");
        bar.UpRolloverSprite = ButtonStateImage(leadingButton, "Normal_rollover");
        bar.UpPressedSprite = ButtonStateImage(leadingButton, "Normal_pressed");
        bar.DownSprite = ButtonStateImage(trailingButton, "Normal");
        bar.DownRolloverSprite = ButtonStateImage(trailingButton, "Normal_rollover");
        bar.DownPressedSprite = ButtonStateImage(trailingButton, "Normal_pressed");
        if (info.TryGetEffectiveBool(0x79u, out bool hideDisabled))
            bar.HideWhenDisabled = hideDisabled;

        if (bar.Horizontal)
        {
            if (leadingButton is { Width: > 0f })
                bar.DecrementButtonExtent = leadingButton.Width;
            if (trailingButton is { Width: > 0f })
                bar.IncrementButtonExtent = trailingButton.Width;

            ElementInfo? scalarThumb = info.Children.FirstOrDefault(child => child.Id == 1u);
            bar.TrackSprite = DefaultImage(info);
            bar.ThumbSprite = scalarThumb is null ? 0u : DefaultImage(scalarThumb);

            if (bar.TrackSprite == 0u)
            {
                ElementInfo? authoredTrack = info.Children.FirstOrDefault(child => child.Id == 4u);
                bar.TrackSprite = authoredTrack is null ? 0u : DefaultImage(authoredTrack);
            }

            ElementInfo? meter = info.Children.FirstOrDefault(child => child.Type == 7u);
            ElementInfo? fill = meter?.Children.FirstOrDefault(child => child.Id == 2u)
                ?? meter?.Children
                    .Where(child => DefaultImage(child) != 0u)
                    .OrderByDescending(child => child.ReadOrder)
                    .FirstOrDefault();
            bar.ScalarFillSprite = fill is null ? 0u : DefaultImage(fill);

            ElementInfo? scalarRange = meter?.Children.FirstOrDefault(
                child => child.Id == 0x100005EFu);
            bar.ScalarRangeSprite = scalarRange is null
                ? 0u
                : DefaultImage(scalarRange);
            bar.ScalarRangeLayoutPolicy = scalarRange is null
                ? null
                : CreateLayoutPolicy(scalarRange);
            bar.ScalarFillFromRight = meter is not null
                && meter.TryGetEffectiveProperty(0x6Fu, out UiPropertyValue direction)
                && direction.Kind == UiPropertyKind.Enum
                && direction.UnsignedValue == 3u;
            return bar;
        }

        if (leadingButton is { Height: > 0f })
            bar.DecrementButtonExtent = leadingButton.Height;
        if (trailingButton is { Height: > 0f })
            bar.IncrementButtonExtent = trailingButton.Height;

        ElementInfo? thumb = info.Children.FirstOrDefault(child =>
            child.Type == 1u && child.Id != incrementId && child.Id != decrementId);
        if (thumb is not null)
        {
            ElementInfo[] slices = thumb.Children
                .Where(child => DefaultImage(child) != 0u)
                .OrderBy(child => child.Y)
                .ThenBy(child => child.ReadOrder)
                .ToArray();
            if (slices.Length > 0)
            {
                bar.ThumbTopSprite = ButtonStateImage(slices[0], "Normal");
                bar.ThumbTopRolloverSprite = ButtonStateImage(slices[0], "Normal_rollover");
                bar.ThumbTopPressedSprite = ButtonStateImage(slices[0], "Normal_pressed");
            }
            if (slices.Length > 1)
            {
                bar.ThumbSprite = ButtonStateImage(slices[1], "Normal");
                bar.ThumbRolloverSprite = ButtonStateImage(slices[1], "Normal_rollover");
                bar.ThumbPressedSprite = ButtonStateImage(slices[1], "Normal_pressed");
            }
            if (slices.Length > 2)
            {
                bar.ThumbBotSprite = ButtonStateImage(slices[^1], "Normal");
                bar.ThumbBotRolloverSprite = ButtonStateImage(slices[^1], "Normal_rollover");
                bar.ThumbBotPressedSprite = ButtonStateImage(slices[^1], "Normal_pressed");
            }

            if (slices.Length == 0)
            {
                bar.ThumbSprite = ButtonStateImage(thumb, "Normal");
                bar.ThumbRolloverSprite = ButtonStateImage(thumb, "Normal_rollover");
                bar.ThumbPressedSprite = ButtonStateImage(thumb, "Normal_pressed");
            }
        }

        return bar;
    }

    private static UiResizeGrip BuildResizeGrip(
        ElementInfo info, Func<uint, (uint tex, int w, int h)> resolve)
    {
        bool bottom = info.TryGetEffectiveBool(0x2Au, out bool bottomValue) && bottomValue;
        bool left   = info.TryGetEffectiveBool(0x2Bu, out bool leftValue) && leftValue;
        bool right  = info.TryGetEffectiveBool(0x2Cu, out bool rightValue) && rightValue;
        bool top    = info.TryGetEffectiveBool(0x2Du, out bool topValue) && topValue;
        return new UiResizeGrip(info, resolve)
        {
            BorderLocation = UiResizeGrip.DecodeBorderLocation(bottom, left, right, top),
        };
    }

    private static UiLayoutPolicy? CreateLayoutPolicy(ElementInfo info)
    {
        if (!info.HasOriginalParentSize) return null;
        return new UiLayoutPolicy(
            info.Left,
            info.Top,
            info.Right,
            info.Bottom,
            UiPixelRect.FromPositionAndSize(
                (int)info.X,
                (int)info.Y,
                (int)info.Width,
                (int)info.Height),
            UiPixelRect.FromPositionAndSize(
                0,
                0,
                (int)info.OriginalParentWidth,
                (int)info.OriginalParentHeight));
    }

    private static uint ReferencedElementId(ElementInfo info, uint propertyId)
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

    private static uint DefaultImage(ElementInfo info)
    {
        uint stateId = info.EffectiveDefaultStateId();
        if (info.States.TryGetValue(stateId, out var state) && state.Image is { } image)
            return image.File;
        if (info.States.TryGetValue(UiStateInfo.DirectStateId, out var direct)
            && direct.Image is { } directImage)
            return directImage.File;
        return 0u;
    }

    private static uint ButtonStateImage(ElementInfo? info, string stateName)
    {
        if (info is null)
            return 0u;
        if (info.StateMedia.TryGetValue(stateName, out var media))
            return media.File;
        UiStateInfo? state = info.States.Values.FirstOrDefault(
            candidate => string.Equals(candidate.Name, stateName, StringComparison.Ordinal));
        if (state?.Image is { } image)
            return image.File;
        return stateName == "Normal" ? DefaultImage(info) : 0u;
    }

    // ── Meter ────────────────────────────────────────────────────────────────

    private static UiMeter BuildMeter(ElementInfo info,
        Func<uint, (uint, int, int)> resolve, UiDatFont? datFont,
        Func<UiStringInfoValue, string?>? stringResolve = null)
    {
        var m = new UiMeter
        {
            ElementId     = info.Id,
            SpriteResolve = resolve,
            DatFont       = datFont,
            // Outline 0x21 from the meter element (round-5 review S2).
            Outline       = info.Outline,
        };
        if (info.OutlineColor.HasValue)
            m.OutlineColor = info.OutlineColor.Value;

        var containers = info.Children
            .Where(c => c.Type == 3)
            .OrderBy(c => c.ReadOrder)
            .ToList();

        if (containers.Count >= 2
            && HasThreeSliceShape(containers[0])
            && HasThreeSliceShape(containers[1]))
        {
            var (bl, bt, br) = SliceIds(containers[0]);
            m.BackLeft  = bl;
            m.BackTile  = bt;
            m.BackRight = br;

            var (fl, ft, fr) = SliceIds(containers[1]);
            m.FrontLeft  = fl;
            m.FrontTile  = ft;
            m.FrontRight = fr;

            ElementInfo? backOverlay = DetailOverlay(containers[0]);
            ElementInfo? frontOverlay = DetailOverlay(containers[1]);
            if (backOverlay is not null || frontOverlay is not null)
            {
                bool passToChildren = info.States.Values.Any(
                    static s => s.Name is "HideDetail" or "ShowDetail" && s.PassToChildren);
                m.ConfigureDetailOverlay(
                    DetailOverlaySpec(backOverlay, containers[0]),
                    DetailOverlaySpec(frontOverlay, containers[1]),
                    passToChildren);
            }
        }
        else if (containers.Count == 1
            && containers[0].X == 0f && containers[0].Y == 0f
            && containers[0].Width == info.Width && containers[0].Height == info.Height
            && info.States.TryGetValue(UiStateInfo.DirectStateId, out var backState)
            && backState.LoopingAnimation is { DrawMode: 1 } backAnimation
            && containers[0].States.TryGetValue(UiStateInfo.DirectStateId, out var frontState)
            && frontState.LoopingAnimation is { DrawMode: 1 } frontAnimation
            && backAnimation.Duration == frontAnimation.Duration)
        {
            m.ConfigureAnimatedTracks(backAnimation, frontAnimation);
        }
        else if (containers.Count == 1 && containers[0].StateMedia.ContainsKey(""))
        {
            m.BackLeft  = 0;
            m.BackTile  = info.StateMedia.TryGetValue("", out var bm) ? bm.File : 0u;
            m.BackRight = 0;

            m.FrontLeft  = 0;
            m.FrontTile  = containers[0].StateMedia.TryGetValue("", out var fm) ? fm.File : 0u;
            m.FrontRight = 0;
        }
        else if (containers.Any(HasStatefulFill))
        {
            m.BackLeft = 0;
            m.BackTile = info.StateMedia.TryGetValue("", out var track) ? track.File : 0u;
            m.BackRight = 0;
            m.FrontLeft = 0;
            m.FrontTile = 0;
            m.FrontRight = 0;

            foreach (ElementInfo container in containers)
            {
                foreach (var (stateId, state) in container.States)
                {
                    if (stateId == UiStateInfo.DirectStateId
                        || !container.StateMedia.TryGetValue(state.Name, out var media))
                        continue;
                    m.ConfigureStateFill(stateId, media.File);
                }
            }

            foreach (ElementInfo textChild in info.Children.Where(static c => c.Type == 12))
            {
                foreach (var (stateId, state) in textChild.States)
                {
                    if (stateId == UiStateInfo.DirectStateId
                        || !state.Properties.Values.TryGetValue(0x17u, out var caption)
                        || caption.Kind != UiPropertyKind.StringInfo)
                        continue;
                    if (stringResolve?.Invoke(caption.StringInfoValue) is not { Length: > 0 } text)
                        continue;
                    // The state's own 0x14 wins; absent → the element-level
                    // justification (ElementReader's same enum mapping).
                    UiMeterLabelAlign align = textChild.HJustify switch
                    {
                        HJustify.Left => UiMeterLabelAlign.Left,
                        HJustify.Right => UiMeterLabelAlign.Right,
                        _ => UiMeterLabelAlign.Center,
                    };
                    if (state.Properties.Values.TryGetValue(0x14u, out var justify)
                        && justify.Kind == UiPropertyKind.Enum)
                    {
                        align = ElementReader.MapHorizontalJustification(
                            justify.UnsignedValue) switch
                        {
                            HJustify.Left => UiMeterLabelAlign.Left,
                            HJustify.Right => UiMeterLabelAlign.Right,
                            _ => UiMeterLabelAlign.Center,
                        };
                    }
                    m.ConfigureStateLabel(stateId, text, align);
                }
            }
        }
        else
        {
            Console.WriteLine($"[UI] meter 0x{info.Id:X8}: {containers.Count} Type-3 containers but no recognized 3-slice, direct-fill, or stateful-fill shape — bar may render as solid-color fallback.");
        }

        return m;
    }

    private static bool HasThreeSliceShape(ElementInfo container)
        => container.Children.Count(c =>
            c.StateMedia.TryGetValue("", out var media) && media.File != 0) >= 3;

    private static ElementInfo? DetailOverlay(ElementInfo container)
        => container.Children.FirstOrDefault(static c =>
            !c.StateMedia.ContainsKey("")
            && c.StateMedia.TryGetValue("ShowDetail", out var media)
            && media.File != 0);

    private static UiMeterDetailOverlaySpec DetailOverlaySpec(
        ElementInfo? overlay, ElementInfo container)
        => overlay is null
            ? default
            : new UiMeterDetailOverlaySpec(
                overlay.StateMedia["ShowDetail"].File,
                overlay.X, overlay.Y, overlay.Width, overlay.Height,
                overlay.Left, overlay.Top, overlay.Right, overlay.Bottom,
                container.Width, container.Height);

    private static bool HasStatefulFill(ElementInfo container)
        => container.States.Any(pair =>
            pair.Key != UiStateInfo.DirectStateId
            && container.StateMedia.TryGetValue(pair.Value.Name, out var media)
            && media.File != 0);

    private static (uint left, uint tile, uint right) SliceIds(ElementInfo container)
    {
        var slices = container.Children
            .Where(c => c.StateMedia.TryGetValue("", out var med) && med.File != 0)
            .Select(c => (c.X, File: c.StateMedia[""].File))
            .OrderBy(t => t.X)
            .ToList();

        uint left  = slices.Count > 0 ? slices[0].File : 0u;
        uint tile  = slices.Count > 1 ? slices[1].File : 0u;
        uint right = slices.Count > 2 ? slices[2].File : 0u;

        return (left, tile, right);
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    private static UiElement BuildText(ElementInfo info, Func<uint, (uint, int, int)> resolve,
        UiDatFont? elementFont = null,
        Func<UiStringInfoValue, string?>? stringResolve = null)
    {
        uint bg = info.StateMedia.TryGetValue(
                      !string.IsNullOrEmpty(info.DefaultStateName) ? info.DefaultStateName
                    : info.StateMedia.ContainsKey("Normal") ? "Normal" : "", out var m)
                  ? m.File : 0u;

        bool editable = info.TryGetEffectiveBool(0x16u, out var editableValue)
            && editableValue;
        bool selectable = info.TryGetEffectiveBool(0x27u, out var selectableValue)
            && selectableValue;
        bool oneLine = info.TryGetEffectiveBool(0x20u, out var oneLineValue)
            && oneLineValue;

        if (editable)
        {
            uint focusSprite = info.StateMedia.TryGetValue("Normal_focussed", out var focus)
                ? focus.File
                : 0u;
            var field = new UiField
            {
                ElementId = info.Id,
                DatFont = elementFont,
                SpriteResolve = resolve,
                BackgroundSprite = bg,
                FocusFieldSprite = focusSprite,
                Selectable = selectable,
                OneLine = oneLine,
                Centered = info.HJustify == HJustify.Center,
                RightAligned = info.HJustify == HJustify.Right,
                // Outline 0x21 from the field element (round-5 review S2).
                Outline = info.Outline,
            };
            if (info.TryGetEffectiveInteger(0x1Eu, out int maxCharacters))
                field.MaxCharacters = maxCharacters;
            if (info.FontColor.HasValue)
                field.TextColor = info.FontColor.Value;
            if (info.OutlineColor.HasValue)
                field.OutlineColor = info.OutlineColor.Value;

            foreach (ElementInfo child in info.Children)
            {
                if (child.Type != 3u
                    || !child.StateMedia.TryGetValue("Normal_focussed", out var railMedia)
                    || railMedia.File == 0u)
                    continue;
                bool leftAnchored = child.X < info.Width * 0.5f;
                if (leftAnchored)
                {
                    field.FocusRailLeftSprite = railMedia.File;
                    if (child.Width > 0f) field.FocusRailLeftWidth = child.Width;
                }
                else
                {
                    field.FocusRailRightSprite = railMedia.File;
                    if (child.Width > 0f) field.FocusRailRightWidth = child.Width;
                }
            }
            return field;
        }

        bool centered     = info.HJustify == HJustify.Center;
        bool rightAligned = info.HJustify == HJustify.Right;
        var  vJustify     = info.VJustify;

        var t = new UiText
        {
            ElementId        = info.Id,
            BackgroundSprite = bg,
            SpriteResolve    = resolve,
            Centered         = centered,
            RightAligned     = rightAligned,
            VerticalJustify  = vJustify,
            OneLine          = oneLine,
            Selectable       = selectable,
            DatFont          = elementFont,
            FontColorPalette = ElementReader.ReadEffectiveColorPalette(
                info,
                0x1Bu),
            // Outline from dat property 0x21 (BoolBaseProperty). Default false — matches
            // ElementInfo.Outline's own default, so this is a no-op for the ~99% of text
            // elements that don't author it.
            Outline          = info.Outline,
            MarginLeft       = info.MarginLeft,
            MarginRight      = info.MarginRight,
            MarginTop        = info.MarginTop,
            MarginBottom     = info.MarginBottom,
        };
        t.ConfigureDatState(info);

        if (info.FontColor.HasValue)
            t.DefaultColor = info.FontColor.Value;
        if (info.TagFontColor.HasValue)
            t.TagColor = info.TagFontColor.Value;

        if (info.OutlineColor.HasValue)
            t.OutlineColor = info.OutlineColor.Value;

        if (ResolveAuthoredString(info, stringResolve) is { Length: > 0 } authored)
        {
            if (authored.Contains('\n'))
            {
                float cachedWidth = float.NaN;
                UiDatFont? cachedFont = null;
                System.Numerics.Vector4 cachedColor = default;
                UiText.Line[]? cachedLines = null;
                t.LinesProvider = () =>
                {
                    if (cachedLines is null
                        || cachedWidth != t.Width
                        || !ReferenceEquals(cachedFont, t.DatFont)
                        || cachedColor != t.DefaultColor)
                    {
                        cachedWidth = t.Width;
                        cachedFont = t.DatFont;
                        cachedColor = t.DefaultColor;
                        float maximumWidth = Math.Max(
                            1f,
                            t.Width - (t.Padding + t.MarginLeft) - (t.Padding + t.MarginRight));
                        Func<string, float> measure = t.DatFont is { } font
                            ? font.MeasureWidth
                            : static value => value.Length * 8f;
                        cachedLines = [.. UiText
                            .WrapWords(authored, measure, maximumWidth)
                            .Select(line => new UiText.Line(line, t.DefaultColor))];
                    }
                    return cachedLines;
                };
            }
            else
            {
                t.LinesProvider = () =>
                    [new UiText.Line(authored, t.DefaultColor)];
            }
        }

        Dictionary<uint, string>? stateStrings = null;
        foreach (var (stateId, state) in info.States)
        {
            if (stateId == UiStateInfo.DirectStateId
                || !state.Properties.Values.TryGetValue(0x17u, out var stateCaption)
                || stateCaption.Kind != UiPropertyKind.StringInfo)
                continue;
            if (stringResolve?.Invoke(stateCaption.StringInfoValue)
                is { Length: > 0 } text)
                (stateStrings ??= new Dictionary<uint, string>())[stateId] = text;
        }
        if (stateStrings is not null)
            t.SetAuthoredStateStrings(stateStrings);

        return t;
    }

    private static UiButton BuildButton(
        ElementInfo info,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? elementFont,
        Func<uint, UiDatFont?>? fontResolve,
        Func<UiStringInfoValue, string?>? stringResolve)
    {
        ElementInfo[] authoredFaces = info.StateMedia.Count == 0
            ? FindStatefulFaceChildren(info)
            : [];
        ElementInfo? face = authoredFaces.Length == 1 ? authoredFaces[0] : null;
        IReadOnlyList<ElementInfo>? faceSegments = authoredFaces.Length > 1
            ? authoredFaces
            : null;

        string? label = ResolveAuthoredString(info, stringResolve);
        ElementInfo labelInfo = info;
        if (label is null)
        {
            foreach (ElementInfo child in info.Children.Where(child => child.Type == 12u))
            {
                label = ResolveAuthoredString(child, stringResolve);
                if (label is null) continue;
                labelInfo = child;
                break;
            }
        }

        UiDatFont? labelFont = elementFont;
        if (labelInfo.FontDid != 0u && fontResolve is not null)
            labelFont = fontResolve(labelInfo.FontDid) ?? elementFont;

        var button = new UiButton(info, resolve, face, faceSegments)
        {
            Label = label,
            LabelFont = labelFont,
            LabelColor = labelInfo.FontColor ?? info.FontColor
                ?? System.Numerics.Vector4.One,
            Outline = labelInfo.Outline || info.Outline,
        };
        if ((labelInfo.OutlineColor ?? info.OutlineColor) is { } buttonOutlineColor)
            button.OutlineColor = buttonOutlineColor;

        if (face is not null)
        {
            button.FaceLeft = face.X;
            button.FaceTop = face.Y;
            button.FaceWidth = face.Width;
            button.FaceHeight = face.Height;

            if (!ReferenceEquals(labelInfo, info))
            {
                button.LabelBox = (labelInfo.X, labelInfo.Y, labelInfo.Width, labelInfo.Height);
                button.LabelAlign = labelInfo.HJustify == HJustify.Left
                    ? UiButton.LabelAlignment.Left
                    : UiButton.LabelAlignment.Center;
            }
            else
            {
                button.LabelAlign = UiButton.LabelAlignment.Left;
                button.LabelOffsetX = face.X + face.Width + 4f;
            }
        }
        else if (labelInfo.HJustify == HJustify.Left)
        {
            button.LabelAlign = UiButton.LabelAlignment.Left;
            if (!ReferenceEquals(labelInfo, info))
                button.LabelOffsetX = labelInfo.X;
        }

        button.SetPerStateLabelStyle(
            ElementReader.BuildPerStateColorMap(labelInfo, 0x1Bu),
            ElementReader.BuildPerStateBoolMap(labelInfo, 0x21u));

        if (ReferenceEquals(labelInfo, info) && label is not null)
        {
            ElementInfo? valueChild = info.Children.FirstOrDefault(
                child => child.Type == 12u && child.StateMedia.Count == 0);
            if (valueChild is not null)
            {
                button.ValueBox = ReflowValueChildRect(valueChild, info);
                button.ValueFont = valueChild.FontDid != 0u && fontResolve is not null
                    ? fontResolve(valueChild.FontDid) ?? elementFont
                    : elementFont;
                button.ValueColor = valueChild.FontColor ?? System.Numerics.Vector4.One;
                button.ValueAlign = valueChild.HJustify switch
                {
                    HJustify.Left => UiButton.LabelAlignment.Left,
                    HJustify.Right => UiButton.LabelAlignment.Right,
                    _ => UiButton.LabelAlignment.Center,
                };
                button.ValueLabel = ResolveAuthoredString(valueChild, stringResolve);
            }
        }

        return button;
    }

    private static (float X, float Y, float Width, float Height) ReflowValueChildRect(
        ElementInfo child, ElementInfo parent)
    {
        float originalParentWidth = child.HasOriginalParentSize ? child.OriginalParentWidth : parent.Width;
        float originalParentHeight = child.HasOriginalParentSize ? child.OriginalParentHeight : parent.Height;

        var originalChild = UiPixelRect.FromPositionAndSize(
            (int)child.X, (int)child.Y, (int)child.Width, (int)child.Height);
        var originalParent = UiPixelRect.FromPositionAndSize(
            0, 0, (int)originalParentWidth, (int)originalParentHeight);
        var currentParent = UiPixelRect.FromPositionAndSize(
            0, 0, (int)parent.Width, (int)parent.Height);
        var noCurrentChild = new UiPixelRect(0, 0, -1, -1);

        UiPixelRect reflowed = UiLayoutPolicy.Apply(
            child.Left, child.Top, child.Right, child.Bottom,
            originalChild, originalParent,
            noCurrentChild, currentParent);

        return (reflowed.X0, reflowed.Y0, reflowed.Width, reflowed.Height);
    }

    private static UiButton BuildCheckbox(
        ElementInfo info,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? elementFont,
        Func<uint, UiDatFont?>? fontResolve,
        Func<UiStringInfoValue, string?>? stringResolve)
    {
        ElementInfo? indicator = FindStatefulFaceChild(info);
        var button = new UiButton(info, resolve, indicator)
        {
            Label = ResolveAuthoredString(info, stringResolve),
            LabelFont = info.FontDid != 0u && fontResolve is not null
                ? fontResolve(info.FontDid) ?? elementFont
                : elementFont,
            LabelColor = info.FontColor ?? System.Numerics.Vector4.One,
            LabelAlign = UiButton.LabelAlignment.Left,
            Outline = info.Outline,
        };
        if (info.OutlineColor.HasValue)
            button.OutlineColor = info.OutlineColor.Value;

        if (indicator is not null)
        {
            button.FaceLeft = indicator.X;
            button.FaceTop = indicator.Y;
            button.FaceWidth = indicator.Width;
            button.FaceHeight = indicator.Height;
            button.LabelOffsetX = indicator.X + indicator.Width + 4f;
        }

        return button;
    }

    private static ElementInfo? FindStatefulFaceChild(ElementInfo info)
        => FindStatefulFaceChildren(info).FirstOrDefault();

    private static ElementInfo[] FindStatefulFaceChildren(ElementInfo info)
        => info.Children.Where(child =>
            child.StateMedia.Count != 0
            && child.StateMedia.Keys.Any(childState =>
                info.States.Values.Any(parentState =>
                    string.Equals(parentState.Name, childState, StringComparison.Ordinal))))
            .OrderBy(child => child.ReadOrder)
            .ToArray();

    private static string? ResolveAuthoredString(
        ElementInfo info,
        Func<UiStringInfoValue, string?>? stringResolve)
    {
        if (stringResolve is null
            || !info.TryGetEffectiveProperty(0x17u, out var property)
            || property.Kind != UiPropertyKind.StringInfo)
            return null;
        return stringResolve(property.StringInfoValue);
    }

    internal static string? ResolveTooltipText(
        ElementInfo info,
        Func<UiStringInfoValue, string?>? stringResolve)
    {
        if (stringResolve is null || info.TooltipText is not { } tooltipText)
            return null;
        return stringResolve(tooltipText);
    }
}
