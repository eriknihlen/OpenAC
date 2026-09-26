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
        // The account rows name their plugins; an install, update or removal renames them.
        Plugins.Installed.CollectionChanged += (_, _) =>
        {
            if (_snapshot is { } snapshot && !_disposed) RefreshAccountRows(snapshot);
            NotifyVersions();
        };
        Plugins.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LauncherPluginsViewModel.IsBusy)) NotifyVersions();
        };
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
        CharacterUsesAccountPlugins = character is not null && !character.HasOwnPlugins;
        if (character is null)
        {
            CharacterPluginChoices.Clear();
            return;
        }

        LoadPluginChoices(
            character.Plugins,
            character.LaunchMode == LaunchMode.Headless
                ? LauncherPluginHostKind.Headless
                : LauncherPluginHostKind.Graphical);
    }

    /// <summary>The checklist for <paramref name="configuredIds"/>. With a host, plugins that cannot
    /// run there are left out; without one (an account, whose characters may launch either way)
    /// every usable plugin is offered and one limited to a host says so.</summary>
    private void LoadPluginChoices(IReadOnlyList<string> configuredIds, LauncherPluginHostKind? host)
    {
        CharacterPluginChoices.Clear();
        var configured = new HashSet<string>(configuredIds, StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wrongHostDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refusedDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blockedDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_pluginInventory is not null)
        {
            IEnumerable<InstalledPluginInfo> installed = _pluginInventory
                .Build(clientResolution: null, Plugins.CurrentCatalog)
                .OrderBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (InstalledPluginInfo info in installed)
            {
                LauncherPluginManifest? manifest = TryReadManifest(info.Directory);
                IReadOnlyList<LauncherPluginHostKind> hosts = manifest?.Hosts
                    ?? [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
                if (host is { } required && !hosts.Contains(required))
                {
                    wrongHostDisplayNames[info.Id] = info.DisplayName;
                    continue;
                }

                if (info.Refusal is not null || info.HasDuplicate)
                {
                    refusedDisplayNames[info.Id] = info.DisplayName;
                    continue;
                }

                // A blocked plugin is dropped at launch, so it is not offered as a working choice.
                if (info.Blocked is not null)
                {
                    blockedDisplayNames[info.Id] = info.DisplayName;
                    continue;
                }

                if (!placed.Add(info.Id))
                {
                    continue;
                }

                string choiceName = host is null && hosts.Count == 1
                    ? $"{info.DisplayName} ({(hosts[0] == LauncherPluginHostKind.Headless ? "headless only" : "windowed only")})"
                    : info.DisplayName;
                CharacterPluginChoices.Add(new CharacterPluginChoiceViewModel(
                    info.Id,
                    choiceName,
                    isChecked: configured.Contains(info.Id),
                    isMissing: false));
            }
        }

        foreach (string id in configuredIds)
        {
            if (string.Equals(id, "none", StringComparison.OrdinalIgnoreCase) || !placed.Add(id))
            {
                continue;
            }

            string displayName = wrongHostDisplayNames.TryGetValue(id, out string? installedName)
                ? $"{installedName} (not available for this mode)"
                : refusedDisplayNames.TryGetValue(id, out string? refusedName)
                    ? $"{refusedName} (refused)"
                    : blockedDisplayNames.TryGetValue(id, out string? blockedName)
                        ? $"{blockedName} (blocked)"
                        : $"{id} (missing)";
            CharacterPluginChoices.Add(new CharacterPluginChoiceViewModel(
                id, displayName, isChecked: true, isMissing: true));
        }
    }

    private (string Server, string Account)? _rowOptionsAccount;
    private bool _characterUsesAccountPlugins;

    /// <summary>True while the plugins dialog edits an account's list rather than one character's.</summary>
    public bool IsAccountRowOptions => _rowOptionsAccount is not null;

    /// <summary>The character dialog's choice between the account's plugins and its own list.</summary>
    public bool ShowCharacterPluginChoice => !IsAccountRowOptions;

    /// <summary>Whether the character launches with its account's plugins; unticked, it keeps its
    /// own list, edited below.</summary>
    public bool CharacterUsesAccountPlugins
    {
        get => _characterUsesAccountPlugins;
        set
        {
            if (SetProperty(ref _characterUsesAccountPlugins, value))
            {
                OnPropertyChanged(nameof(CanEditPluginChoices));
            }
        }
    }

    /// <summary>The checklist is editable except while a character follows its account.</summary>
    public bool CanEditPluginChoices => IsAccountRowOptions || !CharacterUsesAccountPlugins;

    public string RowOptionsTitle => _rowOptionsAccount is { } account
        ? $"Plugins for {account.Account}"
        : $"Plugins for {SelectionTitle}";

    public string RowOptionsPluginsCaption => IsAccountRowOptions
        ? "Every character on this account starts with these plugins."
        : "This character can use its account's plugins or a list of its own.";

    private void SetRowOptionsAccount((string Server, string Account)? account)
    {
        _rowOptionsAccount = account;
        OnPropertyChanged(nameof(IsAccountRowOptions));
        OnPropertyChanged(nameof(ShowCharacterPluginChoice));
        OnPropertyChanged(nameof(CanEditPluginChoices));
        OnPropertyChanged(nameof(RowOptionsTitle));
        OnPropertyChanged(nameof(RowOptionsPluginsCaption));
    }

    private LauncherAccountSnapshot? FindAccountSnapshot((string Server, string Account) key) =>
        _orchestrator.GetSnapshot().Servers
            .FirstOrDefault(server => string.Equals(server.Name, key.Server, StringComparison.Ordinal))
            ?.Accounts.FirstOrDefault(account =>
                string.Equals(account.AccountName, key.Account, StringComparison.Ordinal));

    /// <summary>The account's "Edit plugins": one list for every character on it.</summary>
    private void OpenAccountPlugins(LauncherAccountGroupViewModel group)
    {
        var key = (group.ServerName, group.AccountName);
        if (FindAccountSnapshot(key) is not { } account) return;
        SetRowOptionsAccount(key);
        LoadPluginChoices(account.Plugins, host: null);
        LastError = null;
        IsCharacterOptionsOpen = true;
    }

    /// <summary>Options › Plugins for this character: its account's list or one of its own.</summary>
    private void OpenCharacterPlugins(LauncherAccountServerRowViewModel row)
    {
        if (row.CharacterName is null) return;
        SetRowOptionsAccount(null);
        SelectedNode = Servers.FirstOrDefault(server => server.ServerName == row.ServerName)?.Children
            .FirstOrDefault(account => account.AccountName == row.AccountName)?.Children
            .FirstOrDefault(character => character.CharacterName == row.CharacterName);
        if (SelectedNode is null) return;
        // Reselecting the already-open character leaves SetSelectedNode a no-op, so the draft is
        // rebuilt here to show the saved state on every open.
        LoadCharacterDraft();
        CharacterLaunchMode = row.Mode;
        LastError = null;
        IsCharacterOptionsOpen = true;
    }

    /// <summary>Saves the account list, keeping the order of what was already there and adding newly
    /// ticked plugins after it.</summary>
    private void SaveAccountPluginChoices((string Server, string Account) key)
    {
        try
        {
            LauncherAccountSnapshot account = FindAccountSnapshot(key)
                ?? throw new LauncherOperationException("That account no longer exists.");
            var chosen = CheckedCharacterPluginIds();
            List<string> plugins = [.. account.Plugins.Where(id => chosen.Contains(id, StringComparer.OrdinalIgnoreCase))];
            plugins.AddRange(chosen.Where(id => !plugins.Contains(id, StringComparer.OrdinalIgnoreCase)));
            _orchestrator.UpdateAccountPlugins(key.Server, key.Account, plugins);
            LastError = null;
            OperationStatus = $"Saved plugins for {key.Account}.";
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
        }
    }

    /// <summary>The installed plugins by name, for the account summaries.</summary>
    private string PluginDisplayName(string id) =>
        Plugins.Installed.FirstOrDefault(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? id;

    /// <summary>Blank means none: unchecking every plugin saves an empty list, not the old
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
