using System;
using AcDream.App.UI;
using AcDream.Core.Chat;

namespace AcDream.App.UI.Layout;

public static class ChatOptionsPageController
{
    public const uint RootElementId = 0x1000050Au;

    private const uint PageSlotElementId = 0x1000050Cu;

    /// <summary>The row ListBox (dat Type 5) — <c>m_pOptionBox</c>.</summary>
    public const uint ListBoxElementId = 0x1000050Du;

    public const uint ScrollbarElementId = 0x10000201u;

    private const int HeaderTemplateIndex = 0;
    private const int SeparatorTemplateIndex = 1;
    private const int UnlabelledSliderTemplateIndex = 3;
    private const int LabelledSliderTemplateIndex = 6;
    private const int BitfieldTemplateIndex = 8;

    private const uint StringTableId = 0x23000003u;

    private const uint FilterStringTableId = 0x2300000Du;

    /// <summary>The slider leaf inside either slider row template's subtree.</summary>
    private const uint SliderElementId = 0x1000021Cu;

    private const uint SliderLabelElementId = 0x1000021Bu;

    private const uint SliderRangeMinElementId = 0x1000021Eu;
    private const uint SliderRangeMaxElementId = 0x1000021Fu;

    public readonly record struct FilterRowSpec(ulong Mask, string RetailLabelKey);

    public static readonly FilterRowSpec[] FilterRows =
    {
        new(0x0000000083912021ul, "ID_ChatOption_TextFilter_Gameplay"),
        new(0x0000000000600040ul, "ID_ChatOption_TextFilter_Combat"),
        new(0x0000000000020080ul, "ID_ChatOption_TextFilter_Magic"),
        new(0x0000000000001004ul, "ID_ChatOption_TextFilter_AreaSpeech"),
        new(0x0000000000000018ul, "ID_ChatOption_TextFilter_Tells"),
        new(0x0000000000040C00ul, "ID_ChatOption_TextFilter_Allegience"),
        new(0x0000000000080000ul, "ID_ChatOption_TextFilter_Fellowship"),
        new(0x0000000008000000ul, "ID_ChatOption_TextFilter_General"),
        new(0x0000000010000000ul, "ID_ChatOption_TextFilter_Trade"),
        new(0x0000000020000000ul, "ID_ChatOption_TextFilter_LFG"),
        new(0x0000000040000000ul, "ID_ChatOption_TextFilter_Roleplay"),
        new(0x0000000100000000ul, "ID_ChatOption_TextFilter_Society"),
        new(0x0000000004000000ul, "ID_ChatOption_TextFilter_Error"),
    };

    /// <summary>One authored per-window filter block/section.</summary>
    public readonly record struct FilterBlockSpec(
        int RetailWindowId,
        int CompactWindowId,
        string HeaderKey,
        ulong DefaultFilter,
        bool IncludesGameplayRow);

    public static readonly FilterBlockSpec[] FilterBlocks =
    {
        new(8, ChatWindowState.MainWindowId, "ID_ChatOption_MainChatWindow_Section",
            ChatWindowState.MainWindowDefaultFilter, IncludesGameplayRow: false),
        new(2, 1, "ID_ChatOption_FloatyChatWindow1_Section",
            ChatWindowState.Floaty1DefaultFilter, IncludesGameplayRow: true),
        new(3, 2, "ID_ChatOption_FloatyChatWindow2_Section",
            ChatWindowState.Floaty2DefaultFilter, IncludesGameplayRow: true),
        new(4, 3, "ID_ChatOption_FloatyChatWindow3_Section",
            ChatWindowState.Floaty3DefaultFilter, IncludesGameplayRow: true),
        new(5, 4, "ID_ChatOption_FloatyChatWindow4_Section",
            ChatWindowState.Floaty4DefaultFilter, IncludesGameplayRow: true),
    };

