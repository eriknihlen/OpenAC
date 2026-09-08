namespace AcDream.App.UI;

public readonly record struct UiPixelRect(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;

    public static UiPixelRect FromPositionAndSize(int x, int y, int width, int height)
        => new(x, y, x + width - 1, y + height - 1);
}

public sealed class UiLayoutPolicy
{
    public uint LeftMode { get; }
    public uint TopMode { get; }
    public uint RightMode { get; }
    public uint BottomMode { get; }
    public UiPixelRect OriginalChild { get; private set; }
    public UiPixelRect OriginalParent { get; private set; }
    public bool PreserveCurrentWhenEmpty { get; set; }

    public UiLayoutPolicy(
        uint leftMode,
        uint topMode,
        uint rightMode,
        uint bottomMode,
        UiPixelRect originalChild,
        UiPixelRect originalParent)
    {
        ValidateMode(leftMode, nameof(leftMode));
        ValidateMode(topMode, nameof(topMode));
        ValidateMode(rightMode, nameof(rightMode));
        ValidateMode(bottomMode, nameof(bottomMode));
        LeftMode = leftMode;
        TopMode = topMode;
        RightMode = rightMode;
        BottomMode = bottomMode;
        OriginalChild = originalChild;
        OriginalParent = originalParent;
    }

    public void Rebase(UiPixelRect child, UiPixelRect parent)
    {
        OriginalChild = child;
        OriginalParent = parent;
    }

    public UiPixelRect Apply(UiPixelRect currentChild, UiPixelRect currentParent)
        => Apply(
            LeftMode,
            TopMode,
            RightMode,
            BottomMode,
            OriginalChild,
            OriginalParent,
            currentChild,
            currentParent,
            PreserveCurrentWhenEmpty);

    public static UiPixelRect Apply(
        uint leftMode,
        uint topMode,
        uint rightMode,
        uint bottomMode,
        UiPixelRect originalChild,
        UiPixelRect originalParent,
        UiPixelRect currentChild,
        UiPixelRect currentParent,
        bool preserveCurrentWhenEmpty = false)
    {
        ValidateMode(leftMode, nameof(leftMode));
        ValidateMode(topMode, nameof(topMode));
        ValidateMode(rightMode, nameof(rightMode));
        ValidateMode(bottomMode, nameof(bottomMode));

        int deltaX = currentParent.Width - originalParent.Width;
        int deltaY = currentParent.Height - originalParent.Height;
        double scaleX = originalParent.Width != 0
            ? (double)currentParent.Width / originalParent.Width
            : 0d;
        double scaleY = originalParent.Height != 0
            ? (double)currentParent.Height / originalParent.Height
            : 0d;

        int x0 = ApplyNear(leftMode, originalChild.X0, originalChild.Width,
            currentParent.Width, deltaX, scaleX);
        int y0 = ApplyNear(topMode, originalChild.Y0, originalChild.Height,
            currentParent.Height, deltaY, scaleY);
        int x1 = ApplyFar(rightMode, originalChild.X1, originalChild.Width,
            currentParent.Width, deltaX, scaleX);
        int y1 = ApplyFar(bottomMode, originalChild.Y1, originalChild.Height,
            currentParent.Height, deltaY, scaleY);

        if (currentChild.Width != 0 || currentChild.Height != 0 || preserveCurrentWhenEmpty)
        {
            if (leftMode == 0) x0 = currentChild.X0;
            if (topMode == 0) y0 = currentChild.Y0;
            if (rightMode == 0) x1 = currentChild.X1;
            if (bottomMode == 0) y1 = currentChild.Y1;
        }

        return new UiPixelRect(x0, y0, x1, y1);
    }

    private static int ApplyNear(
        uint mode,
        int originalEdge,
        int originalSize,
        int currentParentSize,
        int parentDelta,
        double scale)
        => mode switch
        {
            2 => originalEdge + parentDelta,
            3 => currentParentSize / 2 - originalSize / 2,
            4 => (int)(originalEdge * scale),
            _ => originalEdge,
        };

    private static int ApplyFar(
        uint mode,
        int originalEdge,
        int originalSize,
        int currentParentSize,
        int parentDelta,
        double scale)
        => mode switch
        {
            1 => originalEdge + parentDelta,
            3 => currentParentSize / 2 + originalSize / 2 - 1,
            4 => (int)(originalEdge * scale),
            _ => originalEdge,
        };

    private static void ValidateMode(uint mode, string parameterName)
    {
        if (mode > 4)
            throw new ArgumentOutOfRangeException(parameterName, mode, "Retail edge mode must be in 0..4.");
    }
}
