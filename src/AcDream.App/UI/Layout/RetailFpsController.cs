using System.Globalization;

namespace AcDream.App.UI.Layout;

public sealed class RetailFpsController
{
    public const uint LayoutId = 0x2100000Fu;
    public const uint DisplayElementId = 0x10000047u;

    private readonly UiText _display;
    private readonly Func<double> _framesPerSecond;
    private readonly Func<double> _degradeMultiplier;
    private readonly Func<bool> _isVisible;

    private RetailFpsController(
        UiText display,
        Func<double> framesPerSecond,
        Func<double> degradeMultiplier,
        Func<bool> isVisible)
    {
        _display = display;
        _framesPerSecond = framesPerSecond;
        _degradeMultiplier = degradeMultiplier;
        _isVisible = isVisible;

        _display.Left = 1f;
        _display.Top = 1f;
        _display.Padding = 1f;
        _display.Selectable = false;
        _display.LinesProvider = BuildLines;
        Tick();
    }

    public UiText Display => _display;

    public static RetailFpsController? Bind(
        ImportedLayout layout,
        Func<double> framesPerSecond,
        Func<double> degradeMultiplier,
        Func<bool> isVisible)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(framesPerSecond);
        ArgumentNullException.ThrowIfNull(degradeMultiplier);
        ArgumentNullException.ThrowIfNull(isVisible);

        return layout.FindElement(DisplayElementId) is UiText display
            ? new RetailFpsController(display, framesPerSecond, degradeMultiplier, isVisible)
            : null;
    }

    public void Tick() => _display.Visible = _isVisible();

    private IReadOnlyList<UiText.Line> BuildLines()
    {
        string fps = _framesPerSecond().ToString("F2", CultureInfo.InvariantCulture);
        string degrade = _degradeMultiplier().ToString("F2", CultureInfo.InvariantCulture);
        return
        [
            new UiText.Line($"FPS: {fps}", _display.DefaultColor),
            new UiText.Line($"DEG: {degrade}", _display.DefaultColor),
        ];
    }
}
