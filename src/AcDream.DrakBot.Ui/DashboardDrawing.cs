using System.Numerics;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// The dashboard's palette and drawing primitives, as RynthAi draws them:
/// the launcher grid button with its line icon, the square and wide
/// toggles, the segmented target bar, the vital rows, and the icons
/// themselves (a handful of shapes on the draw list, no textures).
/// </summary>
public static class DashboardDrawing
{
    public static readonly Vector4 ColTeal = new(0.15f, 0.85f, 0.90f, 1.00f);
    public static readonly Vector4 ColAmber = new(0.91f, 0.70f, 0.20f, 1.00f);
    public static readonly Vector4 ColGreen = new(0.25f, 0.85f, 0.45f, 1.00f);
    public static readonly Vector4 ColTextDim = new(0.85f, 0.90f, 0.95f, 1.00f);
    public static readonly Vector4 ColTextMute = new(0.55f, 0.65f, 0.75f, 1.00f);
    public static readonly Vector4 ColHp = new(0.85f, 0.20f, 0.20f, 1.00f);
    public static readonly Vector4 ColMana = new(0.15f, 0.55f, 0.95f, 1.00f);
    public static readonly Vector4 ColBarBg = new(0.08f, 0.12f, 0.16f, 1.00f);
    public static readonly Vector4 ColPanelBg = new(0.04f, 0.07f, 0.10f, 0.95f);
    public static readonly Vector4 ColBtnOn = new(0.15f, 0.30f, 0.35f, 1.00f);
    public static readonly Vector4 ColBtnFill = new(0.06f, 0.12f, 0.18f, 1.00f);
    public static readonly Vector4 ColBtnHov = new(0.10f, 0.18f, 0.25f, 1.00f);
    public static readonly Vector4 ColBtnAct = new(0.08f, 0.15f, 0.22f, 1.00f);
    public static readonly Vector4 ColBtnBord = new(0.15f, 0.25f, 0.35f, 1.00f);

    /// <summary>A launcher button: icon and label, teal when its window is open. True when clicked.</summary>
    public static bool GridButton(string label, string icon, bool on)
    {
        const float h = 30f;
        ImGui.PushStyleColor(ImGuiCol.Button, ColBtnFill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColBtnHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColBtnAct);
        ImGui.PushStyleColor(ImGuiCol.Border, on ? ColTeal : ColBtnBord);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        Vector2 start = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Button($"##{label}", new Vector2(-1f, h));
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
        float w = ImGui.GetItemRectSize().X;
        Vector2 text = ImGui.CalcTextSize(label);
        DrawIcon(icon, start + new Vector2(8, 6), on ? ColTeal : ColTextMute, 16f);
        ImGui.SetCursorScreenPos(start + new Vector2(30, (h - text.Y) / 2));
        ImGui.TextColored(on ? ColTeal : ColTextDim, label);
        ImGui.SetCursorScreenPos(start + new Vector2(0, h + 2));
        ImGui.Dummy(new Vector2(w, 0));
        return clicked;
    }

