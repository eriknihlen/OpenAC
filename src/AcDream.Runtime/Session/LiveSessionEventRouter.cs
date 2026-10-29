using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Player;
using AcDream.Core.Properties;
using AcDream.Core.Social;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Session;

public sealed record LiveEntitySessionSink(
    Action<WorldSession.EntitySpawn> Spawned,
    Action<DeleteObject.Parsed> Deleted,
    Action<PickupEvent.Parsed> PickedUp,
    Action<WorldSession.EntityMotionUpdate> MotionUpdated,
    Action<WorldSession.EntityPositionUpdate> PositionUpdated,
    Action<VectorUpdate.Parsed> VectorUpdated,
    Action<SetState.Parsed> StateUpdated,
    Action<ParentEvent.Parsed> ParentUpdated,
    Action<uint> TeleportStarted,
    Action<ObjDescEvent.Parsed> AppearanceUpdated,
    Action<WorldSession.EntitySpawn> DescriptionRefreshed,
    Action<PlayPhysicsScript> PlayPhysicsScript,
    Action<PlayPhysicsScriptType> PlayPhysicsScriptType,
    Action<SoundEvent> SoundEvent);

public sealed record LiveEnvironmentSessionSink(
    Action<uint> EnvironChanged,
    Action<double> ServerTimeUpdated);

public sealed record LiveInventorySessionBindings(
    ClientObjectTable Objects,
    Func<uint> PlayerGuid,
    Action<IReadOnlyList<ShortcutEntry>>? OnShortcuts,
    Action<uint>? OnUseDone,
    ItemManaState? ItemMana,
    ExternalContainerState? ExternalContainers,
    Action<AppraiseInfoParser.Parsed>? OnAppraisal = null,
    VendorState? Vendor = null,
    RuntimeBookState? Book = null,
    Func<string>? PlayerName = null);

public sealed record LiveCharacterSessionBindings(
    CombatState Combat,
    RuntimeCharacterState Character,
    Func<uint, uint, IReadOnlyDictionary<uint, uint>, uint>? ResolveSkillFormulaBonus,
    Action<int, int>? OnSkillsUpdated,
    Action<GameEvents.CharacterConfirmationRequest>? OnConfirmationRequest,
    Action<GameEvents.CharacterConfirmationDone>? OnConfirmationDone,
    Func<double>? ClientTime,
    Action? OnMovementStatsUpdated = null,
    Action<uint, uint>? OnCharacterOptionsChanged = null);

public sealed record LiveSocialSessionBindings(
    ChatLog Chat,
    TurbineChatState TurbineChat,
    FriendsState? Friends,
    SquelchState? Squelch,
    Action<string, RetailLogTextType>? AddText = null,
    RuntimeFellowshipState? Fellowship = null,
    RuntimeAllegianceState? Allegiance = null,
    RuntimeTradeState? Trade = null,
    RuntimeHouseState? House = null,
    RuntimeContractState? Contracts = null,
    Func<uint>? PlayerGuid = null,
    Action<string>? OnLocalPlayerDeath = null,
    RuntimeHousePurchaseState? HousePurchase = null,
    RuntimeChessState? Chess = null);

public sealed class LiveSessionEventRouter : ILiveSessionEventRouting
{
    private readonly LiveSessionSubscriptionSet _subscriptions = new();
    private readonly Action<int>? _constructionCheckpoint;
    private readonly WorldSession _session;
    private readonly LiveEntitySessionSink _entities;
    private readonly LiveEnvironmentSessionSink _environment;
    private readonly LiveInventorySessionBindings _inventory;
    private readonly LiveCharacterSessionBindings _character;
    private readonly LiveSocialSessionBindings _social;
    private readonly RuntimeActionState _actions;
    private int _constructionStep;
    private int _accepting;
    private int _lifecycleState;

    /// <summary>
    /// The action state is required rather than optional. Both hosts have
    /// always supplied it, and an optional parameter let a host route a
    /// session without the combat and death signals plugins observe without
    /// anything saying so. The compiler now refuses that shape.
    /// </summary>
    public LiveSessionEventRouter(
        WorldSession session,
        LiveEntitySessionSink entities,
        LiveEnvironmentSessionSink environment,
        LiveInventorySessionBindings inventory,
        LiveCharacterSessionBindings character,
        LiveSocialSessionBindings social,
        RuntimeActionState actions,
        Action<int>? constructionCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(actions);
        Validate(entities, environment, inventory, character, social);
        _session = session;
        _entities = entities;
        _environment = environment;
        _inventory = inventory;
        _character = character;
        _social = social;
        _actions = actions;
        _constructionCheckpoint = constructionCheckpoint;
    }