    public sealed record Bindings(
        Func<float> CurrentDefaultOpacity,
        Func<float> CurrentActiveOpacity,
        Action<float> SetDefaultOpacity,
        Action<float> SetActiveOpacity,
        Action FlushOpacity,
        float DefaultOpacityDatDefault,
        float ActiveOpacityDatDefault,
        Func<int, ulong> CurrentFilter,
        Action<int, ulong> SetFilter,
        ChatOptionsDatCaptions.Caption DefaultOpacityCaption,
        ChatOptionsDatCaptions.Caption ActiveOpacityCaption);

    public static bool Bind(
        ImportedLayout layout,
        OptionPage page,
        Func<uint, uint, UiElement?> templateResolver,
        Func<uint, uint, string?> resolveString,
        Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(templateResolver);
        ArgumentNullException.ThrowIfNull(resolveString);
        ArgumentNullException.ThrowIfNull(bindings);

        if (layout.FindElement(ListBoxElementId) is not UiTemplateListBox listBox)
        {
            Console.WriteLine(
                $"[UI] ChatOptionsPageController: ListBox 0x{ListBoxElementId:X8} "
                + "not found (or not a UiTemplateListBox) in the built Options panel tree — "
                + "the Chat tab will have no rows.");
            return false;
        }

        listBox.TemplateResolver = templateResolver;

        UiElement? chatPageSlot = layout.FindElement(PageSlotElementId);
        UiElement? scrollbarElement = chatPageSlot is null
            ? null
            : UiElement.FindDescendant(chatPageSlot, ScrollbarElementId);
        if (scrollbarElement is UiScrollbar scrollbar)
            scrollbar.Model = listBox.Scroll;
        else
            Console.WriteLine(
                $"[UI] ChatOptionsPageController: scrollbar 0x{ScrollbarElementId:X8} "
                + $"not found under Chat page slot 0x{PageSlotElementId:X8} — the Chat "
                + "tab's row list will not scroll.");

        BuildHeaderRow(listBox, "ID_ChatOption_GeneralOptions_Section", resolveString);
        BuildOpacitySliders(listBox, page, resolveString, bindings);
        BuildSeparatorRow(listBox);

        foreach (FilterBlockSpec spec in FilterBlocks)
        {
            BuildHeaderRow(listBox, spec.HeaderKey, resolveString);
            BuildFilterBlock(listBox, spec, page, templateResolver, resolveString, bindings);
            BuildSeparatorRow(listBox);
        }

        return true;
    }

    private static void BuildHeaderRow(
        UiTemplateListBox listBox, string headerKey, Func<uint, uint, string?> resolveString)
    {
        if (listBox.AddItemFromTemplateList(HeaderTemplateIndex) is not UiText header)
        {
            Console.WriteLine(
                $"[UI] ChatOptionsPageController: header template did not build as "
                + $"UiText for '{headerKey}'.");
            return;
        }

        string? label = resolveString(StringTableId, DatStringResolver.ComputeHash(headerKey));
        if (label is null)
        {
            Console.WriteLine(
                $"[UI] ChatOptionsPageController: header string '{headerKey}' did not "
                + "resolve from the DAT string table — the row renders with no text rather "
                + "than an invented label.");
            return;
        }

        header.LinesProvider = () => new[] { new UiText.Line(label, header.DefaultColor) };
    }

    private static void BuildSeparatorRow(UiTemplateListBox listBox)
    {
        if (listBox.AddItemFromTemplateList(SeparatorTemplateIndex) is null)
            Console.WriteLine("[UI] ChatOptionsPageController: separator template did not build.");
    }

