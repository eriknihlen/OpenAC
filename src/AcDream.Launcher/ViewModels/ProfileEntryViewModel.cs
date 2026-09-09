namespace AcDream.Launcher.ViewModels;

public sealed class ProfileEntryViewModel : ObservableObject
{
    private string _name;
    private string _value;

    public ProfileEntryViewModel(string name, string value, bool isAccount, Action<ProfileEntryViewModel> remove)
    {
        _name = name;
        _value = value;
        PasswordChar = isAccount ? '●' : '\0';
        NameLabel = isAccount ? "Username" : "Server name";
        ValueLabel = isAccount ? "Password" : "Server address and port";
        Placeholder = isAccount ? "Password" : "game.example.com:9000";
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public char PasswordChar { get; }
    public string NameLabel { get; }
    public string ValueLabel { get; }
    public string Placeholder { get; }
    public RelayCommand RemoveCommand { get; }
}