    /// <summary>
    /// Sends whatever a page turn asked for. Opening a book and turning
    /// a page take the same two answers, so they send the same way.
    /// </summary>
    private static void SendBookPageRequest(
        WorldSession session, uint bookGuid, RuntimeBookPageTurn turn)
    {
        switch (turn.Action)
        {
            case RuntimeBookPageAction.RequestPageText:
                session.SendGameAction(BookRequests.BuildBookPageData(
                    session.NextGameActionSequence(), bookGuid, turn.Page));
                break;
            case RuntimeBookPageAction.AddPage:
                session.SendGameAction(BookRequests.BuildBookAddPage(
                    session.NextGameActionSequence(), bookGuid));
                break;
            default:
                break;
        }
    }

    public void Attach()
    {
        if (Interlocked.CompareExchange(ref _lifecycleState, 1, 0) != 0)
            throw new InvalidOperationException(
                "Live-session event routing can only attach once.");

        Interlocked.Exchange(ref _accepting, 1);
        WorldSession session = _session;
        LiveEntitySessionSink entities = _entities;
        LiveEnvironmentSessionSink environment = _environment;
        LiveInventorySessionBindings inventory = _inventory;
        LiveCharacterSessionBindings character = _character;
        LiveSocialSessionBindings social = _social;
        RuntimeFellowshipEventSink? fellowshipEvents = social.Fellowship is { } fellowship
            ? new RuntimeFellowshipEventSink(fellowship, inventory.PlayerGuid, text =>
            {
                if (social.AddText is { } addText)
                    addText(text, RetailLogTextType.Default);
                else
                    social.Chat.OnSystemMessage(text, (uint)RetailLogTextType.Default);
            })
            : null;

        try
        {
            _subscriptions.Add(ObjectTableWiring.Wire(
                session,
                inventory.Objects,
                inventory.PlayerGuid,
                character.Character.LocalPlayer,
                IsAccepting));
            ConstructionCheckpoint();
            _subscriptions.Add(CombatStateWiring.Wire(
                session,
                character.Combat,
                IsAccepting));
            ConstructionCheckpoint();

            // The death fact is read off the wire before either host's own
            // entity sink sees the packet, so a host that draws nothing knows
            // a creature has died at the same moment a host that draws it
            // does.
            RuntimeCreatureDeathState creatureDeath = _actions.CreatureDeath;
            Action<WorldSession.EntitySpawn> spawned = spawn =>
            {
                creatureDeath.ObserveSpawn(spawn);
                entities.Spawned(spawn);
            };
            Action<WorldSession.EntityMotionUpdate> motionUpdated = update =>
            {
                creatureDeath.ObserveMotion(update);
                entities.MotionUpdated(update);
            };
            Action<DeleteObject.Parsed> deleted = delete =>
            {
                try
                {
                    entities.Deleted(delete);
                }
                finally
                {
                    creatureDeath.Forget(delete.Guid, delete.InstanceSequence);
                }
            };
            Subscribe(h => session.EntitySpawned += h, h => session.EntitySpawned -= h, spawned);
            Subscribe(h => session.EntityDeleted += h, h => session.EntityDeleted -= h, deleted);
            Subscribe(h => session.EntityPickedUp += h, h => session.EntityPickedUp -= h, entities.PickedUp);
            Subscribe(h => session.MotionUpdated += h, h => session.MotionUpdated -= h, motionUpdated);
            {
                RuntimeActionState actions = _actions;
                // Streaming the selected creature's health is a question the
                // client asks the server, not a thing a window draws, so it
                // is asked here for every host rather than by the one host
                // that has a health meter to fill.
                _subscriptions.Add(new RuntimeSelectedObjectHealthQuery(
                    actions.Selection,
                    inventory.Objects,
                    inventory.PlayerGuid,
                    session.SendQueryHealth));
            }
            Subscribe(h => session.PositionUpdated += h, h => session.PositionUpdated -= h, entities.PositionUpdated);
            Subscribe(h => session.VectorUpdated += h, h => session.VectorUpdated -= h, entities.VectorUpdated);
            Subscribe(h => session.StateUpdated += h, h => session.StateUpdated -= h, entities.StateUpdated);
            Subscribe(h => session.ParentUpdated += h, h => session.ParentUpdated -= h, entities.ParentUpdated);
            Subscribe(h => session.TeleportStarted += h, h => session.TeleportStarted -= h, entities.TeleportStarted);
            Subscribe(h => session.AppearanceUpdated += h, h => session.AppearanceUpdated -= h, entities.AppearanceUpdated);
            Subscribe(
                h => session.EntityDescriptionRefreshed += h,
                h => session.EntityDescriptionRefreshed -= h,
                entities.DescriptionRefreshed);
            Subscribe(
                h => session.PlayPhysicsScriptReceived += h,
                h => session.PlayPhysicsScriptReceived -= h,
                entities.PlayPhysicsScript);
            Subscribe(
                h => session.PlayPhysicsScriptTypeReceived += h,
                h => session.PlayPhysicsScriptTypeReceived -= h,
                entities.PlayPhysicsScriptType);
            Subscribe(
                h => session.SoundEventReceived += h,
                h => session.SoundEventReceived -= h,
                entities.SoundEvent);

            Subscribe(h => session.EnvironChanged += h, h => session.EnvironChanged -= h, environment.EnvironChanged);
            Subscribe(h => session.ServerTimeUpdated += h, h => session.ServerTimeUpdated -= h, environment.ServerTimeUpdated);

            _subscriptions.Add(GameEventWiring.WireAll(
                session.GameEvents,
                inventory.Objects,
                character.Combat,
                character.Character.Spellbook,
                social.Chat,
                character.Character.LocalPlayer,
                social.TurbineChat,
                onSkillsUpdated: (runSkill, jumpSkill) =>
                {
                    character.Character.UpdateMovementSkillBase(
                        runSkill,
                        jumpSkill);
                    character.OnSkillsUpdated?.Invoke(runSkill, jumpSkill);
                    character.OnMovementStatsUpdated?.Invoke();
                },
                resolveSkillFormulaBonus: character.ResolveSkillFormulaBonus,
                onShortcuts: inventory.OnShortcuts,
                playerGuid: inventory.PlayerGuid,
                onUseDone: inventory.OnUseDone,
                onAppraisal: inventory.OnAppraisal,
                itemMana: inventory.ItemMana,
                onConfirmationRequest: character.OnConfirmationRequest,
                onConfirmationDone: character.OnConfirmationDone,
                friends: social.Friends,
                squelch: social.Squelch,
                onDesiredComponents: null,
                onCharacterOptions: (options1, options2, trailerTruncated) =>
                {
                    if (trailerTruncated)
                        return;
                    character.Character.Options.Replace(
                        options1, options2, armServerSeed: true);
                    character.OnCharacterOptionsChanged?.Invoke(options1, options2);
                },
                clientTime: character.ClientTime,
                externalContainers: inventory.ExternalContainers,
                vendor: inventory.Vendor,
                onInterfaceText: social.AddText,
                onLocalPlayerDeath: social.OnLocalPlayerDeath,
                accepting: IsAccepting,
                onBookOpen: inventory.Book is { } bookOpen
                    ? book =>
                    {
                        // Opening a book ends in a page turn, which may
                        // need a page fetched or added before there is
                        // anything to read.
                        RuntimeBookPageTurn turn = bookOpen.ApplyOpenBook(book);
                        SendBookPageRequest(session, book.BookGuid, turn);
                    }
                    : null,
                onBookPageData: inventory.Book is { } bookPageData
                    ? response => bookPageData.ApplyPageData(response)
                    : null,
                onBookAddPageResponse: inventory.Book is { } bookAddPage
                    ? response =>
                    {
                        // Retail re-reads the whole book whenever it cannot
                        // fold the answer into the open one.
                        if (!bookAddPage.ApplyAddPageResponse(
                                response,
                                inventory.PlayerName?.Invoke() ?? string.Empty))
                        {
                            session.SendGameAction(
                                BookRequests.BuildBookData(
                                    session.NextGameActionSequence(),
                                    response.BookGuid));
                            bookAddPage.MarkRequestPending();
                        }
                    }
                    : null,
                onBookInscription: inventory.Book is { } bookInscription
                    ? inscription => bookInscription.ApplyInscription(inscription)
                    : null,
                onFellowshipFullUpdate: social.Fellowship is { } fellowshipFull
                    ? fellowshipFull.ApplyFullUpdate
                    : null,
                onFellowshipUpdateFellow: social.Fellowship is { } fellowshipUpdate
                    ? fellowshipUpdate.ApplyUpdateFellow
                    : null,
                onFellowshipQuit: fellowshipEvents is null ? null : fellowshipEvents.ApplyQuit,
                onFellowshipDismiss: fellowshipEvents is null ? null : fellowshipEvents.ApplyDismiss,
                onFellowshipDisband: fellowshipEvents is null ? null : fellowshipEvents.ApplyDisband,
                onAllegianceUpdate: social.Allegiance is { } allegianceUpdate
                    ? allegianceUpdate.ApplyUpdate
                    : null,
                onAllegianceUpdateDone: social.Allegiance is { } allegianceUpdateDone
                    ? allegianceUpdateDone.ApplyUpdateDone
                    : null,
                onAllegianceUpdateAborted: social.Allegiance is { } allegianceUpdateAborted
                    ? allegianceUpdateAborted.ApplyUpdateAborted
                    : null,
                onAllegianceLoginNotification: social.Allegiance is { } allegianceLogin
                    ? notice => allegianceLogin.ApplyLoginNotification(
                        notice.CharacterGuid,
                        notice.IsLoggedIn)
                    : null,
                onTradeRegister: social.Trade is { } tradeRegister
                    ? update => tradeRegister.ApplyRegister(update, inventory.PlayerGuid())
                    : null,
                onTradeClose: social.Trade is { } tradeClose
                    ? _ =>
                    {
                        tradeClose.ApplyClose();
                        social.AddText?.Invoke(
                            AcDream.Core.Chat.ClientTextRefusals.TradeCancelled,
                            RetailLogTextType.ClientLocal);
                    }
                    : null,
                onTradeAdd: social.Trade is { } tradeAdd
                    ? tradeAdd.ApplyAdd
                    : null,
                onTradeRemove: social.Trade is { } tradeRemove
                    ? tradeRemove.ApplyRemove
                    : null,
                onTradeAccept: social.Trade is { } tradeAccept
                    ? whoAccepted => tradeAccept.ApplyAccept(whoAccepted, inventory.PlayerGuid())
                    : null,
                onTradeDecline: social.Trade is { } tradeDecline
                    ? whoDeclined => tradeDecline.ApplyDecline(whoDeclined, inventory.PlayerGuid())
                    : null,
                onTradeReset: social.Trade is { } tradeReset
                    ? _ => tradeReset.ApplyReset()
                    : null,
                onTradeFailure: social.Trade is { } tradeFailure
                    ? tradeFailure.ApplyFailure
                    : null,
                onTradeClearAcceptance: social.Trade is { } tradeClear
                    ? tradeClear.ApplyClearAcceptance
                    : null,
                onHouseData: social.House is { } houseData
                    ? data => houseData.ApplyHouseData(data, inventory.PlayerGuid())
                    : null,
                onHouseStatus: social.House is { } houseStatus
                    ? weenieError => houseStatus.ApplyHouseStatus(weenieError, inventory.PlayerGuid())
                    : null,
                onHouseUpdateRentTime: social.House is { } houseRentTime
                    ? rentTime => houseRentTime.ApplyRentTime(rentTime, inventory.PlayerGuid())
                    : null,
                onHouseUpdateRentPayment: social.House is { } houseRentPayment
                    ? rent => houseRentPayment.ApplyRentPayment(rent, inventory.PlayerGuid())
                    : null,
                onHouseProfile: social.HousePurchase is { } housePurchase
                    ? housePurchase.ApplyProfile
                    : null,
                onChessJoinGameResponse: social.Chess is { } chessJoin
                    ? chessJoin.ApplyJoinResponse
                    : null,
                onChessStartGame: social.Chess is { } chessStart
                    ? chessStart.ApplyStartGame
                    : null,
                onChessMoveResponse: social.Chess is { } chessMove
                    ? chessMove.ApplyMoveResponse
                    : null,
                onChessOpponentTurn: social.Chess is { } chessTurn
                    ? chessTurn.ApplyOpponentTurn
                    : null,
                onChessOpponentStalemate: social.Chess is { } chessStalemate
                    ? chessStalemate.ApplyOpponentStalemate
                    : null,
                onChessGameOver: social.Chess is { } chessOver
                    ? chessOver.ApplyGameOver
                    : null,
                onContractTable: social.Contracts is { } contractTable
                    ? contractTable.ApplyTable
                    : null,
                onContractUpdate: social.Contracts is { } contractUpdate
                    ? contractUpdate.ApplyUpdate
                    : null,
                onCharacterTitleTable: (displayTitleId, titleIds) =>
                    character.Character.Titles.ReplaceTable(displayTitleId, titleIds),
                onUpdateTitle: (titleId, setAsDisplay) =>
                    character.Character.Titles.ApplyUpdateTitle(titleId, setAsDisplay)));
            ConstructionCheckpoint();

            SubscribeToRecompute<ClientObject>(
                h => inventory.Objects.ObjectAdded += h,
                h => inventory.Objects.ObjectAdded -= h,
                () => RecomputePlayerQualities(inventory, character));
            SubscribeToRecompute<ClientObject>(
                h => inventory.Objects.ObjectUpdated += h,
                h => inventory.Objects.ObjectUpdated -= h,
                () => RecomputePlayerQualities(inventory, character));
            SubscribeToRecompute<ClientObject>(
                h => inventory.Objects.ObjectRemoved += h,
                h => inventory.Objects.ObjectRemoved -= h,
                () => RecomputePlayerQualities(inventory, character),
                removed => AffectsPlayerQualities(removed, inventory.PlayerGuid()));
            SubscribeToRecompute<ClientObjectMove>(
                h => inventory.Objects.ObjectMoved += h,
                h => inventory.Objects.ObjectMoved -= h,
                () => RecomputePlayerQualities(inventory, character));
            SubscribeToRecompute<uint>(
                h => inventory.Objects.ContainerContentsReplaced += h,
                h => inventory.Objects.ContainerContentsReplaced -= h,
                () => RecomputePlayerQualities(inventory, character));
            SubscribeParameterless(
                h => inventory.Objects.Cleared += h,
                h => inventory.Objects.Cleared -= h,
                () => RecomputePlayerQualities(inventory, character));
            Subscribe<LocalPlayerState.AttributeKind>(
                h => character.Character.LocalPlayer.AttributeChanged += h,
                h => character.Character.LocalPlayer.AttributeChanged -= h,
                kind =>
                {
                    if (kind == LocalPlayerState.AttributeKind.Strength)
                        RecomputeBurden(inventory, character);
                });
            SubscribeParameterless(
                h => character.Character.Spellbook.EnchantmentsChanged += h,
                h => character.Character.Spellbook.EnchantmentsChanged -= h,
                () => RecomputeBurden(inventory, character));

            Subscribe<LocalPlayerState.VitalKind>(
                h => character.Character.LocalPlayer.Changed += h,
                h => character.Character.LocalPlayer.Changed -= h,
                kind => RecomputeStamina(kind, character));

            _subscriptions.Add(new CombatChatTranslator(
                character.Combat,
                social.Chat,
                IsAccepting));
            ConstructionCheckpoint();

            Subscribe<HearSpeech.Parsed>(h => session.SpeechHeard += h, h => session.SpeechHeard -= h, speech =>
                social.Chat.OnLocalSpeech(
                    speech.SenderName,
                    speech.Text,
                    speech.SenderGuid,
                    speech.IsRanged,
                    speech.ChatType));
            Subscribe<ServerMessage.Parsed>(
                h => session.ServerMessageReceived += h,
                h => session.ServerMessageReceived -= h,
                message =>
                {
                    if (social.AddText is { } addText)
                        addText(message.Message, (RetailLogTextType)message.ChatType);
                    else
                        social.Chat.OnSystemMessage(message.Message, message.ChatType);
                });
            Subscribe<EmoteText.Parsed>(h => session.EmoteHeard += h, h => session.EmoteHeard -= h, emote =>
                social.Chat.OnEmote(emote.SenderName, emote.Text, emote.SenderGuid));
            Subscribe<SoulEmote.Parsed>(h => session.SoulEmoteHeard += h, h => session.SoulEmoteHeard -= h, emote =>
                social.Chat.OnSoulEmote(emote.SenderName, emote.Text, emote.SenderGuid));
            Subscribe<PlayerKilled.Parsed>(
                h => session.PlayerKilledReceived += h,
                h => session.PlayerKilledReceived -= h,
                killed => social.Chat.OnPlayerKilled(
                    killed.DeathMessage,
                    killed.VictimGuid,
                    killed.KillerGuid,
                    social.PlayerGuid?.Invoke() ?? 0u));
            Subscribe<TurbineChat.Parsed>(
                h => session.TurbineChatReceived += h,
                h => session.TurbineChatReceived -= h,
                parsed => RouteTurbineChat(social.Chat, parsed));
            Subscribe<PrivateUpdateVital.ParsedFull>(h => session.VitalUpdated += h, h => session.VitalUpdated -= h, vital =>
                character.Character.LocalPlayer.OnVitalUpdate(
                    vital.VitalId,
                    vital.Ranks,
                    vital.Start,
                    vital.Xp,
                    vital.Current));
            Subscribe<PrivateUpdateVital.ParsedCurrent>(
                h => session.VitalCurrentUpdated += h,
                h => session.VitalCurrentUpdated -= h,
                vital => character.Character.LocalPlayer.OnVitalCurrent(
                    vital.VitalId,
                    vital.Current));
            character.Character.LocalPlayer.SkillFormulaBonusResolver =
                character.ResolveSkillFormulaBonus;
            Subscribe<PrivateUpdateAttribute.Parsed>(
                h => session.AttributeUpdated += h,
                h => session.AttributeUpdated -= h,
                attr =>
                {
                    character.Character.LocalPlayer.OnAttributeUpdate(
                        attr.AttributeId,
                        attr.Ranks,
                        attr.Start,
                        attr.Xp);
                    PushMovementSkillTotals(character);
                });
            Subscribe<PrivateUpdateSkill.Parsed>(
                h => session.SkillUpdated += h,
                h => session.SkillUpdated -= h,
                skill =>
                {
                    character.Character.LocalPlayer.OnSkillWireUpdate(
                        skill.SkillId,
                        skill.Ranks,
                        skill.AdvancementClass,
                        skill.Xp,
                        skill.Init,
                        skill.Resistance,
                        skill.LastUsed);
                    // Run=24 / Jump=22 are the only movement inputs.
                    if (skill.SkillId is 22u or 24u)
                        PushMovementSkillTotals(character);
                });

            if (Interlocked.CompareExchange(ref _lifecycleState, 2, 1) != 1)
                throw new ObjectDisposedException(nameof(LiveSessionEventRouter));
        }
        catch
        {
            Interlocked.Exchange(ref _accepting, 0);
            Interlocked.Exchange(ref _lifecycleState, 3);
            throw;
        }
    }

