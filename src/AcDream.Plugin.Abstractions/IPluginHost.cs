// src/AcDream.Plugin.Abstractions/IPluginHost.cs
namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Everything a plugin can reach in the client, handed to it once at
/// startup. A host that cannot provide a facility (a bot process with no
/// window, for example) exposes the inert default for it rather than
/// throwing, so the same plugin still loads there.
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// Whether this host draws a user interface at all. False in a
    /// window-less process, where panels and hotkeys go nowhere.
    /// </summary>
    bool HasUi { get; }

    /// <summary>Writes to the client's log, tagged with this plugin.</summary>
    IPluginLogger Log { get; }

    /// <summary>What the client currently has in the world.</summary>
    IGameState State { get; }

    /// <summary>The client's notifications: tick, login, objects, and the rest.</summary>
    IEvents Events { get; }

    /// <summary>What the player currently has selected.</summary>
    ISelectionService Selection { get; }

    /// <summary>Registers this plugin's own windows and drives the client's.</summary>
    IUiRegistry Ui { get; }

    /// <summary>
    /// Registers chat commands this plugin answers to. Inert on a host that
    /// has no command line.
    /// </summary>
    IPluginCommandRegistry Commands => NoOpPluginCommandRegistry.Instance;

    /// <summary>
    /// Durable storage scoped by the host to this plugin's manifest id.
    /// No-window/test hosts may explicitly expose the inert implementation.
    /// </summary>
    IPluginStorage Storage => NoOpPluginStorage.Instance;

    /// <summary>
    /// Declared package resources and layered user data for this plugin.
    /// </summary>
    IPluginResourceCatalog Resources => NoOpPluginResourceCatalog.Instance;

    /// <summary>Map controls, or an inert registry in a headless host.</summary>
    IPluginMapRegistry Maps => NoOpPluginMapRegistry.Instance;

    /// <summary>Plugin-owned HUD and drawing services, or an inert registry without a renderer.</summary>
    IPluginRenderRegistry Rendering => NoOpPluginRenderRegistry.Instance;

    /// <summary>
    /// Registers this plugin's own rules for deciding what loot is worth
    /// keeping. Inert on a host that does no loot classification.
    /// </summary>
    IPluginLootClassifierRegistry LootClassifiers =>
        NoOpPluginLootClassifierRegistry.Instance;

    /// <summary>Plugin-owned keyboard hotkeys; a no-op on a host with nothing to bind.</summary>
    IHotkeyRegistry Hotkeys => NoOpHotkeyRegistry.Instance;

    /// <summary>
    /// Reading and driving live gameplay: the character, spells, items,
    /// combat, movement and the rest.
    /// </summary>
    IAutomationSurface Automation { get; }

    /// <summary>
    /// A second storage area, shared by every plugin in the process, holding
    /// the automation profile files in the community layout so several
    /// plugins can read one set of profiles. Inert when the host configured
    /// no profile directory.
    /// </summary>
    IPluginStorage VtankProfiles => NoOpPluginStorage.Instance;

    /// <summary>The host's text clipboard; inert without a window.</summary>
    IPluginClipboard Clipboard => NoOpPluginClipboard.Instance;

    /// <summary>The host's own OS window; inert without one.</summary>
    IHostWindow Window => NoOpHostWindow.Instance;

    /// <summary>
    /// Settings this session was started with for this plugin, keyed by
    /// setting name -- for example the profile a bot session should apply on
    /// login. Empty when the session declared none.
    /// </summary>
    IReadOnlyDictionary<string, string> SessionSettings =>
        EmptySessionSettings;

    private static readonly IReadOnlyDictionary<string, string> EmptySessionSettings =
        new Dictionary<string, string>();
}
