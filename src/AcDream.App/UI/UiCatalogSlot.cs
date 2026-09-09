using System;
using System.Numerics;
using AcDream.App.Rendering;

namespace AcDream.App.UI;

public sealed class UiCatalogSlot : UiItemSlot
{
    public uint EntryId { get; set; }
    public uint CatalogIconTexture { get; set; }
    public uint CatalogOverlayTexture { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public bool ShowLabel { get; init; }
    public uint BackgroundSprite { get; init; }
    public UiDatFont? LabelFont { get; init; }
    public Vector4 LabelColor { get; init; } = new(0.92f, 0.88f, 0.70f, 1f);

    public bool Outline { get; init; }

    public Vector4 OutlineColor { get; init; } = UiRenderContext.DefaultOutlineColor;
    public float IconLeft { get; init; }
    public float IconTop { get; init; }
    public float IconWidth { get; init; }
    public float IconHeight { get; init; }
    public float LabelLeft { get; init; }
    public float LabelWidth { get; init; }
    public bool SelectionBehindContent { get; init; }

    public new Action? Clicked { get; set; }
    public new Action? DoubleClicked { get; set; }
    public object? CatalogDragPayload { get; init; }
    public Action<object>? DragBegan { get; init; }
    public Action<object>? DragEnded { get; init; }
    public Action<object>? Dropped { get; init; }

    public Func<object, ItemDragAcceptance>? DragOverAcceptance { get; init; }

    protected override bool IsShortcutOccupied => EntryId != 0u;
    public override bool IsEmptySlot => EntryId == 0u;

    public override string? GetTooltipText() => string.IsNullOrWhiteSpace(Label) ? null : Label;

    public override bool IsDragSource => CatalogDragPayload is not null;
    public override object? GetDragPayload() => CatalogDragPayload;
    public override (uint tex, int w, int h)? GetDragGhost() =>
        CatalogDragPayload is not null && CatalogIconTexture != 0u
            ? (CatalogIconTexture, 32, 32)
            : null;

    internal override void SetDragSourceActive(bool active, object? payload)
    {
        if (!active && payload is not null) DragEnded?.Invoke(payload);
    }

    public override bool OnEvent(in UiEvent e)
    {
        switch (e.Type)
        {
            case UiEventType.MouseDown:
                if (EntryId != 0u
                    && FindList() is { PrimaryCatalogEntryPressed: { } pressed })
                    pressed(EntryId);
                return true;
            case UiEventType.Click:
                Clicked?.Invoke();
                return true;
            case UiEventType.DoubleClick:
                DoubleClicked?.Invoke();
                return true;
            case UiEventType.RightClick:
                if (EntryId == 0u
                    || FindList() is not { ExamineCatalogEntryRequested: { } examine })
                    return false;
                examine(EntryId);
                return true;
            case UiEventType.DragBegin:
                if (e.Payload is not null) DragBegan?.Invoke(e.Payload);
                return true;
            case UiEventType.DragEnter:
                SetDragAcceptVisual(e.Payload is { } enterPayload
                    ? DragOverAcceptance?.Invoke(enterPayload) switch
                    {
                        ItemDragAcceptance.Accept => DragAcceptState.Accept,
                        ItemDragAcceptance.Reject => DragAcceptState.Reject,
                        _ => DragAcceptState.None,
                    }
                    : DragAcceptState.None);
                return true;
            case UiEventType.DragOver:
                SetDragAcceptVisual(DragAcceptState.None);
                return true;
            case UiEventType.DropReleased:
                SetDragAcceptVisual(DragAcceptState.None);
                if (e.Payload is not null) Dropped?.Invoke(e.Payload);
                return true;
            default:
                return false;
        }
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (BackgroundSprite != 0u && SpriteResolve is not null)
        {
            var (texture, _, _) = SpriteResolve(BackgroundSprite);
            if (texture != 0u)
                ctx.DrawSprite(texture, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
        }

        if (SelectionBehindContent)
            DrawSelection(ctx);

        float iconWidth = IconWidth > 0f ? IconWidth : ShowLabel ? MathF.Min(32f, Height) : Width;
        float iconHeight = IconHeight > 0f ? IconHeight : ShowLabel ? MathF.Min(32f, Height) : Height;
        if (CatalogIconTexture != 0)
            ctx.DrawSprite(CatalogIconTexture, IconLeft, IconTop, iconWidth, iconHeight, 0f, 0f, 1f, 1f, Vector4.One);
        else if (SpriteResolve is not null && EmptySprite != 0)
        {
            var (texture, _, _) = SpriteResolve(EmptySprite);
            if (texture != 0)
                ctx.DrawSprite(texture, IconLeft, IconTop, iconWidth, iconHeight, 0f, 0f, 1f, 1f, Vector4.One);
        }

        if (CatalogOverlayTexture != 0)
            ctx.DrawSprite(CatalogOverlayTexture, IconLeft, IconTop, iconWidth, iconHeight, 0f, 0f, 1f, 1f, Vector4.One);

        DrawShortcutOverlay(ctx);

        if (!SelectionBehindContent)
            DrawSelection(ctx);

        if (ShowLabel)
        {
            float labelLeft = LabelLeft > 0f ? LabelLeft : IconLeft + iconWidth + 4f;
            float labelWidth = LabelWidth > 0f ? LabelWidth : MathF.Max(0f, Width - labelLeft);
            if (LabelFont is not null)
            {
                string text = FitText(Label, labelWidth, LabelFont.MeasureWidth);
                float y = MathF.Max(0f, (Height - LabelFont.LineHeight) * 0.5f);
                ctx.DrawStringDat(LabelFont, text, labelLeft, y, LabelColor, Outline, OutlineColor);
            }
            else
            {
                BitmapFont? font = ctx.DefaultFont;
                string text = font is null ? Label : FitText(Label, labelWidth, font.MeasureWidth);
                float y = font is null ? 3f : MathF.Max(0f, (Height - font.LineHeight) * 0.5f);
                ctx.DrawString(text, labelLeft, y, LabelColor, font);
            }
            if (!string.IsNullOrWhiteSpace(Detail))
                ctx.DrawString(Detail!, MathF.Max(labelLeft, Width - 48f), 3f, Vector4.One);
        }

        DrawDragAcceptOverlay(ctx);
    }

    private void DrawSelection(UiRenderContext ctx)
    {
        if (!Selected || SpriteResolve is null || SelectedSprite == 0u) return;
        var (texture, _, _) = SpriteResolve(SelectedSprite);
        if (texture != 0u)
            ctx.DrawSprite(texture, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
    }

    private static string FitText(string text, float width, Func<string, float> measure)
    {
        if (width <= 0f || string.IsNullOrEmpty(text) || measure(text) <= width)
            return text;

        const string ellipsis = "...";
        float ellipsisWidth = measure(ellipsis);
        if (ellipsisWidth >= width) return string.Empty;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (measure(text[..mid]) + ellipsisWidth <= width) low = mid;
            else high = mid - 1;
        }
        return text[..low] + ellipsis;
    }
}