    /// <summary>A 30 px square toggle with an icon; true when clicked (left button).</summary>
    public static bool SquareToggle(string icon, bool state, Vector2 pos, string id)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        const float s = 30f;
        Vector4 bg = state ? ColBtnOn : ColBarBg;
        Vector4 iconCol = state ? ColTeal : ColTextMute;
        dl.AddRectFilled(pos, pos + new Vector2(s, s), ImGui.ColorConvertFloat4ToU32(bg), 4f);
        if (state)
            dl.AddRect(pos, pos + new Vector2(s, s), ImGui.ColorConvertFloat4ToU32(ColTeal), 4f, ImDrawFlags.None, 1f);
        DrawIcon(icon, pos + new Vector2(6, 6), iconCol, 18f);
        ImGui.SetCursorScreenPos(pos);
        return ImGui.InvisibleButton($"##{id}", new Vector2(s, s));
    }

    /// <summary>A wide toggle with an icon and a label; true when clicked.</summary>
    public static bool WideToggle(string label, string icon, bool state, Vector2 pos, string id, float width, float height)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector4 bg = state ? ColBtnOn : ColBarBg;
        Vector4 iconCol = state ? ColTeal : ColTextMute;
        dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(bg), 4f);
        if (state)
            dl.AddRect(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(ColTeal), 4f, ImDrawFlags.None, 1f);
        DrawIcon(icon, pos + new Vector2(4, (height - 12) / 2), iconCol, 12f);
        Vector2 text = ImGui.CalcTextSize(label);
        dl.AddText(pos + new Vector2(18, (height - text.Y) / 2), ImGui.ColorConvertFloat4ToU32(state ? ColTeal : ColTextDim), label);
        ImGui.SetCursorScreenPos(pos);
        return ImGui.InvisibleButton($"##{id}", new Vector2(width, height));
    }

    /// <summary>Fifteen segments, red through yellow to green, lit up to the fraction.</summary>
    public static void SegmentedBar(float pct, float width)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        const float h = 10f, gap = 2f;
        const int segments = 15;
        float sw = (width - (segments - 1) * gap) / segments;
        for (int i = 0; i < segments; i++)
        {
            float t = (float)i / (segments - 1);
            Vector4 col = i / (float)segments <= pct ? GradientColor(t) : ColBarBg;
            dl.AddRectFilled(pos + new Vector2(i * (sw + gap), 0), pos + new Vector2(i * (sw + gap) + sw, h), ImGui.ColorConvertFloat4ToU32(col), 1f);
        }
        ImGui.Dummy(new Vector2(width, h + 2));
    }

    public static void CompactVitalBar(string label, float pct, Vector4 color, string valText, float width)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        const float h = 10f;
        float safePct = Math.Clamp(pct, 0f, 1f);
        dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.ColorConvertFloat4ToU32(ColBarBg), 3f);
        dl.AddRectFilled(pos, pos + new Vector2(width * safePct, h), ImGui.ColorConvertFloat4ToU32(color), 3f);
        dl.AddText(pos + new Vector2(4, -2), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.9f)), $"{label} {valText}");
        ImGui.Dummy(new Vector2(width, h + 1));
    }

    public static void VitalRow(string icon, string label, float pct, Vector4 color, string valText)
    {
        float width = ImGui.GetContentRegionAvail().X - 4f;
        Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        const float h = 14f;
        dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.ColorConvertFloat4ToU32(ColBarBg), 4f);
        float safePct = Math.Clamp(pct, 0f, 1f);
        dl.AddRectFilled(pos, pos + new Vector2(width * safePct, h), ImGui.ColorConvertFloat4ToU32(color), 4f);
        DrawIcon(icon, pos + new Vector2(4, 0), ColTextDim, 13f);
        dl.AddText(pos + new Vector2(22, -1), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), $"{label}: {(int)(safePct * 100)}% ({valText})");
        ImGui.Dummy(new Vector2(width, h + 1));
    }

    private static Vector4 GradientColor(float t)
    {
        if (t < 0.33f)
        {
            float f = t / 0.33f;
            return new Vector4(1f, f, 0f, 1f);
        }
        if (t < 0.66f)
        {
            float f = (t - 0.33f) / 0.33f;
            return new Vector4(1f - f, 1f, 0f, 1f);
        }
        float g = (t - 0.66f) / 0.34f;
        return new Vector4(0f, 1f, g, 1f);
    }

    public static void DrawIcon(string type, Vector2 pos, Vector4 color, float s)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        uint col = ImGui.ColorConvertFloat4ToU32(color);
        switch (type)
        {
            case "gear":
                dl.AddCircle(pos + new Vector2(s / 2, s / 2), s / 3.5f, col, 12, 1.5f);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * (MathF.PI * 2f / 8f);
                    dl.AddLine(pos + new Vector2(s / 2 + MathF.Cos(a) * s / 3, s / 2 + MathF.Sin(a) * s / 3), pos + new Vector2(s / 2 + MathF.Cos(a) * s / 2, s / 2 + MathF.Sin(a) * s / 2), col, 2f);
                }
                break;
            case "target":
                dl.AddCircle(pos + new Vector2(s / 2, s / 2), s / 2.5f, col, 12, 1.5f);
                dl.AddLine(pos + new Vector2(s / 2, 0), pos + new Vector2(s / 2, s), col, 1.5f);
                dl.AddLine(pos + new Vector2(0, s / 2), pos + new Vector2(s, s / 2), col, 1.5f);
                break;
            case "wrench":
                dl.AddCircle(pos + new Vector2(s * 0.3f, s * 0.3f), s * 0.25f, col, 8, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.4f, s * 0.4f), pos + new Vector2(s * 0.9f, s * 0.9f), col, 2f);
                break;
            case "map":
                dl.AddRect(pos, pos + new Vector2(s, s), col, 1.5f);
                dl.AddLine(pos + new Vector2(s / 2, 2), pos + new Vector2(s / 2, s - 2), col, 1.5f);
                dl.AddLine(pos + new Vector2(2, s / 2), pos + new Vector2(s - 2, s / 2), col, 1.5f);
                break;
            case "bag":
                dl.AddRectFilled(pos + new Vector2(2, s / 3), pos + new Vector2(s - 2, s), col, 2f);
                dl.AddCircle(pos + new Vector2(s / 2, s / 4), s / 5, col, 8, 1.5f);
                break;
            case "shield":
                dl.AddLine(pos + new Vector2(s * 0.2f, 0), pos + new Vector2(s * 0.8f, 0), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.8f, 0), pos + new Vector2(s * 0.8f, s * 0.6f), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.8f, s * 0.6f), pos + new Vector2(s * 0.5f, s), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.5f, s), pos + new Vector2(s * 0.2f, s * 0.6f), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.2f, s * 0.6f), pos + new Vector2(s * 0.2f, 0), col, 1.5f);
                break;
            case "code":
                dl.AddLine(pos + new Vector2(s * 0.3f, 2), pos + new Vector2(0, s / 2), col, 1.5f);
                dl.AddLine(pos + new Vector2(0, s / 2), pos + new Vector2(s * 0.3f, s - 2), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.7f, 2), pos + new Vector2(s, s / 2), col, 1.5f);
                dl.AddLine(pos + new Vector2(s, s / 2), pos + new Vector2(s * 0.7f, s - 2), col, 1.5f);
                break;
            case "heart":
                dl.AddCircleFilled(pos + new Vector2(s * 0.3f, s * 0.3f), s * 0.25f, col);
                dl.AddCircleFilled(pos + new Vector2(s * 0.7f, s * 0.3f), s * 0.25f, col);
                dl.AddTriangleFilled(pos + new Vector2(s * 0.05f, s * 0.4f), pos + new Vector2(s * 0.95f, s * 0.4f), pos + new Vector2(s * 0.5f, s * 0.9f), col);
                break;
            case "run":
                dl.AddCircle(pos + new Vector2(s * 0.5f, s * 0.2f), s * 0.15f, col, 8, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.35f), pos + new Vector2(s * 0.5f, s * 0.65f), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.65f), pos + new Vector2(s * 0.2f, s * 0.9f), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.65f), pos + new Vector2(s * 0.8f, s * 0.9f), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.2f, s * 0.4f), pos + new Vector2(s * 0.8f, s * 0.4f), col, 1.5f);
                break;
            case "drop":
                dl.AddCircleFilled(pos + new Vector2(s * 0.5f, s * 0.7f), s * 0.25f, col);
                dl.AddTriangleFilled(pos + new Vector2(s * 0.25f, s * 0.65f), pos + new Vector2(s * 0.75f, s * 0.65f), pos + new Vector2(s * 0.5f, s * 0.1f), col);
                break;
            case "sword":
                dl.AddLine(pos + new Vector2(s * 0.3f, s * 0.7f), pos + new Vector2(s * 0.9f, s * 0.1f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.2f, s * 0.6f), pos + new Vector2(s * 0.4f, s * 0.8f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.1f, s * 0.9f), pos + new Vector2(s * 0.3f, s * 0.7f), col, 2f);
                break;
            case "shoe":
                dl.AddLine(pos + new Vector2(s * 0.3f, s * 0.2f), pos + new Vector2(s * 0.3f, s * 0.8f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.3f, s * 0.8f), pos + new Vector2(s * 0.8f, s * 0.8f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.8f, s * 0.8f), pos + new Vector2(s * 0.8f, s * 0.6f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.8f, s * 0.6f), pos + new Vector2(s * 0.5f, s * 0.5f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.5f), pos + new Vector2(s * 0.5f, s * 0.2f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.2f), pos + new Vector2(s * 0.3f, s * 0.2f), col, 2f);
                break;
            case "buff":
                dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.2f), pos + new Vector2(s * 0.5f, s * 0.8f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.2f, s * 0.5f), pos + new Vector2(s * 0.8f, s * 0.5f), col, 2f);
                dl.AddLine(pos + new Vector2(s * 0.3f, s * 0.3f), pos + new Vector2(s * 0.5f, s * 0.1f), col, 1.5f);
                dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.1f), pos + new Vector2(s * 0.7f, s * 0.3f), col, 1.5f);
                break;
        }
    }
}
