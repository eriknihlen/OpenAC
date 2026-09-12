using AcDream.App.UI;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.App.Net;

internal sealed record LiveSessionCommandBindings(
    ClientCommandController.Bindings ClientCommands,
    ChatLog Chat,
    TurbineChatState TurbineChat,
    Func<uint> PlayerGuid,
    Action<string> SendTalk,
    Action<string, string> SendTell,
    Action<uint, string> SendTalkDirect,
    Action<uint, string> SendChannel,
    Action<uint, uint, uint, uint, string, uint> SendTurbineChat,
    Action<ShortcutEntry> AddShortcut,
    Action<uint> RemoveShortcut,
    Action<uint, int, int> AddFavorite,
    Action<uint, int> RemoveFavorite,
    Action<uint> SetSpellbookFilter,
    Action<uint> ForgetSpell,
    Action<uint, uint> SetDesiredComponent,
    Action ClearDesiredComponents,
    Action<uint, ulong> RaiseAttribute,
    Action<uint, ulong> RaiseVital,
    Action<uint, ulong> RaiseSkill,
    Action<uint, uint> TrainSkill,
    Action<string> AddFriend,
    Action<uint> RemoveFriend,
    Action ClearFriends,
    Action RequestLegacyFriends,
    Action<uint> OpenTradeNegotiations,
    Action CloseTradeNegotiations,
    Action<uint> AddToTrade,
    Action<uint, bool, bool> AcceptTrade,
    Action DeclineTrade,
    Action ResetTrade,
    Action<bool, uint, string, uint> ModifyCharacterSquelch,
    Action<bool, string> ModifyAccountSquelch,
    Action<bool, uint> ModifyGlobalSquelch,
    RuntimeCommunicationState Communication,
    RuntimeCharacterState CharacterState,
    Action<uint, bool> SendSingleCharacterOption,
    Action SaveCharacterOptions,
    Action<uint> SendSetTitle,
    Action<string, bool> SendFellowshipCreate,
    Action<uint> SendFellowshipRecruit,
    Action<uint> SendFellowshipDismiss,
    Action<bool> SendFellowshipQuit,
    Action<uint> SendFellowshipAssignNewLeader,
    Action<bool> SendFellowshipChangeOpenness,
    Action<bool> SendFellowshipUpdateRequest,
    Action<uint> SendAllegianceSwear,
    Action<uint> SendAllegianceBreak,
    Action<uint> SendAllegianceKick,
    Action<string> SendAllegianceInfoRequest,
    Action<bool> SendAllegianceUpdateRequest,
    Action<string>? Log = null,
    Func<string, RetailChatPose?>? ResolvePose = null,
    Action<uint>? ExecuteMotion = null,
    Action<string>? SendSoulEmote = null);

internal readonly record struct AddShortcutRuntimeCmd(ShortcutEntry Entry);
internal readonly record struct RemoveShortcutRuntimeCmd(uint Index);
internal readonly record struct AddFavoriteRuntimeCmd(
    uint SpellId,
    int Position,
    int TabIndex);
internal readonly record struct RemoveFavoriteRuntimeCmd(
    uint SpellId,
    int TabIndex);
internal readonly record struct SetSpellbookFilterRuntimeCmd(uint Filters);
internal readonly record struct ForgetSpellRuntimeCmd(uint SpellId);
internal readonly record struct SetDesiredComponentRuntimeCmd(
    uint ComponentId,
    uint Amount);
internal readonly record struct ClearDesiredComponentsRuntimeCmd;
internal readonly record struct RaiseAttributeRuntimeCmd(uint StatId, ulong Cost);
internal readonly record struct RaiseVitalRuntimeCmd(uint StatId, ulong Cost);
internal readonly record struct RaiseSkillRuntimeCmd(uint StatId, ulong Cost);
internal readonly record struct TrainSkillRuntimeCmd(uint StatId, uint Cost);
internal readonly record struct SetSingleCharacterOptionRuntimeCmd(
    uint OptionId,
    bool Value);
internal readonly record struct SaveCharacterOptionsRuntimeCmd;
internal readonly record struct SetTitleRuntimeCmd(uint TitleId);
internal readonly record struct AddFriendRuntimeCmd(string Name);
internal readonly record struct RemoveFriendRuntimeCmd(uint CharacterId);

