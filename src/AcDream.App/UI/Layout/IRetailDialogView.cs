namespace AcDream.App.UI.Layout;

internal interface IRetailDialogView
{
    UiDialogRoot Root { get; }

    void Tick();

    void SetPendingCount(int count);

    void DetachHandlers();
}