    private static void BuildOpacitySliders(
        UiTemplateListBox listBox,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Bindings bindings)
    {
        UiElement? row1 = listBox.AddItemFromTemplateList(UnlabelledSliderTemplateIndex);
        UiScrollbar? slider1 = row1 is null ? null : UiElement.FindDescendant(row1, SliderElementId) as UiScrollbar;
        if (slider1 is null)
            Console.WriteLine(
                "[UI] ChatOptionsPageController: default-opacity slider template did not "
                + "build a UiScrollbar leaf.");

        UiElement? row2 = listBox.AddItemFromTemplateList(LabelledSliderTemplateIndex);
        UiScrollbar? slider2 = row2 is null ? null : UiElement.FindDescendant(row2, SliderElementId) as UiScrollbar;
        if (slider2 is null)
            Console.WriteLine(
                "[UI] ChatOptionsPageController: active-opacity slider template did not "
                + "build a UiScrollbar leaf.");

        if (row2 is not null)
        {
            SetRangeLabel(row2, SliderRangeMinElementId, "ID_UI_Value_Transparent", resolveString);
            SetRangeLabel(row2, SliderRangeMaxElementId, "ID_UI_Value_Opaque", resolveString);
        }

        if (row1 is not null)
            SetOpacityCaption(row1, bindings.DefaultOpacityCaption, slider1);
        if (row2 is not null)
            SetOpacityCaption(row2, bindings.ActiveOpacityCaption, slider2);

        if (slider1 is null || slider2 is null)
            return;

        float initialDefault = bindings.CurrentDefaultOpacity();
        float initialActive = bindings.CurrentActiveOpacity();
        slider1.SetScalarPosition(initialDefault);
        slider2.SetScalarPosition(initialActive);

        FloatOptionRow? defaultRow = null;
        FloatOptionRow? activeRow = null;

        defaultRow = new FloatOptionRow(
            initialDefault,
            bindings.DefaultOpacityDatDefault,
            apply: value =>
            {
                bindings.SetDefaultOpacity(value);
                slider1.SetScalarPosition(bindings.CurrentDefaultOpacity());
                activeRow!.RefreshFromLink(bindings.CurrentActiveOpacity());
                if (!slider1.IsDragging) bindings.FlushOpacity(); // S1: settle now unless mid-drag
            },
            read: bindings.CurrentDefaultOpacity,
            refresh: value => slider1.SetScalarPosition(value));

        activeRow = new FloatOptionRow(
            initialActive,
            bindings.ActiveOpacityDatDefault,
            apply: value =>
            {
                bindings.SetActiveOpacity(value);
                slider2.SetScalarPosition(bindings.CurrentActiveOpacity());
                defaultRow!.RefreshFromLink(bindings.CurrentDefaultOpacity());
                if (!slider2.IsDragging) bindings.FlushOpacity(); // S1: settle now unless mid-drag
            },
            read: bindings.CurrentActiveOpacity,
            refresh: value => slider2.SetScalarPosition(value));

        slider1.ScalarChanged = value => defaultRow.SetCurrentValue(value);
        slider2.ScalarChanged = value => activeRow.SetCurrentValue(value);

        // S1: the drag-end seam — flushes whatever the tick loop above deferred.
        slider1.DragCompleted = bindings.FlushOpacity;
        slider2.DragCompleted = bindings.FlushOpacity;

        page.Register(defaultRow);
        page.Register(activeRow);
    }

    private static void SetRangeLabel(
        UiElement row, uint elementId, string labelKey, Func<uint, uint, string?> resolveString)
    {
        if (UiElement.FindDescendant(row, elementId) is not UiText text)
            return;
        string? label = resolveString(StringTableId, DatStringResolver.ComputeHash(labelKey));
        if (label is null)
        {
            Console.WriteLine(
                $"[UI] ChatOptionsPageController: range label '{labelKey}' did not "
                + "resolve — rendered with no text rather than invented English.");
            return;
        }
        text.LinesProvider = () => new[] { new UiText.Line(label, text.DefaultColor) };
    }

    private static void SetOpacityCaption(
        UiElement row, ChatOptionsDatCaptions.Caption caption, UiScrollbar? slider)
    {
        if (UiElement.FindDescendant(row, SliderLabelElementId) is UiText text)
        {
            if (caption.Name is { Length: > 0 } name)
                text.LinesProvider = () => new[] { new UiText.Line(name, text.DefaultColor) };
            else
                Console.WriteLine(
                    "[UI] ChatOptionsPageController: opacity slider caption did not "
                    + "resolve from the DAT name/tooltip catalog — the row renders with no "
                    + "text rather than invented English.");
        }

        if (slider is not null && caption.Tooltip is { Length: > 0 } tooltip)
            slider.TooltipText = tooltip;
    }

