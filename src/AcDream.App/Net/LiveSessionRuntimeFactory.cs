using System.Diagnostics;
using AcDream.App.Combat;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Selection;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Settings;
using AcDream.App.Spells;
using AcDream.App.Streaming;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;
using AcDream.Core.Social;
using AcDream.Core.Spells;
using AcDream.Core.World;
using AcDream.Content;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.Runtime.Chat;
using AcDream.UI.Abstractions.Panels.Chat;
using AcDream.UI.Abstractions.Panels.Vitals;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Net;

internal sealed record LiveSessionPlayerRuntime(
    LocalPlayerIdentityState Identity,
    RuntimeLocalPlayerMovementState Controller,
    LiveWorldOriginState WorldOrigin);

internal sealed record LiveSessionDomainRuntime(
    GameRuntime Runtime,
    RuntimeEntityObjectLifetime EntityObjects,
    RuntimeCharacterState Character,
    RuntimeActionState Actions,
    RuntimeInventoryState Inventory,
    RuntimeCommunicationState Communication);

internal sealed record LiveSessionUiRuntime(
    RetailUiRuntime? RetailUi,
    VitalsVM? Vitals,
    CharacterSheetProvider? CharacterSheet,
    MagicRuntime? Magic,
    PaperdollFramePresenter? Paperdoll);

internal sealed record LiveSessionInteractionRuntime(
    RuntimeSettingsController Settings,
    GameplayInputFrameController GameplayInput,
    PlayerModeController PlayerMode,
    PlayerModeAutoEntry PlayerModeAutoEntry,
    ItemInteractionController ItemInteraction,
    RuntimeCombatAttackState CombatAttack,
    SelectionInteractionController SelectionInteractions);

internal sealed record LiveSessionWorldRuntime(
    IDatReaderWriter Dats,
    object DatLock,
    Audio.WorldAudioSessionGate? WorldAudio,
    GpuWorldState WorldState,
    LiveEntityRuntime LiveEntities,
    LiveEntitySessionController EntitySession,
    WorldEnvironmentController Environment,
    DeferredLocalPlayerTeleportNetworkSink Teleport,
    DatSpawnClaimHydrationClassifier SpawnClaims,
    EquippedChildRenderController EquippedChildren,
    RetailSelectionScene SelectionScene,
    ParticleVisibilityController ParticleVisibility,
    RetailInboundEventDispatcher InboundEvents,
    LiveEntityLivenessController Liveness,
    LiveEntityNetworkUpdateController NetworkUpdates,
    LiveEntityHydrationController Hydration,
    EntityEffectController EntityEffects,
    AnimationHookFrameQueue AnimationHookFrames,
    LiveEntityPresentationController Presentation,
    RemoteMovementObservationTracker RemoteMovementObservations,
    RenderSceneShadowRuntime? RenderSceneShadow,
    RuntimePlacementPresentationSink PlacementProjection,
    RuntimePlacementProjectionRetrySlot PlacementRetries,
    RuntimeFirstEntryDriveController FirstEntryDrive,
    RuntimeAcceptedPositionDriveController AcceptedPositionDrive,
    RuntimeRemotePlacementDriveController RemotePlacementDrive);

internal sealed class LiveSessionRuntimeFactory
{
    private readonly LiveSessionPlayerRuntime _player;
    private readonly LiveSessionDomainRuntime _domain;
    private readonly LiveSessionUiRuntime _ui;
    private readonly LiveSessionInteractionRuntime _interaction;
    private readonly LiveSessionWorldRuntime _world;
    private readonly LiveSessionCommandSurface _commands;
    private readonly Action<string> _log;
    private readonly LiveMovementStatsApplier _movementStats;
    private readonly SessionStatusWriter _statusWriter;
    private readonly string _sessionId;
    private readonly IReadOnlyList<string> _loginCommands;
    private readonly TimeSpan _loginCommandDelay;
    private readonly TimeProvider _timeProvider;
    private readonly DatChatPoseCatalog _chatPoses;

    private readonly string _chatLogDirectory;

    private ChatSessionLog? _chatSessionLog;

    private ChatTranscriptLogWriter? _chatLogWriter;

