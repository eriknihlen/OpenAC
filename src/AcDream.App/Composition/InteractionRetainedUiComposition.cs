using System.Collections.Concurrent;
using AcDream.App.Combat;
using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Net;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Settings;
using AcDream.App.Spells;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;
using AcDream.Core.Properties;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions.Input;
using AcDream.UI.Abstractions.Panels.Chat;
using AcDream.UI.Abstractions.Panels.Vitals;
using DatReaderWriter;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace AcDream.App.Composition;

internal sealed record InteractionRetainedUiDependencies(
    RuntimeOptions Options,
    GameWindowGraphics Graphics,
    Func<int, int, byte[]> BackbufferReader,
    IView Window,
    IInputContext Input,
    string ShadersDirectory,
    IDatReaderWriter Dats,
    object DatLock,
    TextureCache TextureCache,
    BitmapFont? DebugFont,
    HostQuiescenceGate HostQuiescence,
    RetainedUiInputCaptureSlot RetainedInputCapture,
    InputDispatcher? InputDispatcher,
    AcDream.App.Streaming.DeferredLocalPlayerTeleportNetworkSink TeleportSink,
    string KeyBindingsFilePath,
    RuntimeSettingsController Settings,
    BuildingDegradeController BuildingDegrades,
    GameRuntime Runtime,
    IRuntimeCombatAttackOperations CombatAttackOperations,
    RuntimeCombatTargetOperationsSlot CombatTargetOperations,
    RuntimeSpellCastOperationsSlot SpellCastOperations,
    MagicCatalog MagicCatalog,
    StackSplitQuantityState StackSplitQuantity,
    BufferedUiRegistry? UiRegistry,
    LiveCombatModeCommandSlot CombatModeCommands,
    ILocalPlayerIdentitySource PlayerIdentity,
    ILocalPlayerModeSource PlayerMode,
    Func<DeferredSelectionViewPlaneSource, SelectionCameraSource>
        SelectionCameraFactory,
    DeferredRenderFrameDiagnosticsSource FrameDiagnostics,
    VitalsVM? ExistingVitals,
    Action<string>? Toast,
    Func<double> ClientTime,
    Action<string> Log,
    AcDream.App.Rendering.Gpu.IGpuDevice GpuDevice,
    ICurrentGpuFrameSource GpuFrameSource,
    Func<AcDream.Core.World.DerethDateTime.Calendar> CurrentCalendar,
    AcDream.App.Rendering.Packs.RenderPackCatalogSource? RenderPackCatalog = null,
    Func<AcDream.App.Rendering.Packs.RenderPackDiagnosticsSnapshot>?
        RenderPackDiagnostics = null,
    string? ScreenshotsDirectory = null,
    AppAutomationSurface? Automation = null,
    Func<GameplayInputFrameController?>? GameplayInputFrame = null)
{
    public RuntimeActionState Actions => Runtime.ActionOwner;

    public RuntimeInventoryState Inventory => Runtime.InventoryOwner;

    public RuntimeCharacterState Character => Runtime.CharacterOwner;

    public RuntimeCommunicationState Communication =>
        Runtime.CommunicationOwner;

    public IRuntimeLocalPlayerControllerSource PlayerController =>
        Runtime.MovementOwner;
}

internal sealed record RetainedUiComposition(
    UiHost Host,
    RetailUiRuntime Runtime,
    VitalsVM Vitals,
    ChatVM Chat,
    CharacterSheetProvider CharacterSheet,
    FrameScreenshotController? Screenshots);

internal sealed record InteractionRetainedUiResult(
    RuntimeCombatAttackState CombatAttack,
    ExternalContainerLifecycleController ExternalContainerLifecycle,
    ItemInteractionController ItemInteraction,
    MagicRuntime Magic,
    RetainedUiComposition? RetainedUi,
    InteractionUiLateBindings LateBindings);

internal interface IGameWindowInteractionRetainedUiPublication
{
    void PublishInteractionRetainedUi(InteractionRetainedUiResult result);
}

internal enum InteractionRetainedUiCompositionPoint
{
    LateBindingsCreated,
    CombatTargetCreated,
    ExternalContainerLifecycleCreated,
    ItemInteractionCreated,
    MagicRuntimeCreated,
    RetainedUiDisabled,
    UiHostAcquired,
    InputCaptureBound,
    CursorAssetsCreated,
    CharacterSheetCreated,
    MouseInputWired,
    KeyboardInputWired,
    UiAssetsCreated,
    UiProbeCreated,
    UiRuntimeMounted,
    InventoryContainerBound,
    ResultPublished,
}

internal sealed class InteractionUiLateBindings : IDisposable
{
    private IDisposable? _inputCapture;
    private IDisposable? _inventoryContainer;
    private readonly List<(string Name, IDisposable Binding)> _lateOwnerBindings = [];
    private SelectionCameraSource? _selectionCamera;
    private bool _deactivationStarted;

    public DeferredLiveSessionUiAuthority Session { get; } = new();
    public DeferredGameRuntimeStateCommands GameRuntime { get; } = new();
    public DeferredSelectionUiAuthority Selection { get; } = new();
    public DeferredSelectionViewPlaneSource SelectionViewPlane { get; } = new();
    public DeferredRadarSnapshotSource Radar { get; } = new();
    public DeferredInventoryContainerSource InventoryContainer { get; } = new();
    public DeferredWorldLifecycleAutomationRuntime Automation { get; } = new();
    public SelectionCameraSource SelectionCamera =>
        _selectionCamera ?? throw new InvalidOperationException(
            "The retained-UI selection camera is not initialized.");