internal readonly record struct OpenTradeNegotiationsRuntimeCmd(uint PartnerGuid);
internal readonly record struct CloseTradeNegotiationsRuntimeCmd;
internal readonly record struct AddToTradeRuntimeCmd(uint ItemGuid);
internal readonly record struct AcceptTradeRuntimeCmd(
    uint PartnerGuid,
    bool SelfAccepted,
    bool PartnerAccepted);
internal readonly record struct DeclineTradeRuntimeCmd;
internal readonly record struct ResetTradeRuntimeCmd;
internal readonly record struct ClearFriendsRuntimeCmd;
internal readonly record struct RequestLegacyFriendsRuntimeCmd;
internal readonly record struct ModifyCharacterSquelchRuntimeCmd(
    bool Add,
    uint CharacterId,
    string Name,
    uint MessageType);
internal readonly record struct ModifyAccountSquelchRuntimeCmd(
    bool Add,
    string Name);
internal readonly record struct ModifyGlobalSquelchRuntimeCmd(
    bool Add,
    uint MessageType);

internal readonly record struct FellowshipCreateRuntimeCmd(
    string FellowshipName,
    bool ShareXp);
internal readonly record struct FellowshipRecruitRuntimeCmd(uint TargetGuid);
internal readonly record struct FellowshipDismissRuntimeCmd(uint TargetGuid);
internal readonly record struct FellowshipQuitRuntimeCmd(bool Disband);
internal readonly record struct FellowshipAssignNewLeaderRuntimeCmd(uint NewLeaderGuid);
internal readonly record struct FellowshipChangeOpennessRuntimeCmd(bool IsOpen);
internal readonly record struct FellowshipUpdateRequestRuntimeCmd(bool PanelOpen);
internal readonly record struct AllegianceSwearRuntimeCmd(uint PatronGuid);
internal readonly record struct AllegianceBreakRuntimeCmd(uint TargetGuid);
internal readonly record struct AllegianceKickRuntimeCmd(uint VassalGuid);
internal readonly record struct AllegianceInfoRequestRuntimeCmd(string PlayerName);
internal readonly record struct AllegianceUpdateRequestRuntimeCmd(bool On);

internal sealed class LiveSessionCommandRouter : ILiveSessionCommandRouting
{
    private readonly object _gate = new();
    private LiveCommandBus? _commands;
    private LiveChatCommandRoute? _chatCommands;
    private ClientCommandController.Bindings? _clientCommands;
    private int _state;

