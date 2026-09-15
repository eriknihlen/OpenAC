using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

/// <summary>One plugin offered in the character options checklist: an installed plugin compatible
/// with the character's launch mode, or an id already in the character's saved list that is no
/// longer offered there ("missing"), kept checked until the user unchecks it.</summary>
public sealed class CharacterPluginChoiceViewModel(
    string id,
    string displayName,
    bool isChecked,
    bool isMissing)
    : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public bool IsMissing { get; } = isMissing;

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>The two top-level panels the redesigned window switches between.</summary>
public enum LauncherMainTab
{
    Accounts,
    Plugins,
}

public sealed partial class LauncherWindowViewModel
{
    private PluginInventory? _pluginInventory;
    private LauncherMainTab _selectedTab = LauncherMainTab.Accounts;

    public LauncherPluginsViewModel Plugins { get; private set; } = null!;

    public ObservableCollection<CharacterPluginChoiceViewModel> CharacterPluginChoices { get; } = [];

    public LauncherMainTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsAccountsTabSelected));
                OnPropertyChanged(nameof(IsPluginsTabSelected));
            }
        }
    }

    public bool IsAccountsTabSelected => SelectedTab == LauncherMainTab.Accounts;

    public bool IsPluginsTabSelected => SelectedTab == LauncherMainTab.Plugins;

    public RelayCommand SelectAccountsTabCommand { get; private set; } = null!;

    public RelayCommand SelectPluginsTabCommand { get; private set; } = null!;

    private void InitializePlugins()
    {
        Plugins = new LauncherPluginsViewModel(_orchestrator, _dispatcher, () => CanInteract);
        Plugins.InstallDialog.PropertyChanged += OnModalPropertyChanged;
        Plugins.PropertyChanged += OnModalPropertyChanged;
        SelectAccountsTabCommand = new RelayCommand(() => SelectedTab = LauncherMainTab.Accounts);
        SelectPluginsTabCommand = new RelayCommand(() =>
        {
            SelectedTab = LauncherMainTab.Plugins;
            _ = Plugins.RefreshDiscoverDetailsAsync();
        });
    }

    /// <summary>Wires the real plugin backend, built by <c>LauncherPluginComposition</c> in
    /// App.axaml.cs; left unset (as in most tests), the Plugins tab shows no rows and the character
    /// checklist shows only ids already saved on the character, all as "missing".</summary>
    internal void ConfigurePlugins(
        LauncherPluginComposition composition,
        Func<ClientVersionResolution?> clientVersionResolver)
    {
        ArgumentNullException.ThrowIfNull(composition);
        _pluginInventory = composition.Inventory;
        Plugins.Configure(composition, clientVersionResolver);
    }

    /// <summary>Rebuilds the character options checklist against the live plugin inventory,
    /// filtered by the character's launch mode through <c>hosts</c>. Ids already saved on the
    /// character that are not offered here (not installed, or installed for the other host) are
    /// kept as checked, missing rows so a save never silently drops them.</summary>
    private void LoadCharacterPluginChoices(LauncherCharacterSnapshot? character)
    {
        CharacterPluginChoices.Clear();
        if (character is null)
        {
            return;
        }

        var configured = new HashSet<string>(character.Plugins, StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wrongHostDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LauncherPluginHostKind host = character.LaunchMode == LaunchMode.Headless
            ? LauncherPluginHostKind.Headless
            : LauncherPluginHostKind.Graphical;

        if (_pluginInventory is not null)
        {
            IEnumerable<InstalledPluginInfo> installed = _pluginInventory
                .Build(clientResolution: null, catalog: null)
                .OrderBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (InstalledPluginInfo info in installed)
            {
                LauncherPluginManifest? manifest = TryReadManifest(info.Directory);
                IReadOnlyList<LauncherPluginHostKind> hosts = manifest?.Hosts
                    ?? [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
                if (!hosts.Contains(host))
                {
                    wrongHostDisplayNames[info.Id] = info.DisplayName;
                    continue;
                }

                if (!placed.Add(info.Id))
                {
                    continue;
                }

                CharacterPluginChoices.Add(new CharacterPluginChoiceViewModel(
                    info.Id,
                    info.DisplayName,
                    isChecked: configured.Contains(info.Id),
                    isMissing: false));
            }
        }

        foreach (string id in character.Plugins)
        {
            if (string.Equals(id, "none", StringComparison.OrdinalIgnoreCase) || !placed.Add(id))
            {
                continue;
            }

            string displayName = wrongHostDisplayNames.TryGetValue(id, out string? installedName)
                ? $"{installedName} (not available for this mode)"
                : $"{id} (missing)";
            CharacterPluginChoices.Add(new CharacterPluginChoiceViewModel(
                id, displayName, isChecked: true, isMissing: true));
        }
    }

    /// <summary>Blank means none (L-302): unchecking every plugin saves an empty list, not the old
    /// literal "none" sentinel a free-text box once needed.</summary>
    private IReadOnlyList<string> CheckedCharacterPluginIds() =>
        [.. CharacterPluginChoices.Where(choice => choice.IsChecked).Select(choice => choice.Id)];

    private static LauncherPluginManifest? TryReadManifest(string directory)
    {
        try
        {
            return LauncherPluginManifest.Parse(
                File.ReadAllText(Path.Combine(directory, "plugin.json")));
        }
        catch (Exception ex) when (ex is IOException or LauncherPluginManifestException)
        {
            return null;
        }
    }
}