    public void InitializeSelectionCamera(SelectionCameraSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_deactivationStarted, this);
        if (_selectionCamera is not null)
            throw new InvalidOperationException("The retained selection camera is already initialized.");
        _selectionCamera = source;
    }

    public void AdoptInputCapture(IDisposable binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_deactivationStarted, this);
        if (_inputCapture is not null)
            throw new InvalidOperationException("Retained input capture is already owned.");
        _inputCapture = binding;
    }

    public void AdoptInventoryContainer(IDisposable binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_deactivationStarted, this);
        if (_inventoryContainer is not null)
            throw new InvalidOperationException("Retained inventory binding is already owned.");
        _inventoryContainer = binding;
    }

    public void AdoptLateOwnerBinding(string name, IDisposable binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_deactivationStarted, this);
        _lateOwnerBindings.Add((name, binding));
    }

    public void Dispose()
    {
        if (_deactivationStarted
            && _lateOwnerBindings.Count == 0
            && _inventoryContainer is null
            && _inputCapture is null)
            return;
        _deactivationStarted = true;

        List<Exception>? failures = null;
        Automation.Deactivate();
        Radar.Deactivate();
        SelectionViewPlane.Deactivate();
        Selection.Deactivate();
        GameRuntime.Deactivate();
        Session.Deactivate();
        InventoryContainer.Deactivate();
        for (int i = _lateOwnerBindings.Count - 1; i >= 0; i--)
        {
            (string name, IDisposable binding) = _lateOwnerBindings[i];
            try
            {
                binding.Dispose();
                _lateOwnerBindings.RemoveAt(i);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(new InvalidOperationException(
                    $"Retained UI late owner binding '{name}' did not detach.",
                    failure));
            }
        }
        Release(ref _inventoryContainer, "inventory container binding", ref failures);
        Release(ref _inputCapture, "retained input capture", ref failures);

        if (failures is not null)
            throw new AggregateException("Retained UI late binding cleanup failed.", failures);
    }

    private static void Release(
        ref IDisposable? binding,
        string name,
        ref List<Exception>? failures)
    {
        IDisposable? current = binding;
        if (current is null)
            return;
        try
        {
            current.Dispose();
            binding = null;
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(new InvalidOperationException(
                $"Retained UI {name} did not detach.",
                failure));
        }
    }
}

internal interface IInteractionRetainedUiCompositionFactory
{
    IDisposable BindCombatTarget(
        InteractionRetainedUiDependencies dependencies,
        DeferredSelectionUiAuthority selection);

    ExternalContainerLifecycleController CreateExternalContainerLifecycle(
        InteractionRetainedUiDependencies dependencies,
        DeferredLiveSessionUiAuthority session);

    ItemInteractionController CreateItemInteraction(
        InteractionRetainedUiDependencies dependencies,
        InteractionUiLateBindings lateBindings);

    MagicRuntime CreateMagicRuntime(
        InteractionRetainedUiDependencies dependencies,
        InteractionUiLateBindings lateBindings,
        ItemInteractionController itemInteraction);

    RetainedUiComposition CreateRetainedUi(
        InteractionRetainedUiDependencies dependencies,
        InteractionUiLateBindings lateBindings,
        RetailUiRuntimeLease lease,
        RuntimeCombatAttackState combatAttack,
        ItemInteractionController itemInteraction,
        MagicRuntime magic,
        Action<InteractionRetainedUiCompositionPoint> checkpoint);

    void Release(IDisposable resource);
}

