namespace AcDream.App.UI.Layout;

internal sealed record CharacterCreationUiMountResources(
    uint LayoutId,
    ImportedLayout Layout,
    Func<uint, uint, UiElement?> TemplateResolver,
    CharacterCreationUiController.DialogStrings Strings);

internal sealed class CharacterCreationUiMountCoordinator : IDisposable
{
    private readonly UiRoot _host;
    private readonly CharacterCreationRuntimeBindings _bindings;
    private readonly Func<RetailDialogFactory?> _ensureDialogs;
    private readonly Func<CharacterCreationUiMountResources?> _loadResources;
    private bool _disposed;

    public CharacterCreationUiMountCoordinator(
        UiRoot host,
        CharacterCreationRuntimeBindings bindings,
        Func<RetailDialogFactory?> ensureDialogs,
        Func<CharacterCreationUiMountResources?> loadResources)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _ensureDialogs = ensureDialogs
            ?? throw new ArgumentNullException(nameof(ensureDialogs));
        _loadResources = loadResources
            ?? throw new ArgumentNullException(nameof(loadResources));
    }

    public CharacterCreationUiController? Controller { get; private set; }

    public void Tick()
    {
        if (_disposed || Controller is not null)
            return;

        try
        {
            RetailDialogFactory? dialogs = _ensureDialogs();
            if (dialogs is null)
                return;

            CharacterCreationUiMountResources? resources = _loadResources();
            if (resources is null)
                return;

            CharacterCreationUiController? candidate =
                CharacterCreationUiController.CreateDetached(
                    _host,
                    resources.Layout,
                    resources.TemplateResolver,
                    dialogs,
                    _bindings,
                    resources.Strings);
            if (candidate is null)
                return;

            Controller = candidate;
            candidate.AttachAndTick();
            Console.WriteLine(
                $"[UI] retail character creation from enum table 5 "
                + $"(0x10000039 -> 0x{resources.LayoutId:X8}, root 0x100003CC).");
        }
        catch (Exception error)
        {
            CharacterCreationUiController? partial = Controller;
            Controller = null;
            try
            {
                partial?.Dispose();
            }
            catch (Exception cleanupError)
            {
                Console.WriteLine(
                    "[UI] character creation partial-mount cleanup failed: "
                    + cleanupError.Message);
            }
            Console.WriteLine(
                "[UI] character creation mount will retry after resource "
                + $"recovery: {error.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Controller?.Dispose();
        Controller = null;
    }
}
