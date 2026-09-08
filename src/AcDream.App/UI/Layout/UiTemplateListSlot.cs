namespace AcDream.App.UI.Layout;

public sealed class UiTemplateListSlot : UiItemSlot
{
    private readonly IUiDatStateful? _statefulRoot;
    private readonly uint _normalState;
    private readonly uint? _selectedState;
    private bool? _stateWasSelected;

    public UiTemplateListSlot(
        ImportedLayout content,
        uint entryId,
        uint normalState,
        uint? selectedState)
    {
        Content = content;
        EntryId = entryId;
        _normalState = normalState;
        _selectedState = selectedState;
        _statefulRoot = content.Root as IUiDatStateful;

        Width = content.Root.Width;
        Height = content.Root.Height;
        content.Root.Left = 0f;
        content.Root.Top = 0f;
        content.Root.Anchors = AnchorEdges.Left | AnchorEdges.Top
            | AnchorEdges.Right | AnchorEdges.Bottom;
        AddChild(content.Root);
    }

    public ImportedLayout Content { get; }
    public uint EntryId { get; }
    public new Action? Clicked { get; set; }

    public override bool HandlesClick => Clicked is not null;

    public void SetSelected(bool selected)
    {
        Selected = selected;
        ApplySelectionState();
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type == UiEventType.MouseDown)
            return true;
        if (e.Type == UiEventType.Click && Clicked is not null)
        {
            Clicked();
            return true;
        }
        return false;
    }

    protected override void OnDraw(UiRenderContext ctx)
        => ApplySelectionState();

    private void ApplySelectionState()
    {
        if (_stateWasSelected == Selected)
            return;

        _stateWasSelected = Selected;
        _statefulRoot?.TrySetRetailState(
            Selected && _selectedState.HasValue ? _selectedState.Value : _normalState);
    }
}