    public bool Accepting => IsAccepting();

    public void Dispose()
    {
        Interlocked.Exchange(ref _accepting, 0);
        Interlocked.Exchange(ref _lifecycleState, 3);
        _subscriptions.Dispose();
    }

    private void Subscribe<T>(
        Action<Action<T>> attach,
        Action<Action<T>> detach,
        Action<T> sink)
    {
        Action<T> handler = value =>
        {
            if (Volatile.Read(ref _accepting) != 0)
                sink(value);
        };

        attach(handler);
        _subscriptions.Add(() => detach(handler));

        ConstructionCheckpoint();
    }

    private void SubscribeToRecompute<T>(
        Action<Action<T>> attach,
        Action<Action<T>> detach,
        Action recompute,
        Func<T, bool>? affects = null) =>
        Subscribe(
            attach,
            detach,
            (T value) =>
            {
                if (affects is null || affects(value))
                    recompute();
            });

    /// <summary>
    /// Whether removing this object can change anything the player-quality
    /// recompute reads. It reads the player's own record and the burden of
    /// what the player carries, so the player itself always counts, and so
    /// does anything a container or a wielder holds. An object that neither
    /// holds says it is out in the world, and the world is in nobody's
    /// carried set.
    ///
    /// Without this, every object the client forgets -- and it forgets a
    /// landblock's worth at a time, twenty-five seconds after the player
    /// walks away -- recomputed the player's burden, player-killer status and
    /// augmentation bonuses and pushed movement stats to everything watching.
    /// That was the largest single cost of destroying an object.
    /// </summary>
    private static bool AffectsPlayerQualities(ClientObject removed, uint playerGuid)
    {
        ArgumentNullException.ThrowIfNull(removed);
        return removed.ObjectId == playerGuid
            || removed.ContainerId != 0u
            || removed.WielderId != 0u;
    }

