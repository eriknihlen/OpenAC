using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Profiles;

public sealed class RosterMergeTests
{
    private static LauncherProfileStore NewStoreWithServerAndAccount()
    {
        var store = new LauncherProfileStore(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "testpassword");
        return store;
    }

    [Fact]
    public void FirstMergeAddsNewCharactersWithDefaultLaunchModeGuiSelect()
    {
        LauncherProfileStore store = NewStoreWithServerAndAccount();

        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [
                new CharacterRosterEntry(0x5000000A, "+Acdream", 0),
                new CharacterRosterEntry(0x5000000B, "+Second", 0),
            ]);

        List<CharacterProfile> characters =
            store.Document.Servers.Single().Accounts.Single().Characters;
        Assert.Equal(2, characters.Count);

        CharacterProfile first = characters.Single(c => c.Name == "+Acdream");
        Assert.Equal("0x5000000A", first.Id);
        Assert.Equal(LaunchMode.GuiSelect, first.LaunchMode);
        Assert.Empty(first.Plugins);
        Assert.Empty(first.LoginCommands);

        CharacterProfile second = characters.Single(c => c.Name == "+Second");
        Assert.Equal("0x5000000B", second.Id);
        Assert.Equal(LaunchMode.GuiSelect, second.LaunchMode);
    }

    [Fact]
    public void SecondMergePreservesUserSettingsOnAnExistingCharacter()
    {
        LauncherProfileStore store = NewStoreWithServerAndAccount();
        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+Acdream", 0)]);
        store.EditCharacter(
            "Local ACE",
            "testaccount",
            "+Acdream",
            launchMode: LaunchMode.Headless,
            plugins: ["ExamplePlugin"],
            loginCommands: ["/vt start"]);

        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+Acdream", 0)]);

        CharacterProfile character = Assert.Single(
            store.Document.Servers.Single().Accounts.Single().Characters);
        Assert.Equal(LaunchMode.Headless, character.LaunchMode);
        Assert.Equal(["ExamplePlugin"], character.Plugins);
        Assert.Equal(["/vt start"], character.LoginCommands);
    }

    [Fact]
    public void MergeUpdatesNameWhenIdMatchesButDisplayNameChanged()
    {
        LauncherProfileStore store = NewStoreWithServerAndAccount();
        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+OldName", 0)]);

        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+NewName", 0)]);

        CharacterProfile character = Assert.Single(
            store.Document.Servers.Single().Accounts.Single().Characters);
        Assert.Equal("+NewName", character.Name);
        Assert.Equal("0x5000000A", character.Id);
    }

    [Fact]
    public void CharacterAbsentFromALaterRosterSnapshotIsRetained()
    {
        LauncherProfileStore store = NewStoreWithServerAndAccount();
        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [
                new CharacterRosterEntry(0x5000000A, "+Acdream", 0),
                new CharacterRosterEntry(0x5000000B, "+PendingDelete", 1),
            ]);

        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+Acdream", 0)]);

        List<CharacterProfile> characters =
            store.Document.Servers.Single().Accounts.Single().Characters;
        Assert.Equal(2, characters.Count);
        Assert.Contains(characters, c => c.Name == "+Acdream");
        Assert.Contains(characters, c => c.Name == "+PendingDelete");
    }

    [Fact]
    public void MergeNormalizesAnUnprefixedHexIdInsteadOfCreatingADuplicateRow()
    {
        LauncherProfileStore store = NewStoreWithServerAndAccount();
        store.Document.Servers.Single().Accounts.Single().Characters.Add(
            new CharacterProfile { Id = "5000000A", Name = "+Acdream" });

        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+Acdream", 0)]);

        CharacterProfile character = Assert.Single(
            store.Document.Servers.Single().Accounts.Single().Characters);
        Assert.Equal("0x5000000A", character.Id);
        Assert.Equal("+Acdream", character.Name);
    }

    [Fact]
    public void SameNameRosterEntryCorrectsAWrongValidIdAndPreservesSettings()
    {
        LauncherProfileStore store = NewStoreWithServerAndAccount();
        store.AddCharacter(
            "Local ACE",
            "testaccount",
            "+Acdream",
            "0x50000001",
            LaunchMode.Headless,
            ["ExamplePlugin"],
            ["/vt start"]);

        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+Acdream", 0)]);

        CharacterProfile character = Assert.Single(
            store.Document.Servers.Single().Accounts.Single().Characters);
        Assert.Equal("0x5000000A", character.Id);
        Assert.Equal(LaunchMode.Headless, character.LaunchMode);
        Assert.Equal(["ExamplePlugin"], character.Plugins);
        Assert.Equal(["/vt start"], character.LoginCommands);
    }

    [Fact]
    public void AuthoritativeMergeCollapsesCorrectIdAndSameNameDuplicates()
    {
        LauncherProfileStore store = NewStoreWithServerAndAccount();
        AccountProfile account = store.Document.Servers.Single().Accounts.Single();
        account.Characters.Add(new CharacterProfile
        {
            Id = "0x5000000A",
            Name = "+OldName",
            LaunchMode = LaunchMode.Headless,
            Plugins = ["Canonical.Plugin"],
            LoginCommands = ["/canonical"],
        });
        account.Characters.Add(new CharacterProfile
        {
            Id = "0x50000001",
            Name = "+Acdream",
            LaunchMode = LaunchMode.Gui,
            Plugins = ["Duplicate.Plugin"],
            LoginCommands = ["/duplicate"],
        });

        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+Acdream", 0)]);

        CharacterProfile character = Assert.Single(account.Characters);
        Assert.Equal("0x5000000A", character.Id);
        Assert.Equal("+Acdream", character.Name);
        Assert.Equal(LaunchMode.Headless, character.LaunchMode);
        Assert.Equal(["Canonical.Plugin"], character.Plugins);
    }

    [Fact]
    public void MergeThrowsForUnknownServerOrAccount()
    {
        LauncherProfileStore store = NewStoreWithServerAndAccount();

        Assert.Throws<LauncherProfileException>(
            () => store.MergeRoster(
                "Nope",
                "testaccount",
                [new CharacterRosterEntry(1, "x", 0)]));

        Assert.Throws<LauncherProfileException>(
            () => store.MergeRoster(
                "Local ACE",
                "nope",
                [new CharacterRosterEntry(1, "x", 0)]));
    }
}
