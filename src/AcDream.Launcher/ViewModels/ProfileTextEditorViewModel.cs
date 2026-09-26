using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.ViewModels;

/// <summary>The clipboard, as the text editors' Copy all and Paste all use it.</summary>
public interface IProfileEditorClipboard
{
    Task SetTextAsync(string text);

    Task<string?> GetTextAsync();
}

/// <summary>
/// The profile editors: accounts and logon commands as one plain text each, servers as name and
/// address fields. The text is checked as a whole when saved; a wrong line is reported by number
/// and nothing is saved.
/// </summary>
public sealed partial class ProfileTextEditorViewModel : ObservableObject
{
    /// <summary>What a hidden password shows as.</summary>
    public const string HiddenPassword = "••••••";

    private readonly ILauncherOrchestrator _orchestrator;
    private IProfileEditorClipboard? _clipboard;
    private bool _isOpen;
    private bool _showPasswords;
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
        CopyAllCommand = new AsyncRelayCommand(CopyAllAsync, () => IsOpen && IsTextEditor && _clipboard is not null);
        PasteAllCommand = new AsyncRelayCommand(PasteAllAsync, () => IsOpen && IsTextEditor && _clipboard is not null);
    }

    public bool IsOpen { get => _isOpen; private set { if (SetProperty(ref _isOpen, value)) NotifyCommands(); } }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string HelpText { get => _helpText; private set => SetProperty(ref _helpText, value); }

    /// <summary>The text itself, passwords included.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? ""))
            {
                OnPropertyChanged(nameof(DisplayedText));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    /// <summary>What the text box shows: the text, with passwords hidden unless Show passwords is on.
    /// While they are hidden the box is read-only, so a hidden password is never saved.</summary>
    public string DisplayedText
    {
        get => IsPasswordHidden ? HidePasswords(Text) : Text;
        set
        {
            if (!IsPasswordHidden)
            {
                Text = value;
            }
        }
    }

    public bool IsAccountsEditor => _kind == LauncherTextEditorKind.Accounts;

    /// <summary>Show passwords, in the accounts editor. Off by default, for a shared screen.</summary>
    public bool ShowPasswords
    {
        get => _showPasswords;
        set
        {
            if (SetProperty(ref _showPasswords, value))
            {
                OnPropertyChanged(nameof(IsPasswordHidden));
                OnPropertyChanged(nameof(DisplayedText));
            }
        }
    }

    public bool IsPasswordHidden => IsAccountsEditor && !ShowPasswords;

    /// <summary>"2 servers, 8 accounts" under the logon commands, counted from the text as typed.</summary>
    public string Summary
    {
        get
        {
            if (_kind != LauncherTextEditorKind.LogonCommands) return "";
            string[] lines = [.. Text.Split('\n').Select(line => line.Trim())];
            int servers = lines.Count(line => line.StartsWith('#') && !line.StartsWith("##", StringComparison.Ordinal));
            int accounts = lines.Count(line => line.StartsWith("##", StringComparison.Ordinal));
            return $"{servers} {(servers == 1 ? "server" : "servers")}, {accounts} {(accounts == 1 ? "account" : "accounts")}"
                + " · an account with no lines runs no commands";
        }
    }

    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddRowCommand { get; }
    public AsyncRelayCommand CopyAllCommand { get; }
    public AsyncRelayCommand PasteAllCommand { get; }
    public ObservableCollection<ProfileEntryViewModel> Rows { get; } = [];
    public bool IsFieldsEditor => _kind == LauncherTextEditorKind.Servers;
    public bool IsTextEditor => !IsFieldsEditor;
    public bool IsLogonCommandsEditor => _kind == LauncherTextEditorKind.LogonCommands;
    public string FirstColumnLabel => "Server name";
    public string SecondColumnLabel => "Address:port";
    public string AddRowText => "Add server";

    /// <summary>Gives Copy all and Paste all the clipboard; the view supplies it.</summary>
    public void UseClipboard(IProfileEditorClipboard clipboard)
    {
        _clipboard = clipboard;
        NotifyCommands();
    }

    public void Open(LauncherTextEditorKind kind)
    {
        _kind = kind;
        Title = kind switch
        {
            LauncherTextEditorKind.Accounts => "Accounts",
            LauncherTextEditorKind.Servers => "Edit servers",
            _ => "Logon commands",
        };
        HelpText = kind switch
        {
            LauncherTextEditorKind.Accounts =>
                "One account per line, in plain text, under its #Server line. Profiles tag accounts so you can filter and play them together.",
            LauncherTextEditorKind.Servers =>
                "Enter a server name and address including its port, such as game.example.com:9000. Keep the name to retain saved characters.",
            _ => "Every server and account you have, in one plain text. Copy it all, paste it all.",
        };
        Error = null;
        ShowPasswords = false;
        _originalText = _orchestrator.ReadProfileText(kind);
        Text = IsTextEditor ? _originalText : "";
        Rows.Clear();
        if (IsFieldsEditor)
        {
            var draft = new LauncherProfileDocument();
            LauncherProfileText.Apply(draft, kind, _originalText);
            foreach (var server in draft.Servers)
                AddRow(server.Name, FormatEndpoint(server.Host, server.Port));
            if (Rows.Count == 0) AddRow();
        }

        foreach (string property in new[]
                 {
                     nameof(IsFieldsEditor), nameof(IsTextEditor), nameof(IsAccountsEditor), nameof(IsLogonCommandsEditor),
                     nameof(IsPasswordHidden), nameof(DisplayedText), nameof(Summary),
                 })
            OnPropertyChanged(property);
        IsOpen = true;
        NotifyCommands();
    }

    public void Close()
    {
        IsOpen = false;
        Text = _originalText = "";
        ShowPasswords = false;
        foreach (var row in Rows) row.Value = "";
        Rows.Clear();
        Error = null;
        NotifyCommands();
    }

    private void Save()
    {
        try { _orchestrator.SaveProfileText(_kind, IsFieldsEditor ? ReadFields() : Text, _originalText); Close(); }
        catch (Exception ex) when (ex is LauncherProfileException or LauncherOperationException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex is LauncherProfileException or LauncherOperationException
                ? ex.Message
                : "Unable to save. Stop active sessions and check that the profile folder is writable.";
        }
    }

    private async Task CopyAllAsync()
    {
        if (_clipboard is { } clipboard) await clipboard.SetTextAsync(Text).ConfigureAwait(true);
    }

    private async Task PasteAllAsync()
    {
        if (_clipboard is not { } clipboard) return;
        string? pasted = await clipboard.GetTextAsync().ConfigureAwait(true);
        if (pasted is not null && IsOpen) Text = pasted;
    }

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        AddRowCommand.NotifyCanExecuteChanged();
        CopyAllCommand.NotifyCanExecuteChanged();
        PasteAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The accounts text with every password replaced by <see cref="HiddenPassword"/>.</summary>
    internal static string HidePasswords(string text) =>
        PasswordField().Replace(text, match => match.Groups["key"].Value + HiddenPassword);

    [GeneratedRegex("(?<key>(?:^|,)[ \\t]*Password[ \\t]*=)(?:[ \\t]*\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"[ \\t]*|[^,\\r\\n]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PasswordField();

    private void AddRow(string name = "", string value = "") => Rows.Add(new ProfileEntryViewModel(
        name, value, isAccount: false, row =>
        {
            row.Value = "";
            Rows.Remove(row);
        }));

    private string ReadFields()
    {
        var draft = new LauncherProfileDocument
        {
            Servers = [.. Rows.Select(row =>
            {
                (string host, int port) = ParseEndpoint(row.Value);
                return new ServerProfile { Name = row.Name, Host = host, Port = port };
            })],
        };
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
