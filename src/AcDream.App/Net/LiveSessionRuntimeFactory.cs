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
using AcDream.Content.Skills;
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
    RuntimeItemInteraction ItemInteraction,
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
    RuntimeEntityLivenessController Liveness,
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
    private readonly Action<string> _log;

    /// <summary>Told when the server has seeded the character options.</summary>
    private readonly Action _noteOptionsSeeded;

    public LiveSessionRuntimeFactory(
        LiveSessionPlayerRuntime player,
        LiveSessionDomainRuntime domain,
        LiveSessionUiRuntime ui,
        LiveSessionInteractionRuntime interaction,
        LiveSessionWorldRuntime world,
        Action<string> log,
        Action? noteOptionsSeeded = null)
    {
        _noteOptionsSeeded = noteOptionsSeeded ?? (() => { });
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _interaction = interaction
            ?? throw new ArgumentNullException(nameof(interaction));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// What this window lends the session host's live session while it is
    /// attached: its event route, its generation reset, the client commands
    /// it carries out, and the owners the login steps reach.
    /// </summary>
    public AcDream.App.Plugins.GraphicalSessionPresentation Create()
    {
        var resetHost = new GraphicalRuntimeGenerationResetHost(
            _world.LiveEntities,
            () => _world.RenderSceneShadow?.DrainUpdateBoundary());
        LiveSessionResetPlan reset =
            LiveSessionResetManifest.Create(
                CreateResetBindings(resetHost));
        return new AcDream.App.Plugins.GraphicalSessionPresentation
        {
            CreateEvents = CreateEventRouter,
            Reset = reset.Execute,
            ClientCommands = CreateClientCommandHooks(),
            Settings = _interaction.Settings,
            PlayerModeAutoEntry = _interaction.PlayerModeAutoEntry,
            WorldState = _world.WorldState,
            Teleport = _world.Teleport,
            Vitals = _ui.Vitals,
            RetainedUi = _ui.RetailUi,
            Paperdoll = _ui.Paperdoll,
            WorldAudio = _world.WorldAudio,
            ApplyCharacterOptions = options2 =>
                AcDream.App.Plugins.GraphicalAutomationCapabilities
                    .ApplyServerCharacterOptions(
                        _interaction.Settings,
                        _domain.Character.Options,
                        options2,
                        _noteOptionsSeeded),
            PresentPlayingWorld = _world.Hydration.PresentPlayingWorld,
        };
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
                HousePurchase: _domain.Runtime.HousePurchaseOwner,
                Chess: _domain.Runtime.ChessOwner,
                Contracts: _domain.Runtime.ContractsOwner,
                PlayerGuid: () => _player.Identity.ServerGuid,
                OnLocalPlayerDeath:
                    _domain.Communication.ReportLocalPlayerDeath),
            actions: _domain.Runtime.ActionOwner);
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
            bool castReceipt = _domain.Inventory.Transactions.ItemBusyCount == 0;
            _domain.Inventory.ExternalContainers.ApplyUseDone(error);
            if (castReceipt)
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
        Vendor: _domain.Inventory.Vendor,
        Book: _domain.Runtime.BookOwner,
        PlayerName: () =>
            _domain.Inventory.Objects.Get(_player.Identity.ServerGuid)?.Name
            ?? string.Empty);

    private LiveCharacterSessionBindings CreateCharacterBindings(
        SkillTable? skillTable)
    {
        var skillCreditResolver = new LiveSkillCreditResolver(skillTable);
        return AcDream.App.Plugins.GraphicalAutomationCapabilities
            .BuildCharacterSessionBindings(
                new AcDream.App.Plugins.GraphicalCharacterSessionParts
                {
                    Character = _domain.Character,
                    Combat = _domain.Actions.Combat,
                    Settings = _interaction.Settings,
                    MovementStats = _domain.Runtime.MovementStats,
                    ResolveSkillFormulaBonus = skillCreditResolver.Resolve,
                    // Effects are stamped on the clock the plugin surface
                    // reads their time left against.
                    ClientTime = () => _domain.Runtime.EffectClock.NowSeconds,
                    RetainedUi = _ui.RetailUi,
                    NoteOptionsSeeded = _noteOptionsSeeded,
                    Warn = _log,
                });
    }

    private RuntimeClientCommandHostBindings CreateClientCommandHooks() => new()
    {
        ToggleFrameRate = _interaction.Settings.ToggleFrameRate,
        SetUiLocked = _interaction.Settings.SetUiLocked,
        ShowConfirmation = (message, completed) =>
            _ui.RetailUi?.ShowConfirmation(message, completed),
        SaveUi = name => _ui.RetailUi?.SaveNamedLayout(name),
        LoadUi = name => _ui.RetailUi?.RestoreNamedLayout(name),
        SaveAutoUi = () => _ui.RetailUi?.SaveLayout(),
        LoadAutoUi = () => _ui.RetailUi?.RestoreLayout(),
        FillComponentBuyList = (category, maximumPrice) =>
            _ui.RetailUi?.FillComponentBuyList(
                category ?? VendorComponentFill.AnyCategory,
                maximumPrice),
        SetLandscapeRadius = radius =>
            _interaction.Settings.SaveDisplay(
                _interaction.Settings.Display with
                {
                    LandscapeDrawDistance = radius,
                }),
        SetFieldOfView = degrees =>
            _interaction.Settings.SaveDisplay(
                _interaction.Settings.Display with
                {
                    FieldOfView = degrees,
                }),
    };
}