    private void SubscribeParameterless(
        Action<Action> attach,
        Action<Action> detach,
        Action sink)
    {
        Action handler = () =>
        {
            if (Volatile.Read(ref _accepting) != 0)
                sink();
        };

        attach(handler);
        _subscriptions.Add(() => detach(handler));

        ConstructionCheckpoint();
    }

    private static void PushMovementSkillTotals(
        LiveCharacterSessionBindings character)
    {
        (int runSkill, int jumpSkill) =
            character.Character.LocalPlayer.MovementSkillTotals();
        if (runSkill < 0 && jumpSkill < 0)
            return;
        character.Character.UpdateMovementSkillBase(runSkill, jumpSkill);
        character.OnSkillsUpdated?.Invoke(runSkill, jumpSkill);
        character.OnMovementStatsUpdated?.Invoke();
    }

    private static void RecomputeBurden(
        LiveInventorySessionBindings inventory,
        LiveCharacterSessionBindings character,
        bool notify = true)
    {
        uint player = inventory.PlayerGuid();
        ClientObject? playerObject = inventory.Objects.Get(player);
        int strength = character.Character.LocalPlayer
            .GetEffectiveAttribute(LocalPlayerState.AttributeKind.Strength) ?? 0;
        int aug = playerObject?.Properties.GetInt(
            (uint)PropertyInt.AugmentationIncreasedCarryingCapacity) ?? 0;
        int capacity = EncumbranceSystem.EncumbranceCapacity(strength, aug);
        int burden = playerObject is not null
            && playerObject.Properties.Ints.TryGetValue(
                (uint)PropertyInt.EncumbranceVal, out int wireBurden)
                ? wireBurden
                : inventory.Objects.SumCarriedBurden(player);
        float load = EncumbranceSystem.Load(capacity, burden);
        character.Character.MovementSkills.UpdateBurden(load);
        if (notify)
            character.OnMovementStatsUpdated?.Invoke();
    }

