namespace AcDream.App.UI.Layout;

internal sealed class RetailConfirmationDialogView : IRetailDialogView
{
    public const uint RootElementId = 0x15u;
    public const uint AcceptButtonId = 0x17u;
    public const uint RejectButtonId = 0x19u;
    public const uint PendingDisplayId = 0x33u;
    public const uint PendingDisplayTextId = 0x34u;
    public const uint PopupElementId = 0x3Du;
    public const uint MessageElementId = 0x3Eu;

    private readonly UiRoot _host;
    private readonly RetailDialogData _data;
    private readonly uint _context;
    private readonly Action<uint> _closeDialog;
    private readonly UiElement _popup;
    private readonly UiText _message;
    private readonly UiButton _accept;
    private readonly UiButton _reject;
    private readonly UiElement? _pendingDisplay;
    private readonly float _basePopupHeight;
    private readonly float _baseMessageHeight;

    public RetailConfirmationDialogView(
        UiRoot host,
        ImportedLayout layout,
        RetailDialogData data,
        uint context,
        Action<uint> closeDialog)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(layout);
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _context = context;
        _closeDialog = closeDialog ?? throw new ArgumentNullException(nameof(closeDialog));

        Root = layout.Root as UiDialogRoot
            ?? throw new ArgumentException("Confirmation layout root is not a UiDialogRoot.", nameof(layout));
        _popup = layout.FindElement(PopupElementId)
            ?? throw new ArgumentException("Confirmation layout is missing popup element 0x3D.", nameof(layout));
        _message = layout.FindElement(MessageElementId) as UiText
            ?? throw new ArgumentException("Confirmation layout is missing text element 0x3E.", nameof(layout));
        _accept = layout.FindElement(AcceptButtonId) as UiButton
            ?? throw new ArgumentException("Confirmation layout is missing accept button 0x17.", nameof(layout));
        _reject = layout.FindElement(RejectButtonId) as UiButton
            ?? throw new ArgumentException("Confirmation layout is missing reject button 0x19.", nameof(layout));
        _pendingDisplay = layout.FindElement(PendingDisplayId);

        _basePopupHeight = _popup.Height;
        _baseMessageHeight = _message.Height;
        _popup.LayoutPolicy = null;
        _popup.Anchors = AnchorEdges.None;
        _message.LayoutPolicy = null;
        _message.Anchors = AnchorEdges.None;
        _message.Padding = 0f;
        _message.Selectable = false;

        if (_data.GetString(RetailDialogProperty.AcceptLabel) is { } acceptLabel)
            _accept.Label = acceptLabel;
        if (_data.GetString(RetailDialogProperty.RejectLabel) is { } rejectLabel)
            _reject.Label = rejectLabel;

        Root.Cancel = Reject;
        _accept.OnClick = Accept;
        _reject.OnClick = Reject;
        SetMessage(_data.GetString(RetailDialogProperty.Message) ?? string.Empty);
        SizeAndCenter();
    }

    public UiDialogRoot Root { get; }

    public RetailDialogData Data => _data;

    public void Tick() => SizeAndCenter();

    public void SetPendingCount(int count)
    {
        if (_pendingDisplay is IUiDatStateful stateful)
            stateful.TrySetRetailState(count == 0 ? 0x19u : 0x18u);
    }

    public void DetachHandlers()
    {
        Root.Cancel = null;
        _accept.OnClick = null;
        _reject.OnClick = null;
    }

    private void Accept()
    {
        _data.Set(RetailDialogProperty.ConfirmationResult, true);
        _closeDialog(_context);
    }

    private void Reject()
    {
        _data.Set(RetailDialogProperty.ConfirmationResult, false);
        _closeDialog(_context);
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
