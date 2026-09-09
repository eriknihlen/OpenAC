namespace AcDream.Launcher.ViewModels;

public sealed class LauncherShellViewModel : ObservableObject
{
    private bool _isOpen;

    public LauncherShellViewModel(
        string title,
        string body,
        string status,
        Func<bool>? canOpen = null)
    {
        Title = title;
        Body = body;
        Status = status;
        OpenCommand = new RelayCommand(() => IsOpen = true, canOpen);
        CloseCommand = new RelayCommand(() => IsOpen = false);
    }

    public string Title { get; }

    public string Body { get; }

    public string Status { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    public void NotifyCommandStates()
    {
        OpenCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }
}
