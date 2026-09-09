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
    }

    public bool IsOpen { get => _isOpen; private set { if (SetProperty(ref _isOpen, value)) { SaveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); } } }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string HelpText { get => _helpText; private set => SetProperty(ref _helpText, value); }
    public string Text { get => _text; set => SetProperty(ref _text, value); }
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    public void Open(LauncherTextEditorKind kind)
    {
        _kind = kind;
        Title = kind switch { LauncherTextEditorKind.Users => "Edit Users", LauncherTextEditorKind.Servers => "Edit Servers", _ => "Logon commands" };
        HelpText = kind switch
        {
            LauncherTextEditorKind.Users => "One user per line: username | password | server1, server2. Omit servers to use all configured servers. Empty passwords are allowed. Put values containing | or commas in double quotes (JSON escapes). Removing an association removes its saved characters.",
            LauncherTextEditorKind.Servers => "One server per line: name | host | port. Example: Local | 127.0.0.1 | 9000. Put names containing | or commas in double quotes. Keep the name to retain its accounts; renaming or removing it removes its account associations.",
            _ => "Commands run for the named character after login. Keep server, account and character unchanged; edit commands in order. Use [] for no commands. Removing an entry clears its commands.",
        };
        Error = null;
        Text = _originalText = _orchestrator.ReadProfileText(kind);
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        Text = _originalText = "";
        Error = null;
    }

    private void Save()
    {
        try { _orchestrator.SaveProfileText(_kind, Text, _originalText); Close(); }
        catch (Exception ex) when (ex is LauncherProfileException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex is LauncherProfileException ? ex.Message : "Unable to save. Stop active sessions and check that the profile folder is writable.";
        }
    }
}
