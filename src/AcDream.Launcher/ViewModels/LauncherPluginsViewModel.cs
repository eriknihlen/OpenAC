using System.Collections.ObjectModel;
using System.Text;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using Avalonia.Media.Imaging;

namespace AcDream.Launcher.ViewModels;

/// <summary>The letters on a plugin card's tile until plugins can ship an icon: the first letters of
/// the first two words, or of the first two capitalised parts of a single word ("BuffBot" is BB).</summary>
internal static class PluginMonogram
{
    public static string From(string name)
    {
        string[] words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "?";
        if (words.Length > 1)
            return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));

        string word = words[0];
        for (int i = 1; i < word.Length; i++)
        {
            if (char.IsUpper(word[i]))
                return string.Concat(char.ToUpperInvariant(word[0]), word[i]);
        }

        return char.ToUpperInvariant(word[0]).ToString();
    }
}

/// <summary>One channel's resolved release for a Discover row: what its picker
/// option would install and display if chosen, cached alongside the row so switching the picker
/// never re-fetches.</summary>
internal readonly record struct DiscoverChannelOption(
    string Tag,
    string Version,
    string Compatibility,
    bool CompatibilityIsWarning,
    IReadOnlyList<LauncherPluginCapabilityDeclaration> Capabilities);

/// <summary>One listed plugin not yet installed, shown on the Discover list.</summary>
public sealed class PluginDiscoverRowViewModel(
    string id,
    string name,
    string author,
    string description,
    string repo,
    RelayCommand installCommand)
    : ObservableObject
{
    public const string StableChannelLabel = "Stable";
    public const string BetaChannelLabel = "Beta";

    private string? _latestVersion;
    private string? _compatibility;
    private bool _compatibilityIsWarning;
    private IReadOnlyList<LauncherPluginCapabilityDeclaration> _capabilities = [];
    private Bitmap? _icon;
    private bool _showChannelPicker;
    private PluginReleaseChannel _selectedChannel = PluginReleaseChannel.Stable;
    private DiscoverChannelOption? _stableOption;
    private DiscoverChannelOption? _betaOption;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Author { get; } = author;
    public string Description { get; } = description;
    public string Repo { get; } = repo;
    public string AuthorAndRepo { get; } = $"by {author} · {repo}";
    public string Initials { get; } = PluginMonogram.From(name);
    public string InstallAutomationName { get; } = $"Install {name}";
    public string ChannelPickerAutomationName { get; } = $"Release channel for {name}";
    public RelayCommand InstallCommand { get; } = installCommand;

    public IReadOnlyList<string> ChannelChoices { get; } = [StableChannelLabel, BetaChannelLabel];

    /// <summary>Shown for every row whenever Show beta plugins is on: opting a plugin into Beta is a
    /// durable subscription to its prereleases, useful before the first one ever ships, so visibility
    /// never depends on whether this plugin happens to have one today.</summary>
    public bool ShowChannelPicker => _showChannelPicker;

    public string SelectedChannelLabel
    {
        get => _selectedChannel == PluginReleaseChannel.Beta ? BetaChannelLabel : StableChannelLabel;
        set
        {
            PluginReleaseChannel channel = string.Equals(value, StableChannelLabel, StringComparison.Ordinal)
                ? PluginReleaseChannel.Stable
                : PluginReleaseChannel.Beta;
            if (_selectedChannel == channel)
            {
                return;
            }

            _selectedChannel = channel;
            OnPropertyChanged();
            ApplySelectedOption();
        }
    }

    internal PluginReleaseChannel SelectedChannel => _selectedChannel;

    /// <summary>What Install pins to for the channel currently selected, and nothing else. It never
    /// falls back to the other channel: picking Stable on a plugin that has only ever published
    /// prereleases must refuse, not quietly install the beta the row happened to resolve.</summary>
    internal DiscoverChannelOption? SelectedOption =>
        _selectedChannel == PluginReleaseChannel.Beta ? _betaOption : _stableOption;

    /// <summary>Why Install is refused for the selected channel, or null when it can proceed. Only
    /// meaningful once a resolve pass has run; a row with neither channel resolved is not shown at
    /// all.</summary>
    public string? ChannelUnavailableText => SelectedOption is not null
        ? null
        : _selectedChannel == PluginReleaseChannel.Beta
            ? "No beta release yet."
            : "No stable release yet.";

    public bool HasChannelUnavailable => ChannelUnavailableText is not null;

    internal bool CanInstallSelectedChannel => SelectedOption is not null;

    internal void SetShowChannelPicker(bool value) =>
        SetProperty(ref _showChannelPicker, value, nameof(ShowChannelPicker));

    /// <summary>Registers both candidates one resolve pass found, cached alongside the row so
    /// switching the picker never re-fetches. Reapplies whichever channel is
    /// currently selected, so a re-check never silently drops the choice someone made.</summary>
    internal void SetChannelOptions(DiscoverChannelOption? stable, DiscoverChannelOption? beta)
    {
        _stableOption = stable;
        _betaOption = beta;
        ApplySelectedOption();
    }

    /// <summary>Puts the picker's own selection back to its default, alongside whatever else a
    /// channel-wide toggle already clears on the row.</summary>
    internal void ClearChannelOptions()
    {
        _stableOption = null;
        _betaOption = null;
        if (_selectedChannel != PluginReleaseChannel.Stable)
        {
            _selectedChannel = PluginReleaseChannel.Stable;
            OnPropertyChanged(nameof(SelectedChannelLabel));
        }

        ApplySelectedOption();
    }

    private void ApplySelectedOption()
    {
        if (SelectedOption is { } option)
        {
            LatestVersion = option.Version;
            Compatibility = option.Compatibility;
            CompatibilityIsWarning = option.CompatibilityIsWarning;
            Capabilities = option.Capabilities;
        }
        else
        {
            // Nothing on this channel: the card must show no version rather than the other
            // channel's, so what Install would do and what the row says stay the same thing.
            LatestVersion = null;
            Compatibility = null;
            CompatibilityIsWarning = false;
            Capabilities = [];
        }

        OnPropertyChanged(nameof(ChannelUnavailableText));
        OnPropertyChanged(nameof(HasChannelUnavailable));
        InstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether the release resolver has found a usable release for this listed plugin: the row exists from the moment the curated list loads, but only counts toward
    /// Discover, search, and the shown list once this is true.</summary>
    internal bool IsVisible { get; set; }

    /// <summary>Filled in once the Plugins tab is opened (plan, "Request budget"): blank until
    /// then, so opening Discover costs one request per listed, not-installed plugin rather than
    /// every Check pass paying for plugins nobody is looking at.</summary>
    public string? LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (SetProperty(ref _latestVersion, value))
            {
                OnPropertyChanged(nameof(HasLatestVersion));
            }
        }
    }

    public bool HasLatestVersion => !string.IsNullOrWhiteSpace(LatestVersion);

    public string? Compatibility
    {
        get => _compatibility;
        set
        {
            if (SetProperty(ref _compatibility, value))
            {
                OnPropertyChanged(nameof(HasCompatibilityNote));
                OnPropertyChanged(nameof(ShowCompatibilityWarning));
                OnPropertyChanged(nameof(ShowCompatibilityMuted));
            }
        }
    }

    public bool HasCompatibilityNote => !string.IsNullOrWhiteSpace(Compatibility);

    public bool CompatibilityIsWarning
    {
        get => _compatibilityIsWarning;
        set
        {
            if (SetProperty(ref _compatibilityIsWarning, value))
            {
                OnPropertyChanged(nameof(ShowCompatibilityWarning));
                OnPropertyChanged(nameof(ShowCompatibilityMuted));
            }
        }
    }

    public bool ShowCompatibilityWarning => HasCompatibilityNote && CompatibilityIsWarning;
    public bool ShowCompatibilityMuted => HasCompatibilityNote && !CompatibilityIsWarning;
    public string CompatibilityText => LauncherPluginCompatibility.WithoutClientVersion(Compatibility ?? string.Empty);
    public bool ShowCompatibilityInfo => ShowCompatibilityMuted
        && !Compatibility!.StartsWith(LauncherPluginCompatibility.CompatiblePrefix, StringComparison.Ordinal);

    /// <summary>Filled in alongside <see cref="Compatibility"/>: a count only, never the claims
    /// themselves, since browsing is not a consent surface.</summary>
    public IReadOnlyList<LauncherPluginCapabilityDeclaration> Capabilities
    {
        get => _capabilities;
        set
        {
            if (SetProperty(ref _capabilities, value))
            {
                OnPropertyChanged(nameof(HasCapabilities));
                OnPropertyChanged(nameof(CapabilityCountText));
            }
        }
    }

    public bool HasCapabilities => Capabilities.Count > 0;

    public string CapabilityCountText => Capabilities.Count switch
    {
        0 => string.Empty,
        1 => "1 capability",
        var count => $"{count} capabilities",
    };

    /// <summary>Borrowed from <see cref="LauncherPluginsViewModel"/>'s icon cache; this row never
    /// disposes it.</summary>
    public Bitmap? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => Icon is not null;
}

