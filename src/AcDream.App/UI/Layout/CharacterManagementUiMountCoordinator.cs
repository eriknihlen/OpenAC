namespace AcDream.App.UI.Layout;

internal sealed record CharacterManagementUiMountResources(
    uint LayoutId,
    ImportedLayout Layout,
    Func<uint, uint, UiElement?> TemplateResolver,
    CharacterManagementUiController.DialogStrings Strings);

internal sealed class CharacterManagementUiMountCoordinator : IDisposable
{
    private readonly UiRoot _host;
    private readonly CharacterSelectionRuntimeBindings _bindings;
    private readonly Func<RetailDialogFactory?> _ensureDialogs;
    private readonly Func<CharacterManagementUiMountResources?> _loadResources;
    private readonly Action? _openCredits;
    private bool _disposed;

    public CharacterManagementUiMountCoordinator(
        UiRoot host,
        CharacterSelectionRuntimeBindings bindings,
        Func<RetailDialogFactory?> ensureDialogs,
        Func<CharacterManagementUiMountResources?> loadResources,
        Action? openCredits = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _ensureDialogs = ensureDialogs
            ?? throw new ArgumentNullException(nameof(ensureDialogs));
        _loadResources = loadResources
            ?? throw new ArgumentNullException(nameof(loadResources));
        _openCredits = openCredits;
    }

    public CharacterManagementUiController? Controller { get; private set; }

    public void Tick()
    {
        if (_disposed || Controller is not null)
            return;

        try
        {
            RetailDialogFactory? dialogs = _ensureDialogs();
            if (dialogs is null)
                return;

            CharacterManagementUiMountResources? resources = _loadResources();
            if (resources is null)
                return;

            CharacterManagementUiController? candidate =
                CharacterManagementUiController.CreateDetached(
                _host,
                resources.Layout,
                resources.TemplateResolver,
                    dialogs,
                    _bindings,
                    resources.Strings,
                    _openCredits);
            if (candidate is null)
                return;

            Controller = candidate;
            candidate.AttachAndTick();
            Console.WriteLine(
                $"[UI] retail character management from enum table 5 "
                + $"(0x10000005 -> 0x{resources.LayoutId:X8}, "
                + "root 0x1000039A; flat list, no viewport).");
        }
        catch (Exception error)
        {
            CharacterManagementUiController? partial = Controller;
            Controller = null;
            try
            {
                partial?.Dispose();
            }
            catch (Exception cleanupError)
            {
                Console.WriteLine(
                    "[UI] character management partial-mount cleanup failed: "
                    + cleanupError.Message);
            }
            Console.WriteLine(
                "[UI] character management mount will retry after resource "
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