    private static void BuildFilterBlock(
        UiTemplateListBox listBox,
        FilterBlockSpec spec,
        OptionPage page,
        Func<uint, uint, UiElement?> templateResolver,
        Func<uint, uint, string?> resolveString,
        Bindings bindings)
    {
        if (BitfieldTemplateIndex >= listBox.Templates.Count)
        {
            Console.WriteLine(
                $"[UI] ChatOptionsPageController: bitfield template index "
                + $"{BitfieldTemplateIndex} missing from the Chat ListBox's authored "
                + $"template list — window {spec.RetailWindowId}'s filter block was not built.");
            return;
        }

        UiTemplateListEntry entry = listBox.Templates[BitfieldTemplateIndex];
        UiElement? built = templateResolver(entry.TemplateLayoutId, entry.TemplateElementId);
        if (built is not UiCheckboxBitfield64 block)
        {
            Console.WriteLine(
                $"[UI] ChatOptionsPageController: filter block template did not build as "
                + $"UiCheckboxBitfield64 for window {spec.RetailWindowId}.");
            return;
        }

        block.TemplateResolver = templateResolver;

        (ulong defaultLow, ulong defaultHigh) = SplitMask(spec.DefaultFilter);
        block.SetDefaultValue(defaultLow, defaultHigh);

        for (int i = 0; i < FilterRows.Length; i++)
        {
            if (i == 0 && !spec.IncludesGameplayRow)
                continue; // research doc §5.2: the main window has no Gameplay row.

            FilterRowSpec rowSpec = FilterRows[i];
            string? label = resolveString(FilterStringTableId, DatStringResolver.ComputeHash(rowSpec.RetailLabelKey));
            string? tooltip = resolveString(
                FilterStringTableId, DatStringResolver.ComputeHash(rowSpec.RetailLabelKey + "_Desc"));
            if (label is null)
                Console.WriteLine(
                    $"[UI] ChatOptionsPageController: filter row label "
                    + $"'{rowSpec.RetailLabelKey}' did not resolve — row renders with no "
                    + "caption rather than invented English.");

            (ulong low, ulong high) = SplitMask(rowSpec.Mask);
            if (block.AddChild(low, high, label ?? string.Empty, tooltip) is null)
                Console.WriteLine(
                    $"[UI] ChatOptionsPageController: filter row '{rowSpec.RetailLabelKey}' "
                    + $"did not build for window {spec.RetailWindowId}.");
        }

        listBox.AddPrebuiltRow(block);

        ulong initial = bindings.CurrentFilter(spec.CompactWindowId);
        (ulong initialLow, ulong initialHigh) = SplitMask(initial);
        block.SetCurrentValue(initialLow, initialHigh); // live seed, not the window default

        var row = new BitfieldOptionRow(
            initial,
            spec.DefaultFilter,
            apply: value =>
            {
                (ulong low, ulong high) = SplitMask(value);
                block.SetCurrentValue(low, high);
                bindings.SetFilter(spec.CompactWindowId, value);
            },
            read: () => bindings.CurrentFilter(spec.CompactWindowId),
            refresh: value =>
            {
                (ulong low, ulong high) = SplitMask(value);
                block.SetCurrentValue(low, high);
            });

        block.ValueChanged = (low, high) => row.SetCurrentValue(CombineMask(low, high));

        page.Register(row);
    }

    private static (ulong Low, ulong High) SplitMask(ulong combined)
        => (combined & 0xFFFFFFFFul, (combined >> 32) & 0xFFFFFFFFul);

    private static ulong CombineMask(ulong low, ulong high)
        => ((high & 0xFFFFFFFFul) << 32) | (low & 0xFFFFFFFFul);
}
