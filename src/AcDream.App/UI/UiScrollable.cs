using System;

namespace AcDream.App.UI;

public sealed class UiScrollable
{
    public int ContentHeight { get; set; }
    /// <summary>Visible viewport height in px.</summary>
    public int ViewHeight { get; set; }
    public int LineHeight { get; set; } = 16;

    private int _scrollY;
    public int ScrollY => _scrollY;

    public int MaxScroll => Math.Max(0, ContentHeight - ViewHeight);

    public bool HasOverflow => ContentHeight > ViewHeight;

    /// <summary>True when the offset is at (or past) the bottom — used for bottom-pin.</summary>
    public bool AtEnd => _scrollY >= MaxScroll;

    public void SetExtents(int contentHeight, int viewHeight, bool preserveEnd = false)
    {
        bool wasAtEnd = AtEnd;
        ContentHeight = Math.Max(0, contentHeight);
        ViewHeight = Math.Max(0, viewHeight);

        if (preserveEnd && wasAtEnd)
            ScrollToEnd();
        else
            SetScrollY(_scrollY);
    }

    public void SetScrollY(int y) => _scrollY = Math.Clamp(y, 0, MaxScroll);

    public void ScrollToEnd() => _scrollY = MaxScroll;

    public float ThumbRatio => ContentHeight <= 0 ? 1f : Math.Min(1f, (float)ViewHeight / ContentHeight);

    public float PositionRatio => MaxScroll <= 0 ? 0f : (float)_scrollY / MaxScroll;

    /// <summary>Inverse of PositionRatio — used when the user drags the thumb.</summary>
    public void SetPositionRatio(float ratio)
        => SetScrollY((int)MathF.Round(Math.Clamp(ratio, 0f, 1f) * MaxScroll));

    /// <summary>Scroll by whole lines (sign: +down/newer, -up/older).</summary>
    public void ScrollByLines(int lines) => SetScrollY(_scrollY + lines * LineHeight);

    public void ScrollByPage(int pages) => SetScrollY(_scrollY + pages * ViewHeight);
}
