using System.Numerics;

namespace AcDream.UI.Abstractions;

public interface IPanelRenderer
{
    bool Begin(string title);

    void End();

    /// <summary>Draw a single line of text. No formatting / markdown.</summary>
    void Text(string text);

    /// <summary>Keep the next widget on the same line as the previous one.</summary>
    void SameLine();

    /// <summary>Horizontal rule separator.</summary>
    void Separator();

    void ProgressBar(float fraction, float width, string? overlay = null);


    void TextColored(Vector4 rgba, string text);

    bool CollapsingHeader(string label, bool defaultOpen = true);

    bool TreeNode(string label);

    void TreePop();

    /// <summary>
    /// A boolean toggle. Returns <c>true</c> on the frame the user
    /// flipped the box (and updates <paramref name="value"/> in place);
    /// returns <c>false</c> the rest of the time.
    /// </summary>
    bool Checkbox(string label, ref bool value);

    bool Button(string label);

    bool Combo(string label, ref int selectedIndex, string[] items);

    bool SliderFloat(string label, ref float value, float min, float max);

    void PlotLines(
        string label,
        float[] values,
        int count,
        int offset = 0,
        string? overlay = null,
        float? min = null,
        float? max = null,
        Vector2? size = null);

    void BeginTable(string id, int columns);

    void TableNextColumn();

    void EndTable();

    bool InputTextSubmit(string label, ref string buffer, int maxLen, out string? submitted);

    /// <summary>Insert a small vertical gap.</summary>
    void Spacing();

    /// <summary>Reserve invisible space of the given size, useful for
    /// layout-only padding.</summary>
    void Dummy(Vector2 size);

    void TextWrapped(string text);


    bool BeginChild(string id, Vector2 size, bool border = false);

    void EndChild();

    float FrameHeightWithSpacing();

    void SetScrollHereY(float ratio);

    void SetKeyboardFocusHere();


    bool BeginMainMenuBar();

    void EndMainMenuBar();

    bool BeginMenu(string label);

    void EndMenu();

    bool MenuItem(string label, string? shortcut = null);

    // -- Tab bar (Settings panel + future tabbed surfaces) ---------------

    bool BeginTabBar(string id);

    void EndTabBar();

    bool BeginTabItem(string label);

    void EndTabItem();

    void TextMultilineReadOnly(string id, string content, Vector2 size);
}