    public LiveSessionCommandRouter(LiveSessionCommandBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(bindings.ClientCommands);
        ArgumentNullException.ThrowIfNull(bindings.Chat);
        ArgumentNullException.ThrowIfNull(bindings.TurbineChat);
        ArgumentNullException.ThrowIfNull(bindings.PlayerGuid);
        ArgumentNullException.ThrowIfNull(bindings.SendTalk);
        ArgumentNullException.ThrowIfNull(bindings.SendTell);
        ArgumentNullException.ThrowIfNull(bindings.SendTalkDirect);
        ArgumentNullException.ThrowIfNull(bindings.SendChannel);
        ArgumentNullException.ThrowIfNull(bindings.SendTurbineChat);
        ArgumentNullException.ThrowIfNull(bindings.Communication);
        ArgumentNullException.ThrowIfNull(bindings.CharacterState);

        _clientCommands = bindings.ClientCommands;
        var commands = new LiveCommandBus();
        var clientCommands = new ClientCommandController(
            BuildGuardedClientCommands(bindings.ClientCommands));
        _chatCommands = new LiveChatCommandRoute(new LiveChatCommandBindings(
            clientCommands.Execute,
            bindings.Communication,
            bindings.Chat,
            bindings.TurbineChat,
            bindings.CharacterState,
            bindings.PlayerGuid,
            bindings.SendTalk,
            bindings.SendTell,
            bindings.SendTalkDirect,
            bindings.SendChannel,
            bindings.SendTurbineChat,
            bindings.Log,
            bindings.ResolvePose,
            bindings.ExecuteMotion,
            bindings.SendSoulEmote));
        commands.Register<AddShortcutRuntimeCmd>(
            command => SendIfActive(() => bindings.AddShortcut(command.Entry)));
        commands.Register<RemoveShortcutRuntimeCmd>(
            command => SendIfActive(() => bindings.RemoveShortcut(command.Index)));
        commands.Register<AddFavoriteRuntimeCmd>(
            command => SendIfActive(() => bindings.AddFavorite(
                command.SpellId,
                command.Position,
                command.TabIndex)));
        commands.Register<RemoveFavoriteRuntimeCmd>(
            command => SendIfActive(() => bindings.RemoveFavorite(
                command.SpellId,
                command.TabIndex)));
        commands.Register<SetSpellbookFilterRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SetSpellbookFilter(command.Filters)));
        commands.Register<ForgetSpellRuntimeCmd>(
            command => SendIfActive(() => bindings.ForgetSpell(command.SpellId)));
        commands.Register<SetDesiredComponentRuntimeCmd>(
            command => SendIfActive(() => bindings.SetDesiredComponent(
                command.ComponentId,
                command.Amount)));
        commands.Register<ClearDesiredComponentsRuntimeCmd>(
            _ => SendIfActive(bindings.ClearDesiredComponents));
        commands.Register<RaiseAttributeRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.RaiseAttribute(command.StatId, command.Cost)));
        commands.Register<RaiseVitalRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.RaiseVital(command.StatId, command.Cost)));
        commands.Register<RaiseSkillRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.RaiseSkill(command.StatId, command.Cost)));
        commands.Register<TrainSkillRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.TrainSkill(command.StatId, command.Cost)));
        commands.Register<SetSingleCharacterOptionRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendSingleCharacterOption(
                    command.OptionId,
                    command.Value)));
        commands.Register<SaveCharacterOptionsRuntimeCmd>(
            _ => SendIfActive(bindings.SaveCharacterOptions));
        commands.Register<SetTitleRuntimeCmd>(
            command => SendIfActive(() => bindings.SendSetTitle(command.TitleId)));
        commands.Register<AddFriendRuntimeCmd>(
            command => SendIfActive(() => bindings.AddFriend(command.Name)));
        commands.Register<OpenTradeNegotiationsRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.OpenTradeNegotiations(command.PartnerGuid)));
        commands.Register<CloseTradeNegotiationsRuntimeCmd>(
            _ => SendIfActive(bindings.CloseTradeNegotiations));
        commands.Register<AddToTradeRuntimeCmd>(
            command => SendIfActive(() => bindings.AddToTrade(command.ItemGuid)));
        commands.Register<AcceptTradeRuntimeCmd>(
            command => SendIfActive(() => bindings.AcceptTrade(
                command.PartnerGuid,
                command.SelfAccepted,
                command.PartnerAccepted)));
        commands.Register<DeclineTradeRuntimeCmd>(
            _ => SendIfActive(bindings.DeclineTrade));
        commands.Register<ResetTradeRuntimeCmd>(
            _ => SendIfActive(bindings.ResetTrade));
        commands.Register<RemoveFriendRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.RemoveFriend(command.CharacterId)));
        commands.Register<ClearFriendsRuntimeCmd>(
            _ => SendIfActive(bindings.ClearFriends));
        commands.Register<RequestLegacyFriendsRuntimeCmd>(
            _ => SendIfActive(bindings.RequestLegacyFriends));
        commands.Register<ModifyCharacterSquelchRuntimeCmd>(
            command => SendIfActive(() => bindings.ModifyCharacterSquelch(
                command.Add,
                command.CharacterId,
                command.Name,
                command.MessageType)));
        commands.Register<ModifyAccountSquelchRuntimeCmd>(
            command => SendIfActive(() => bindings.ModifyAccountSquelch(
                command.Add,
                command.Name)));
        commands.Register<ModifyGlobalSquelchRuntimeCmd>(
            command => SendIfActive(() => bindings.ModifyGlobalSquelch(
                command.Add,
                command.MessageType)));
        commands.Register<FellowshipCreateRuntimeCmd>(
            command => SendIfActive(() => bindings.SendFellowshipCreate(
                command.FellowshipName,
                command.ShareXp)));
        commands.Register<FellowshipRecruitRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendFellowshipRecruit(command.TargetGuid)));
        commands.Register<FellowshipDismissRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendFellowshipDismiss(command.TargetGuid)));
        commands.Register<FellowshipQuitRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendFellowshipQuit(command.Disband)));
        commands.Register<FellowshipAssignNewLeaderRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendFellowshipAssignNewLeader(command.NewLeaderGuid)));
        commands.Register<FellowshipChangeOpennessRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendFellowshipChangeOpenness(command.IsOpen)));
        commands.Register<FellowshipUpdateRequestRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendFellowshipUpdateRequest(command.PanelOpen)));
        commands.Register<AllegianceSwearRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendAllegianceSwear(command.PatronGuid)));
        commands.Register<AllegianceBreakRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendAllegianceBreak(command.TargetGuid)));
        commands.Register<AllegianceKickRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendAllegianceKick(command.VassalGuid)));
        commands.Register<AllegianceInfoRequestRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendAllegianceInfoRequest(command.PlayerName)));
        commands.Register<AllegianceUpdateRequestRuntimeCmd>(
            command => SendIfActive(() =>
                bindings.SendAllegianceUpdateRequest(command.On)));
        _commands = commands;
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _state == 1;
        }
    }

    public void Activate()
    {
        lock (_gate)
        {
            if (_state == 2)
                throw new ObjectDisposedException(nameof(LiveSessionCommandRouter));
            _chatCommands?.Activate();
            _state = 1;
        }
    }

    public void Publish<T>(T command) where T : notnull
    {
        lock (_gate)
        {
            if (_state == 1)
            {
                if (_chatCommands?.TryPublish(command) != true)
                    _commands?.Publish(command);
            }
        }
    }

    public void Dispose()
    {
        LiveCommandBus? commands;
        LiveChatCommandRoute? chatCommands;
        lock (_gate)
        {
            _state = 2;
            commands = _commands;
            _commands = null;
            chatCommands = _chatCommands;
            _chatCommands = null;
            _clientCommands = null;
        }

        chatCommands?.Dispose();
        commands?.Clear();
    }

    private ClientCommandController.Bindings BuildGuardedClientCommands(
        ClientCommandController.Bindings source) => new(
        TeleportToLifestone: () => InvokeClient(static b => b.TeleportToLifestone()),
        TeleportToMarketplace: () => InvokeClient(static b => b.TeleportToMarketplace()),
        TeleportToPkArena: () => InvokeClient(static b => b.TeleportToPkArena()),
        TeleportToPkLiteArena: () => InvokeClient(static b => b.TeleportToPkLiteArena()),
        TeleportToHouse: () => InvokeClient(static b => b.TeleportToHouse()),
        TeleportToMansion: () => InvokeClient(static b => b.TeleportToMansion()),
        QueryAge: () => InvokeClient(static b => b.QueryAge()),
        QueryBirth: () => InvokeClient(static b => b.QueryBirth()),
        ToggleFrameRate: () => InvokeClient(static b => b.ToggleFrameRate()),
        ToggleUiLock: () => InvokeClient(static b => b.ToggleUiLock()),
        ShowSystemMessage: text => InvokeClient(b => b.ShowSystemMessage(text)),
        ShowClientLocalMessage: text =>
            InvokeClient(b => b.ShowClientLocalMessage(text)),
        ShowWeenieError: error => InvokeClient(b => b.ShowWeenieError(error)),
        PlayerPublicWeenieBitfield: () =>
            ReadClient(static b => b.PlayerPublicWeenieBitfield(), default(uint?)),
        ClientVersion: () => ReadClient(static b => b.ClientVersion(), string.Empty),
        CurrentPosition: () =>
            ReadClient(static b => b.CurrentPosition(), default(AcDream.Core.Physics.Position?)),
        LastOutsideCorpsePosition: () =>
            ReadClient(static b => b.LastOutsideCorpsePosition(), default(AcDream.Core.Physics.Position?)),
        ShowConfirmation: (text, callback) =>
            InvokeClient(b => b.ShowConfirmation(text, callback)),
        Suicide: () => InvokeClient(static b => b.Suicide()),
        ClearChat: all => InvokeClient(b => b.ClearChat(all)),
        SetChatLogFile: name => ReadClient(
            b => b.SetChatLogFile(name),
            default(AcDream.Core.Chat.ChatLogResult)),
        SaveUi: name => InvokeClient(b => b.SaveUi(name)),
        LoadUi: name => InvokeClient(b => b.LoadUi(name)),
        SaveAutoUi: () => InvokeClient(static b => b.SaveAutoUi()),
        LoadAutoUi: () => InvokeClient(static b => b.LoadAutoUi()),
        IsAway: () => ReadClient(static b => b.IsAway(), false),
        SetAway: away => InvokeClient(b => b.SetAway(away)),
        SetAwayMessage: message => InvokeClient(b => b.SetAwayMessage(message)),
        AcceptLootPermits: () => ReadClient(static b => b.AcceptLootPermits(), false),
        SetAcceptLootPermits: accept => InvokeClient(b => b.SetAcceptLootPermits(accept)),
        DisplayConsent: () => InvokeClient(static b => b.DisplayConsent()),
        ClearConsent: () => InvokeClient(static b => b.ClearConsent()),
        RemoveConsent: name => InvokeClient(b => b.RemoveConsent(name)),
        SendEmote: text => InvokeClient(b => b.SendEmote(text)),
        Friends: source.Friends,
        AddFriend: name => InvokeClient(b => b.AddFriend(name)),
        RemoveFriend: id => InvokeClient(b => b.RemoveFriend(id)),
        ClearFriends: () => InvokeClient(static b => b.ClearFriends()),
        RequestLegacyFriends: () => InvokeClient(static b => b.RequestLegacyFriends()),
        Squelch: source.Squelch,
        ModifyCharacterSquelch: (add, id, name, chatType) =>
            InvokeClient(b => b.ModifyCharacterSquelch(add, id, name, chatType)),
        ModifyAccountSquelch: (add, name) =>
            InvokeClient(b => b.ModifyAccountSquelch(add, name)),
        ModifyGlobalSquelch: (add, chatType) =>
            InvokeClient(b => b.ModifyGlobalSquelch(add, chatType)),
        LastTeller: () => ReadClient(static b => b.LastTeller(), default(string?)),
        ClearDesiredComponents: () => InvokeClient(static b => b.ClearDesiredComponents()),
        HasOpenVendor: () => ReadClient(static b => b.HasOpenVendor(), false),
        FillComponentBuyList: (componentId, targetCount) =>
            InvokeClient(b => b.FillComponentBuyList(componentId, targetCount)),
        EnterPkLite: () => InvokeClient(static b => b.EnterPkLite()),
        IsUsingTurbineChat: () => ReadClient(static b => b.IsUsingTurbineChat(), false),
        SetChatTitle: title => InvokeClient(b => b.SetChatTitle(title)),
        SetSingleCharacterOption: (optionId, value) =>
            InvokeClient(b => b.SetSingleCharacterOption(optionId, value)),
        AddPlayerPermission: name => InvokeClient(b => b.AddPlayerPermission(name)),
        RemovePlayerPermission: name => InvokeClient(b => b.RemovePlayerPermission(name)),
        RequestAvailableHouses: houseType => InvokeClient(b => b.RequestAvailableHouses(houseType)),
        RequestChannelIndex: () => InvokeClient(static b => b.RequestChannelIndex()),
        RequestChannelList: channelId => InvokeClient(b => b.RequestChannelList(channelId)),
        JoinGmChannel: channelId => InvokeClient(b => b.JoinGmChannel(channelId)),
        LeaveGmChannel: channelId => InvokeClient(b => b.LeaveGmChannel(channelId)),
        RecallAllegianceHometown: () => InvokeClient(static b => b.RecallAllegianceHometown()),
        RequestAllegianceInfo: name => InvokeClient(b => b.RequestAllegianceInfo(name)),
        AbandonHouse: () => InvokeClient(static b => b.AbandonHouse()),
        Administration: BuildGuardedAdministration(),
        IsPersistentDaylight: () =>
            ReadClient(static b => b.IsPersistentDaylight(), false),
        SetPersistentDaylight: value =>
            InvokeClient(b => b.SetPersistentDaylight(value)),
        SetLandscapeRadius: radius =>
            InvokeClient(b => b.SetLandscapeRadius(radius)),
        SetFieldOfView: degrees =>
            InvokeClient(b => b.SetFieldOfView(degrees)));

    private ClientCommandController.AdministrationBindings BuildGuardedAdministration() =>
        new(
            BreakAllegianceBoot: (name, account) =>
                InvokeClient(b => b.Administration.BreakAllegianceBoot(name, account)),
            AllegianceChatBoot: (name, reason) =>
                InvokeClient(b => b.Administration.AllegianceChatBoot(name, reason)),
            AllegianceChatGag: (name, enabled) =>
                InvokeClient(b => b.Administration.AllegianceChatGag(name, enabled)),
            AllegianceBroadcast: text =>
                InvokeClient(b => b.Administration.AllegianceBroadcast(text)),
            ListAllegianceBans: () =>
                InvokeClient(static b => b.Administration.ListAllegianceBans()),
            AddAllegianceBan: name =>
                InvokeClient(b => b.Administration.AddAllegianceBan(name)),
            RemoveAllegianceBan: name =>
                InvokeClient(b => b.Administration.RemoveAllegianceBan(name)),
            ListAllegianceOfficers: () =>
                InvokeClient(static b => b.Administration.ListAllegianceOfficers()),
            ClearAllegianceOfficers: () =>
                InvokeClient(static b => b.Administration.ClearAllegianceOfficers()),
            SetAllegianceOfficer: (name, level) =>
                InvokeClient(b => b.Administration.SetAllegianceOfficer(name, level)),
            RemoveAllegianceOfficer: name =>
                InvokeClient(b => b.Administration.RemoveAllegianceOfficer(name)),
            ListAllegianceOfficerTitles: () =>
                InvokeClient(static b => b.Administration.ListAllegianceOfficerTitles()),
            ClearAllegianceOfficerTitles: () =>
                InvokeClient(static b => b.Administration.ClearAllegianceOfficerTitles()),
            SetAllegianceOfficerTitle: (level, title) =>
                InvokeClient(b => b.Administration.SetAllegianceOfficerTitle(level, title)),
            QueryAllegianceName: () =>
                InvokeClient(static b => b.Administration.QueryAllegianceName()),
            SetAllegianceName: name =>
                InvokeClient(b => b.Administration.SetAllegianceName(name)),
            ClearAllegianceName: () =>
                InvokeClient(static b => b.Administration.ClearAllegianceName()),
            AllegianceLockAction: action =>
                InvokeClient(b => b.Administration.AllegianceLockAction(action)),
            SetAllegianceApprovedVassal: name =>
                InvokeClient(b => b.Administration.SetAllegianceApprovedVassal(name)),
            AllegianceHouseAction: action =>
                InvokeClient(b => b.Administration.AllegianceHouseAction(action)),
            QueryMotd: () =>
                InvokeClient(static b => b.Administration.QueryMotd()),
            SetMotd: motd =>
                InvokeClient(b => b.Administration.SetMotd(motd)),
            ClearMotd: () =>
                InvokeClient(static b => b.Administration.ClearMotd()),
            SetOpenHouseStatus: open =>
                InvokeClient(b => b.Administration.SetOpenHouseStatus(open)),
            AddPermanentGuest: name =>
                InvokeClient(b => b.Administration.AddPermanentGuest(name)),
            RemovePermanentGuest: name =>
                InvokeClient(b => b.Administration.RemovePermanentGuest(name)),
            RemoveAllPermanentGuests: () =>
                InvokeClient(static b => b.Administration.RemoveAllPermanentGuests()),
            ChangeStoragePermission: (name, enabled) =>
                InvokeClient(b => b.Administration.ChangeStoragePermission(name, enabled)),
            AddAllStoragePermission: () =>
                InvokeClient(static b => b.Administration.AddAllStoragePermission()),
            RemoveAllStoragePermission: () =>
                InvokeClient(static b => b.Administration.RemoveAllStoragePermission()),
            RequestFullGuestList: () =>
                InvokeClient(static b => b.Administration.RequestFullGuestList()),
            BootSpecificHouseGuest: name =>
                InvokeClient(b => b.Administration.BootSpecificHouseGuest(name)),
            BootEveryone: () =>
                InvokeClient(static b => b.Administration.BootEveryone()),
            SetHooksVisibility: visible =>
                InvokeClient(b => b.Administration.SetHooksVisibility(visible)),
            ModifyAllegianceGuestPermission: enabled =>
                InvokeClient(b => b.Administration.ModifyAllegianceGuestPermission(enabled)),
            ModifyAllegianceStoragePermission: enabled =>
                InvokeClient(b => b.Administration.ModifyAllegianceStoragePermission(enabled)));

    private bool InvokeClient(Action<ClientCommandController.Bindings> invoke)
    {
        lock (_gate)
        {
            ClientCommandController.Bindings? bindings = _clientCommands;
            if (_state != 1 || bindings is null)
                return false;
            invoke(bindings);
            return true;
        }
    }

    private TResult ReadClient<TResult>(
        Func<ClientCommandController.Bindings, TResult> read,
        TResult fallback)
    {
        lock (_gate)
        {
            ClientCommandController.Bindings? bindings = _clientCommands;
            return _state == 1 && bindings is not null
                ? read(bindings)
                : fallback;
        }
    }

    private bool SendIfActive(Action send)
    {
        lock (_gate)
        {
            if (_state != 1)
                return false;
            send();
            return true;
        }
    }
}