/// <summary>One installed plugin, shown on the Installed list.</summary>
public sealed class PluginInstalledRowViewModel(
    string id,
    string displayName,
    string version,
    string sourceBadge,
    string compatibility,
    bool compatibilityIsWarning,
    string? blocked,
    bool conflict,
    bool canRemove,
    bool showBetaToggle,
    bool isBetaChannel,
    bool isPrerelease,
    bool updateAvailable,
    string? updateVersion,
    string? updateCompatibilityNote,
    bool updateCompatibilityIsWarning,
    bool updateCapabilitiesChanged,
    string? updateWithheldReason,
    RelayCommand? updateCommand,
    RelayCommand? removeCommand,
    string? refusal,
    bool hasDuplicate,
    Action<bool> onBetaToggled,
    Func<bool> canToggleBeta,
    IReadOnlyList<LauncherPluginCapabilityDeclaration> capabilities,
    Bitmap? icon,
    bool showBetaPlugins)
    : ObservableObject
{
    private bool _isBetaChannel = isBetaChannel;
    private bool _showBetaPlugins = showBetaPlugins;

    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public string Version { get; } = version;
    public string SourceBadge { get; } = sourceBadge;
    public string Summary { get; } = $"{id} · v{version} · {sourceBadge}";
    public string Initials { get; } = PluginMonogram.From(displayName);
    /// <summary>Borrowed from <see cref="LauncherPluginsViewModel"/>'s icon cache; this row never
    /// disposes it.</summary>
    public Bitmap? Icon { get; } = icon;
    public bool HasIcon => Icon is not null;
    public string Compatibility { get; } = compatibility;
    public bool HasCompatibilityNote => !string.IsNullOrWhiteSpace(Compatibility);
    public bool CompatibilityIsWarning { get; } = compatibilityIsWarning;
    public bool ShowCompatibilityWarning => HasCompatibilityNote && CompatibilityIsWarning;
    public bool ShowCompatibilityMuted => HasCompatibilityNote && !CompatibilityIsWarning;
    public string CompatibilityText => LauncherPluginCompatibility.WithoutClientVersion(Compatibility ?? string.Empty);
    public bool ShowCompatibilityInfo => ShowCompatibilityMuted
        && !Compatibility!.StartsWith(LauncherPluginCompatibility.CompatiblePrefix, StringComparison.Ordinal);
    public string? Blocked { get; } = blocked;
    public bool IsBlocked => !string.IsNullOrWhiteSpace(Blocked);
    public string? BlockedText => IsBlocked ? $"Blocked: {Blocked}" : null;
    public bool Conflict { get; } = conflict;
    public bool CanRemove { get; } = canRemove;
    public string? Refusal { get; } = refusal;
    public bool IsRefused => !string.IsNullOrWhiteSpace(Refusal);
    public string? RefusedText => IsRefused ? $"Refused: {Refusal}" : null;
    public bool HasDuplicate { get; } = hasDuplicate;
    // A refused plugin never loads, so its refusal takes the version line over a compatibility note.
    public bool ShowSkipped => ShowCompatibilityWarning && !IsRefused;
    public bool ShowCompatibilityInfoLine => ShowCompatibilityInfo && !IsRefused;
    public bool UpdateAvailable { get; } = updateAvailable;
    public string? UpdateVersion { get; } = updateVersion;
    public bool HasUpdateChip => UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateVersion);
    /// <summary>Whether the update declares a different set of capabilities than the installed
    /// version (added, removed, or reworded): a card-level heads-up before the dialog spells it
    /// out.</summary>
    public bool UpdateCapabilitiesChanged { get; } = updateCapabilitiesChanged;
    public string? UpdateChipText => HasUpdateChip
        ? UpdateCapabilitiesChanged
            ? $"Update available: v{UpdateVersion} · new capabilities"
            : $"Update available: v{UpdateVersion}"
        : null;
    public string? UpdateCompatibilityNote { get; } = updateCompatibilityNote;
    public bool UpdateCompatibilityIsWarning { get; } = updateCompatibilityIsWarning;
    // The row's own Compatibility already covers the installed release; this only adds a line
    // when the newer one reads differently, so an unchanged note is never shown twice.
    public bool ShowUpdateCompatibilityNote => HasUpdateChip
        && !string.IsNullOrWhiteSpace(UpdateCompatibilityNote)
        && !string.Equals(UpdateCompatibilityNote, Compatibility, StringComparison.Ordinal);
    public string? UpdateWithheldReason { get; } = updateWithheldReason;
    public bool HasUpdateWithheldReason => !UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateWithheldReason);
    public bool HasNotes => ShowUpdateCompatibilityNote || Conflict || HasUpdateWithheldReason;
    public bool HasChips => IsPrerelease || HasCapabilities || HasUpdateChip || HasDuplicate || IsBlocked;
    public string UpdateAutomationName { get; } = $"Update {displayName}";
    public string RemoveAutomationName { get; } = $"Remove {displayName}";
    public RelayCommand? UpdateCommand { get; } = updateCommand;
    public RelayCommand? RemoveCommand { get; } = removeCommand;

    /// <summary>Launcher-managed only: Direct and Bundled plugins have no channel.</summary>
    public bool ShowBetaToggle { get; } = showBetaToggle;

    public string BetaToggleAutomationName { get; } = $"Beta updates for {displayName}";

    /// <summary>Whether the installed release itself carries a SemVer prerelease part, regardless
    /// of the plugin's channel (a beta-channel plugin reads stable most of the time).</summary>
    public bool IsPrerelease { get; } = isPrerelease;

    /// <summary>The card corner's own channel label: quiet metadata about what
    /// the plugin will fetch next, shown only for a beta-channel, launcher-managed plugin while Show
    /// beta plugins is on. A stable-channel plugin shows nothing there; the "beta install" chip
    /// already covers what a prerelease install has, so the corner never repeats it for Stable.</summary>
    /// <summary>Mutable for the same reason Discover's picker is: toggling Show beta plugins must
    /// reach cards that are already built, and installed rows are only rebuilt by a Check pass.</summary>
    public bool ShowBetaChannelLabel => _isBetaChannel && _showBetaPlugins;

    internal void SetShowBetaPlugins(bool value)
    {
        if (_showBetaPlugins == value)
        {
            return;
        }

        _showBetaPlugins = value;
        OnPropertyChanged(nameof(ShowBetaChannelLabel));
    }

    public bool IsBetaChannel
    {
        get => _isBetaChannel;
        set
        {
            if (!SetProperty(ref _isBetaChannel, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowBetaChannelLabel));
            onBetaToggled(value);
        }
    }

    public bool IsBetaToggleEnabled => canToggleBeta();

    internal void NotifyBetaToggleEnabledChanged() => OnPropertyChanged(nameof(IsBetaToggleEnabled));

    /// <summary>Puts the toggle back without re-triggering <c>onBetaToggled</c>, for a refused
    /// channel write (the barrier's lease refusal, shown the way Remove shows it).</summary>
    internal void RevertBetaChannel(bool value)
    {
        if (_isBetaChannel == value)
        {
            return;
        }

        _isBetaChannel = value;
        OnPropertyChanged(nameof(IsBetaChannel));
        OnPropertyChanged(nameof(ShowBetaChannelLabel));
    }

    public IReadOnlyList<LauncherPluginCapabilityDeclaration> Capabilities { get; } = capabilities;
    public bool HasCapabilities => Capabilities.Count > 0;
    public string CapabilityCountText => Capabilities.Count switch
    {
        0 => string.Empty,
        1 => "1 capability",
        var count => $"{count} capabilities",
    };
}

/// <summary>Discover/Installed, Refresh list, Add from URL, and the install and remove dialogs
/// (plan, "MainWindow.axaml and view models"). Repo URLs are text only; nothing here opens a
/// browser, loads an assembly, or starts a process.</summary>
public sealed class LauncherPluginsViewModel : ObservableObject, IDisposable
{
    private readonly ILauncherOrchestrator _orchestrator;
    private readonly Func<bool> _canInteract;

