using System.Numerics;

namespace AcDream.UI.Abstractions.Tests;

internal sealed class FakePanelRenderer : IPanelRenderer
{
    /// <summary>Ordered list of (method, args) pairs recorded across this renderer's lifetime.</summary>
    public List<(string Method, object?[] Args)> Calls { get; } = new();

    public bool BeginReturns { get; set; } = true;

    // -- Inputs the panel under test can mutate via ref args -----------

    public bool CheckboxNextReturn { get; set; }
    public bool? CheckboxNextValue { get; set; }

    public bool ButtonNextReturn { get; set; }

    public bool ComboNextReturn { get; set; }
    public int? ComboNextSelectedIndex { get; set; }

    public bool SliderFloatNextReturn { get; set; }
    public float? SliderFloatNextValue { get; set; }

    public bool CollapsingHeaderNextReturn { get; set; } = true;

    public bool TreeNodeNextReturn { get; set; } = true;

    public bool BeginTableNextReturn { get; set; } = true;

    public bool BeginChildNextReturn { get; set; } = true;

    public float FrameHeightWithSpacingValue { get; set; } = 24f;

    public string? InputTextSubmitNextSubmitted { get; set; }
    public string? InputTextSubmitNextBufferAfter { get; set; }

    public bool MainMenuBarReturns { get; set; } = true;
    public bool MenuReturns { get; set; } = true;
    public bool MenuItemReturns { get; set; }

    public bool Begin(string title)
    {
        Calls.Add(("Begin", new object?[] { title }));
        return BeginReturns;
    }

    public void End() => Calls.Add(("End", Array.Empty<object?>()));

    public void Text(string text) => Calls.Add(("Text", new object?[] { text }));

    public void SameLine() => Calls.Add(("SameLine", Array.Empty<object?>()));

    public void Separator() => Calls.Add(("Separator", Array.Empty<object?>()));

    public void ProgressBar(float fraction, float width, string? overlay = null)
        => Calls.Add(("ProgressBar", new object?[] { fraction, width, overlay }));

    public void TextColored(Vector4 rgba, string text)
        => Calls.Add(("TextColored", new object?[] { rgba, text }));

    public bool CollapsingHeader(string label, bool defaultOpen = true)
    {
        Calls.Add(("CollapsingHeader", new object?[] { label, defaultOpen }));
        return CollapsingHeaderNextReturn;
    }

    public bool TreeNode(string label)
    {
        Calls.Add(("TreeNode", new object?[] { label }));
        return TreeNodeNextReturn;
    }

    public void TreePop() => Calls.Add(("TreePop", Array.Empty<object?>()));

    public bool Checkbox(string label, ref bool value)
    {
        Calls.Add(("Checkbox", new object?[] { label, value }));
        if (CheckboxNextValue is bool nv) value = nv;
        return CheckboxNextReturn;
    }

    public bool Button(string label)
    {
        Calls.Add(("Button", new object?[] { label }));
        return ButtonNextReturn;
    }

    public bool Combo(string label, ref int selectedIndex, string[] items)
    {
        Calls.Add(("Combo", new object?[] { label, selectedIndex, items }));
        if (ComboNextSelectedIndex is int idx) selectedIndex = idx;
        return ComboNextReturn;
    }

    public bool SliderFloat(string label, ref float value, float min, float max)
    {
        Calls.Add(("SliderFloat", new object?[] { label, value, min, max }));
        if (SliderFloatNextValue is float v) value = v;
        return SliderFloatNextReturn;
    }

    public void PlotLines(
        string label,
        float[] values,
        int count,
        int offset = 0,
        string? overlay = null,
        float? min = null,
        float? max = null,
        Vector2? size = null)
        => Calls.Add(("PlotLines", new object?[] { label, values, count, offset, overlay, min, max, size }));

    public void BeginTable(string id, int columns)
        => Calls.Add(("BeginTable", new object?[] { id, columns }));

    public void TableNextColumn() => Calls.Add(("TableNextColumn", Array.Empty<object?>()));

    public void EndTable() => Calls.Add(("EndTable", Array.Empty<object?>()));

    public bool InputTextSubmit(string label, ref string buffer, int maxLen, out string? submitted)
    {
        Calls.Add(("InputTextSubmit", new object?[] { label, buffer, maxLen }));
        submitted = InputTextSubmitNextSubmitted;
        if (submitted is not null)
            buffer = InputTextSubmitNextBufferAfter ?? string.Empty;
        return submitted is not null;
    }

    public void Spacing() => Calls.Add(("Spacing", Array.Empty<object?>()));

    public void Dummy(Vector2 size) => Calls.Add(("Dummy", new object?[] { size }));

    public void TextWrapped(string text) => Calls.Add(("TextWrapped", new object?[] { text }));

    public bool BeginChild(string id, Vector2 size, bool border = false)
    {
        Calls.Add(("BeginChild", new object?[] { id, size, border }));
        return BeginChildNextReturn;
    }

    public void EndChild() => Calls.Add(("EndChild", Array.Empty<object?>()));

    public float FrameHeightWithSpacing()
    {
        Calls.Add(("FrameHeightWithSpacing", Array.Empty<object?>()));
        return FrameHeightWithSpacingValue;
    }

    public void SetScrollHereY(float ratio)
        => Calls.Add(("SetScrollHereY", new object?[] { ratio }));

    public void SetKeyboardFocusHere()
        => Calls.Add(("SetKeyboardFocusHere", Array.Empty<object?>()));


    public bool BeginMainMenuBar()
    {
        Calls.Add(("BeginMainMenuBar", Array.Empty<object?>()));
        return MainMenuBarReturns;
    }

    public void EndMainMenuBar()
        => Calls.Add(("EndMainMenuBar", Array.Empty<object?>()));

    public bool BeginMenu(string label)
    {
        Calls.Add(("BeginMenu", new object?[] { label }));
        return MenuReturns;
    }

    public void EndMenu()
        => Calls.Add(("EndMenu", Array.Empty<object?>()));

    public bool MenuItem(string label, string? shortcut = null)
    {
        Calls.Add(("MenuItem", new object?[] { label, shortcut }));
        return MenuItemReturns;
    }

    // -- Tab bar -----------------------------------------------------------

    public bool TabBarReturns { get; set; } = true;

    public string? ActiveTabLabel { get; set; }

    private string? _firstTabSeen;

    public bool BeginTabBar(string id)
    {
        Calls.Add(("BeginTabBar", new object?[] { id }));
        _firstTabSeen = null;
        return TabBarReturns;
    }

    public void EndTabBar() => Calls.Add(("EndTabBar", Array.Empty<object?>()));

    public bool BeginTabItem(string label)
    {
        Calls.Add(("BeginTabItem", new object?[] { label }));
        _firstTabSeen ??= label;
        return ActiveTabLabel is null
            ? string.Equals(label, _firstTabSeen, StringComparison.Ordinal)
            : string.Equals(label, ActiveTabLabel, StringComparison.Ordinal);
    }

    public void EndTabItem() => Calls.Add(("EndTabItem", Array.Empty<object?>()));

    public void TextMultilineReadOnly(string id, string content, Vector2 size)
        => Calls.Add(("TextMultilineReadOnly", new object?[] { id, content, size }));
}