internal sealed class RetailInteractionRetainedUiCompositionFactory
    : IInteractionRetainedUiCompositionFactory
{
    internal static FpsRuntimeBindings CreateFpsBindings(
        BuildingDegradeController buildingDegrades,
        Func<bool> isVisible)
    {
        ArgumentNullException.ThrowIfNull(buildingDegrades);
        ArgumentNullException.ThrowIfNull(isVisible);
        return new FpsRuntimeBindings(
            () => buildingDegrades.Fps,
            () => buildingDegrades.ActiveMultiplier,
            isVisible);
    }

    public IDisposable BindCombatTarget(
        InteractionRetainedUiDependencies d,
        DeferredSelectionUiAuthority selection) =>
        d.CombatTargetOperations.BindOwned(new LiveCombatTargetOperations(
            autoTarget: () => d.Character.Options.GetOptionBit(
                CharacterOptionId.AutoTarget),
            selectClosestTarget: () =>
                selection.SelectClosestCombatTarget(showToast: false)));

    public ExternalContainerLifecycleController CreateExternalContainerLifecycle(
        InteractionRetainedUiDependencies d,
        DeferredLiveSessionUiAuthority session) =>
        new(
            d.Inventory.ExternalContainers,
            d.Inventory.Objects,
            guid => session.CurrentSession?.SendNoLongerViewingContents(guid));

    public ItemInteractionController CreateItemInteraction(
        InteractionRetainedUiDependencies d,
        InteractionUiLateBindings late)
    {
        DeferredLiveSessionUiAuthority session = late.Session;
        DeferredSelectionUiAuthority selection = late.Selection;
        return new ItemInteractionController(
            d.Inventory.Objects,
            d.Actions.Transactions,
            d.Actions.Interaction,
            playerGuid: () => d.PlayerIdentity.ServerGuid,
            sendUse: null,
            sendExamine: guid => session.CurrentSession?.SendAppraise(guid),
            sendUseWithTarget: (source, target) =>
                session.CurrentSession?.SendUseWithTarget(source, target),
            sendWield: (item, mask) =>
                session.CurrentSession?.SendGetAndWieldItem(item, mask),
            sendDrop: item => session.CurrentSession?.SendDropItem(item),
            sendGive: (target, item, amount) =>
                session.CurrentSession?.SendGiveObject(target, item, amount),
            dragOnPlayerOpensSecureTrade: () =>
                d.Character.Options.DragItemOnPlayerOpensSecureTrade,
            toast: d.Toast,
            readyForInventoryRequest: () => session.IsInWorld,
            playerOnGround: () =>
                d.PlayerMode.IsPlayerMode
                && d.PlayerController.Controller is { IsAirborne: false },
            inNonCombatMode: () =>
                d.Actions.Combat.CurrentMode == CombatMode.NonCombat,
            combatState: d.Actions.Combat,
            sendChangeCombatMode: mode =>
                session.CurrentSession?.SendChangeCombatMode(mode),
            isComponentPack: d.MagicCatalog.IsComponentPack,
            placeInBackpack: selection.SendPickup,
            backpackContainerId: () => late.InventoryContainer.Current(
                d.PlayerIdentity.ServerGuid),
            groundObjectId: () =>
                d.Inventory.ExternalContainers.CurrentContainerId,
            activeVendorId: () => d.Inventory.Vendor.VendorId,
            sendSplitToWorld: (item, amount) =>
                session.CurrentSession?.SendStackableSplitTo3D(item, amount),
            selectedObjectId: () =>
                d.Actions.Selection.SelectedObjectId ?? 0u,
            stackSplitQuantity: d.StackSplitQuantity,
            systemMessage:
                text => d.Communication.AddText(text, RetailLogTextType.ClientLocal),
            interfaceText: (text, type) => d.Communication.AddText(text, type),
            sendPutItemInContainer: (item, container, placement) =>
                session.CurrentSession?.SendPutItemInContainer(
                    item,
                    container,
                    placement),
            sendSplitToContainer: (item, container, placement, amount) =>
                session.CurrentSession?.SendStackableSplitToContainer(
                    item,
                    container,
                    placement,
                    amount),
            sendStackableMerge: (source, target, amount) =>
                session.CurrentSession?.SendStackableMerge(source, target, amount),
            requestExternalContainer: guid =>
            {
                ClientObject? container = d.Inventory.Objects.Get(guid);
                bool isCorpse = container is not null
                    && ((PublicWeenieFlags)(container.PublicWeenieBitfield ?? 0u)
                        & PublicWeenieFlags.Corpse) != 0;
                d.Inventory.ExternalContainers.RequestOpen(guid, isCorpse);
            },
            requestUse: selection.RequestUse,
            sendBuy: (vendorGuid, itemGuid, amount, alternateCurrencyId) =>
            {
                if (session.CurrentSession is not { } activeSession || !session.IsInWorld)
                    return false;
                activeSession.SendBuy(vendorGuid, itemGuid, amount, alternateCurrencyId);
                return true;
            },
            sendBuyAll: (vendorGuid, items, alternateCurrencyId) =>
            {
                if (session.CurrentSession is not { } activeSession || !session.IsInWorld)
                    return false;
                activeSession.SendBuy(vendorGuid, items, alternateCurrencyId);
                return true;
            },
            sendSell: (vendorGuid, items) =>
            {
                if (session.CurrentSession is not { } activeSession || !session.IsInWorld)
                    return false;
                activeSession.SendSell(vendorGuid, items);
                return true;
            },
            sendSalvage: (toolGuid, itemGuids) =>
            {
                if (session.CurrentSession is not { } activeSession || !session.IsInWorld)
                    return false;
                activeSession.SendSalvage(toolGuid, itemGuids);
                return true;
            });
    }

    public MagicRuntime CreateMagicRuntime(
        InteractionRetainedUiDependencies d,
        InteractionUiLateBindings late,
        ItemInteractionController itemInteraction) =>
        MagicRuntime.Create(
            d.MagicCatalog,
            d.Actions.SpellCast,
            d.SpellCastOperations,
            d.Inventory.Objects,
            localPlayerId: () => d.PlayerIdentity.ServerGuid,
            accountName: () => late.Session.AccountName
                ?? d.Options.LiveUser
                ?? string.Empty,
            stopCompletely: d.CombatAttackOperations.PrepareAttackRequest,
            sendUntargeted: spellId =>
                late.Session.CurrentSession?.SendCastUntargetedSpell(spellId),
            sendTargeted: (target, spellId) =>
                late.Session.CurrentSession?.SendCastTargetedSpell(
                    target,
                    spellId),
            displayMessage:
                text => d.Communication.AddText(text, RetailLogTextType.ClientLocal),
            incrementBusy: itemInteraction.IncrementBusyCount,
            canSend: () => late.Session.IsInWorld);

    internal static ChatVM CreateChatViewModel(InteractionRetainedUiDependencies d) =>
        new ChatVM(
            d.Communication.Chat,
            displayLimit: 200,
            commandTargets: d.Communication.CommandTargets)
        {
            OnInterfaceText = text =>
                d.Communication.AddText(text, RetailLogTextType.ClientLocal),
        };

    public RetainedUiComposition CreateRetainedUi(
        InteractionRetainedUiDependencies d,
        InteractionUiLateBindings late,
        RetailUiRuntimeLease lease,
        RuntimeCombatAttackState combatAttack,
        ItemInteractionController itemInteraction,
        MagicRuntime magic,
        Action<InteractionRetainedUiCompositionPoint> checkpoint)
    {
        IDisposable? inputCapture = null;
        IDisposable? inventoryContainer = null;
        try
        {
            VitalsVM vitals = d.ExistingVitals
                ?? new VitalsVM(
                    d.Actions.Combat,
                    d.Character.LocalPlayer);
            UiHost host = lease.AcquireHost(
                () => new UiHost(
                    d.GpuDevice,
                    d.GpuFrameSource,
                    d.ShadersDirectory,
                    d.DebugFont,
                    d.HostQuiescence));
            checkpoint(InteractionRetainedUiCompositionPoint.UiHostAcquired);
            host.TextRenderer.LinearTwinResolver = d.TextureCache.GetOrCreateLinearUiTwin;
            inputCapture = d.RetainedInputCapture.Bind(host.Root);
            checkpoint(InteractionRetainedUiCompositionPoint.InputCaptureBound);
            host.Root.UiLocked = d.Character.Options.GetOptionBit(
                CharacterOptionId.LockUI);

            var cursorFeedback = new CursorFeedbackController(
                itemInteraction,
                worldTargetProvider: () =>
                    late.Selection.PickAtCursor(includeSelf: true) ?? 0u,
                combatModeProvider: () =>
                    d.Actions.Combat.CurrentMode);
            var cursorManager = new RetailCursorManager(d.Dats, d.DatLock);
            checkpoint(InteractionRetainedUiCompositionPoint.CursorAssetsCreated);

            var characterTitleResolver = new CharacterTitleResolver(d.Dats);
            var characterUiStrings = new DatStringResolver(d.Dats);
            var characterSheet = new CharacterSheetProvider(
                d.Inventory.Objects,
                d.Character.LocalPlayer,
                playerGuid: () => d.PlayerIdentity.ServerGuid,
                activeToonName: () => d.Settings.ActiveToonKey,
                fallbackSheet: SampleData.SampleCharacter,
                canSendRaise: () => late.GameRuntime.IsInWorld,
                sendRaiseAttribute: (statId, cost) =>
                    late.GameRuntime.Advance(
                        RuntimeAdvancementKind.Attribute,
                        statId,
                        cost),
                sendRaiseVital: (statId, cost) =>
                    late.GameRuntime.Advance(
                        RuntimeAdvancementKind.Vital,
                        statId,
                        cost),
                sendRaiseSkill: (statId, cost) =>
                    late.GameRuntime.Advance(
                        RuntimeAdvancementKind.Skill,
                        statId,
                        cost),
                sendTrainSkill: (statId, credits) =>
                    late.GameRuntime.Advance(
                        RuntimeAdvancementKind.TrainSkill,
                        statId,
                        credits),
                titles: d.Character.Titles,
                resolveDisplayTitle: titleId =>
                {
                    lock (d.DatLock) return characterTitleResolver.Resolve(titleId);
                },
                resolveUiString: key =>
                {
                    lock (d.DatLock)
                        return characterUiStrings.Resolve(0x23000001u, DatStringResolver.ComputeHash(key));
                });
            checkpoint(InteractionRetainedUiCompositionPoint.CharacterSheetCreated);

            uint MagicSkillLevel(MagicSchool school)
            {
                static uint SkillId(MagicSchool value) => value switch
                {
                    MagicSchool.CreatureEnchantment => 0x1Fu,
                    MagicSchool.ItemEnchantment => 0x20u,
                    MagicSchool.LifeMagic => 0x21u,
                    MagicSchool.WarMagic => 0x22u,
                    MagicSchool.VoidMagic => 0x2Bu,
                    _ => 0u,
                };

                uint skillId = SkillId(school);
                if (skillId != 0u)
                    return d.Character.LocalPlayer.GetSkill(skillId)?.CurrentLevel ?? 0u;

                uint highest = 0u;
                foreach (MagicSchool candidate in new[]
                {
                    MagicSchool.CreatureEnchantment,
                    MagicSchool.ItemEnchantment,
                    MagicSchool.LifeMagic,
                    MagicSchool.WarMagic,
                    MagicSchool.VoidMagic,
                })
                {
                    highest = Math.Max(
                        highest,
                        d.Character.LocalPlayer.GetSkill(
                            SkillId(candidate))?.CurrentLevel ?? 0u);
                }
                return highest;
            }

            foreach (IMouse mouse in d.Input.Mice)
                host.WireMouse(mouse);
            checkpoint(InteractionRetainedUiCompositionPoint.MouseInputWired);
            foreach (IKeyboard keyboard in d.Input.Keyboards)
                host.WireKeyboard(keyboard);
            checkpoint(InteractionRetainedUiCompositionPoint.KeyboardInputWired);

            (uint, int, int) ResolveChrome(uint id)
            {
                uint texture = d.TextureCache.GetOrUploadRenderSurface(
                    id,
                    out int width,
                    out int height);
                return (texture, width, height);
            }

            var iconComposer = new IconComposer(d.Dats, d.TextureCache);
            ControlsIni controls = d.Options.AcDir is { } acDir
                ? ControlsIni.Load(Path.Combine(acDir, "controls", "controls.ini"))
                : ControlsIni.Parse(string.Empty);
            UiDatFont? defaultFont;
            lock (d.DatLock)
                defaultFont = UiDatFont.Load(d.Dats, d.TextureCache);
            var datFontCache = new ConcurrentDictionary<uint, UiDatFont?>();
            if (defaultFont is not null)
                datFontCache.TryAdd(UiDatFont.DefaultFontId, defaultFont);
            UiDatFont? ResolveDatFont(uint fontDid) =>
                datFontCache.GetOrAdd(fontDid, id =>
                {
                    lock (d.DatLock)
                        return UiDatFont.Load(d.Dats, d.TextureCache, id);
                });
            d.Log(defaultFont is not null
                ? "[UI] vitals dat-font 0x40000000 loaded for numeric overlay."
                : "[UI] vitals dat-font 0x40000000 unavailable — falling back to debug font.");
            checkpoint(InteractionRetainedUiCompositionPoint.UiAssetsCreated);

            host.Root.Width = d.Window.Size.X;
            host.Root.Height = d.Window.Size.Y;
            var chat = CreateChatViewModel(d);
            AcDream.UI.Abstractions.Panels.Settings.SettingsStore? layoutStore =
                d.Settings.LayoutStore;
            RetailUiPersistenceBindings? persistence = layoutStore is null
                ? null
                : new RetailUiPersistenceBindings(
                    layoutStore,
                    CharacterKey: () => d.Settings.ActiveToonKey,
                    ScreenSize: () => (d.Window.Size.X, d.Window.Size.Y));
            void ProbeLog(string message) => d.Log("[UI-PROBE] " + message);
            string screenshotDirectory =
                d.Options.UiProbeEnabled
                && d.Options.AutomationArtifactDirectory is { } artifactDirectory
                    ? Path.Combine(artifactDirectory, "screenshots")
                    : !string.IsNullOrWhiteSpace(d.ScreenshotsDirectory)
                        ? d.ScreenshotsDirectory
                        : Path.Combine(
                            Path.GetDirectoryName(d.KeyBindingsFilePath)!,
                            "screenshots");
            var screenshots = new FrameScreenshotController(
                d.BackbufferReader,
                screenshotDirectory,
                ProbeLog,
                d.RenderPackDiagnostics);
            checkpoint(InteractionRetainedUiCompositionPoint.UiProbeCreated);

            var assets = new RetailUiAssets(
                d.Dats,
                d.DatLock,
                ResolveChrome,
                ResolveDatFont,
                defaultFont,
                d.DebugFont,
                controls,
                iconComposer,
                d.TextureCache);
            var characterCreationStrings = new DatStringResolver(d.Dats);
            var chargenSkillScoreResolver = new ChargenSkillScoreResolver(
                d.Runtime.CharacterCreation.Options);
            AcDream.Core.Quests.ContractCatalog? contractCatalog = null;
            AcDream.Core.Quests.ContractCatalog questCatalog()
            {
                if (contractCatalog is not null)
                    return contractCatalog;
                lock (d.DatLock)
                    contractCatalog = AcDream.Content.ContractTableReader.Load(d.Dats);
                return contractCatalog;
            }

            var bindings = new RetailUiRuntimeBindings(
                Host: host,
                Assets: assets,
                Vitals: new VitalsRuntimeBindings(vitals),
                Chat: new ChatRuntimeBindings(
                    chat,
                    () => late.Session.Commands,
                    d.Communication.ChatWindows,
                    layoutStore),
                Radar: new RadarRuntimeBindings(
                    late.Radar.Snapshot,
                    d.Actions.Selection,
                    d.Settings.RequestUiLocked),
                Combat: new CombatRuntimeBindings(
                    d.Actions.Combat,
                    combatAttack),
                Magic: new MagicRuntimeBindings(
                    d.Character.Spellbook,
                    magic.Casting,
                    d.Inventory.Objects,
                    () => d.PlayerIdentity.ServerGuid,
                    d.MagicCatalog.Components,
                    iconComposer.GetIcon,
                    iconComposer.GetDragIcon,
                    iconComposer.GetSpellIcon,
                    iconComposer.GetSpellComponentIcon,
                    d.Actions.Selection,
                    d.MagicCatalog.GetSpellLevel,
                    magic.GetExamineComponents,
                    MagicSkillLevel,
                    guid => d.Actions.Selection.Select(
                        guid,
                        SelectionChangeSource.Inventory),
                    guid => late.Session.TryUseItem(guid, d.Log),
                    (tab, position, spellId) =>
                        late.GameRuntime.AddFavorite(tab, position, spellId),
                    (tab, spellId) =>
                        late.GameRuntime.RemoveFavorite(tab, spellId),
                    filters => late.GameRuntime.SetSpellbookFilter(filters),
                    spellId => late.GameRuntime.ForgetSpell(spellId),
                    (componentId, amount) =>
                        late.GameRuntime.SetDesiredComponent(
                            componentId,
                            amount),
                    d.ClientTime),
                JumpPowerbar: new JumpPowerbarRuntimeBindings(
                    () => d.PlayerController.Controller?.JumpCharge ?? default),
                Fps: CreateFpsBindings(
                    d.BuildingDegrades,
                    () => d.Settings.DisplayPreview.ShowFps),
                VividTarget: new VividTargetRuntimeBindings(
                    d.Actions.Selection,
                    () => d.PlayerIdentity.ServerGuid,
                    () => d.Character.Options.GetOptionBit(
                        CharacterOptionId.VividTargetingIndicator),
                    late.Selection.ResolveVividTargetInfo,
                    late.SelectionCamera.UiSnapshot),
                Indicators: new IndicatorRuntimeBindings(
                    d.Character.Spellbook,
                    d.Inventory.Objects,
                    () => d.PlayerIdentity.ServerGuid,
                    () => d.Character.LocalPlayer.GetEffectiveAttribute(
                        LocalPlayerState.AttributeKind.Strength),
                    () => late.Session.LinkStatus,
                    d.ClientTime,
                    () => late.Session.CurrentSession?.RequestLinkStatusPing(),
                    EndCharacterSession: d.TeleportSink.RequestLogout,
                    ExitGame: d.Window.Close),
                Toolbar: new ToolbarRuntimeBindings(
                    d.Inventory.Objects,
                    d.Inventory.Shortcuts,
                    iconComposer.GetIcon,
                    iconComposer.GetDragIcon,
                    guid => late.Session.TryUseItem(guid, d.Log),
                    d.Actions.Combat,
                    d.Inventory.ItemMana,
                    d.CombatModeCommands.Toggle,
                    itemInteraction,
                    entry => late.GameRuntime.AddShortcut(entry),
                    index => late.GameRuntime.RemoveShortcut(index),
                    d.Actions.Selection,
                    handler => d.Actions.Combat.HealthChanged += handler,
                    handler => d.Actions.Combat.HealthChanged -= handler,
                    late.Selection.ShouldShowHealth,
                    guid => d.Inventory.Objects.Get(guid)?.GetAppropriateName(),
                    d.Actions.Combat.GetHealthPercent,
                    d.Actions.Combat.HasHealth,
                    guid =>
                        (uint)(d.Inventory.Objects.Get(guid)?.StackSize ?? 0),
                    guid => late.Session.CurrentSession?.SendQueryHealth(guid),
                    guid => late.Session.CurrentSession?.SendQueryItemMana(guid),
                    () => d.PlayerIdentity.ServerGuid,
                    (item, container, placement) =>
                        late.Session.CurrentSession?.SendPutItemInContainer(
                            item,
                            container,
                            placement),
                    guid =>
                        d.Inventory.Vendor.VendorId != 0u
                        && d.Inventory.Objects.Get(guid) is { } vendorCandidate
                        && vendorCandidate.ContainerId == d.Inventory.Vendor.VendorId
                        && VendorSplitPolicy.IsSplitExempt(vendorCandidate.Type)),
                Character: new CharacterRuntimeBindings(
                    characterSheet,
                    d.Character.Titles,
                    characterTitleResolver,
                    SendSetTitle: titleId => late.GameRuntime.SetTitle(titleId)),
                Inventory: new InventoryRuntimeBindings(
                    d.Inventory.Objects,
                    () => d.PlayerIdentity.ServerGuid,
                    iconComposer.GetIcon,
                    iconComposer.GetDragIcon,
                    () => d.Character.LocalPlayer.GetEffectiveAttribute(
                        LocalPlayerState.AttributeKind.Strength),
                    d.Character.Spellbook,
                    guid => late.Session.CurrentSession?.SendUse(guid),
                    (item, container, placement) =>
                        late.Session.CurrentSession?.SendPutItemInContainer(
                            item,
                            container,
                            placement),
                    (item, container, placement, amount) =>
                        late.Session.CurrentSession?.SendStackableSplitToContainer(
                            item,
                            container,
                            placement,
                            amount),
                    (source, target, amount) =>
                        late.Session.CurrentSession?.SendStackableMerge(
                            source,
                            target,
                            amount),
                    itemInteraction,
                    d.Actions.Selection),
                ExternalContainer: new ExternalContainerRuntimeBindings(
                    d.Inventory.ExternalContainers,
                    d.Inventory.Objects,
                    iconComposer.GetIcon,
                    iconComposer.GetDragIcon,
                    itemInteraction,
                    d.Actions.Selection,
                    guid => late.Session.CurrentSession?.SendUse(guid),
                    (item, container, placement) =>
                        late.Session.CurrentSession?.SendPutItemInContainer(
                            item,
                            container,
                            placement),
                    (item, container, placement, amount) =>
                        late.Session.CurrentSession?.SendStackableSplitToContainer(
                            item,
                            container,
                            placement,
                            amount),
                    late.Selection.IsWithinExternalContainerUseRange),
                Vendor: new VendorRuntimeBindings(
                    d.Inventory.Vendor,
                    iconComposer.GetIcon,
                    itemInteraction,
                    d.Actions.Selection,
                    text => d.Communication.AddText(text, RetailLogTextType.ClientLocal)),
                Cursor: new RetailUiCursorBindings(cursorFeedback, cursorManager),
                WorldTooltip: new WorldTooltipRuntimeBindings(
                    HoverGuidAtCursor: () => late.Selection.PickAtCursor(includeSelf: true),
                    ResolveName: guid => d.Inventory.Objects.Get(guid)?.GetAppropriateName(),
                    Enabled: () => d.Character.Options.GetOptionBit(
                        CharacterOptionId.ShowTooltips)),
                Confirmations: new ConfirmationRuntimeBindings(
                    (type, context, accepted) =>
                        late.Session.CurrentSession?.SendConfirmationResponse(
                            type,
                            context,
                            accepted)),
                Appraisal: new AppraisalRuntimeBindings(
                    characterSheet.CharacterName,
                    (item, inscription) =>
                        late.Session.CurrentSession?.SendSetInscription(
                            item,
                            inscription),
                    text =>
                        d.Communication.AddText(text, RetailLogTextType.ClientLocal),
                    LocalFactionBits: () =>
                        d.Character.LocalPlayer.Properties.GetInt(
                            (uint)PropertyInt.Faction1Bits)),
                Options: new OptionsRuntimeBindings(
                    CommandBus: () => late.Session.Commands,
                    IsGrounded: () =>
                        d.PlayerMode.IsPlayerMode
                        && d.PlayerController.Controller is { } liveController
                            ? !liveController.IsAirborne
                            : (bool?)null,
                    IsUseMouseTurningEnabled: () =>
                        CharacterOptionTable.TryGet(
                            CharacterOptionId.UseMouseTurning,
                            out CharacterOptionTableEntry entry)
                        && (d.Character.Options.Options2 & entry.Mask) != 0u,
                    DisplaySystemMessage: text =>
                        d.Communication.AddText(text, RetailLogTextType.ClientLocal),
                    DisplayMouseTurningMacroLine: text =>
                        d.Communication.AddText(text, RetailLogTextType.Magic),
                    LoadCameraTurning: d.Settings.LoadCameraTurning,
                    SaveCameraTurning: d.Settings.SaveCameraTurning,
                    CurrentCharacterOption: id => d.Character.Options.GetOptionBit(id),
                    LoadDisplay: () => d.Settings.Display,
                    SaveDisplay: d.Settings.SaveDisplay,
                    LoadAudio: () => d.Settings.Audio,
                    SaveAudio: d.Settings.SaveAudio,
                    LoadRenderPackChoices: d.RenderPackCatalog is null
                        ? null
                        : () => d.RenderPackCatalog.Snapshot().Entries
                            .Select(entry =>
                                new ConfigOptionsPageController.RenderPackChoice(
                                    entry.Descriptor.Id,
                                    entry.Descriptor.DisplayName,
                                    entry.Descriptor.PackVersion.ToString(),
                                    entry.IsCompatible,
                                    entry.IncompatibilityReason,
                                    entry.Descriptor.QualityPresets
                                        .Select(preset =>
                                        {
                                            entry.PresetIncompatibilityReasons.TryGetValue(
                                                preset.Id,
                                                out string? reason);
                                            return new ConfigOptionsPageController.RenderPackPresetChoice(
                                                preset.Id,
                                                preset.DisplayName,
                                                entry.IsCompatible && reason is null,
                                                reason ?? entry.IncompatibilityReason)
                                            {
                                                SettingOverrides = preset.SettingOverrides,
                                                MaxResidentGpuBytes = preset.MaxResidentGpuBytes,
                                                MaxIncrementalGpuMillisecondsP50 =
                                                    preset.MaxIncrementalGpuMillisecondsP50,
                                                MaxIncrementalGpuMillisecondsP99 =
                                                    preset.MaxIncrementalGpuMillisecondsP99,
                                                MaxIncrementalCpuMillisecondsP50 =
                                                    preset.MaxIncrementalCpuMillisecondsP50,
                                                MaxIncrementalCpuMillisecondsP99 =
                                                    preset.MaxIncrementalCpuMillisecondsP99,
                                            };
                                        })
                                        .ToArray())
                                {
                                    FeatureSummary = entry.Descriptor.FeatureSummary,
                                    Settings = entry.Descriptor.Settings,
                                })
                            .ToArray(),
                    LoadRenderPackCatalogRevision: d.RenderPackCatalog is null
                        ? null
                        : () => d.RenderPackCatalog.Revision,
                    LoadRenderPackFailureNotice: d.RenderPackDiagnostics is null
                        ? null
                        : () => d.RenderPackDiagnostics().FailureReason),
                Social: new SocialRuntimeBindings(
                    () => d.Runtime.Fellowship.Snapshot,
                    () => d.Runtime.Allegiance.Snapshot,
                    d.Communication.Friends,
                    d.Communication.Squelch,
                    () => d.Runtime.Fellowship.GetMembers(),
                    (name, shareXp) => late.GameRuntime.FellowshipCreate(name, shareXp),
                    guid => late.GameRuntime.FellowshipRecruit(guid),
                    guid => late.GameRuntime.FellowshipDismiss(guid),
                    disband => late.GameRuntime.FellowshipQuit(disband),
                    guid => late.GameRuntime.FellowshipAssignLeader(guid),
                    isOpen => late.GameRuntime.FellowshipSetOpen(isOpen),
                    panelOpen => late.GameRuntime.FellowshipSetPanelOpen(panelOpen),
                    d.Actions.Selection,
                    () => d.PlayerIdentity.ServerGuid,
                    AllegianceMonarch: () =>
                        d.Runtime.Allegiance.TryGetMonarch(out var monarch)
                            ? monarch
                            : (RuntimeAllegianceMemberSnapshot?)null,
                    AllegiancePatron: guid =>
                        d.Runtime.Allegiance.TryGetPatron(guid, out var patron)
                            ? patron
                            : (RuntimeAllegianceMemberSnapshot?)null,
                    AllegianceMember: guid =>
                        d.Runtime.Allegiance.TryGetMember(guid, out var member)
                            ? member
                            : (RuntimeAllegianceMemberSnapshot?)null,
                    AllegianceVassals: guid => d.Runtime.Allegiance.GetVassals(guid),
                    AllegianceSwear: guid => late.GameRuntime.AllegianceSwear(guid),
                    AllegianceBreak: guid => late.GameRuntime.AllegianceBreak(guid),
                    AllegianceKick: guid => late.GameRuntime.AllegianceKick(guid),
                    AllegianceSetUpdateSubscription: on =>
                        late.GameRuntime.AllegianceSetUpdateSubscription(on),
                    Trade: d.Runtime.Trade),
                MapHouse: new MapHouseRuntimeBindings(
                    CurrentCalendar: d.CurrentCalendar,
                    PlayerCellId: () => d.PlayerController.Controller?.CellId ?? 0u,
                    HousePosition: () => d.Runtime.HouseOwner.Position,
                    HouseLines: () => d.Runtime.HouseOwner.Lines,
                    HousePanelLines: () => d.Runtime.HouseOwner.PanelLines),
                Quests: new QuestRuntimeBindings(
                    Contracts: d.Runtime.ContractsOwner.View,
                    Catalog: questCatalog,
                    Journal: d.Runtime.JournalOwner.View,
                    JournalCommands: d.Runtime.JournalOwner,
                    PlayerCell: () => d.PlayerController.Controller?.CellId ?? 0u,
                    AbandonContract: contractId =>
                        late.Session.CurrentSession?.SendAbandonContract(contractId),
                    JournalDirectory: System.IO.Path.Combine(
                        AcDream.Platform.ApplicationPathSet.Resolve().DataDirectory,
                        "journal"),
                    Report: message =>
                        d.Communication.Chat.OnSystemMessage(message, 0x0Fu)),
                StackSplitQuantity: d.StackSplitQuantity,
                Plugins: d.UiRegistry,
                Persistence: persistence,
                Probe: new RetailUiProbeBindings(
                    d.Options.UiProbeEnabled,
                    d.Options.UiProbeScript,
                    d.Options.UiProbeDump,
                    ProbeLog,
                    action => d.InputDispatcher?.TryInvokeAutomationAction(action) == true,
                    (action, held) =>
                        d.InputDispatcher?.TrySetAutomationActionHeld(action, held) == true,
                    late.Automation,
                    QueueMouseLookDelta: (dx, dy) =>
                        d.GameplayInputFrame?.Invoke()?.QueueRawMouseDelta(dx, dy)),
                Keyboard: new KeyboardRuntimeBindings(
                    d.InputDispatcher,
                    d.KeyBindingsFilePath),
                CharacterSelection: new CharacterSelectionRuntimeBindings(
                    () => late.GameRuntime.CharacterSelection,
                    late.GameRuntime.CharacterSelectionHighlight,
                    late.GameRuntime.CharacterSelectionEnter,
                    late.GameRuntime.CharacterSelectionRequestDelete,
                    late.GameRuntime.CharacterSelectionConfirmDelete,
                    late.GameRuntime.CharacterSelectionRestore,
                    late.GameRuntime.CharacterSelectionCancel,
                    d.Window.Close,
                    DirectCharacterLaunch: d.Options.LiveCharacterSelector is not null),
                CharacterCreation: new CharacterCreationRuntimeBindings(
                        () => late.GameRuntime.CharacterCreation,
                        late.GameRuntime.CharacterCreationSelectHeritage,
                        late.GameRuntime.CharacterCreationSelectGender,
                        late.GameRuntime.CharacterCreationSelectTemplate,
                        late.GameRuntime.CharacterCreationSetAttribute,
                        late.GameRuntime.CharacterCreationSetAttributeLock,
                        late.GameRuntime.CharacterCreationTrainSkill,
                        late.GameRuntime.CharacterCreationSpecializeSkill,
                        late.GameRuntime.CharacterCreationUntrainSkill,
                        late.GameRuntime.CharacterCreationSelectStartArea,
                        late.GameRuntime.CharacterCreationFinish,
                        RequestExit: () => { },
                        SetAppearanceIndex: late.GameRuntime.CharacterCreationSetAppearanceIndex,
                        SetShade: late.GameRuntime.CharacterCreationSetShade,
                        ResolveText: key =>
                        {
                            lock (d.DatLock)
                            {
                                return characterCreationStrings.Resolve(
                                    0x23000002u,
                                    DatStringResolver.ComputeHash(key));
                            }
                        },
                        SetName: late.GameRuntime.CharacterCreationSetName,
                        AcknowledgeRejection: late.GameRuntime.CharacterCreationAcknowledgeRejection,
                        RandomizeCharacter: late.GameRuntime.CharacterCreationRandomizeCharacter,
                        RandomizeAppearance: late.GameRuntime.CharacterCreationRandomizeAppearance,
                        RandomizeClothing: late.GameRuntime.CharacterCreationRandomizeClothing,
                        GetSkillScore: chargenSkillScoreResolver.Resolve,
                    OpenOnStart: d.Options.OpenCharacterCreationOnStart),
                CaptureScreenshot: () =>
                {
                    if (screenshots.TryRequestRetailScreenshot(
                            out string path,
                            out string error))
                    {
                        d.Communication.AddText(
                            $"Screenshot saved to {path}",
                            RetailLogTextType.ClientLocal);
                    }
                    else
                    {
                        d.Communication.AddText(
                            $"Screenshot failed: {error}",
                            RetailLogTextType.ClientLocal);
                    }
                },
                ProjectileDebugSamples: d.Automation is null
                    ? null
                    : d.Automation.CaptureProjectileDebugSamples,
                Connection: new ConnectionRuntimeBindings(
                    () => late.GameRuntime.Connection, d.Window.Close,
                    ShowProgress: d.Options.LiveCharacterSelector is null),
                IsGameplayDisplay: () => d.Settings.IsGameplayDisplay,
                SynchronizeDisplayPhase: () =>
                {
                    if (late.GameRuntime.Connection?.Snapshot.Status is
                            AcDream.Runtime.Session.RuntimeConnectionStatus.Failed or
                            AcDream.Runtime.Session.RuntimeConnectionStatus.Unsupported
                        || late.GameRuntime.CharacterSelection?.Snapshot.Error is not null)
                        d.Settings.EndDirectLaunch();
                    d.Settings.SetGameplayDisplay(
                        !d.Options.LiveMode || late.GameRuntime.CharacterSelection?.Snapshot.Lifecycle ==
                            AcDream.Runtime.Session.RuntimeCharacterSelectionLifecycle.InWorld);
                });
            RetailUiRuntime runtime = lease.Mount(
                () => RetailUiRuntime.CreateUninitialized(bindings));
            checkpoint(InteractionRetainedUiCompositionPoint.UiRuntimeMounted);
            d.Settings.ServerOptionsSeeded = () =>
            {
                runtime.OptionsPanelController?.OnServerOptionsSeeded();
                runtime.CombatUiController?.OnServerOptionsSeeded();
            };
            inventoryContainer = late.InventoryContainer.Bind(runtime);
            checkpoint(InteractionRetainedUiCompositionPoint.InventoryContainerBound);

            late.AdoptInputCapture(inputCapture);
            inputCapture = null;
            late.AdoptInventoryContainer(inventoryContainer);
            inventoryContainer = null;
            return new RetainedUiComposition(
                host,
                runtime,
                vitals,
                chat,
                characterSheet,
                screenshots);
        }
        catch (Exception failure)
        {
            List<Exception>? cleanup = null;
            TryRelease(ref inventoryContainer, "inventory container", ref cleanup);
            TryRelease(ref inputCapture, "input capture", ref cleanup);
            if (cleanup is not null)
            {
                cleanup.Insert(0, failure);
                throw new AggregateException(
                    "Retained UI construction and local binding rollback failed.",
                    cleanup);
            }
            throw;
        }
    }

    public void Release(IDisposable resource) => resource.Dispose();

    private static void TryRelease(
        ref IDisposable? resource,
        string name,
        ref List<Exception>? failures)
    {
        if (resource is null)
            return;
        try
        {
            resource.Dispose();
            resource = null;
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(new InvalidOperationException(
                $"Retained UI {name} rollback failed.",
                failure));
        }
    }
}