    private static void RecomputePlayerQualities(
        LiveInventorySessionBindings inventory,
        LiveCharacterSessionBindings character)
    {
        RecomputeBurden(inventory, character, notify: false);
        RecomputePvpStatus(inventory, character, notify: false);

        uint player = inventory.PlayerGuid();
        PropertyBundle properties = inventory.Objects.Get(player)?.Properties
            ?? character.Character.LocalPlayer.Properties;
        character.Character.UpdateMovementSkillAugmentations(
            PlayerSkillMath.AugmentationBonuses.FromProperties(properties));
        character.OnMovementStatsUpdated?.Invoke();
    }

    private static void RecomputePvpStatus(
        LiveInventorySessionBindings inventory,
        LiveCharacterSessionBindings character,
        bool notify = true)
    {
        uint player = inventory.PlayerGuid();
        ClientObject? playerObject = inventory.Objects.Get(player);
        uint bitfield = playerObject?.PublicWeenieBitfield ?? 0u;
        int pkStatus = playerObject?.Properties.Ints.TryGetValue(
            (uint)PropertyInt.PlayerKillerStatus, out int wirePkStatus) == true
            ? wirePkStatus
            : -1;
        float? lastPkAttackTimestamp =
            playerObject?.Properties.Floats.TryGetValue(
                (uint)PropertyFloat.LastPkAttackTimestamp, out double wireTimestamp) == true
                ? (float)wireTimestamp
                : null;
        character.Character.MovementSkills.UpdateOwnPwdBitfield(bitfield);
        character.Character.MovementSkills.UpdatePlayerKillerStatus(
            pkStatus,
            lastPkAttackTimestamp);
        if (notify)
            character.OnMovementStatsUpdated?.Invoke();
    }

