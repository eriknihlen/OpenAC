using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Orchestration;

namespace AcDream.Launcher.ViewModels;

public enum LauncherTreeNodeKind
{
    Server,
    Account,
    Character,
}

public sealed class LauncherTreeNodeViewModel
{
    private LauncherTreeNodeViewModel(
        LauncherTreeNodeKind kind,
        string serverName,
        string? accountName,
        string? characterName,
        string displayName,
        string secondaryText)
    {
        Kind = kind;
        ServerName = serverName;
        AccountName = accountName;
        CharacterName = characterName;
        DisplayName = displayName;
        SecondaryText = secondaryText;
    }

    public LauncherTreeNodeKind Kind { get; }

    public string ServerName { get; }

    public string? AccountName { get; }

    public string? CharacterName { get; }

    public string DisplayName { get; }

    public string SecondaryText { get; }

    public ObservableCollection<LauncherTreeNodeViewModel> Children { get; } = [];

    public static LauncherTreeNodeViewModel FromServer(LauncherServerSnapshot server)
    {
        var node = new LauncherTreeNodeViewModel(
            LauncherTreeNodeKind.Server,
            server.Name,
            accountName: null,
            characterName: null,
            server.Name,
            $"{server.Host}:{server.Port}");
        foreach (LauncherAccountSnapshot account in server.Accounts)
        {
            node.Children.Add(FromAccount(account));
        }

        return node;
    }

    private static LauncherTreeNodeViewModel FromAccount(LauncherAccountSnapshot account)
    {
        var node = new LauncherTreeNodeViewModel(
            LauncherTreeNodeKind.Account,
            account.ServerName,
            account.AccountName,
            characterName: null,
            account.AccountName,
            account.ActivityStatus);
        foreach (LauncherCharacterSnapshot character in account.Characters)
        {
            node.Children.Add(FromCharacter(character));
        }

        return node;
    }

    private static LauncherTreeNodeViewModel FromCharacter(
        LauncherCharacterSnapshot character) =>
        new(
            LauncherTreeNodeKind.Character,
            character.ServerName,
            character.AccountName,
            character.Name,
            character.Name,
            character.HasRunningSession
                ? character.SessionStatus
                : character.LaunchMode.ToString());
}