    private LauncherPluginComposition? _composition;
    private Func<ClientVersionResolution?> _clientVersionResolver = () => null;

    /// <summary>The plugin list as last checked, so another view can judge a block the same way
    /// this panel does. Null until a check has produced one.</summary>
    internal PluginCatalog? CurrentCatalog => _composition?.CurrentCatalog;
    private CancellationTokenSource? _cancellation;
    private DateTimeOffset? _listAgeUtc;
    private readonly Dictionary<string, DiscoverDetails> _discoverDetailsCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every icon decoded this session, keyed by plugin id and version: rows only ever
    /// borrow a <see cref="Bitmap"/> from here, so one decode serves every row and refresh that
    /// shares a key. A missing icon is also cached, as a <see langword="null"/> value, so a release
    /// with none is never re-fetched; that half of the cache is memory-only and never touches disk.</summary>
    private readonly Dictionary<(string Id, string Version, bool Installed), Bitmap?> _iconCache =
        new(new IconCacheKeyComparer());

    private bool _disposed;

    private bool _isBusy;
    private string? _error;
    private string? _statusText;
    private bool _isRateLimited;
    private bool _showBetaPlugins;
    private int _discoverPendingCount;
    private bool _isDiscoverChecking;
    private string? _discoverStatusLine;
    /// <summary>Whether <see cref="RefreshDiscoverDetailsAsync"/> has run at least once this
    /// session: a later Check pass only re-resolves Discover when this is set, so the startup Check
    /// never pays for a look nobody has taken yet.</summary>
    private bool _discoverDetailsRequested;
    private string _addFromUrlText = string.Empty;
    private bool _isRemoveDialogOpen;
    private InstalledPluginInfo? _removeTarget;
    private string _removeDisplayName = string.Empty;
    private bool _removeDeleteStorage;

    public LauncherPluginsViewModel(
        ILauncherOrchestrator orchestrator,
        IUiDispatcher dispatcher,
        Func<bool>? canInteract = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        ArgumentNullException.ThrowIfNull(dispatcher);
        _canInteract = canInteract ?? (() => true);

        // Not gated on the window's CanInteract: that is false whenever a modal is open, and this
        // dialog is one, so it would disable its own Install button. Its Confirm is gated on its own
        // IsOpen and IsBusy, like the remove dialog's.
        InstallDialog = new PluginInstallDialogViewModel();
        CheckNowCommand = new AsyncRelayCommand(
            CheckNowAsync,
            () => _canInteract() && !IsBusy && _composition is not null);
        AddFromUrlCommand = new AsyncRelayCommand(
            AddFromUrlAsync,
            () => _canInteract() && !IsBusy && _composition is not null
                && !string.IsNullOrWhiteSpace(AddFromUrlText));
        ConfirmRemoveCommand = new RelayCommand(
            ConfirmRemove,
            () => IsRemoveDialogOpen && !IsBusy);
        CancelRemoveCommand = new RelayCommand(
            () => IsRemoveDialogOpen = false,
            () => !IsBusy);
    }

    public ObservableCollection<PluginDiscoverRowViewModel> Discover { get; } = [];

    public ObservableCollection<PluginInstalledRowViewModel> Installed { get; } = [];

    public bool HasDiscover => Discover.Count > 0;

    public bool HasInstalled => Installed.Count > 0;

    private readonly List<PluginInstalledRowViewModel> _allInstalled = [];
    private readonly List<PluginDiscoverRowViewModel> _allDiscover = [];
    private string _installedFilter = "";
    private string _discoverFilter = "";

    public string InstalledFilter
    {
        get => _installedFilter;
        set { if (SetProperty(ref _installedFilter, value ?? "")) ApplyFilters(); }
    }

    public string DiscoverFilter
    {
        get => _discoverFilter;
        set { if (SetProperty(ref _discoverFilter, value ?? "")) ApplyFilters(); }
    }

    public string InstalledEmptyText => _installedFilter.Length > 0
        ? "No installed plugin matches that search."
        : "No plugins are installed yet.";

    public string DiscoverEmptyText => _discoverFilter.Length > 0
        ? "No listed plugin matches that search."
        : "No listed plugins are available to install.";

    /// <summary>The empty-state text only makes sense once resolution has finished; while it is
    /// still running with nothing shown yet, <see cref="DiscoverCheckingText"/> takes its place.</summary>
    public bool ShowDiscoverEmptyText => !HasDiscover && !IsDiscoverChecking;

    /// <summary>Which empty state Discover shows: a search that matched nothing is a transient, so
    /// it keeps the one-line <see cref="DiscoverEmptyText"/>, while an empty list is a state of its
    /// own and gets the headline and subtext. Only <see cref="ApplyFilters"/> can change it,
    /// because only the <see cref="DiscoverFilter"/> setter reaches it.</summary>
    public bool DiscoverHasFilter => _discoverFilter.Length > 0;