    private static void RecomputeStamina(
        LocalPlayerState.VitalKind kind,
        LiveCharacterSessionBindings character)
    {
        if (kind != LocalPlayerState.VitalKind.Stamina) return;
        if (character.Character.LocalPlayer.Get(LocalPlayerState.VitalKind.Stamina)
            is not LocalPlayerState.VitalSnapshot stamina)
        {
            return;
        }

        character.Character.MovementSkills.UpdateStamina((int)stamina.Current);
        character.OnMovementStatsUpdated?.Invoke();
    }

    private void ConstructionCheckpoint() =>
        _constructionCheckpoint?.Invoke(++_constructionStep);

    private bool IsAccepting() => Volatile.Read(ref _accepting) != 0;

    private static void RouteTurbineChat(ChatLog chat, TurbineChat.Parsed parsed)
    {
        switch (parsed.Body)
        {
            case TurbineChat.Payload.EventSendToRoom message:
                chat.OnChannelBroadcast(
                    message.RoomId,
                    message.SenderName,
                    message.Message,
                    logTextType: TurbineChatDisplayNames.LogTextType(message.ChatType),
                    channelName: TurbineChatDisplayNames.Resolve(
                        message.RoomId,
                        message.ChatType));
                return;

            case TurbineChat.Payload.Response { HResult: not 0 } response:
                chat.OnSystemMessage(
                    "TurbineChat send rejected "
                        + $"(hresult=0x{unchecked((uint)response.HResult):X8}).",
                    (uint)RetailLogTextType.Default);
                return;

            default:
                // Response with HResult==0, or Unknown — nothing to surface.
                return;
        }
    }

