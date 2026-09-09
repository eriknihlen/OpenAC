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
        IPluginStorage? vtankProfiles = null)
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
}
