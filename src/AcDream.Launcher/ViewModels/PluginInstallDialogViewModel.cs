using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Plugins;

namespace AcDream.Launcher.ViewModels;

/// <summary>Who to enable a newly installed plugin for. Install itself never writes a character's
/// plugin list (L-300); only a non-<see cref="None"/> choice does, and only after install
/// succeeds.</summary>
public enum PluginEnableChoice
{
    None,
    All,
    Choose,
}

/// <summary>A character offered in the install dialog's "Choose" list.</summary>
public sealed record PluginCharacterOption(
    string ServerName,
    string AccountName,
    string CharacterName,
    string DisplayName);

public sealed class PluginCharacterChoiceViewModel(PluginCharacterOption option) : ObservableObject
{
    private bool _isChecked;

    public PluginCharacterOption Option { get; } = option;

    public string DisplayName => Option.DisplayName;

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>The overlay a Discover row's Install (or "Add from URL") opens, in the existing
/// <c>IsOpen</c> idiom. Repo URLs are shown as text only: nothing here opens a browser or runs
/// downloaded code (L-300).</summary>
public sealed class PluginInstallDialogViewModel : ObservableObject
{
    private readonly Func<bool> _canInteract;
    private Func<CancellationToken, Task<PluginInstallResult>>? _installAsync;
    private Action<string, IReadOnlyList<PluginCharacterOption>>? _enableForCharacters;
    private CancellationTokenSource? _cancellation;
    private bool _isOpen;
    private bool _isBusy;
    private bool _isUpdate;
    private PluginEnableChoice _choice = PluginEnableChoice.None;
    private string? _error;

    public PluginInstallDialogViewModel(Func<bool>? canInteract = null)
    {
        _canInteract = canInteract ?? (() => true);
        ConfirmCommand = new AsyncRelayCommand(
            ConfirmAsync,
            () => IsOpen && !IsBusy && _canInteract());
        CancelCommand = new RelayCommand(Close, () => !IsBusy);
    }

    public string Repo { get; private set; } = string.Empty;

    public string PluginId { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public bool IsListed { get; private set; }

    /// <summary>Whether the dialog opened for an already-installed plugin's Update; the enable
    /// choice is offered only on a first install (plan).</summary>
    public bool IsUpdate
    {
        get => _isUpdate;
        private set
        {
            if (SetProperty(ref _isUpdate, value))
            {
                OnPropertyChanged(nameof(ShowEnableChoice));
            }
        }
    }

    public bool ShowEnableChoice => !IsUpdate;

    /// <summary>The responsibility notice every install and update dialog shows, every time
    /// (L-313): no wording here says or implies OpenAC reviews plugins, listed or not.</summary>
    public string WarningText => IsListed
        ? "Plugins are made by third parties, not OpenAC. Installing one is your choice and your "
            + "responsibility. Only install plugins from authors you trust."
        : "Plugins are made by third parties, not OpenAC. Installing one is your choice and your "
            + "responsibility. Only install plugins from authors you trust.\n"
            + "This plugin is not on the OpenAC plugin list.";

    public ObservableCollection<PluginCharacterChoiceViewModel> Characters { get; } = [];

    public bool HasCharacters => Characters.Count > 0;

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                NotifyCommandStates();
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

    public PluginEnableChoice Choice
    {
        get => _choice;
        private set
        {
            if (SetProperty(ref _choice, value))
            {
                OnPropertyChanged(nameof(EnableNone));
                OnPropertyChanged(nameof(EnableAll));
                OnPropertyChanged(nameof(EnableChoose));
                OnPropertyChanged(nameof(IsChooseListVisible));
            }
        }
    }

    public bool EnableNone
    {
        get => Choice == PluginEnableChoice.None;
        set { if (value) Choice = PluginEnableChoice.None; }
    }

    public bool EnableAll
    {
        get => Choice == PluginEnableChoice.All;
        set { if (value) Choice = PluginEnableChoice.All; }
    }

    public bool EnableChoose
    {
        get => Choice == PluginEnableChoice.Choose;
        set { if (value) Choice = PluginEnableChoice.Choose; }
    }

    public bool IsChooseListVisible => Choice == PluginEnableChoice.Choose;

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

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Opens the dialog for a resolved repo/manifest. The caller supplies the actual
    /// network install and the character-enable write, so this view model stays testable without
    /// either one.</summary>
    public void Open(
        string repo,
        string pluginId,
        string displayName,
        bool isListed,
        bool isUpdate,
        IReadOnlyList<PluginCharacterOption> characters,
        Func<CancellationToken, Task<PluginInstallResult>> installAsync,
        Action<string, IReadOnlyList<PluginCharacterOption>> enableForCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(characters);
        Repo = repo;
        PluginId = pluginId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? pluginId : displayName;
        IsListed = isListed;
        IsUpdate = isUpdate;
        _installAsync = installAsync ?? throw new ArgumentNullException(nameof(installAsync));
        _enableForCharacters = enableForCharacters
            ?? throw new ArgumentNullException(nameof(enableForCharacters));

        Characters.Clear();
        foreach (PluginCharacterOption option in characters)
        {
            Characters.Add(new PluginCharacterChoiceViewModel(option));
        }

        Choice = PluginEnableChoice.None;
        Error = null;
        OnPropertyChanged(nameof(Repo));
        OnPropertyChanged(nameof(PluginId));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsListed));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasCharacters));
        IsOpen = true;
    }

    public void Close()
    {
        if (IsBusy)
        {
            return;
        }

        _cancellation?.Cancel();
        IsOpen = false;
    }

    private async Task ConfirmAsync()
    {
        if (IsBusy || _installAsync is null || _enableForCharacters is null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Error = null;
        try
        {
            // Install succeeded once this returns: close the dialog before touching character
            // profiles, so a slow or failing enable step can never leave Confirm sitting there to
            // be pressed again and run the install a second time.
            PluginInstallResult result = await _installAsync(cancellation.Token).ConfigureAwait(true);
            IsOpen = false;
            if (Choice != PluginEnableChoice.None)
            {
                PluginCharacterOption[] chosen = Choice == PluginEnableChoice.All
                    ? [.. Characters.Select(character => character.Option)]
                    : [.. Characters.Where(character => character.IsChecked)
                        .Select(character => character.Option)];
                if (chosen.Length > 0)
                {
                    _enableForCharacters(result.Id, chosen);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Error = "Install cancelled.";
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message) ? "The install failed." : ex.Message;
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

    private void NotifyCommandStates()
    {
        ConfirmCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
