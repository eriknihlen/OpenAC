namespace AcDream.Launcher.ViewModels;

public enum ProfileEditorKind
{
    AddServer,
    EditServer,
    AddAccount,
    EditAccount,
    AddCharacter,
    EditCharacter,
    Remove,
}

public sealed class ProfileEditorDialogViewModel : ObservableObject
{
    private Action<ProfileEditorDialogViewModel>? _submit;
    private bool _isOpen;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _name = string.Empty;
    private string _host = string.Empty;
    private string _port = "9000";
    private string _password = string.Empty;
    private string _characterId = string.Empty;
    private string? _error;
    private ProfileEditorKind _kind;

    public ProfileEditorDialogViewModel()
    {
        SubmitCommand = new RelayCommand(Submit, () => IsOpen);
        CancelCommand = new RelayCommand(Close, () => IsOpen);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                SubmitCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ProfileEditorKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsServerEditor));
                OnPropertyChanged(nameof(IsAccountEditor));
                OnPropertyChanged(nameof(IsCharacterEditor));
                OnPropertyChanged(nameof(IsRemoveConfirmation));
                OnPropertyChanged(nameof(SubmitText));
            }
        }
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string CharacterId
    {
        get => _characterId;
        set => SetProperty(ref _characterId, value);
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

    public bool IsServerEditor => Kind is ProfileEditorKind.AddServer or ProfileEditorKind.EditServer;

    public bool IsAccountEditor => Kind is ProfileEditorKind.AddAccount or ProfileEditorKind.EditAccount;

    public bool IsCharacterEditor => Kind is ProfileEditorKind.AddCharacter or ProfileEditorKind.EditCharacter;

    public bool IsRemoveConfirmation => Kind == ProfileEditorKind.Remove;

    public string SubmitText => IsRemoveConfirmation ? "Remove" : "Save";

    public RelayCommand SubmitCommand { get; }

    public RelayCommand CancelCommand { get; }

    public void Open(
        ProfileEditorKind kind,
        string title,
        Action<ProfileEditorDialogViewModel> submit,
        string name = "",
        string host = "",
        int port = 9000,
        string characterId = "",
        string message = "")
    {
        ArgumentNullException.ThrowIfNull(submit);
        Kind = kind;
        Title = title;
        Message = message;
        Name = name;
        Host = host;
        Port = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Password = string.Empty;
        CharacterId = characterId;
        Error = null;
        _submit = submit;
        IsOpen = true;
    }

    public bool TryGetPort(out int port) =>
        int.TryParse(
            Port,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out port)
        && port is >= 1 and <= 65535;

    public void Close()
    {
        Password = string.Empty;
        Error = null;
        _submit = null;
        IsOpen = false;
    }

    private void Submit()
    {
        try
        {
            _submit?.Invoke(this);
            Close();
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            if (!string.IsNullOrEmpty(Password))
            {
                message = message.Replace(
                    Password,
                    "[redacted]",
                    StringComparison.Ordinal);
            }

            Error = string.IsNullOrWhiteSpace(message)
                ? "The profile change could not be saved."
                : message;
        }
    }
}
