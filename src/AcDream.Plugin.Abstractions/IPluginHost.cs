// src/AcDream.Plugin.Abstractions/IPluginHost.cs
namespace AcDream.Plugin.Abstractions;

public interface IPluginHost
{
    bool HasUi { get; }

    IPluginLogger Log { get; }
    IGameState State { get; }
    IEvents Events { get; }
    ISelectionService Selection { get; }
    IUiRegistry Ui { get; }
    IPluginCommandRegistry Commands => NoOpPluginCommandRegistry.Instance;
    /// <summary>
    /// Durable storage scoped by the host to this plugin's manifest id.
    /// No-window/test hosts may explicitly expose the inert implementation.
    /// </summary>
    IPluginStorage Storage => NoOpPluginStorage.Instance;
    IPluginLootClassifierRegistry LootClassifiers =>
        NoOpPluginLootClassifierRegistry.Instance;

    IAutomationSurface Automation { get; }

    IPluginStorage VtankProfiles => NoOpPluginStorage.Instance;

    IReadOnlyDictionary<string, string> SessionSettings =>
        EmptySessionSettings;

    private static readonly IReadOnlyDictionary<string, string> EmptySessionSettings =
        new Dictionary<string, string>();
}
