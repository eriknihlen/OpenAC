using System;

namespace AcDream.App.UI.Layout;

public sealed class MiniGameUiController : IRetainedPanelController
{
    public const uint LayoutId = 0x2100001Eu;
    public const uint RootId = 0x1000016Au;
    public const uint CloseId = 0x1000016Bu;

    private readonly UiButton _close;

    private MiniGameUiController(UiButton close, Action? closeWindow)
    {
        _close = close;
        _close.OnClick = closeWindow;
    }

    public static MiniGameUiController? Bind(
        ImportedLayout layout,
        Action? close = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.FindElement(CloseId) is UiButton button
            ? new MiniGameUiController(button, close)
            : null;
    }

    public void Dispose() => _close.OnClick = null;
}
