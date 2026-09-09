using System;
using System.Linq;
using AcDream.App.UI;

namespace AcDream.App.UI.Layout;

public sealed class RetailTooltipPresenter : IDisposable
{
    private readonly UiRoot _host;

    private readonly Func<uint, uint, ImportedLayout?> _createLayout;

    private UiElement? _popupRoot;
    private UiElement? _owner;
    private bool _disposed;

    public RetailTooltipPresenter(UiRoot host, Func<uint, uint, ImportedLayout?> createLayout)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _createLayout = createLayout ?? throw new ArgumentNullException(nameof(createLayout));
        _host.TooltipShow += OnTooltipShow;
        _host.TooltipHide += OnTooltipHide;
    }

    public bool Enabled { get; set; } = true;

    private static string? ResolveTooltipText(UiElement widget, out bool fromRuntime)
    {
        string? runtime = widget.GetTooltipText();
        if (!string.IsNullOrEmpty(runtime))
        {
            fromRuntime = true;
            return runtime;
        }
        fromRuntime = false;
        return widget.AuthoredTooltipText;
    }

    private void OnTooltipShow(UiElement widget)
    {
        RemovePopup();

        if (!Enabled)
            return;

        string? tooltipText = ResolveTooltipText(widget, out bool fromRuntime);
        if (string.IsNullOrEmpty(tooltipText))
            return;

        if (!fromRuntime && !widget.AuthoredTooltipEnabled)
            return;

        if (widget.AuthoredTooltipRootElementId == 0u)
            return;

        uint layoutDid = widget.AuthoredTooltipLayoutDid != 0u
            ? widget.AuthoredTooltipLayoutDid
            : widget.SourceLayoutDid;
        if (layoutDid == 0u)
            return;

        if (TryBuildAndMountPopup(widget.AuthoredTooltipRootElementId, layoutDid, tooltipText!))
            _owner = widget;
    }

    private bool TryBuildAndMountPopup(uint rootElementId, uint layoutDid, string tooltipText)
    {
        RemovePopup();

        ImportedLayout? layout;
        try
        {
            layout = _createLayout(layoutDid, rootElementId);
        }
        catch (Exception error)
        {
            Console.WriteLine(
                $"[UI] tooltip popup layout=0x{layoutDid:X8} "
                + $"root=0x{rootElementId:X8} failed to build: {error.Message}");
            return false;
        }
        if (layout is null)
            return false;

        UiElement root = layout.Root;
        UiElement? textChild = root.AuthoredTooltipTextChildElementId != 0u
            ? layout.FindElement(root.AuthoredTooltipTextChildElementId)
            : null;
        if (textChild is not UiText text)
            return false;

        root.LayoutPolicy = null;
        root.Anchors = AnchorEdges.None;
        text.LayoutPolicy = null;
        text.Anchors = AnchorEdges.None;

        ApplyTooltipText(root, text, tooltipText);

        SetClickThroughRecursive(root);
        PositionAtMouse(root);

        _host.AddChild(root);
        _host.BringToFront(root);
        _popupRoot = root;
        return true;
    }

    private void OnTooltipHide(UiElement widget)
    {
        if (ReferenceEquals(_owner, widget))
            RemovePopup();
    }

    private void RemovePopup()
    {
        if (_popupRoot is null)
            return;
        _host.RemoveChild(_popupRoot);
        _popupRoot = null;
        _owner = null;
        _worldTooltipShowing = false;
    }


    public const uint SharedPopupSkinRootElementId = 0x10000395u;
    public const uint SharedPopupSkinLayoutDid = 0x21000041u;

    private uint _worldHoverGuid;
    private bool _worldTooltipShowing;

    private string? _worldStagedText;

    private long _worldTooltipShownMs;

    private bool _worldRearmRequiresMouseMove;

    private int _worldLastSeenMouseX = int.MinValue;
    private int _worldLastSeenMouseY = int.MinValue;

    public Func<uint?>? WorldHoverGuidProvider { get; set; }

    public Func<uint, string?>? WorldHoverNameResolver { get; set; }

    public Func<bool>? WorldTooltipsEnabled { get; set; }

    private void UpdateWorldHoverTooltip()
    {
        if (WorldHoverGuidProvider is null)
            return;

        if (_host.MouseX != _worldLastSeenMouseX || _host.MouseY != _worldLastSeenMouseY)
        {
            _worldLastSeenMouseX = _host.MouseX;
            _worldLastSeenMouseY = _host.MouseY;
            _worldRearmRequiresMouseMove = false;
        }

        uint found = _host.Pick(_host.MouseX, _host.MouseY) is null
            ? WorldHoverGuidProvider() ?? 0u
            : 0u;

        if (found != _worldHoverGuid)
        {
            _worldHoverGuid = found;

            string? staged = _worldStagedText;
            if (found == 0u || WorldTooltipsEnabled?.Invoke() != true)
            {
                staged = null;
            }
            else
            {
                string? name = WorldHoverNameResolver?.Invoke(found);
                if (!string.IsNullOrEmpty(name))
                    staged = name;
            }

            if (!string.Equals(staged, _worldStagedText, StringComparison.Ordinal))
            {
                _worldStagedText = staged;
                if (_worldTooltipShowing)
                    RemovePopup();

                if (!string.IsNullOrEmpty(staged) && _host.DragSource is not null)
                {
                    if (TryBuildAndMountPopup(
                            SharedPopupSkinRootElementId, SharedPopupSkinLayoutDid, staged!))
                    {
                        _worldTooltipShowing = true;
                        _worldTooltipShownMs = _host.NowMs;
                    }
                    return;
                }
            }
        }

        if (_worldTooltipShowing)
        {
            if (_host.NowMs - _worldTooltipShownMs >= _host.TooltipDurationMs)
            {
                RemovePopup();
                _worldRearmRequiresMouseMove = true;
            }
            return;
        }

        if (string.IsNullOrEmpty(_worldStagedText))
            return;

        if (_host.Captured is not null)
            return;

        if (_worldRearmRequiresMouseMove)
            return;

        if (_host.MouseIdleMs < _host.TooltipDelayMs)
            return;

        if (!Enabled)
            return;

        if (TryBuildAndMountPopup(
                SharedPopupSkinRootElementId, SharedPopupSkinLayoutDid, _worldStagedText!))
        {
            _worldTooltipShowing = true;
            _worldTooltipShownMs = _host.NowMs;
        }
    }

    public void HideCurrent()
    {
        RemovePopup();
        _worldHoverGuid = 0u;
        _worldStagedText = null;
        _worldRearmRequiresMouseMove = false;
    }

    private void ApplyTooltipText(UiElement root, UiText text, string tooltipText)
    {
        float authoredTextWidth = text.Width;
        float authoredTextHeight = text.Height;
        text.Padding = 0f;

        Func<string, float> measure = text.DatFont is { } datFont
            ? datFont.MeasureWidth
            : text.Font is { } bitmapFont
                ? bitmapFont.MeasureWidth
                : static s => s.Length * 8f;

        float lineHeight = text.DatFont?.LineHeight ?? text.Font?.LineHeight ?? 14f;


        float marginsX = text.MarginLeft + text.MarginRight;
        float marginsY = text.MarginTop + text.MarginBottom;

        float wrapBound = text.AuthoredResizeMaxWidth is { } authoredMaxTextWidth
            ? MathF.Max(1f, authoredMaxTextWidth)
            : MathF.Max(1f, _host.EffectiveCanvasSize.X);
        float measureWrapWidth = MathF.Max(1f, wrapBound - marginsX);
        var measured = UiText.WrapWords(tooltipText, measure, measureWrapWidth);
        float measuredWidth =
            (measured.Count == 0 ? 0f : measured.Max(measure)) + marginsX;
        float measuredHeight = measured.Count * lineHeight + marginsY;

        float requestedWidth  = root.Width  + (measuredWidth  - authoredTextWidth);
        float requestedHeight = root.Height + (measuredHeight - authoredTextHeight);

        if (root.AuthoredResizeMaxHeight is { } maxHeight && requestedHeight > maxHeight)
            requestedHeight = maxHeight;
        if (root.AuthoredResizeMinHeight is { } minHeight && requestedHeight < minHeight)
            requestedHeight = minHeight;
        if (root.AuthoredResizeMaxWidth is { } maxWidth && requestedWidth > maxWidth)
            requestedWidth = maxWidth;
        if (root.AuthoredResizeMinWidth is { } minWidth && requestedWidth < minWidth)
            requestedWidth = minWidth;

        float authoredRootWidth  = root.Width;
        float authoredRootHeight = root.Height;
        root.Width  = requestedWidth;
        root.Height = requestedHeight;

        float textFinalWidth = MathF.Max(
            1f, authoredTextWidth + (requestedWidth - authoredRootWidth));
        if (text.AuthoredResizeMaxWidth is { } textMaxWidth)
            textFinalWidth = MathF.Min(textFinalWidth, textMaxWidth);
        float textFinalHeight =
            authoredTextHeight + (requestedHeight - authoredRootHeight);

        var wrapped = UiText.WrapWords(
            tooltipText, measure, MathF.Max(1f, textFinalWidth - marginsX));
        text.LinesProvider = () => wrapped
            .Select(line => new UiText.Line(line, text.DefaultColor))
            .ToArray();
        text.Width = wrapped.Count == 0
            ? 0f
            : MathF.Min(textFinalWidth, wrapped.Max(measure) + marginsX);
        float rewrappedHeight = wrapped.Count * lineHeight + marginsY;

        if (rewrappedHeight > textFinalHeight)
        {
            float grownHeight = root.Height + (rewrappedHeight - textFinalHeight);
            if (root.AuthoredResizeMaxHeight is { } maxH2 && grownHeight > maxH2)
                grownHeight = maxH2;
            if (root.AuthoredResizeMinHeight is { } minH2 && grownHeight < minH2)
                grownHeight = minH2;
            root.Height = grownHeight;
        }
        text.Height = rewrappedHeight;
    }

    private const float MouseOffsetPx = 32f;

    private void PositionAtMouse(UiElement root)
    {
        var canvas = _host.EffectiveCanvasSize;
        float x = Math.Clamp(_host.MouseX + MouseOffsetPx, 0, MathF.Max(0f, canvas.X - root.Width));
        float y = Math.Clamp(_host.MouseY + MouseOffsetPx, 0, MathF.Max(0f, canvas.Y - root.Height));
        root.Left = x;
        root.Top  = y;
    }

    private static void SetClickThroughRecursive(UiElement element)
    {
        element.ClickThrough = true;
        foreach (UiElement child in element.Children)
            SetClickThroughRecursive(child);
    }

    public void Tick()
    {
        if (_popupRoot is not null)
            _host.BringToFront(_popupRoot);

        UpdateWorldHoverTooltip();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.TooltipShow -= OnTooltipShow;
        _host.TooltipHide -= OnTooltipHide;
        RemovePopup();
    }
}
