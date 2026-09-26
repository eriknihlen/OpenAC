using System.Collections.ObjectModel;
using System.Globalization;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Status;

namespace AcDream.Launcher.ViewModels;

/// <summary>One known server in the Add a server dialog.</summary>
public sealed class KnownServerRowViewModel : ObservableObject
{
    private bool _isAdded;

    public KnownServerRowViewModel(KnownServer server, Action<KnownServerRowViewModel> add)
    {
        Server = server;
        AddCommand = new RelayCommand(() => add(this), () => !IsAdded);
    }

    public KnownServer Server { get; }

    public string Name => Server.Name;

    public string Endpoint => $"{Server.Host}:{Server.Port.ToString(CultureInfo.InvariantCulture)}";

    public string Type => Server.Type ?? "";

    public string Players => Server.PlayerCount is { } count ? count.ToString("N0", CultureInfo.InvariantCulture) : "—";

    public string About => Server.Description ?? "";

    public Uri? Website => Server.Website;

    public Uri? Discord => Server.Discord;

    public bool HasWebsite => Website is not null;

    public bool HasDiscord => Discord is not null;

    public bool IsAdded
    {
        get => _isAdded;
        set
        {
            if (SetProperty(ref _isAdded, value))
            {
                OnPropertyChanged(nameof(IsNotAdded));
                AddCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsNotAdded => !IsAdded;

    public RelayCommand AddCommand { get; }
}

/// <summary>
/// Add a server: one of your own by name, host and port, or one from the list of known public
/// servers with its player count. The known list is saved each time it loads, so it also opens
/// offline; with nothing saved, only your own server can be added.
/// </summary>
public sealed class AddServerDialogViewModel : ObservableObject
{
    public const string AllTypes = "All types";

    private readonly ILauncherOrchestrator _orchestrator;
    private KnownServerCatalog? _catalog;
    private IReadOnlyList<KnownServerRowViewModel> _all = [];
    private bool _isOpen;
    private bool _isLoading;
    private string _name = "";
    private string _host = "";
    private string _port = "9000";
    private string _search = "";
    private string _type = AllTypes;
    private string? _error;
    private string _listStatus = "";
    private string? _notice;

    public AddServerDialogViewModel(ILauncherOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        AddOwnCommand = new RelayCommand(AddOwn);
        CloseCommand = new RelayCommand(Close);
    }

    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    public string Name { get => _name; set => SetProperty(ref _name, value ?? ""); }

    public string Host { get => _host; set => SetProperty(ref _host, value ?? ""); }

    public string Port { get => _port; set => SetProperty(ref _port, value ?? ""); }

    public string Search { get => _search; set { if (SetProperty(ref _search, value ?? "")) ApplyFilter(); } }

    public ObservableCollection<string> Types { get; } = [AllTypes];

    public string SelectedType { get => _type; set { if (SetProperty(ref _type, value ?? AllTypes)) ApplyFilter(); } }

    /// <summary>The known servers the search and type filter show.</summary>
    public ObservableCollection<KnownServerRowViewModel> Servers { get; } = [];

    public bool HasKnownServers => _all.Count > 0;

    /// <summary>"47 servers · list and player counts from TreeStats", or why there is no list.</summary>
    public string ListStatus { get => _listStatus; private set => SetProperty(ref _listStatus, value); }

    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>What was just added, and what to do next.</summary>
    public string? Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    public RelayCommand AddOwnCommand { get; }

    public RelayCommand CloseCommand { get; }

    public void UseCatalog(KnownServerCatalog catalog) => _catalog = catalog;

    /// <summary>Opens the dialog and loads the known servers in the background.</summary>
    public Task OpenAsync()
    {
        Error = null;
        Notice = null;
        Name = "";
        Host = "";
        Port = "9000";
        IsOpen = true;
        return LoadAsync();
    }

    public void Close() => IsOpen = false;

    private async Task LoadAsync()
    {
        if (_catalog is null)
        {
            ListStatus = "The known server list is not available; add your own server above.";
            return;
        }

        IsLoading = true;
        ListStatus = "Loading the known servers…";
        KnownServerList? list;
        try
        {
            list = await _catalog.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Opened from a button, with nothing above it to catch: a list that fails in a way the
            // catalog did not expect still leaves the dialog usable for your own server.
            _all = [];
            Servers.Clear();
            OnPropertyChanged(nameof(HasKnownServers));
            ListStatus = $"The known server list could not be loaded ({ex.Message}). You can still add your own server.";
            return;
        }
        finally
        {
            IsLoading = false;
        }

        ShowList(list);
    }

    internal void ShowList(KnownServerList? list)
    {
        _all = list is null
            ? []
            : [.. list.Servers.Select(server => new KnownServerRowViewModel(server, AddKnown))];
        string[] types = [AllTypes, .. _all.Select(row => row.Type).Where(type => type.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        string selected = types.Contains(_type) ? _type : AllTypes;
        // Changed in place, never cleared: clearing would drop the box's selection.
        foreach (string gone in Types.Where(type => !types.Contains(type)).ToArray())
        {
            Types.Remove(gone);
        }

        foreach (string type in types.Where(type => !Types.Contains(type)))
        {
            Types.Add(type);
        }

        SelectedType = selected;

        ListStatus = list is null
            ? "Offline: the known server list could not be loaded. You can still add your own server."
            : $"{list.Servers.Count} servers · list and player counts from TreeStats"
              + (list.IsFromCache
                  ? " · offline copy from " + list.SavedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
                  : "");
        OnPropertyChanged(nameof(HasKnownServers));
        RefreshAdded();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string search = Search.Trim();
        Servers.Clear();
        foreach (KnownServerRowViewModel row in _all)
        {
            bool typeMatches = SelectedType == AllTypes || string.Equals(row.Type, SelectedType, StringComparison.OrdinalIgnoreCase);
            bool searchMatches = search.Length == 0
                || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Endpoint.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.About.Contains(search, StringComparison.OrdinalIgnoreCase);
            if (typeMatches && searchMatches)
            {
                Servers.Add(row);
            }
        }
    }

    /// <summary>A known server shows as added when a profile server has its name or its host and port.</summary>
    internal void RefreshAdded()
    {
        IReadOnlyList<LauncherServerSnapshot> servers = _orchestrator.GetSnapshot().Servers;
        foreach (KnownServerRowViewModel row in _all)
        {
            row.IsAdded = servers.Any(server =>
                string.Equals(server.Name, row.Name, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(server.Host, row.Server.Host, StringComparison.OrdinalIgnoreCase)
                    && server.Port == row.Server.Port));
        }
    }

    private void AddKnown(KnownServerRowViewModel row)
    {
        if (TryAdd(row.Name, row.Server.Host, row.Server.Port))
        {
            RefreshAdded();
        }
    }

    private void AddOwn()
    {
        string name = Name.Trim();
        string host = Host.Trim();
        if (name.Length == 0 || host.Length == 0)
        {
            Error = "Enter a name and a host for your server.";
            return;
        }

        if (!int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            Error = "The port is a number from 1 to 65535.";
            return;
        }

        if (TryAdd(name, host, port))
        {
            Name = "";
            Host = "";
            Port = "9000";
            RefreshAdded();
        }
    }

    private bool TryAdd(string name, string host, int port)
    {
        try
        {
            _orchestrator.AddServer(name, host, port);
            Error = null;
            Notice = $"Added {name}. Add your accounts on it with Accounts.";
            return true;
        }
        catch (Exception ex) when (ex is LauncherProfileException or LauncherOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Notice = null;
            Error = ex is IOException or UnauthorizedAccessException
                ? "Unable to save. Check that the profile folder is writable."
                : ex.Message;
            return false;
        }
    }
}