    private static void Validate(
        LiveEntitySessionSink entities,
        LiveEnvironmentSessionSink environment,
        LiveInventorySessionBindings inventory,
        LiveCharacterSessionBindings character,
        LiveSocialSessionBindings social)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(social);
        ArgumentNullException.ThrowIfNull(entities.Spawned);
        ArgumentNullException.ThrowIfNull(entities.Deleted);
        ArgumentNullException.ThrowIfNull(entities.PickedUp);
        ArgumentNullException.ThrowIfNull(entities.MotionUpdated);
        ArgumentNullException.ThrowIfNull(entities.PositionUpdated);
        ArgumentNullException.ThrowIfNull(entities.VectorUpdated);
        ArgumentNullException.ThrowIfNull(entities.StateUpdated);
        ArgumentNullException.ThrowIfNull(entities.ParentUpdated);
        ArgumentNullException.ThrowIfNull(entities.TeleportStarted);
        ArgumentNullException.ThrowIfNull(entities.AppearanceUpdated);
        ArgumentNullException.ThrowIfNull(entities.PlayPhysicsScript);
        ArgumentNullException.ThrowIfNull(entities.PlayPhysicsScriptType);
        ArgumentNullException.ThrowIfNull(entities.SoundEvent);
        ArgumentNullException.ThrowIfNull(environment.EnvironChanged);
        ArgumentNullException.ThrowIfNull(environment.ServerTimeUpdated);
        ArgumentNullException.ThrowIfNull(inventory.Objects);
        ArgumentNullException.ThrowIfNull(inventory.PlayerGuid);
        ArgumentNullException.ThrowIfNull(character.Combat);
        ArgumentNullException.ThrowIfNull(character.Character);
        ArgumentNullException.ThrowIfNull(social.Chat);
        ArgumentNullException.ThrowIfNull(social.TurbineChat);
    }
}