    private static bool Matches(string filter, params string?[] fields) =>
        filter.Length == 0
        || fields.Any(field => field is not null && field.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>Rebuilds the two shown lists from the full ones: Installed by the search alone,
    /// Discover by the search over only the rows whose release has resolved.</summary>
    private void ApplyFilters()
    {
        string installedFilter = _installedFilter.Trim();
        Installed.Clear();
        foreach (PluginInstalledRowViewModel row in _allInstalled
            .Where(row => Matches(installedFilter, row.DisplayName, row.Id, row.SourceBadge)))
        {
            Installed.Add(row);
        }

        string discoverFilter = _discoverFilter.Trim();
        Discover.Clear();
        foreach (PluginDiscoverRowViewModel row in _allDiscover
            .Where(row => row.IsVisible
                && Matches(discoverFilter, row.Name, row.Id, row.Author, row.Description, row.Repo)))
        {
            Discover.Add(row);
        }

        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasDiscover));
        OnPropertyChanged(nameof(InstalledEmptyText));
        OnPropertyChanged(nameof(DiscoverEmptyText));
        OnPropertyChanged(nameof(DiscoverHasFilter));
        OnPropertyChanged(nameof(ShowDiscoverEmptyText));
    }

    /// <summary>
    /// Wide panels put Discover beside Installed; narrow ones stack them, installed first, so the
    /// plugins someone already has stay at the top of the scroll.
    /// </summary>
    public const double SideBySideWidth = 820;

    private bool _isSideBySide;

    public bool IsSideBySide
    {
        get => _isSideBySide;
        set
        {
            if (!SetProperty(ref _isSideBySide, value)) return;
            OnPropertyChanged(nameof(InstalledColumn));
            OnPropertyChanged(nameof(InstalledRow));
            OnPropertyChanged(nameof(InstalledColumnSpan));
            OnPropertyChanged(nameof(InstalledRowSpan));
            OnPropertyChanged(nameof(DiscoverColumn));
            OnPropertyChanged(nameof(DiscoverRow));
            OnPropertyChanged(nameof(DiscoverColumnSpan));
            OnPropertyChanged(nameof(DiscoverRowSpan));
        }
    }

    public void SetPanelWidth(double width) => IsSideBySide = width >= SideBySideWidth;

    public int InstalledColumn => IsSideBySide ? 1 : 0;
    public int InstalledRow => 0;
    public int InstalledColumnSpan => IsSideBySide ? 1 : 2;
    public int InstalledRowSpan => IsSideBySide ? 2 : 1;
    public int DiscoverColumn => 0;
    public int DiscoverRow => IsSideBySide ? 0 : 1;
    public int DiscoverColumnSpan => IsSideBySide ? 1 : 2;
    public int DiscoverRowSpan => IsSideBySide ? 2 : 1;

    public PluginInstallDialogViewModel InstallDialog { get; }

    public AsyncRelayCommand CheckNowCommand { get; }

    public AsyncRelayCommand AddFromUrlCommand { get; }

    public RelayCommand ConfirmRemoveCommand { get; }

    public RelayCommand CancelRemoveCommand { get; }

    public string AddFromUrlText
    {
        get => _addFromUrlText;
        set
        {
            if (SetProperty(ref _addFromUrlText, value))
            {
                AddFromUrlCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>An informational result, e.g. "already installed" from Add from URL: shown in the
    /// panel's normal text, not the error style, since nothing went wrong.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatusText));
            }
        }
    }

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsRateLimited
    {
        get => _isRateLimited;
        private set => SetProperty(ref _isRateLimited, value);
    }

    /// <summary>Set while <see cref="RefreshDiscoverDetailsAsync"/> is resolving rows that have
    /// never resolved before: true only until the first one becomes visible, or until the
    /// whole pass finishes finding none, whichever comes first.</summary>
    public bool IsDiscoverChecking => _isDiscoverChecking;

    public string DiscoverCheckingText => _discoverPendingCount == 1
        ? "Checking 1 plugin…"
        : $"Checking {_discoverPendingCount} plugins…";

    /// <summary>Set for the panel, not per-row: a rate-limited or unreachable release fetch
    /// still leaves its row hidden, but is worth explaining once rather than leaving the list merely
    /// short.</summary>
    public string? DiscoverStatusLine
    {
        get => _discoverStatusLine;
        private set
        {
            if (SetProperty(ref _discoverStatusLine, value))
            {
                OnPropertyChanged(nameof(HasDiscoverStatusLine));
            }
        }
    }

    public bool HasDiscoverStatusLine => !string.IsNullOrWhiteSpace(DiscoverStatusLine);

    private void UpdateDiscoverCheckingState()
    {
        _isDiscoverChecking = _discoverPendingCount > 0 && !HasDiscover;
        OnPropertyChanged(nameof(IsDiscoverChecking));
        OnPropertyChanged(nameof(DiscoverCheckingText));
        OnPropertyChanged(nameof(ShowDiscoverEmptyText));
    }

    /// <summary>Launcher-wide: offers a beta-only repo in Discover and Add from
    /// URL when on, persisted through the orchestrator like every other launcher setting. Off keeps
    /// every resolve on <see cref="PluginReleaseChannel.Stable"/>, exactly as before the setting
    /// existed. Toggling re-runs Discover's own details for the rows already showing, never a full
    /// Check.</summary>
    public bool ShowBetaPlugins
    {
        get => _showBetaPlugins;
        set
        {
            if (!SetProperty(ref _showBetaPlugins, value))
            {
                return;
            }

            _orchestrator.SetShowBetaPlugins(value);
            foreach (PluginInstalledRowViewModel installed in _allInstalled)
            {
                installed.SetShowBetaPlugins(value);
            }

            foreach (PluginDiscoverRowViewModel row in _allDiscover)
            {
                _discoverDetailsCache.Remove(row.Id);
                row.LatestVersion = null;
                row.Compatibility = null;
                row.SetShowChannelPicker(value);
                row.ClearChannelOptions();
                // The new channel's own resolve decides visibility; a row stays hidden
                // until it does, same as the first time the list loaded.
                row.IsVisible = false;
            }

            ApplyFilters();
            _ = RefreshDiscoverDetailsAsync();
        }
    }

    /// <summary>What Discover's own details and Add from URL resolve with.</summary>
    private PluginReleaseChannel DiscoverChannel =>
        ShowBetaPlugins ? PluginReleaseChannel.Beta : PluginReleaseChannel.Stable;

    public bool IsUsingCachedList => _listAgeUtc is not null;

    public string ListAgeText => _listAgeUtc is { } age
        ? $"Showing the plugin list from {FormatAge(DateTimeOffset.UtcNow - age)} ago."
        : string.Empty;

    public bool IsRemoveDialogOpen
    {
        get => _isRemoveDialogOpen;
        private set
        {
            if (SetProperty(ref _isRemoveDialogOpen, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string RemoveDisplayName
    {
        get => _removeDisplayName;
        private set => SetProperty(ref _removeDisplayName, value);
    }

    public bool RemoveDeleteStorage
    {
        get => _removeDeleteStorage;
        set => SetProperty(ref _removeDeleteStorage, value);
    }

    /// <summary>Wires the real backend (App.axaml.cs); left unset, the panel shows no rows and its
    /// commands stay disabled, matching the other child view models' unavailable defaults.</summary>
    internal void Configure(
        LauncherPluginComposition composition,
        Func<ClientVersionResolution?> clientVersionResolver)
    {
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
        _clientVersionResolver = clientVersionResolver
            ?? throw new ArgumentNullException(nameof(clientVersionResolver));
        RestoreShowBetaPluginsFromProfile();
        NotifyCommandStates();
    }

    /// <summary>Reads the saved Show beta plugins back without writing it again, which the public
    /// setter would. At startup the panel is configured before the profiles load, so the first read
    /// sees the empty store's default; the window calls this again once they have, before any Check
    /// builds a row, and the loops below keep any row that already exists in step.</summary>
    internal void RestoreShowBetaPluginsFromProfile()
    {
        bool saved = _orchestrator.GetSnapshot().ShowBetaPlugins;
        if (!SetProperty(ref _showBetaPlugins, saved, nameof(ShowBetaPlugins)))
        {
            return;
        }

        foreach (PluginInstalledRowViewModel installed in _allInstalled)
        {
            installed.SetShowBetaPlugins(saved);
        }

        foreach (PluginDiscoverRowViewModel row in _allDiscover)
        {
            row.SetShowChannelPicker(saved);
        }
    }

    private async Task CheckNowAsync()
    {
        if (_composition is null || IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Error = null;
        // Clears whatever Add from URL left on this line; install and remove both trigger a check.
        StatusText = null;
        IsRateLimited = false;
        try
        {
            // Discover has already been looked at this session: a fresh release or icon should
            // replace whatever that look found, not hide behind it.
            if (_discoverDetailsRequested)
            {
                ClearStaleDiscoverCaches();
            }

            PluginCheckOutcome outcome = await _composition
                .CheckAsync(_clientVersionResolver(), cancellation.Token)
                .ConfigureAwait(true);
            ApplyOutcome(outcome);
            if (_discoverDetailsRequested)
            {
                await RefreshDiscoverDetailsAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin list could not be checked."
                : ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            IsBusy = false;
        }
    }

    private void ApplyOutcome(PluginCheckOutcome outcome)
    {
        // The catalog the next launched session filters blocked ids against, regardless
        // of whether this pass fetched fresh or fell back to the cache.
        _orchestrator.SetPluginCatalog(outcome.Catalog);

        IsRateLimited = outcome.RateLimited;
        _listAgeUtc = outcome.ListAgeUtc;
        OnPropertyChanged(nameof(IsUsingCachedList));
        OnPropertyChanged(nameof(ListAgeText));
        if (IsRateLimited)
        {
            Error = "GitHub is rate limiting; try later.";
        }
        else if (outcome.Catalog is null)
        {
            Error = "Could not reach the plugin list.";
        }

        _allInstalled.Clear();
        foreach (InstalledPluginInfo info in outcome.Installed)
        {
            outcome.UpdatesAvailable.TryGetValue(info.Id, out PluginUpdateAvailability? availability);
            outcome.UpdateWithheldReasons.TryGetValue(info.Id, out string? withheldReason);
            _allInstalled.Add(BuildInstalledRow(info, availability, withheldReason));
        }

        _allDiscover.Clear();
        foreach (PluginDiscoverEntry entry in outcome.Discover)
        {
            // The row doesn't exist until the constructor returns; the install command's closure
            // reads it back through this local once construction has finished, the same pattern
            // BuildInstalledRow's own toggle callback uses.
            PluginDiscoverRowViewModel? self = null;
            var install = new RelayCommand(
                () => OpenDiscoverInstallDialog(entry, self!),
                () => _canInteract() && !IsBusy && self!.CanInstallSelectedChannel);
            var row = new PluginDiscoverRowViewModel(
                entry.Id, entry.Name, entry.Author, entry.Description, entry.Repo, install);
            self = row;
            row.SetShowChannelPicker(ShowBetaPlugins);
            if (_discoverDetailsCache.TryGetValue(entry.Id, out DiscoverDetails cached))
            {
                row.Icon = cached.Icon;
                row.SetChannelOptions(cached.Stable, cached.Beta);
                // Already resolved this session: shown right away, no re-checking wait.
                row.IsVisible = true;
            }

            _allDiscover.Add(row);
        }

        // A fresh list replaces whatever the last refresh pass was doing; nothing is checking
        // again until RefreshDiscoverDetailsAsync actually runs.
        _discoverPendingCount = 0;
        DiscoverStatusLine = null;
        UpdateDiscoverCheckingState();
        ApplyFilters();
    }

    /// <summary>Builds one Installed row from the inventory and its update check, reused by both a
    /// full Check pass and a single-plugin re-check after the beta toggle.</summary>
    private PluginInstalledRowViewModel BuildInstalledRow(
        InstalledPluginInfo info, PluginUpdateAvailability? availability, string? withheldReason)
    {
        bool canRemove = info.Source is InstalledPluginSource.Managed or InstalledPluginSource.Direct;
        bool updateAvailable = info.Source == InstalledPluginSource.Managed && availability is not null;
        bool showBetaToggle = info.Source == InstalledPluginSource.Managed;
        bool isBetaChannel = showBetaToggle
            && _composition!.RecordStore.Find(info.Id)?.Channel == PluginReleaseChannel.Beta;
        bool isPrerelease = LauncherVersion.TryParse(info.Version, out LauncherVersion? installedVersion)
            && installedVersion.IsPreRelease;
        IReadOnlyList<LauncherPluginCapabilityDeclaration> installedCapabilities = ReadInstalledCapabilities(info);
        bool updateCapabilitiesChanged = updateAvailable
            && !PluginInstaller.CapabilitiesMatch(installedCapabilities, availability!.Capabilities);
        RelayCommand? updateCommand = updateAvailable
            ? new RelayCommand(
                () => OpenUpdateDialog(
                    info, availability!.Tag, availability!.Version, installedCapabilities,
                    availability!.Capabilities),
                () => _canInteract() && !IsBusy)
            : null;
        RelayCommand? removeCommand = canRemove
            ? new RelayCommand(() => OpenRemoveDialog(info), () => _canInteract() && !IsBusy)
            : null;

        // The toggle callback needs the row it belongs to (to revert it on a lease refusal); the
        // row doesn't exist until the constructor returns, so the closure reads it back through
        // this local once construction has finished.
        PluginInstalledRowViewModel? self = null;
        var row = new PluginInstalledRowViewModel(
            info.Id,
            info.DisplayName,
            info.Version,
            DescribeSource(info.Source, info.ListedSource),
            info.Compatibility,
            info.CompatibilityIsWarning,
            info.Blocked,
            info.Conflict,
            canRemove,
            showBetaToggle,
            isBetaChannel,
            isPrerelease,
            updateAvailable,
            updateAvailable ? availability!.Version : null,
            updateAvailable ? availability!.CompatibilityNote : null,
            updateAvailable && availability!.CompatibilityIsWarning,
            updateCapabilitiesChanged,
            withheldReason,
            updateCommand,
            removeCommand,
            info.Refusal,
            info.HasDuplicate,
            onBetaToggled: isBeta => _ = ToggleBetaAsync(self!, isBeta),
            canToggleBeta: () => _canInteract() && !IsBusy,
            installedCapabilities,
            ResolveInstalledIcon(info.Id, info.Version, info.Directory),
            ShowBetaPlugins);
        self = row;
        return row;
    }

    /// <summary>The beta toggle's own write: sets the channel under the installer's
    /// exclusive lease, then re-checks that one plugin (never the full pass every other trigger
    /// runs) and rebuilds its row with the fresh result. A lease refusal reverts the toggle and is
    /// shown the way Remove shows it.</summary>
    private async Task ToggleBetaAsync(PluginInstalledRowViewModel row, bool isBeta)
    {
        if (_composition is null || IsBusy)
        {
            row.RevertBetaChannel(!isBeta);
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            _composition.Installer.SetChannel(
                row.Id, isBeta ? PluginReleaseChannel.Beta : PluginReleaseChannel.Stable);
            PluginSingleCheckResult result = await _composition
                .CheckSingleAsync(row.Id, _clientVersionResolver())
                .ConfigureAwait(true);
            ReplaceInstalledRow(row.Id, result.Available, result.WithheldReason);
        }
        catch (LauncherUpdateException ex)
        {
            row.RevertBetaChannel(!isBeta);
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin's channel could not be changed."
                : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Rebuilds one Installed row in place, keeping its position in both the full list and
    /// whatever the current search filter shows.</summary>
    private void ReplaceInstalledRow(
        string id, PluginUpdateAvailability? availability, string? withheldReason)
    {
        if (_composition is null)
        {
            return;
        }

        InstalledPluginInfo? info = _composition.Inventory.Find(
            id, _clientVersionResolver(), _composition.CurrentCatalog);
        if (info is null)
        {
            return;
        }

        PluginInstalledRowViewModel updated = BuildInstalledRow(info, availability, withheldReason);
        int allIndex = _allInstalled.FindIndex(
            row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase));
        if (allIndex >= 0)
        {
            _allInstalled[allIndex] = updated;
        }

        for (int index = 0; index < Installed.Count; index++)
        {
            if (string.Equals(Installed[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                Installed[index] = updated;
                break;
            }
        }
    }

    /// <summary>Opening Discover's own request (plan, "Request budget"): one <c>plugin.json</c> per
    /// listed, not-installed plugin still missing its details, cached here so switching tabs never
    /// re-fetches it. A listed plugin shows only once this resolves it to a usable release for the
    /// current channel; rows appear one at a time, in the curated list's own order, as each
    /// finishes.</summary>
    internal async Task RefreshDiscoverDetailsAsync()
    {
        if (_composition is null)
        {
            return;
        }

        _discoverDetailsRequested = true;
        PluginDiscoverRowViewModel[] pending = [.. _allDiscover
            .Where(row => !row.IsVisible && !_discoverDetailsCache.ContainsKey(row.Id))];
        _discoverPendingCount = pending.Length;
        bool sawRateLimited = false;
        bool sawTransportFailure = false;
        UpdateDiscoverCheckingState();

        foreach (PluginDiscoverRowViewModel row in pending)
        {
            ManifestFetch fetch = await FetchPluginManifestAsync(row.Repo, CancellationToken.None)
                .ConfigureAwait(true);
            switch (fetch.Status)
            {
                case PluginReleaseResolveStatus.RateLimited:
                    sawRateLimited = true;
                    break;
                case null:
                    sawTransportFailure = true;
                    break;
            }

            if (fetch.Manifest is { } manifest && fetch.Tag is { } tag)
            {
                LauncherVersion? remoteVersion = LauncherVersion.TryParse(
                    manifest.Version, out LauncherVersion? parsed)
                    ? parsed
                    : null;
                // A version-specific block can only be judged once the latest version is
                // known; a wildcard block never reaches here, since the Check pipeline already hid it.
                if (_composition.CurrentCatalog?.IsBlocked(row.Id, remoteVersion) != true)
                {
                    await ApplyDiscoverDetailsAsync(
                            row.Id, row.Repo, manifest, tag, fetch.Candidates!, CancellationToken.None)
                        .ConfigureAwait(true);
                    row.IsVisible = true;
                    if (Matches(
                        _discoverFilter.Trim(), row.Name, row.Id, row.Author, row.Description, row.Repo))
                    {
                        // Appended, never inserted: rows resolve in the curated list's own order, so
                        // the next one to arrive always belongs after everything shown so far.
                        Discover.Add(row);
                        OnPropertyChanged(nameof(HasDiscover));
                        OnPropertyChanged(nameof(ShowDiscoverEmptyText));
                    }
                }
            }

            _discoverPendingCount--;
            UpdateDiscoverCheckingState();
        }

        DiscoverStatusLine = sawRateLimited
            ? "GitHub is rate limiting; some plugins could not be checked. Try Refresh list in a minute."
            : sawTransportFailure
                ? "Some plugins could not be checked. Check your connection and Refresh list."
                : null;
    }

    /// <summary>Drops every resolved release and every failed icon fetch, so a Check pass the user
    /// asked for re-reads each listed plugin rather than repeating what an earlier look found. A
    /// decoded icon and Installed's own icon cache are left alone.</summary>
    private void ClearStaleDiscoverCaches()
    {
        _discoverDetailsCache.Clear();
        foreach ((string Id, string Version, bool Installed) key in _iconCache
            .Where(entry => !entry.Key.Installed && entry.Value is null)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _iconCache.Remove(key);
        }
    }

    /// <summary>The capabilities Discover hadn't fetched yet when Install was pressed: the same
    /// <c>plugin.json</c> fetch <see cref="RefreshDiscoverDetailsAsync"/> makes for every other row,
    /// run for this one plugin on demand so the install dialog never opens without knowing what it
    /// declares. Feeds the row and the shared cache exactly as the background pass does, so the row
    /// reflects it too.</summary>
    private async Task<IReadOnlyList<LauncherPluginCapabilityDeclaration>> LoadDiscoverCapabilitiesAsync(
        string pluginId, string repo, CancellationToken cancellationToken)
    {
        ManifestFetch fetch = await FetchPluginManifestAsync(repo, cancellationToken).ConfigureAwait(true);
        if (fetch.Manifest is not { } manifest || fetch.Tag is not { } tag)
        {
            throw new LauncherUpdateException(
                fetch.ErrorMessage ?? "This plugin's details could not be checked.");
        }

        await ApplyDiscoverDetailsAsync(pluginId, repo, manifest, tag, fetch.Candidates!, cancellationToken)
            .ConfigureAwait(true);
        return manifest.Capabilities;
    }

    /// <summary><see cref="Status"/> is null only for an exception the resolver itself threw (an
    /// oversized document): the one case a fetch never reached a <see cref="PluginReleaseResolveStatus"/>
    /// at all, which <see cref="RefreshDiscoverDetailsAsync"/> still treats as worth a panel status
    /// line, the same as <see cref="PluginReleaseResolveStatus.RateLimited"/>. <see cref="Candidates"/>
    /// carries both the stable and (on <see cref="PluginReleaseChannel.Beta"/>) the beta resolve, so
    /// <see cref="ApplyDiscoverDetailsAsync"/> can offer the row's channel picker both options from
    /// this one pass; non-null whenever a resolve was actually attempted.</summary>
    private readonly record struct ManifestFetch(
        LauncherPluginManifest? Manifest, string? Tag, string? ErrorMessage,
        PluginReleaseResolveStatus? Status, PluginReleaseCandidates? Candidates);

    /// <summary>Resolves through the plugin's channel, the same as the update check and
    /// Discover's background pass, so a beta plugin's Discover details and install/update consent
    /// always reflect the beta release rather than the last stable one.</summary>
    private async Task<ManifestFetch> FetchPluginManifestAsync(string repo, CancellationToken cancellationToken)
    {
        if (_composition is null)
        {
            return new ManifestFetch(null, null, "This plugin's details could not be checked.", null, null);
        }

        PluginReleaseCandidates candidates;
        try
        {
            candidates = await _composition.ReleaseResolver
                .ResolveCandidatesAsync(repo, DiscoverChannel, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (LauncherUpdateException)
        {
            return new ManifestFetch(null, null, "This plugin's details could not be checked.", null, null);
        }

        PluginReleaseResolveResult result = candidates.For(DiscoverChannel);
        switch (result.Status)
        {
            case PluginReleaseResolveStatus.RateLimited:
                return new ManifestFetch(
                    null, null, "GitHub is rate limiting; try later.", result.Status, candidates);
            case PluginReleaseResolveStatus.Success:
                break;
            case PluginReleaseResolveStatus.Prerelease:
                return new ManifestFetch(
                    null, null, result.Error ?? "This plugin's details could not be checked.", result.Status,
                    candidates);
            default:
                return new ManifestFetch(
                    null, null, "This plugin's details could not be checked.", result.Status, candidates);
        }

        PluginReleaseResolution resolution = result.Resolution!;
        return new ManifestFetch(resolution.Manifest, resolution.Tag, null, result.Status, candidates);
    }

    /// <summary>Writes a freshly fetched manifest's compatibility, capabilities and icon into the
    /// shared cache and, when the row is still on Discover, onto the row itself: the stable option
    /// always, and the beta option (<see cref="PluginReleaseCandidates.For"/>, so it never disagrees
    /// with the resolver's own precedence) only while Show beta plugins is on, since that is the only
    /// time the feed was even read.</summary>
    private async Task ApplyDiscoverDetailsAsync(
        string pluginId, string repo, LauncherPluginManifest manifest, string tag,
        PluginReleaseCandidates candidates, CancellationToken cancellationToken)
    {
        LauncherVersion? clientVersion = _clientVersionResolver()?.Version;
        Bitmap? icon = await ResolveDiscoverIconAsync(pluginId, repo, manifest, tag, cancellationToken)
            .ConfigureAwait(true);
        DiscoverChannelOption? stableOption = BuildChannelOption(candidates.Stable, clientVersion);
        DiscoverChannelOption? betaOption = ShowBetaPlugins
            ? BuildChannelOption(candidates.For(PluginReleaseChannel.Beta), clientVersion)
            : null;

        var details = new DiscoverDetails(stableOption, betaOption, icon);
        _discoverDetailsCache[pluginId] = details;

        PluginDiscoverRowViewModel? row = _allDiscover.FirstOrDefault(
            candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        row.Icon = details.Icon;
        row.SetShowChannelPicker(ShowBetaPlugins);
        row.SetChannelOptions(details.Stable, details.Beta);
    }

    private static DiscoverChannelOption? BuildChannelOption(
        PluginReleaseResolveResult result, LauncherVersion? clientVersion)
    {
        if (result.Status != PluginReleaseResolveStatus.Success)
        {
            return null;
        }

        PluginReleaseResolution resolution = result.Resolution!;
        LauncherPluginCompatibility.CompatibilityDescription compatibility =
            LauncherPluginCompatibility.Describe(resolution.Manifest, clientVersion);
        return new DiscoverChannelOption(
            resolution.Tag, resolution.Manifest.Version, compatibility.Text, compatibility.IsWarning,
            resolution.Manifest.Capabilities);
    }

    private readonly record struct DiscoverDetails(
        DiscoverChannelOption? Stable,
        DiscoverChannelOption? Beta,
        Bitmap? Icon);

    /// <summary>The Discover-side half of the icon rule: the tag the manifest fetch
    /// resolved to must name this exact version, so a plain 200 with no redirect (no tag) or a
    /// release that moved between the manifest and asset fetch never reaches the disk cache or the
    /// network. The disk cache is tried first, keyed the same way <see cref="_iconCache"/> is.</summary>
    private async Task<Bitmap?> ResolveDiscoverIconAsync(
        string pluginId,
        string repo,
        LauncherPluginManifest manifest,
        string? tag,
        CancellationToken cancellationToken)
    {
        if (_composition is null
            || tag is null
            || !manifest.MatchesTag(tag)
            || !LauncherVersion.TryParse(manifest.Version, out _)
            || !LauncherPluginManifest.IsWellFormedId(pluginId))
        {
            return null;
        }

        (string Id, string Version, bool Installed) key = (pluginId, manifest.Version, false);
        if (_iconCache.TryGetValue(key, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap? icon = await FetchDiscoverIconAsync(pluginId, repo, manifest.Version, tag, cancellationToken)
            .ConfigureAwait(true);
        StoreIcon(key, icon);
        return icon;
    }

    private async Task<Bitmap?> FetchDiscoverIconAsync(
        string pluginId, string repo, string version, string tag, CancellationToken cancellationToken)
    {
        string iconDirectory = Path.Combine(_composition!.Paths.CacheDirectory, "plugin-icons", pluginId);
        string iconPath = Path.Combine(iconDirectory, $"v{version}.png");
        byte[] bytes;
        bool fromDisk = File.Exists(iconPath);
        if (fromDisk)
        {
            bytes = await File.ReadAllBytesAsync(iconPath, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            PluginReleaseFetchResult fetch;
            try
            {
                fetch = await _composition.ReleaseClient
                    .FetchDocumentAsync(
                        GitHubReleaseLocator.TaggedAsset(repo, tag, LauncherPluginIcon.FileName),
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (LauncherUpdateException)
            {
                return null;
            }

            if (fetch.Status != PluginReleaseFetchStatus.Success)
            {
                return null;
            }

            bytes = fetch.Document!.Content;
        }

        try
        {
            LauncherPluginIcon.Validate(bytes);
        }
        catch (LauncherUpdateException)
        {
            return null;
        }

        if (!fromDisk)
        {
            WriteIconCache(iconDirectory, iconPath, bytes);
        }

        return DecodeIcon(bytes);
    }

    /// <summary>Only one file per id survives: an update's icon replaces the previous version's
    /// rather than accumulating one PNG per release ever seen.</summary>
    private static void WriteIconCache(string iconDirectory, string iconPath, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(iconDirectory);
            foreach (string existing in Directory.EnumerateFiles(iconDirectory, "v*.png"))
            {
                File.Delete(existing);
            }

            string tempPath = iconPath + ".tmp";
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, iconPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The Installed-side half of the icon rule: <see cref="PluginInstaller"/> already
    /// validated this file at install time, so this only guards against it having changed on disk
    /// since. Kept apart from Discover's entries in <see cref="_iconCache"/>: a plugin on disk need not
    /// carry the icon its published release does.</summary>
    private Bitmap? ResolveInstalledIcon(string id, string version, string directory)
    {
        (string Id, string Version, bool Installed) key = (id, version, true);
        if (_iconCache.TryGetValue(key, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap? icon = LoadInstalledIconFromDisk(directory);
        StoreIcon(key, icon);
        return icon;
    }

    private static Bitmap? LoadInstalledIconFromDisk(string directory)
    {
        string path = Path.Combine(directory, LauncherPluginIcon.FileName);
        byte[] bytes;
        try
        {
            using FileStream stream = File.OpenRead(path);
            var buffer = new byte[LauncherPluginIcon.MaximumBytes + 1];
            int total = 0;
            int read;
            while (total < buffer.Length
                && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            bytes = buffer[..total];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            LauncherPluginIcon.Validate(bytes);
        }
        catch (LauncherUpdateException)
        {
            return null;
        }

        return DecodeIcon(bytes);
    }

    private static Bitmap? DecodeIcon(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Discover's Install button: pins to whichever channel the row's own picker currently
    /// has selected, read from the row itself rather than a fresh cache lookup, so
    /// what was shown is what installs.</summary>
    private void OpenDiscoverInstallDialog(PluginDiscoverEntry entry, PluginDiscoverRowViewModel row)
    {
        if (row.SelectedOption is not { } option)
        {
            // Belt and braces behind the disabled button: an unpinned install would resolve latest
            // itself and land the very release the selected channel excludes.
            Error = row.ChannelUnavailableText;
            return;
        }

        OpenInstallDialog(
            entry.Repo, entry.Id, entry.Name, isUpdate: false, option.Tag, option.Version,
            option.Capabilities,
            cancellationToken => LoadDiscoverCapabilitiesAsync(entry.Id, entry.Repo, cancellationToken),
            channel: row.SelectedChannel);
    }

    /// <summary>Opens the install/update dialog for a repo. <paramref name="pinnedTag"/> is the
    /// release tag whichever caller already resolved (the update check, Discover's own details, or
    /// Add from URL); when Discover hasn't fetched details yet, it is null and Confirm resolves
    /// latest itself instead of the install ever doing so. <paramref name="offeredVersion"/>
    /// travels the same way, so the dialog's notice can name a pre-release offer.
    /// <paramref name="capabilities"/> is the declared list when already known; <see langword="null"/>
    /// means it still needs fetching, and <paramref name="loadCapabilities"/> is the fetch to run for
    /// it. <paramref name="channel"/> is the channel the player chose for this install
    /// (Discover's per-row picker); omitted for update and Add from URL, which keep
    /// today's version-inferred channel.</summary>
    private void OpenInstallDialog(
        string repo,
        string pluginId,
        string displayName,
        bool isUpdate,
        string? pinnedTag,
        string? offeredVersion,
        IReadOnlyList<LauncherPluginCapabilityDeclaration>? capabilities,
        Func<CancellationToken, Task<IReadOnlyList<LauncherPluginCapabilityDeclaration>>>? loadCapabilities = null,
        IReadOnlyList<LauncherPluginCapabilityDeclaration>? installedCapabilities = null,
        IReadOnlyList<string>? affectedCharacters = null,
        PluginReleaseChannel? channel = null)
    {
        if (_composition is null)
        {
            return;
        }

        bool isListed = _composition.CurrentCatalog?.Plugins.Any(entry =>
            string.Equals(entry.Repo, repo, StringComparison.Ordinal)) == true;
        InstallDialog.Open(
            repo,
            pluginId,
            displayName,
            isListed,
            isUpdate,
            offeredVersion,
            BuildCharacterOptions(),
            (displayedCapabilities, cancellationToken) =>
                InstallAsync(repo, pinnedTag, displayedCapabilities, channel, cancellationToken),
            EnableForCharacters,
            capabilities,
            loadCapabilities,
            installedCapabilities,
            affectedCharacters,
            StripFromEveryCharacter);
    }

    private void OpenUpdateDialog(
        InstalledPluginInfo info,
        string tag,
        string version,
        IReadOnlyList<LauncherPluginCapabilityDeclaration> installedCapabilities,
        IReadOnlyList<LauncherPluginCapabilityDeclaration> capabilities)
    {
        if (info.Repo is { } repo)
        {
            OpenInstallDialog(
                repo, info.Id, info.DisplayName, isUpdate: true, tag, version, capabilities,
                installedCapabilities: installedCapabilities,
                affectedCharacters: CharactersWithPluginEnabled(info.Id));
        }
    }

    /// <summary>Who to name in the update dialog's keep-enabled choice: every account whose list has
    /// this id, and every character whose own list has it, in the "name (server)" shape the
    /// enable-choice list uses.</summary>
    private IReadOnlyList<string> CharactersWithPluginEnabled(string pluginId)
    {
        var names = new List<string>();
        foreach (LauncherAccountSnapshot account in _orchestrator.GetSnapshot().Servers.SelectMany(server => server.Accounts))
        {
            if (account.Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
            {
                names.Add($"{account.AccountName} ({account.ServerName})");
            }

            names.AddRange(account.Characters
                .Where(character => character.HasOwnPlugins
                    && character.Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                .Select(character => $"{character.Name} ({account.AccountName}@{account.ServerName})"));
        }

        return names;
    }

    private async Task<PluginInstallResult> InstallAsync(
        string repo,
        string? pinnedTag,
        IReadOnlyList<LauncherPluginCapabilityDeclaration> displayedCapabilities,
        PluginReleaseChannel? channel,
        CancellationToken cancellationToken)
    {
        string tag = pinnedTag
            ?? await ResolveLatestTagAsync(repo, channel ?? PluginReleaseChannel.Stable, cancellationToken)
                .ConfigureAwait(true);
        PluginInstallResult result = await _composition!.Installer.InstallOrUpdateAsync(
                repo,
                tag,
                _composition.CurrentCatalog,
                _clientVersionResolver(),
                displayedCapabilities,
                channel,
                cancellationToken)
            .ConfigureAwait(true);
        _ = CheckNowAsync();
        return result;
    }

    /// <summary>Discover's own fallback when its details fetch hasn't populated a tag yet: resolved
    /// once, right before install, never inside <see cref="PluginInstaller.InstallOrUpdateAsync"/>
    /// itself.</summary>
    private async Task<string> ResolveLatestTagAsync(
        string repo, PluginReleaseChannel channel, CancellationToken cancellationToken)
    {
        PluginReleaseResolveResult result = await _composition!.ReleaseResolver
            .ResolveAsync(repo, channel, cancellationToken)
            .ConfigureAwait(true);
        return result.Status switch
        {
            PluginReleaseResolveStatus.Success => result.Resolution!.Tag,
            PluginReleaseResolveStatus.RateLimited =>
                throw new LauncherUpdateException("GitHub is rate limiting; try later."),
            PluginReleaseResolveStatus.Prerelease =>
                throw new LauncherUpdateException(result.Error!),
            PluginReleaseResolveStatus.Invalid =>
                throw new LauncherUpdateException("That repository's plugin.json could not be read."),
            _ => throw new LauncherUpdateException("The plugin release is unavailable."),
        };
    }

    /// <summary>The install dialog's only profile write: the plugin joins the plugin list of each
    /// account chosen there, which every character on it without a list of its own launches with.
    /// Runs after the dialog has already closed (install succeeded), so any trouble here is reported
    /// on the panel, not the dialog.</summary>
    internal void EnableForCharacters(string pluginId, IReadOnlyList<PluginCharacterOption> accounts)
    {
        List<LauncherAccountSnapshot> snapshots = [.. _orchestrator.GetSnapshot().Servers
            .SelectMany(server => server.Accounts)];
        try
        {
            foreach (PluginCharacterOption option in accounts)
            {
                LauncherAccountSnapshot? account = snapshots.FirstOrDefault(candidate =>
                    string.Equals(candidate.ServerName, option.ServerName, StringComparison.Ordinal)
                    && string.Equals(candidate.AccountName, option.AccountName, StringComparison.Ordinal));
                if (account is null || account.Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                _orchestrator.UpdateAccountPlugins(account.ServerName, account.AccountName, [.. account.Plugins, pluginId]);
            }
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin installed, but could not be enabled for every chosen account."
                : ex.Message;
        }
    }

    private string InstalledDisplayName(string pluginId) =>
        _composition?.Inventory.Find(pluginId, _clientVersionResolver(), _composition.CurrentCatalog)
            ?.DisplayName
        ?? pluginId;

    /// <summary>The installed row's own count, read straight from its <c>plugin.json</c>, since
    /// <see cref="InstalledPluginInfo"/> does not carry it.</summary>
    private static IReadOnlyList<LauncherPluginCapabilityDeclaration> ReadInstalledCapabilities(
        InstalledPluginInfo info)
    {
        try
        {
            return LauncherPluginManifest.Parse(
                File.ReadAllText(Path.Combine(info.Directory, "plugin.json"))).Capabilities;
        }
        catch (Exception ex) when (ex is IOException or LauncherPluginManifestException)
        {
            return [];
        }
    }

    /// <summary>The install dialog's enable choices: one per account, named "account (server)".</summary>
    private IReadOnlyList<PluginCharacterOption> BuildCharacterOptions() =>
        [.. _orchestrator.GetSnapshot().Servers
            .SelectMany(server => server.Accounts)
            .Select(account => new PluginCharacterOption(
                account.ServerName,
                account.AccountName,
                string.Empty,
                $"{account.AccountName} ({account.ServerName})"))];

    private async Task AddFromUrlAsync()
    {
        if (_composition is null || IsBusy)
        {
            return;
        }

        if (!GitHubReleaseLocator.TryParseRepoUrl(AddFromUrlText, out string repo))
        {
            Error = "Enter a URL like https://github.com/owner/name.";
            return;
        }

        InstalledPluginRecord? installedByRepo = _composition.RecordStore.Records.FirstOrDefault(
            record => string.Equals(record.Repo, repo, StringComparison.OrdinalIgnoreCase));
        if (installedByRepo is not null)
        {
            AddFromUrlText = string.Empty;
            StatusText = $"{InstalledDisplayName(installedByRepo.Id)} is already installed.";
            return;
        }

        IsBusy = true;
        Error = null;
        StatusText = null;
        try
        {
            PluginReleaseResolveResult result = await _composition.ReleaseResolver
                .ResolveAsync(repo, DiscoverChannel)
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case PluginReleaseResolveStatus.Success:
                    PluginReleaseResolution resolution = result.Resolution!;
                    LauncherPluginManifest manifest = resolution.Manifest;
                    AddFromUrlText = string.Empty;
                    // Same repo already installed (a race with another Add or Check between the
                    // resolve above and here): same neutral status as the early check above.
                    InstalledPluginRecord? installedById = _composition.RecordStore.Find(manifest.Id);
                    if (installedById is not null
                        && string.Equals(installedById.Repo, repo, StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText = $"{manifest.DisplayName} is already installed.";
                        break;
                    }

                    // The id is already installed from a different repo: refuse up front with the
                    // same reason PluginInstaller would give, rather than opening a dialog whose
                    // Install could only fail.
                    if (installedById is not null)
                    {
                        Error = $"'{manifest.Id}' is already installed from '{installedById.Repo}'.";
                        break;
                    }

                    // Blocked for "*" or for this fetched version: refuse the same way,
                    // rather than opening a dialog whose Install could only fail.
                    LauncherVersion? manifestVersion = LauncherVersion.TryParse(
                        manifest.Version, out LauncherVersion? parsedVersion)
                        ? parsedVersion
                        : null;
                    if (_composition.CurrentCatalog?.BlockReason(manifest.Id, manifestVersion)
                        is { } blockReason)
                    {
                        Error = $"'{manifest.Id}' is blocked: {blockReason}";
                        break;
                    }

                    OpenInstallDialog(
                        repo, manifest.Id, manifest.DisplayName, isUpdate: false,
                        resolution.Tag, manifest.Version, manifest.Capabilities);
                    break;
                case PluginReleaseResolveStatus.RateLimited:
                    Error = "GitHub is rate limiting; try later.";
                    break;
                case PluginReleaseResolveStatus.Invalid:
                    Error = "That repository's plugin.json could not be read.";
                    break;
                case PluginReleaseResolveStatus.Prerelease:
                    Error = result.Error!;
                    break;
                default:
                    Error = "No release was found for that repository.";
                    break;
            }
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message) ? "Could not add that plugin." : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenRemoveDialog(InstalledPluginInfo info)
    {
        _removeTarget = info;
        RemoveDisplayName = info.DisplayName;
        RemoveDeleteStorage = false;
        Error = null;
        IsRemoveDialogOpen = true;
    }

    /// <summary>Escape's path to the remove dialog, matching <see cref="PluginInstallDialogViewModel.Close"/>:
    /// a no-op while the removal itself is running.</summary>
    public void CloseRemoveDialog()
    {
        if (IsBusy)
        {
            return;
        }

        IsRemoveDialogOpen = false;
    }

    private void ConfirmRemove()
    {
        if (_composition is null || _removeTarget is not { } target)
        {
            return;
        }

        try
        {
            if (target.Source == InstalledPluginSource.Direct)
            {
                _composition.Installer.RemoveDirect(target.Directory, RemoveDeleteStorage);
            }
            else
            {
                _composition.Installer.Remove(target.Id, RemoveDeleteStorage);
            }
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin could not be removed."
                : ex.Message;
            return;
        }

        IsRemoveDialogOpen = false;
        _ = CheckNowAsync();
        StripFromEveryCharacter(target.Id);
    }

    /// <summary>The remove dialog's own profile write: every account list and every character's own
    /// list still holding the removed id loses it, so reinstalling it never inherits an old enable.
    /// Runs after <see cref="PluginInstaller.Remove"/> has already succeeded, so trouble here is
    /// reported without undoing the removal.</summary>
    private void StripFromEveryCharacter(string pluginId)
    {
        List<LauncherAccountSnapshot> accounts = [.. _orchestrator.GetSnapshot().Servers
            .SelectMany(server => server.Accounts)];
        static List<string> Without(IEnumerable<string> plugins, string id) =>
            [.. plugins.Where(plugin => !string.Equals(plugin, id, StringComparison.OrdinalIgnoreCase))];
        try
        {
            foreach (LauncherAccountSnapshot account in accounts)
            {
                if (account.Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                {
                    _orchestrator.UpdateAccountPlugins(account.ServerName, account.AccountName, Without(account.Plugins, pluginId));
                }

                foreach (LauncherCharacterSnapshot character in account.Characters.Where(character =>
                    character.HasOwnPlugins && character.Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase)))
                {
                    _orchestrator.UpdateCharacterSettings(
                        character.ServerName,
                        character.AccountName,
                        character.Name,
                        character.LaunchMode,
                        Without(character.Plugins, pluginId));
                }
            }
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin was removed, but could not be unchecked everywhere."
                : ex.Message;
        }
    }

    public void NotifyCommandStates()
    {
        CheckNowCommand.NotifyCanExecuteChanged();
        AddFromUrlCommand.NotifyCanExecuteChanged();
        ConfirmRemoveCommand.NotifyCanExecuteChanged();
        CancelRemoveCommand.NotifyCanExecuteChanged();
        foreach (PluginDiscoverRowViewModel row in Discover)
        {
            row.InstallCommand.NotifyCanExecuteChanged();
        }

        foreach (PluginInstalledRowViewModel row in Installed)
        {
            row.UpdateCommand?.NotifyCanExecuteChanged();
            row.RemoveCommand?.NotifyCanExecuteChanged();
            row.NotifyBetaToggleEnabledChanged();
        }
    }

    private static string DescribeSource(InstalledPluginSource source, PluginInstallSource? listedSource) =>
        source switch
        {
            InstalledPluginSource.Managed => listedSource == PluginInstallSource.Unlisted
                ? "Unlisted"
                : "Listed",
            InstalledPluginSource.Direct => "Direct install",
            InstalledPluginSource.Bundled => "Bundled",
            _ => source.ToString(),
        };

    private static string FormatAge(TimeSpan age) => age.TotalDays >= 1
        ? $"{age.TotalDays:0} day(s)"
        : age.TotalHours >= 1
            ? $"{age.TotalHours:0} hour(s)"
            : $"{Math.Max(1, age.TotalMinutes):0} minute(s)";

    /// <summary>Replaces one cache entry, disposing whatever bitmap it held: the only place an icon
    /// is ever disposed short of <see cref="Dispose"/> itself.</summary>
    private void StoreIcon((string Id, string Version, bool Installed) key, Bitmap? icon)
    {
        if (_iconCache.Remove(key, out Bitmap? previous))
        {
            previous?.Dispose();
        }

        _iconCache[key] = icon;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Bitmap? icon in _iconCache.Values)
        {
            icon?.Dispose();
        }

        _iconCache.Clear();
    }

    private sealed class IconCacheKeyComparer : IEqualityComparer<(string Id, string Version, bool Installed)>
    {
        public bool Equals((string Id, string Version, bool Installed) x, (string Id, string Version, bool Installed) y) =>
            string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Version, y.Version, StringComparison.Ordinal)
            && x.Installed == y.Installed;

        public int GetHashCode((string Id, string Version, bool Installed) obj) =>
            HashCode.Combine(
                obj.Id.GetHashCode(StringComparison.OrdinalIgnoreCase),
                obj.Version.GetHashCode(StringComparison.Ordinal),
                obj.Installed);
    }
}
