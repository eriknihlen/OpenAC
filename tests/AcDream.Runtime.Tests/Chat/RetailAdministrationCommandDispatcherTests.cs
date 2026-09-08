using AcDream.Runtime.Chat;

namespace AcDream.Runtime.Tests.Chat;

public sealed class RetailAdministrationCommandDispatcherTests
{
    [Fact]
    public void CompleteAdministrationIdFamily_IsOwnedByTheSharedDispatcher()
    {
        var calls = new List<string>();
        RetailAdministrationCommandDispatcher dispatcher = NewDispatcher(calls);
        ClientCommandId[] ids =
        [
            ClientCommandId.AllegianceInfo,
            ClientCommandId.AllegianceBoot,
            ClientCommandId.AllegianceBan,
            ClientCommandId.AllegianceChat,
            ClientCommandId.AllegianceBroadcast,
            ClientCommandId.AllegianceOfficer,
            ClientCommandId.AllegianceOfficerTitle,
            ClientCommandId.AllegianceName,
            ClientCommandId.AllegianceLock,
            ClientCommandId.AllegianceHouse,
            ClientCommandId.AllegianceMotd,
            ClientCommandId.AllegianceUnrecognizedSubcommand,
            ClientCommandId.HouseOpenStatus,
            ClientCommandId.HouseStorage,
            ClientCommandId.HouseBoot,
            ClientCommandId.HouseBootAll,
            ClientCommandId.HouseGuests,
            ClientCommandId.HouseHooks,
            ClientCommandId.HouseUnrecognizedSubcommand,
        ];

        foreach (ClientCommandId id in ids)
            Assert.True(dispatcher.TryExecute(id, string.Empty));

        Assert.False(dispatcher.TryExecute(ClientCommandId.QueryAge, string.Empty));
    }

    [Fact]
    public void RepresentativeMultiFieldGrammar_UsesSemanticHostBindings()
    {
        var calls = new List<string>();
        RetailAdministrationCommandDispatcher dispatcher = NewDispatcher(calls);

        dispatcher.TryExecute(ClientCommandId.AllegianceOfficer, "add 0x2 Lord Bob");
        dispatcher.TryExecute(ClientCommandId.AllegianceChat, "on");
        dispatcher.TryExecute(ClientCommandId.AllegianceChat, "kick Bob, Be civil");
        dispatcher.TryExecute(ClientCommandId.HouseGuests, "add Lord Bob");
        dispatcher.TryExecute(ClientCommandId.HouseStorage, "remove -all");
        dispatcher.TryExecute(ClientCommandId.AllegianceMotd, "set Welcome home");

        Assert.Equal(
            [
                "officer:2:Lord Bob",
                "option:27:True",
                "chatboot:Bob:Be civil",
                "guest:add:Lord Bob",
                "storage:remove_all",
                "motd:set:Welcome home",
            ],
            calls);
    }

    [Fact]
    public void InvalidForms_UseClientLocalFeedbackNotSystemChat()
    {
        var calls = new List<string>();
        RetailAdministrationCommandDispatcher dispatcher = NewDispatcher(calls);

        dispatcher.TryExecute(ClientCommandId.AllegianceOfficer, "add nope Bob");
        dispatcher.TryExecute(ClientCommandId.HouseUnrecognizedSubcommand, "nope");

        Assert.Equal(
            [
                "local:Please specify a valid officer level as a number between 1 and 3. Check the game help files for more information on officer levels.",
                "local:Please see @help House for more information on how to use this command.",
            ],
            calls);
    }

    private static RetailAdministrationCommandDispatcher NewDispatcher(
        List<string> calls) => new(
        new RetailAdministrationCommandDispatcher.FeedbackBindings(
            ShowSystemMessage: text => calls.Add("system:" + text),
            ShowClientLocalMessage: text => calls.Add("local:" + text),
            SetSingleCharacterOption: (id, enabled) =>
                calls.Add($"option:{id}:{enabled}"),
            RequestAllegianceInfo: name => calls.Add("info:" + name)),
        new RetailAdministrationCommandDispatcher.ActionBindings(
            BreakAllegianceBoot: (name, account) => calls.Add($"boot:{name}:{account}"),
            AllegianceChatBoot: (name, reason) => calls.Add($"chatboot:{name}:{reason}"),
            AllegianceChatGag: (name, enabled) => calls.Add($"gag:{name}:{enabled}"),
            AllegianceBroadcast: text => calls.Add("broadcast:" + text),
            ListAllegianceBans: () => calls.Add("ban:list"),
            AddAllegianceBan: name => calls.Add("ban:add:" + name),
            RemoveAllegianceBan: name => calls.Add("ban:remove:" + name),
            ListAllegianceOfficers: () => calls.Add("officer:list"),
            ClearAllegianceOfficers: () => calls.Add("officer:clear"),
            SetAllegianceOfficer: (name, level) => calls.Add($"officer:{level}:{name}"),
            RemoveAllegianceOfficer: name => calls.Add("officer:remove:" + name),
            ListAllegianceOfficerTitles: () => calls.Add("title:list"),
            ClearAllegianceOfficerTitles: () => calls.Add("title:clear"),
            SetAllegianceOfficerTitle: (level, title) => calls.Add($"title:{level}:{title}"),
            QueryAllegianceName: () => calls.Add("name:query"),
            SetAllegianceName: name => calls.Add("name:set:" + name),
            ClearAllegianceName: () => calls.Add("name:clear"),
            AllegianceLockAction: action => calls.Add("lock:" + action),
            SetAllegianceApprovedVassal: name => calls.Add("bypass:" + name),
            AllegianceHouseAction: action => calls.Add("alleghouse:" + action),
            QueryMotd: () => calls.Add("motd:query"),
            SetMotd: text => calls.Add("motd:set:" + text),
            ClearMotd: () => calls.Add("motd:clear"),
            SetOpenHouseStatus: open => calls.Add("open:" + open),
            AddPermanentGuest: name => calls.Add("guest:add:" + name),
            RemovePermanentGuest: name => calls.Add("guest:remove:" + name),
            RemoveAllPermanentGuests: () => calls.Add("guest:remove_all"),
            ChangeStoragePermission: (name, enabled) => calls.Add($"storage:{enabled}:{name}"),
            AddAllStoragePermission: () => calls.Add("storage:add_all"),
            RemoveAllStoragePermission: () => calls.Add("storage:remove_all"),
            RequestFullGuestList: () => calls.Add("guest:list"),
            BootSpecificHouseGuest: name => calls.Add("houseboot:" + name),
            BootEveryone: () => calls.Add("houseboot:all"),
            SetHooksVisibility: visible => calls.Add("hooks:" + visible),
            ModifyAllegianceGuestPermission: enabled => calls.Add("guest:all:" + enabled),
            ModifyAllegianceStoragePermission: enabled => calls.Add("storage:all:" + enabled)));
}
