namespace AcDream.App.UI.Layout;

internal sealed class RetailWaitDialogView : IRetailDialogView
{
    public const uint RootElementId = 0x31u;
    public const uint PopupElementId = 0x3Du;
    public const uint MessageElementId = 0x3Eu;

    private readonly UiRoot _host;
    private readonly UiElement _popup;
    private readonly UiText _message;
    private readonly float _basePopupHeight;
    private readonly float _baseMessageHeight;

    public RetailWaitDialogView(
        UiRoot host,
        ImportedLayout layout,
        RetailDialogData data)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(data);

        Root = layout.Root as UiDialogRoot
            ?? throw new ArgumentException("Wait layout root is not a UiDialogRoot.", nameof(layout));
        _popup = layout.FindElement(PopupElementId)
            ?? throw new ArgumentException("Wait layout is missing popup element 0x3D.", nameof(layout));
        _message = layout.FindElement(MessageElementId) as UiText
            ?? throw new ArgumentException("Wait layout is missing text element 0x3E.", nameof(layout));

        _basePopupHeight = _popup.Height;
        _baseMessageHeight = _message.Height;
        _popup.LayoutPolicy = null;
        _popup.Anchors = AnchorEdges.None;
        _message.LayoutPolicy = null;
        _message.Anchors = AnchorEdges.None;
        _message.Padding = 0f;
        _message.Selectable = false;

        SetMessage(data.GetString(RetailDialogProperty.Message) ?? string.Empty);
        SizeAndCenter();
    }

    public UiDialogRoot Root { get; }

    public void Tick() => SizeAndCenter();

    public void SetPendingCount(int count)
    {
    }

    public void DetachHandlers()
    {
    }

    private void SetMessage(string text)
    {
        float maximumWidth = Math.Max(1f, _message.Width - 2f * _message.Padding);
        Func<string, float> measure = _message.DatFont is { } font
            ? font.MeasureWidth
            : static value => value.Length * 8f;
        IReadOnlyList<string> wrapped = UiText.WrapWords(text, measure, maximumWidth);
        var lines = new UiText.Line[wrapped.Count];
        for (int i = 0; i < wrapped.Count; i++)
            lines[i] = new UiText.Line(wrapped[i], _message.DefaultColor);
        _message.LinesProvider = () => lines;

        float lineHeight = _message.DatFont?.LineHeight ?? 16f;
        _message.Height = Math.Max(_baseMessageHeight, lines.Length * lineHeight);
        _popup.Height = _basePopupHeight + (_message.Height - _baseMessageHeight);
    }

    private void SizeAndCenter()
    {
        var space = _host.EffectiveCanvasSize;
        Root.Left = 0f;
        Root.Top = 0f;
        Root.Width = space.X;
        Root.Height = space.Y;
        _popup.Left = MathF.Round((Root.Width - _popup.Width) * 0.5f);
        _popup.Top = MathF.Round((Root.Height - _popup.Height) * 0.5f);
    }
}