internal sealed class InteractionRetainedUiCompositionPhase
    : IInteractionUiCompositionPhase<
        GameWindowPlatformResult<GameWindowGraphics, IInputContext>,
        HostInputCameraResult,
        ContentEffectsAudioResult,
        SettingsDevToolsResult,
        WorldRenderResult,
        InteractionRetainedUiResult>
{
    private readonly InteractionRetainedUiDependencies _dependencies;
    private readonly RetailUiRuntimeLease _retainedUiLease;
    private readonly IGameWindowInteractionRetainedUiPublication _publication;
    private readonly IInteractionRetainedUiCompositionFactory _factory;
    private readonly Action<InteractionRetainedUiCompositionPoint>? _faultInjection;

    public InteractionRetainedUiCompositionPhase(
        InteractionRetainedUiDependencies dependencies,
        RetailUiRuntimeLease retainedUiLease,
        IGameWindowInteractionRetainedUiPublication publication,
        IInteractionRetainedUiCompositionFactory? factory = null,
        Action<InteractionRetainedUiCompositionPoint>? faultInjection = null)
    {
        _dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));
        _retainedUiLease = retainedUiLease
            ?? throw new ArgumentNullException(nameof(retainedUiLease));
        _publication = publication
            ?? throw new ArgumentNullException(nameof(publication));
        _factory = factory ?? new RetailInteractionRetainedUiCompositionFactory();
        _faultInjection = faultInjection;
    }

    public InteractionRetainedUiResult Compose()
    {
        var scope = new CompositionAcquisitionScope();
        try
        {
            var lateLease = scope.Acquire(
                "retained UI late bindings",
                static () => new InteractionUiLateBindings(),
                _factory.Release);
            InteractionUiLateBindings late = lateLease.Resource;
            late.InitializeSelectionCamera(
                _dependencies.SelectionCameraFactory(late.SelectionViewPlane));
            Fault(InteractionRetainedUiCompositionPoint.LateBindingsCreated);

            IDisposable combatTargetBinding = _factory.BindCombatTarget(
                _dependencies,
                late.Selection);
            late.AdoptLateOwnerBinding(
                "combat-target operations",
                combatTargetBinding);
            Fault(InteractionRetainedUiCompositionPoint.CombatTargetCreated);
            var externalLease = scope.Acquire(
                "external container lifecycle",
                () => _factory.CreateExternalContainerLifecycle(
                    _dependencies,
                    late.Session),
                _factory.Release);
            Fault(InteractionRetainedUiCompositionPoint.ExternalContainerLifecycleCreated);
            var itemLease = scope.Acquire(
                "item interaction controller",
                () => _factory.CreateItemInteraction(_dependencies, late),
                _factory.Release);
            Fault(InteractionRetainedUiCompositionPoint.ItemInteractionCreated);
            var magicLease = scope.Acquire(
                "magic runtime",
                () => _factory.CreateMagicRuntime(
                    _dependencies,
                    late,
                    itemLease.Resource),
                _factory.Release);
            Fault(InteractionRetainedUiCompositionPoint.MagicRuntimeCreated);

            var uiLease = scope.Own(
                "retained UI runtime lease",
                _retainedUiLease,
                _factory.Release);
            RetainedUiComposition? retainedUi = null;
            if (_dependencies.Options.RetailUi)
            {
                retainedUi = _factory.CreateRetainedUi(
                    _dependencies,
                    late,
                    _retainedUiLease,
                    _dependencies.Actions.CombatAttack,
                    itemLease.Resource,
                    magicLease.Resource,
                    Fault);
            }
            else
            {
                Fault(InteractionRetainedUiCompositionPoint.RetainedUiDisabled);
            }

            var result = new InteractionRetainedUiResult(
                _dependencies.Actions.CombatAttack,
                externalLease.Resource,
                itemLease.Resource,
                magicLease.Resource,
                retainedUi,
                late);
            _publication.PublishInteractionRetainedUi(result);
            lateLease.Transfer();
            externalLease.Transfer();
            itemLease.Transfer();
            magicLease.Transfer();
            uiLease.Transfer();
            Fault(InteractionRetainedUiCompositionPoint.ResultPublished);
            scope.Complete();
            return result;
        }
        catch (Exception failure)
        {
            scope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    public InteractionRetainedUiResult Compose(
        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform,
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        SettingsDevToolsResult settings,
        WorldRenderResult world)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(world);
        if (!ReferenceEquals(_dependencies.Graphics, platform.Graphics)
            || !ReferenceEquals(_dependencies.Input, platform.Input)
            || !ReferenceEquals(_dependencies.InputDispatcher, host.InputDispatcher)
            || !ReferenceEquals(_dependencies.Dats, content.Dats)
            || !ReferenceEquals(_dependencies.MagicCatalog, content.MagicCatalog)
            || !ReferenceEquals(_dependencies.TextureCache, world.Foundation.TextureCache)
            || !ReferenceEquals(_dependencies.DebugFont, world.Foundation.DebugFont))
        {
            throw new InvalidOperationException(
                "Interaction/UI dependencies do not match the ordered phase results.");
        }

        return Compose();
    }

    private void Fault(InteractionRetainedUiCompositionPoint point) =>
        _faultInjection?.Invoke(point);
}
