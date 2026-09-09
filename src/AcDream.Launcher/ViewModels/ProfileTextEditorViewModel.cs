using System.Collections.ObjectModel;
using System.Globalization;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.ViewModels;

public sealed class ProfileTextEditorViewModel : ObservableObject
{
    private readonly ILauncherOrchestrator _orchestrator;
    private bool _isOpen;
    private string _title = "";
    private string _helpText = "";
    private string _text = "";
    private string? _error;
    private string _originalText = "";
    private LauncherTextEditorKind _kind;

    public ProfileTextEditorViewModel(ILauncherOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        SaveCommand = new RelayCommand(Save, () => IsOpen);
        CancelCommand = new RelayCommand(Close, () => IsOpen);
        AddRowCommand = new RelayCommand(() => AddRow(), () => IsOpen && IsFieldsEditor);
    }

    public bool IsOpen { get => _isOpen; private set { if (SetProperty(ref _isOpen, value)) { SaveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); } } }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string HelpText { get => _helpText; private set => SetProperty(ref _helpText, value); }
    public string Text { get => _text; set => SetProperty(ref _text, value); }
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddRowCommand { get; }
    public ObservableCollection<ProfileEntryViewModel> Rows { get; } = [];
    public bool IsFieldsEditor => _kind != LauncherTextEditorKind.LogonCommands;
    public bool IsTextEditor => !IsFieldsEditor;
    public string FirstColumnLabel => _kind == LauncherTextEditorKind.Users ? "Username" : "Server name";
    public string SecondColumnLabel => _kind == LauncherTextEditorKind.Users ? "Password" : "Address:port";
    public string AddRowText => _kind == LauncherTextEditorKind.Users ? "Add account" : "Add server";

    public void Open(LauncherTextEditorKind kind)
    {
        _kind = kind;
        Title = kind switch { LauncherTextEditorKind.Users => "Edit accounts", LauncherTextEditorKind.Servers => "Edit servers", _ => "Logon commands" };
        HelpText = kind switch
        {
            LauncherTextEditorKind.Users => "Each account is available on every configured server. Passwords may be left empty.",
            LauncherTextEditorKind.Servers => "Enter a server name and address including its port, such as game.example.com:9000. Keep the name to retain saved characters.",
            _ => "Commands run for the named character after login. Keep server, account and character unchanged; edit commands in order. Use [] for no commands. Removing an entry clears its commands.",
        };
        Error = null;
        _originalText = _orchestrator.ReadProfileText(kind);
        Text = IsTextEditor ? _originalText : "";
        Rows.Clear();
        if (IsFieldsEditor)
        {
            var draft = new LauncherProfileDocument();
            if (kind == LauncherTextEditorKind.Users)
                foreach (var user in LauncherProfileText.ParseUsers(_originalText)) AddRow(user.Account, user.Password);
            else
            {
                LauncherProfileText.Apply(draft, kind, _originalText);
                foreach (var server in draft.Servers)
                    AddRow(server.Name, FormatEndpoint(server.Host, server.Port));
            }
            if (Rows.Count == 0) AddRow();
        }
        foreach (string property in new[] { nameof(IsFieldsEditor), nameof(IsTextEditor), nameof(FirstColumnLabel), nameof(SecondColumnLabel), nameof(AddRowText) })
            OnPropertyChanged(property);
        IsOpen = true;
        AddRowCommand.NotifyCanExecuteChanged();
    }

    public void Close()
    {
        IsOpen = false;
        Text = _originalText = "";
        foreach (var row in Rows) row.Value = "";
        Rows.Clear();
        Error = null;
        AddRowCommand.NotifyCanExecuteChanged();
    }

    private void Save()
    {
        try { _orchestrator.SaveProfileText(_kind, IsFieldsEditor ? ReadFields() : Text, _originalText); Close(); }
        catch (Exception ex) when (ex is LauncherProfileException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex is LauncherProfileException ? ex.Message : "Unable to save. Stop active sessions and check that the profile folder is writable.";
        }
    }

    private void AddRow(string name = "", string value = "") => Rows.Add(new ProfileEntryViewModel(
        name, value, _kind == LauncherTextEditorKind.Users, row =>
        {
            row.Value = "";
            Rows.Remove(row);
        }));

    private string ReadFields()
    {
        var draft = new LauncherProfileDocument();
        if (_kind == LauncherTextEditorKind.Users)
            draft.Users = Rows.Select(row => new LauncherUser(row.Name, row.Value)).ToList();
        else
            draft.Servers = Rows.Select(row =>
            {
                (string host, int port) = ParseEndpoint(row.Value);
                return new ServerProfile { Name = row.Name, Host = host, Port = port };
            }).ToList();
        return LauncherProfileText.Read(draft, _kind);
    }

    private static string FormatEndpoint(string host, int port) =>
        $"{(host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host)}:{port}";

    private static (string Host, int Port) ParseEndpoint(string value)
    {
        string address = value.Trim();
        int separator = address.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(address.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new LauncherProfileException("Enter an address and port, for example game.example.com:9000. Ports must be from 1 to 65535.");
        string host = address[..separator];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
            if (Uri.CheckHostName(host) == UriHostNameType.IPv6) return (host, port);
        }
        else if (Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4)
            return (host, port);
        throw new LauncherProfileException("Enter a hostname or IP address followed by its port. Use [IPv6 address]:port for IPv6.");
    }
}