internal sealed class LiveSessionSubscriptionSet : IDisposable
{
    private readonly object _gate = new();
    private readonly List<RetryableSubscription> _subscriptions = [];
    private bool _disposeRequested;

    public void Add(IDisposable subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        AddRetained(new RetryableSubscription(subscription.Dispose));
    }

    public void Add(Action unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(unsubscribe);
        AddRetained(new RetryableSubscription(unsubscribe));
    }

    private void AddRetained(RetryableSubscription retained)
    {
        bool disposeNow;
        lock (_gate)
        {
            disposeNow = _disposeRequested;
            _subscriptions.Add(retained);
        }

        if (!disposeNow)
            return;
        retained.Dispose();
        throw new ObjectDisposedException(nameof(LiveSessionSubscriptionSet));
    }

    public void Dispose()
    {
        RetryableSubscription[] subscriptions;
        lock (_gate)
        {
            _disposeRequested = true;
            subscriptions = _subscriptions.ToArray();
        }

        List<Exception>? errors = null;
        for (int index = subscriptions.Length - 1; index >= 0; index--)
        {
            try
            {
                subscriptions[index].Dispose();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        if (errors is not null)
            throw new AggregateException(
                "one or more live-session subscriptions failed to detach",
                errors);
    }

    private sealed class RetryableSubscription(Action dispose) : IDisposable
    {
        private readonly object _gate = new();
        private Action? _dispose = dispose;
        private bool _executing;
        private int _executingThreadId;

        public void Dispose()
        {
            Action? operation;
            int threadId = Environment.CurrentManagedThreadId;
            lock (_gate)
            {
                while (_executing)
                {
                    if (_executingThreadId == threadId)
                    {
                        throw new InvalidOperationException(
                            "Live-session subscription cleanup cannot complete reentrantly.");
                    }
                    Monitor.Wait(_gate);
                }

                operation = _dispose;
                if (operation is null)
                    return;
                _executing = true;
                _executingThreadId = threadId;
            }

            try
            {
                operation();
            }
            catch
            {
                CompleteAttempt(succeeded: false);
                throw;
            }

            CompleteAttempt(succeeded: true);
        }

        private void CompleteAttempt(bool succeeded)
        {
            lock (_gate)
            {
                if (succeeded)
                    _dispose = null;
                _executing = false;
                _executingThreadId = 0;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
