namespace AcDream.App.UI.Layout;

internal sealed class RetailConfirmationTextInputDialogView : IRetailDialogView
{
    public const uint RootElementId = 0x2Cu;
    public const uint InputElementId = 0x2Cu;
    public const uint AcceptButtonId = 0x2Eu;
    public const uint RejectButtonId = 0x2Fu;
    public const uint PopupElementId = 0x3Du;
    public const uint MessageElementId = 0x3Eu;

    private readonly UiRoot _host;
    private readonly RetailDialogData _data;
    private readonly uint _context;
    private readonly Action<uint> _closeDialog;
    private readonly UiElement _popup;
    private readonly UiText _message;
    private readonly UiField _input;
    private readonly UiButton _accept;
    private readonly UiButton _reject;
    private readonly float _basePopupHeight;
    private readonly float _baseMessageHeight;
    private bool _focusPending = true;

    public RetailConfirmationTextInputDialogView(
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
            ?? throw new ArgumentException(
                "Confirmation-text-input layout root is not a UiDialogRoot.",
                nameof(layout));
        _popup = layout.FindElement(PopupElementId)
            ?? throw new ArgumentException(
                "Confirmation-text-input layout is missing popup element 0x3D.",
                nameof(layout));
        _message = layout.FindElement(MessageElementId) as UiText
            ?? throw new ArgumentException(
                "Confirmation-text-input layout is missing text element 0x3E.",
                nameof(layout));
        _input = layout.FindElement(InputElementId) as UiField
            ?? throw new ArgumentException(
                "Confirmation-text-input layout is missing input field 0x2C.",
                nameof(layout));
        _accept = layout.FindElement(AcceptButtonId) as UiButton
            ?? throw new ArgumentException(
                "Confirmation-text-input layout is missing accept button 0x2E.",
                nameof(layout));
        _reject = layout.FindElement(RejectButtonId) as UiButton
            ?? throw new ArgumentException(
                "Confirmation-text-input layout is missing reject button 0x2F.",
                nameof(layout));

        _basePopupHeight = _popup.Height;
        _baseMessageHeight = _message.Height;
        _popup.LayoutPolicy = null;
        _popup.Anchors = AnchorEdges.None;
        _message.LayoutPolicy = null;
        _message.Anchors = AnchorEdges.None;
        _message.Padding = 0f;
        _message.Selectable = false;
        _input.ClearOnSubmit = false;
        _input.RecordHistory = false;

        if (_data.GetString(RetailDialogProperty.TextInputAcceptLabel) is { } acceptLabel)
            _accept.Label = acceptLabel;
        if (_data.GetString(RetailDialogProperty.TextInputRejectLabel) is { } rejectLabel)
            _reject.Label = rejectLabel;

        Root.Cancel = Reject;
        _accept.OnClick = Accept;
        _reject.OnClick = Reject;
        _input.OnSubmit = _ => Accept();
        SetMessage(_data.GetString(RetailDialogProperty.Message) ?? string.Empty);
        SizeAndCenter();
    }

    public UiDialogRoot Root { get; }

    public void Tick()
    {
        SizeAndCenter();
        if (_focusPending && Root.Parent is not null)
        {
            _host.SetKeyboardFocus(_input);
            _focusPending = false;
        }
    }

    public void SetPendingCount(int count)
    {
    }

    public void DetachHandlers()
    {
        Root.Cancel = null;
        _accept.OnClick = null;
        _reject.OnClick = null;
        _input.OnSubmit = null;
    }

    private void Accept()
    {
        _data.Set(RetailDialogProperty.TextInputResult, _input.Text);
        _closeDialog(_context);
    }

    private void Reject()
    {
        _data.Set(RetailDialogProperty.TextInputResult, string.Empty);
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