    public LiveSessionRuntimeFactory(
        LiveSessionPlayerRuntime player,
        LiveSessionDomainRuntime domain,
        LiveSessionUiRuntime ui,
        LiveSessionInteractionRuntime interaction,
        LiveSessionWorldRuntime world,
        LiveSessionCommandSurface commands,
        Action<string> log,
        SessionStatusWriter? statusWriter = null,
        string sessionId = "app",
        IReadOnlyList<string>? loginCommands = null,
        int loginCommandDelayMs = 500,
        TimeProvider? timeProvider = null,
        string? chatLogDirectory = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _interaction = interaction
            ?? throw new ArgumentNullException(nameof(interaction));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _statusWriter = statusWriter ?? new SessionStatusWriter(null);
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        if (loginCommandDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(loginCommandDelayMs));
        }
        _chatLogDirectory = chatLogDirectory
            ?? AcDream.Platform.ApplicationPathSet.Resolve().LogsDirectory;
        _loginCommands = loginCommands is null ? [] : [.. loginCommands];
        _loginCommandDelay = TimeSpan.FromMilliseconds(loginCommandDelayMs);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _chatPoses = DatChatPoseCatalog.Load(_world.Dats, _world.DatLock);
        _movementStats = new LiveMovementStatsApplier(
            _player.Controller,
            _domain.Character.MovementSkills,
            _log);
    }

    public LiveSessionHost Create(
        LiveSessionController controller,
        LiveSessionConnectOptions connectOptions)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(connectOptions);
        var resetHost = new GraphicalRuntimeGenerationResetHost(
            _world.LiveEntities,
            () => _world.RenderSceneShadow?.DrainUpdateBoundary());
        LiveSessionResetPlan reset =
            LiveSessionResetManifest.Create(
                CreateResetBindings(resetHost));
        var loginCommands = new LoginCommandSequence(
            _loginCommands,
            _loginCommandDelay,
            new RuntimeChatCommandFeedback(_domain.Communication),
            _commands,
            failure => _statusWriter.LoginCommandFailed(
                _sessionId,
                failure.CommandIndex,
                failure.Command,
                failure.Error),
            _timeProvider);
        return new LiveSessionHost(controller, new LiveSessionHostBindings(
            Routing: new(
                CreateEventRouter,
                session => _commands.Attach(new LiveSessionCommandRouter(
                    CreateCommandBindings(session)))),
            Reset: reset.Execute,
            Selection: new(
                SetPlayerIdentity: id => _player.Identity.ServerGuid = id,
                SetVitalsIdentity: id => _ui.Vitals?.SetLocalPlayerGuid(id),
                SetChatIdentity: _domain.Communication.Chat.SetLocalPlayerGuid,
                MarkPersistent: _world.WorldState.MarkPersistent,
                SetVanishProbeIdentity: id => EntityVanishProbe.PlayerGuid = id,
                ClearCombat: _domain.Actions.Combat.Clear,
                ArmLoginTunnel: _world.Teleport.ArmLoginTunnel),
            EnteredWorld: new(
                SetActiveCharacter: _interaction.Settings.SetActiveCharacter,
                RestoreLayout: () =>
                {
                    _interaction.Settings.SetGameplayDisplay(true);
                    _ui.RetailUi?.RestoreLayout();
                    _ui.RetailUi?.InventoryPanelController?.Populate();
                    _ui.Paperdoll?.MarkDirty();
                    _ui.RetailUi?.RedeclareSocialPanelAfterWorldEntry();
                },
                SyncToolbar: () => _ui.RetailUi?.SyncToolbarWindowButtons(),
                LoadCharacterSettings: name =>
                {
                    _interaction.Settings.LoadCharacterContext(name);
                    _ui.RetailUi?.LoadJournal(name);
                },
                ArmPlayerModeAutoEntry: _interaction.PlayerModeAutoEntry.Arm,
                ResumeWorldAudio: () => _world.WorldAudio?.ResumeForWorldEntry()),
            Connecting: (host, port, user) =>
                _domain.Communication.Chat.OnSystemMessage(
                    $"connecting to {host}:{port} as {user}",
                    chatType: 1),
            Connected: () =>
            {
                _domain.Communication.Chat.OnSystemMessage(
                    "connected — character list received",
                    chatType: 1);
                _statusWriter.Connected(_sessionId);
            },
            Roster: roster => _statusWriter.CharacterList(_sessionId, roster),
            CharacterEntered: selection => _statusWriter.EnteredWorld(
                _sessionId,
                selection.CharacterId,
                selection.CharacterName),
            LoginCommands: loginCommands,
            CharacterCreated: identity => _statusWriter.CharacterCreated(
                _sessionId,
                identity.Guid,
                identity.Name),
            CreationFailed: rejection => _statusWriter.CreationFailed(
                _sessionId,
                rejection.RawCode,
                rejection.Reason,
                rejection.AttemptedName)),
            connectOptions with { PollConnectionDuringTicks = true });
    }

    private ChatLogResult SetChatLogFile(string name)
    {
        ChatSessionLog log = _chatSessionLog ??= new ChatSessionLog(_chatLogDirectory);
        ChatTranscriptLogWriter writer = _chatLogWriter ??= new ChatTranscriptLogWriter(log);
        string? closedName = log.CurrentName;

        writer.Detach();
        bool closed = log.Close();

        if (string.IsNullOrWhiteSpace(name))
            return new ChatLogResult(Opened: false, closed, string.Empty, closedName);

        bool opened = log.Open(name, out string resolved);
        if (opened)
            writer.Attach(_domain.Communication.Chat);

        return new ChatLogResult(opened, closed, resolved, closedName);
    }

    private LiveSessionResetBindings CreateResetBindings(
        IRuntimeGenerationResetHost resetHost) => new()
    {
        MouseCapture = _interaction.GameplayInput.ResetSession,
        PlayerPresentation = ResetPlayerPresentation,
        TeleportPresentation =
            _world.Teleport.ResetGenerationPresentation,
        WorldAudio = () => _world.WorldAudio?.SuspendForSessionReset(),
        SessionDialogs = () => _ui.RetailUi?.ResetSessionTransientUi(),
        SettingsCharacterContext =
            _interaction.Settings.RestoreDefaultCharacterContext,
        EquippedChildren = _world.EquippedChildren.Clear,
        InteractionPresentation =
            _interaction.SelectionInteractions.ResetGenerationPresentation,
        SelectionPresentation = _world.SelectionScene.Reset,
        ParticleVisibility = _world.ParticleVisibility.Reset,
        InboundEventFifo = _world.InboundEvents.Clear,
        LiveLiveness = _world.Liveness.Clear,
        RuntimeGeneration = generation =>
            _domain.Runtime.ResetGeneration(generation, resetHost),
        SessionIdentityPresentation = ResetIdentityPresentation,
        NetworkEffects = _world.EntityEffects.ClearNetworkState,
        AnimationHookFrames = _world.AnimationHookFrames.Clear,
        LivePresentation = _world.Presentation.Clear,
        RemoteMovementDiagnostics = _world.RemoteMovementObservations.Clear,
    };

    private void ResetPlayerPresentation()
    {
        _movementStats.Reset();
        _interaction.PlayerMode.ResetSession();
        _world.SpawnClaims.Reset();
    }

    private void ResetIdentityPresentation(
        RuntimeGenerationToken retiringGeneration)
    {
        RuntimeGenerationResetSnapshot reset =
            _domain.Runtime.GenerationReset.CaptureSnapshot();
        if (reset.IsActive
            || reset.LastCompletedGeneration != retiringGeneration)
        {
            throw new InvalidOperationException(
                $"Runtime generation {retiringGeneration.Value} has not "
                + "converged before App identity projection teardown.");
        }

        _ui.Vitals?.SetLocalPlayerGuid(0u);
        EntityVanishProbe.PlayerGuid = 0u;
        _interaction.Settings.ResetActiveCharacterKey();
        _world.NetworkUpdates.ResetSessionState();
        _world.Hydration.ResetSessionState();
        _ui.Paperdoll?.ResetSession();

        _player.WorldOrigin.Reset();
    }

    private ILiveSessionEventRouting CreateEventRouter(WorldSession session)
    {
        session.DataVersions = new DddDataVersions(
            _world.Dats.PortalIteration, _world.Dats.CellIteration, _world.Dats.LanguageIteration);
        SkillTable? skillTable = _world.Dats.Get<SkillTable>(0x0E000004u);
        if (_ui.CharacterSheet is not null)
        {
            _ui.CharacterSheet.SkillTable = skillTable;
            _ui.CharacterSheet.ExperienceTable =
                CharacterSheetProvider.LoadExperienceTable(_world.Dats, _log);
        }

        var route = new LiveSessionEventRouter(
            session,
            _world.EntitySession.CreateSink(),
            new LiveEnvironmentSessionSink(
                _world.Environment.ApplyAdminEnvirons,
                _world.Environment.SynchronizeFromServer),
            CreateInventoryBindings(),
            CreateCharacterBindings(skillTable),
            new LiveSocialSessionBindings(
                _domain.Communication.Chat,
                _domain.Communication.TurbineChat,
                _domain.Communication.Friends,
                _domain.Communication.Squelch,
                (text, type) => _domain.Communication.AddText(text, type),
                Fellowship: _domain.Runtime.FellowshipOwner,
                Allegiance: _domain.Runtime.AllegianceOwner,
                Trade: _domain.Runtime.TradeOwner,
                House: _domain.Runtime.HouseOwner,
                Contracts: _domain.Runtime.ContractsOwner,
                PlayerGuid: () => _player.Identity.ServerGuid));
        return new GraphicalSessionEventRoute(
            route,
            _domain.Runtime,
            _world.PlacementProjection,
            _world.PlacementRetries,
            _world.FirstEntryDrive,
            _ =>
            {
                _world.Teleport.OnLocalPlayerFirstEntryCompleted();
                session.SendHouseQuery();
            },
            _world.AcceptedPositionDrive,
            _world.RemotePlacementDrive);
    }

    private LiveInventorySessionBindings CreateInventoryBindings() => new(
        _domain.Inventory.Objects,
        PlayerGuid: () => _player.Identity.ServerGuid,
        OnShortcuts: _domain.Inventory.Shortcuts.Load,
        OnUseDone: error =>
        {
            _domain.Inventory.ExternalContainers.ApplyUseDone(error);
            _domain.Actions.SpellCast.CompleteUse(error);
            _domain.Actions.Transactions.CompleteUse(error);
        },
        _domain.Inventory.ItemMana,
        ExternalContainers: _domain.Inventory.ExternalContainers,
        OnAppraisal: appraisal =>
        {
            if (_ui.RetailUi is { } retailUi)
                retailUi.HandleAppraisal(appraisal);
            else
                _interaction.ItemInteraction.AcceptAppraisalResponse(appraisal.Guid);
        },
        Vendor: _domain.Inventory.Vendor);

    private LiveCharacterSessionBindings CreateCharacterBindings(
        SkillTable? skillTable)
    {
        var skillCreditResolver = new LiveSkillCreditResolver(skillTable);
        return new LiveCharacterSessionBindings(
            _domain.Actions.Combat,
            _domain.Character,
            ResolveSkillFormulaBonus: skillCreditResolver.Resolve,
            OnSkillsUpdated: (runSkill, jumpSkill) =>
                _movementStats.Apply("skills"),
            OnConfirmationRequest: request =>
                _ui.RetailUi?.HandleConfirmationRequest(request),
            OnConfirmationDone: done =>
                _ui.RetailUi?.HandleConfirmationDone(done),
            ClientTime: ClientTimerNow,
            OnMovementStatsUpdated: () => _movementStats.Apply("stats"),
            OnCharacterOptionsChanged: (_, options2) =>
            {
                _interaction.Settings.SyncChatFromServerOptions(options2);
                _interaction.Settings.SetUiLocked(
                    _domain.Character.Options.GetOptionBit(CharacterOptionId.LockUI));
                // OP4 re-review R2: open option-bearing panels re-read live
                // bits at every seed (login + reconnect) — see
                // RuntimeSettingsController.ServerOptionsSeeded.
                _interaction.Settings.NotifyServerOptionsSeeded();
            });
    }

    private LiveSessionCommandBindings CreateCommandBindings(
        WorldSession session)
    {
        void SendSingleCharacterOption(uint optionId, bool value) =>
            _domain.Character.Options.TrySetOption(
                optionId,
                value,
                sendAutoSave: session.SendSetSingleCharacterOption);

        void SaveCharacterOptionsIfDirty() =>
            _domain.Character.Options.TryFlush(() =>
            {
                CharacterOptionsBlobEcho echo = CharacterOptionsBlobSource.Capture(
                    _domain.Character,
                    _domain.Inventory.Shortcuts);
                session.SendSetCharacterOptions(
                    echo.Options1,
                    echo.Options2,
                    echo.Shortcuts,
                    echo.FavoriteSpells,
                    echo.DesiredComponents,
                    echo.SpellbookFilters);
            });

        return new(
        ClientCommands: new ClientCommandController.Bindings(
            TeleportToLifestone: session.SendTeleportToLifestone,
            TeleportToMarketplace: session.SendTeleportToMarketplace,
            TeleportToPkArena: session.SendTeleportToPkArena,
            TeleportToPkLiteArena: session.SendTeleportToPkLiteArena,
            TeleportToHouse: session.SendTeleportToHouse,
            TeleportToMansion: session.SendTeleportToMansion,
            QueryAge: session.SendQueryAge,
            QueryBirth: session.SendQueryBirth,
            ToggleFrameRate: _interaction.Settings.ToggleFrameRate,
            ToggleUiLock: () =>
            {
                bool locked = !_domain.Character.Options.GetOptionBit(
                    CharacterOptionId.LockUI);
                SendSingleCharacterOption((uint)CharacterOptionId.LockUI, locked);
                _interaction.Settings.SetUiLocked(locked);
            },
            ShowSystemMessage:
                text => _domain.Communication.Chat.OnSystemMessage(text, 0x00u),
            ShowClientLocalMessage:
                text => _domain.Communication.AddText(
                    text, RetailLogTextType.ClientLocal),
            ShowWeenieError:
                code =>
                {
                    (string? text, RetailLogTextType type) = WeenieErrorMessages.Resolve(code, null);
                    if (text is not null)
                        _domain.Communication.AddText(text, type);
                    else
                        Console.WriteLine($"[weenie-error] unmapped code=0x{code:X4}");
                },
            PlayerPublicWeenieBitfield: () =>
                _domain.EntityObjects.Objects.Get(_player.Identity.ServerGuid)?
                    .PublicWeenieBitfield,
            ClientVersion: () =>
                typeof(LiveSessionRuntimeFactory).Assembly
                    .GetName().Version?.ToString(3)
                ?? "unknown",
            CurrentPosition: () => _player.Controller.Controller?.CellPosition,
            LastOutsideCorpsePosition: () =>
                _domain.Character.LocalPlayer.GetPosition(0x0Eu),
            ShowConfirmation: (message, completed) =>
                _ui.RetailUi?.ShowConfirmation(message, completed),
            Suicide: session.SendSuicide,
            ClearChat: _ => _domain.Communication.Chat.Clear(),
            SetChatLogFile: SetChatLogFile,
            SaveUi: name => _ui.RetailUi?.SaveNamedLayout(name),
            LoadUi: name => _ui.RetailUi?.RestoreNamedLayout(name),
            SaveAutoUi: () => _ui.RetailUi?.SaveLayout(),
            LoadAutoUi: () => _ui.RetailUi?.RestoreLayout(),
            IsAway: () =>
                _domain.EntityObjects.Objects.Get(_player.Identity.ServerGuid)?
                    .Properties.GetBool(0x6Eu) == true,
            SetAway: session.SendSetAfkMode,
            SetAwayMessage: session.SendSetAfkMessage,
            AcceptLootPermits: () =>
                _domain.Character.Options.GetOptionBit(
                    CharacterOptionId.AcceptLootPermits),
            SetAcceptLootPermits: value =>
                SendSingleCharacterOption(
                    (uint)CharacterOptionId.AcceptLootPermits, value),
            DisplayConsent: session.SendDisplayConsent,
            ClearConsent: session.SendClearConsent,
            RemoveConsent: session.SendRemoveConsent,
            SendEmote: session.SendEmote,
            _domain.Communication.Friends,
            AddFriend: session.SendAddFriend,
            RemoveFriend: session.SendRemoveFriend,
            ClearFriends: session.SendClearFriends,
            RequestLegacyFriends: session.SendLegacyFriendsListRequest,
            _domain.Communication.Squelch,
            ModifyCharacterSquelch: session.SendModifyCharacterSquelch,
            ModifyAccountSquelch: session.SendModifyAccountSquelch,
            ModifyGlobalSquelch: session.SendModifyGlobalSquelch,
            LastTeller: () =>
                _domain.Communication.CommandTargets.LastIncomingTellSender,
            ClearDesiredComponents: () =>
            {
                session.SendClearDesiredComponents();
            },
            HasOpenVendor: () => false,
            FillComponentBuyList: (_, _) => { },
            EnterPkLite: session.SendEnterPkLite,
            IsUsingTurbineChat: () => _domain.Communication.TurbineChat.Enabled,
            SetChatTitle: _ => { },
            SetSingleCharacterOption: SendSingleCharacterOption,
            AddPlayerPermission: session.SendAddPlayerPermission,
            RemovePlayerPermission: session.SendRemovePlayerPermission,
            RequestAvailableHouses: session.SendListAvailableHouses,
            RequestChannelIndex: session.SendIndexChannels,
            RequestChannelList: session.SendListChannel,
            JoinGmChannel: session.SendOnChannel,
            LeaveGmChannel: session.SendOffChannel,
            RecallAllegianceHometown: session.SendRecallAllegianceHometown,
            RequestAllegianceInfo: session.SendAllegianceInfoRequest,
            AbandonHouse: session.SendAbandonHouse,
            Administration: new ClientCommandController.AdministrationBindings(
                BreakAllegianceBoot: session.SendBreakAllegianceBoot,
                AllegianceChatBoot: session.SendAllegianceChatBoot,
                AllegianceChatGag: session.SendAllegianceChatGag,
                AllegianceBroadcast: text =>
                    session.SendChannel(0x02000000u, text),
                ListAllegianceBans: session.SendListAllegianceBans,
                AddAllegianceBan: session.SendAddAllegianceBan,
                RemoveAllegianceBan: session.SendRemoveAllegianceBan,
                ListAllegianceOfficers: session.SendListAllegianceOfficers,
                ClearAllegianceOfficers: session.SendClearAllegianceOfficers,
                SetAllegianceOfficer: session.SendSetAllegianceOfficer,
                RemoveAllegianceOfficer: session.SendRemoveAllegianceOfficer,
                ListAllegianceOfficerTitles: session.SendListAllegianceOfficerTitles,
                ClearAllegianceOfficerTitles: session.SendClearAllegianceOfficerTitles,
                SetAllegianceOfficerTitle: session.SendSetAllegianceOfficerTitle,
                QueryAllegianceName: session.SendQueryAllegianceName,
                SetAllegianceName: session.SendSetAllegianceName,
                ClearAllegianceName: session.SendClearAllegianceName,
                AllegianceLockAction: session.SendAllegianceLockAction,
                SetAllegianceApprovedVassal: session.SendSetAllegianceApprovedVassal,
                AllegianceHouseAction: session.SendAllegianceHouseAction,
                QueryMotd: session.SendQueryMotd,
                SetMotd: session.SendSetMotd,
                ClearMotd: session.SendClearMotd,
                SetOpenHouseStatus: session.SendSetOpenHouseStatus,
                AddPermanentGuest: session.SendAddPermanentGuest,
                RemovePermanentGuest: session.SendRemovePermanentGuest,
                RemoveAllPermanentGuests: session.SendRemoveAllPermanentGuests,
                ChangeStoragePermission: session.SendChangeStoragePermission,
                AddAllStoragePermission: session.SendAddAllStoragePermission,
                RemoveAllStoragePermission: session.SendRemoveAllStoragePermission,
                RequestFullGuestList: session.SendRequestFullGuestList,
                BootSpecificHouseGuest: session.SendBootSpecificHouseGuest,
                BootEveryone: session.SendBootEveryone,
                SetHooksVisibility: session.SendSetHooksVisibility,
                ModifyAllegianceGuestPermission:
                    session.SendModifyAllegianceGuestPermission,
                ModifyAllegianceStoragePermission:
                    session.SendModifyAllegianceStoragePermission),
            IsPersistentDaylight: () =>
                _domain.Character.Options.GetOptionBit(
                    CharacterOptionId.PersistentAtDay),
            SetPersistentDaylight: enabled =>
                SendSingleCharacterOption(
                    (uint)CharacterOptionId.PersistentAtDay,
                    enabled),
            SetLandscapeRadius: radius =>
                _interaction.Settings.SaveDisplay(
                    _interaction.Settings.Display with
                    {
                        LandscapeDrawDistance = radius,
                    }),
            SetFieldOfView: degrees =>
                _interaction.Settings.SaveDisplay(
                    _interaction.Settings.Display with
                    {
                        FieldOfView = degrees,
                    })),
        _domain.Communication.Chat,
        _domain.Communication.TurbineChat,
        PlayerGuid: () => _player.Identity.ServerGuid,
        SendTalk: session.SendTalk,
        SendTell: session.SendTell,
        SendTalkDirect: session.SendTalkDirect,
        SendChannel: session.SendChannel,
        SendTurbineChat: (
            roomId,
            chatType,
            dispatchType,
            senderGuid,
            text,
            cookie) => session.SendTurbineChatTo(
                roomId,
                chatType,
                dispatchType,
                senderGuid,
                text,
                cookie),
        AddShortcut: session.SendAddShortcut,
        RemoveShortcut: session.SendRemoveShortcut,
        AddFavorite: session.SendAddSpellFavorite,
        RemoveFavorite: session.SendRemoveSpellFavorite,
        SetSpellbookFilter: session.SendSpellbookFilter,
        ForgetSpell: session.SendRemoveSpell,
        SetDesiredComponent: session.SendSetDesiredComponentLevel,
        ClearDesiredComponents: session.SendClearDesiredComponents,
        RaiseAttribute: session.SendRaiseAttribute,
        RaiseVital: session.SendRaiseVital,
        RaiseSkill: session.SendRaiseSkill,
        TrainSkill: session.SendTrainSkill,
        AddFriend: session.SendAddFriend,
        RemoveFriend: session.SendRemoveFriend,
        ClearFriends: session.SendClearFriends,
        RequestLegacyFriends: session.SendLegacyFriendsListRequest,
        OpenTradeNegotiations: session.SendOpenTradeNegotiations,
        CloseTradeNegotiations: session.SendCloseTradeNegotiations,
        AddToTrade: item => session.SendAddToTrade(item),
        AcceptTrade: (partner, selfAccepted, partnerAccepted) =>
            session.SendAcceptTrade(
                partner, 0d, 0u, partner, selfAccepted, partnerAccepted),
        DeclineTrade: session.SendDeclineTrade,
        ResetTrade: session.SendResetTrade,
        ModifyCharacterSquelch: session.SendModifyCharacterSquelch,
        ModifyAccountSquelch: session.SendModifyAccountSquelch,
        ModifyGlobalSquelch: session.SendModifyGlobalSquelch,
        Communication: _domain.Communication,
        CharacterState: _domain.Character,
        SendSingleCharacterOption: SendSingleCharacterOption,
        SaveCharacterOptions: SaveCharacterOptionsIfDirty,
        SendSetTitle: session.SendSetTitle,
        SendFellowshipCreate: session.SendFellowshipCreate,
        SendFellowshipRecruit: session.SendFellowshipRecruit,
        SendFellowshipDismiss: session.SendFellowshipDismiss,
        SendFellowshipQuit: session.SendFellowshipQuit,
        SendFellowshipAssignNewLeader: session.SendFellowshipAssignNewLeader,
        SendFellowshipChangeOpenness: session.SendFellowshipChangeOpenness,
        SendFellowshipUpdateRequest: session.SendFellowshipUpdateRequest,
        SendAllegianceSwear: session.SendAllegianceSwear,
        SendAllegianceBreak: session.SendAllegianceBreak,
        SendAllegianceKick: session.SendAllegianceKick,
        SendAllegianceInfoRequest: session.SendAllegianceInfoRequest,
        SendAllegianceUpdateRequest: session.SendAllegianceUpdateRequest,
        Log: _log,
        ResolvePose: command => _chatPoses.Resolve(
            command,
            male: _domain.EntityObjects.Objects
                .Get(_player.Identity.ServerGuid)?
                .Properties.GetInt(0x71u) == 1),
        ExecuteMotion: motion => _player.Controller.ExecuteMotion(motion),
        SendSoulEmote: session.SendSoulEmote);
    }

    private static double ClientTimerNow() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
