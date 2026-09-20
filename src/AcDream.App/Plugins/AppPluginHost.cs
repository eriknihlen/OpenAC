using AcDream.Plugin.Abstractions;

namespace AcDream.App.Plugins;

public sealed class AppPluginHost : IPluginHost
{
    public AppPluginHost(
        IPluginLogger log,
        IGameState state,
        IEvents events,
        ISelectionService selection,
        IUiRegistry ui,
        IAutomationSurface automation,
        IPluginStorage? storage = null,
        IPluginCommandRegistry? commands = null,
        IPluginLootClassifierRegistry? lootClassifiers = null,
        IPluginStorage? vtankProfiles = null,
        IPluginClipboard? clipboard = null,
        IHotkeyRegistry? hotkeys = null,
        IHostWindow? window = null,
        IPluginMapRegistry? maps = null,
        IPluginMapResourceCatalog? mapResources = null,
        IPluginRenderRegistry? rendering = null)
    {
        Log = log;
        State = state;
        Events = events;
        Selection = selection;
        Ui = ui;
        Automation = automation;
        Storage = storage ?? NoOpPluginStorage.Instance;
        Commands = commands ?? NoOpPluginCommandRegistry.Instance;
        LootClassifiers = lootClassifiers
            ?? NoOpPluginLootClassifierRegistry.Instance;
        VtankProfiles = vtankProfiles ?? NoOpPluginStorage.Instance;
        Clipboard = clipboard ?? NoOpPluginClipboard.Instance;
        Hotkeys = hotkeys ?? NoOpHotkeyRegistry.Instance;
        Window = window ?? NoOpHostWindow.Instance;
        Maps = maps ?? NoOpPluginMapRegistry.Instance;
        MapResources = mapResources ?? NoOpPluginMapResourceCatalog.Instance;
        Rendering = rendering ?? NoOpPluginRenderRegistry.Instance;
    }

    public bool HasUi => true;
    public IPluginLogger Log { get; }
    public IGameState State { get; }
    public IEvents Events { get; }
    public ISelectionService Selection { get; }
    public IUiRegistry Ui { get; }
    public IAutomationSurface Automation { get; }
    public IPluginStorage Storage { get; }
    public IPluginCommandRegistry Commands { get; }
    public IPluginLootClassifierRegistry LootClassifiers { get; }
    public IPluginStorage VtankProfiles { get; }
    public IPluginClipboard Clipboard { get; }
    public IHotkeyRegistry Hotkeys { get; }
    public IHostWindow Window { get; }
    public IPluginMapRegistry Maps { get; }
    public IPluginMapResourceCatalog MapResources { get; }
    public IPluginRenderRegistry Rendering { get; }
}
