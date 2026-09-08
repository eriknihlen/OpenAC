using AcDream.App.UI;

namespace AcDream.App.UI.Layout;

internal sealed class RetailConfirmationMenuDialogView : IRetailDialogView
{
    public const uint RootElementId = 0x1Fu;
    public const uint MenuElementId = 0x21u;
    public const uint AcceptButtonId = 0x22u;
    public const uint RejectButtonId = 0x23u;
    public const uint PopupElementId = 0x3Du;

    private readonly UiRoot _host;
    private readonly RetailDialogData _data;
    private readonly uint _context;
    private readonly Action<uint> _closeDialog;
    private readonly UiElement? _popup;
    private readonly UiMenu _menu;
    private readonly UiButton _accept;
    private readonly UiButton _reject;

    public RetailConfirmationMenuDialogView(
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
                "Confirmation-menu layout root is not a UiDialogRoot.", nameof(layout));
        _popup = layout.FindElement(PopupElementId);
        _menu = layout.FindElement(MenuElementId) as UiMenu
            ?? throw new ArgumentException(
                "Confirmation-menu layout is missing menu element 0x21.", nameof(layout));
        _accept = layout.FindElement(AcceptButtonId) as UiButton
            ?? throw new ArgumentException(
                "Confirmation-menu layout is missing accept button 0x22.", nameof(layout));
        _reject = layout.FindElement(RejectButtonId) as UiButton
            ?? throw new ArgumentException(
                "Confirmation-menu layout is missing reject button 0x23.", nameof(layout));

        IReadOnlyList<string> items = _data.TryGet<string[]>(
            RetailDialogProperty.MenuItems, out string[] values)
            ? values
            : Array.Empty<string>();
        _menu.Items = items.Select(
            static (label, index) => new UiMenu.MenuItem(label, index)).ToArray();
        int selected = Math.Clamp(
            _data.GetInt32(RetailDialogProperty.MenuSelection),
            0,
            Math.Max(0, items.Count - 1));
        _menu.Selected = items.Count == 0 ? null : selected;
        _menu.OnSelect = payload => _menu.Selected = payload;
        _menu.ButtonLabelProvider = () =>
            _menu.Selected is int index && index >= 0 && index < items.Count
                ? items[index]
                : string.Empty;

        if (_data.GetString(RetailDialogProperty.MenuAcceptLabel) is { } acceptLabel)
            _accept.Label = acceptLabel;
        if (_data.GetString(RetailDialogProperty.MenuRejectLabel) is { } rejectLabel)
            _reject.Label = rejectLabel;

        Root.Cancel = Reject;
        _accept.OnClick = Accept;
        _reject.OnClick = Reject;
        SizeAndCenter();
    }

    public UiDialogRoot Root { get; }

    public void Tick() => SizeAndCenter();

    public void SetPendingCount(int count)
    {
    }

    public void DetachHandlers()
    {
        Root.Cancel = null;
        _accept.OnClick = null;
        _reject.OnClick = null;
        _menu.OnSelect = null;
    }

    private void Accept()
    {
        _data.Set(
            RetailDialogProperty.MenuSelection,
            _menu.Selected is int selected ? selected : -1);
        _closeDialog(_context);
    }

    private void Reject()
    {
        _data.Set(RetailDialogProperty.MenuSelection, -1);
        _closeDialog(_context);
    }

    private void SizeAndCenter()
    {
        var space = _host.EffectiveCanvasSize;
        Root.Left = 0f;
        Root.Top = 0f;
        Root.Width = space.X;
        Root.Height = space.Y;
        if (_popup is null) return;
        _popup.LayoutPolicy = null;
        _popup.Anchors = AnchorEdges.None;
        _popup.Left = MathF.Round((Root.Width - _popup.Width) * 0.5f);
        _popup.Top = MathF.Round((Root.Height - _popup.Height) * 0.5f);
    }
}
