using System.Collections.Generic;
using AcDream.App.Plugins;
using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.Spells;
using AcDream.App.UI.Layout;
using AcDream.App.UI.Testing;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Properties;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.Content;
using AcDream.Core.Input;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;
using AcDream.UI.Abstractions.Panels.Settings;
using AcDream.UI.Abstractions.Panels.Vitals;
using AcDream.UI.Abstractions.Input;
using AcDream.Plugin.Abstractions;
using DatReaderWriter;
using Silk.NET.Input;

namespace AcDream.App.UI;

public sealed record RetailUiAssets(
    IDatReaderWriter Dats,
    object DatLock,
    Func<uint, (uint Texture, int Width, int Height)> ResolveSprite,
    Func<uint, UiDatFont?> ResolveFont,
    UiDatFont? DefaultFont,
    BitmapFont? DebugFont,
    ControlsIni Controls,
    IconComposer Icons,
    TextureCache TextureCache);

public sealed record VitalsRuntimeBindings(VitalsVM ViewModel);

public sealed record ChatRuntimeBindings(
    ChatVM ViewModel,
    Func<ICommandBus> CommandBus,
    ChatWindowState Windows,
    SettingsStore? Store = null);

public sealed record RadarRuntimeBindings(
    Func<UiRadarSnapshot> Snapshot,
    SelectionState Selection,
    Action<bool> SetUiLocked);

public sealed record CombatRuntimeBindings(
    CombatState State,
    RuntimeCombatAttackState Attacks);

public sealed record MagicRuntimeBindings(
    Spellbook Spellbook,
    RuntimeSpellCastState Casting,
    ClientObjectTable Objects,
    Func<uint> PlayerGuid,
    IReadOnlyDictionary<uint, SpellComponentDescriptor> Components,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveIcon,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveDragIcon,
    Func<uint, uint> ResolveSpellIcon,
    Func<uint, uint> ResolveComponentIcon,
    SelectionState Selection,
    Func<uint, int> SpellLevel,
    Func<uint, IReadOnlyList<SpellExamineComponent>> SpellComponents,
    Func<MagicSchool, uint> MagicSkill,
    Action<uint> SelectObject,
    Action<uint> UseItem,
    Action<int, int, uint> AddFavorite,
    Action<int, uint> RemoveFavorite,
    Action<uint> SendSpellbookFilter,
    Action<uint> RemoveSpell,
    Action<uint, uint> SetDesiredComponent,
    Func<double> ServerTime);

public sealed record JumpPowerbarRuntimeBindings(Func<JumpChargeSnapshot> Snapshot);

public sealed record FpsRuntimeBindings(
    Func<double> FramesPerSecond,
    Func<double> DegradeMultiplier,
    Func<bool> IsVisible);

public sealed record IndicatorRuntimeBindings(
    Spellbook Spellbook,
    ClientObjectTable Objects,
    Func<uint> PlayerGuid,
    Func<int?> Strength,
    Func<LinkStatusSnapshot> LinkStatus,
    Func<double> CurrentTime,
    Action RequestLinkStatusPing,
    Action EndCharacterSession,
    Action ExitGame);

public sealed record ToolbarRuntimeBindings(
    ClientObjectTable Objects,
    ShortcutStore Shortcuts,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveIcon,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveDragIcon,
    Action<uint> UseItem,
    CombatState Combat,
    ItemManaState ItemMana,
    Action ToggleCombat,
    ItemInteractionController ItemInteraction,
    Action<ShortcutEntry>? SendAddShortcut,
    Action<uint>? SendRemoveShortcut,
    SelectionState Selection,
    Action<Action<uint, float>> SubscribeHealthChanged,
    Action<Action<uint, float>> UnsubscribeHealthChanged,
    Func<uint, bool> IsHealthTarget,
    Func<uint, string?> ResolveName,
    Func<uint, float> HealthPercent,
    Func<uint, bool> HasHealth,
    Func<uint, uint> StackSize,
    Action<uint> SendQueryHealth,
    Action<uint> SendQueryItemMana,
    Func<uint> PlayerGuid,
    Action<uint, uint, int>? SendPutItemInContainer,
    Func<uint, bool> IsVendorSplitExempt);

public sealed record CharacterRuntimeBindings(
    CharacterSheetProvider Provider,
    RuntimeCharacterTitleState Titles,
    CharacterTitleResolver TitleResolver,
    Func<uint, RuntimeCommandResult> SendSetTitle);

public sealed record OptionsRuntimeBindings(
    Func<ICommandBus> CommandBus,
    Func<bool?> IsGrounded,
    Func<bool> IsUseMouseTurningEnabled,
    Action<string> DisplaySystemMessage,
    Action<string> DisplayMouseTurningMacroLine,
    Func<CameraTurningSettings> LoadCameraTurning,
    Action<CameraTurningSettings> SaveCameraTurning,
    Func<uint, bool> CurrentCharacterOption,
    Func<DisplaySettings> LoadDisplay,
    Action<DisplaySettings> SaveDisplay,
    Func<AudioSettings> LoadAudio,
    Action<AudioSettings> SaveAudio,
    Func<IReadOnlyList<Layout.ConfigOptionsPageController.RenderPackChoice>>?
        LoadRenderPackChoices = null,
    Func<long>? LoadRenderPackCatalogRevision = null,
    Func<string?>? LoadRenderPackFailureNotice = null);

public sealed record SocialRuntimeBindings(
    Func<AcDream.Runtime.RuntimeFellowshipSnapshot> FellowshipSnapshot,
    Func<AcDream.Runtime.RuntimeAllegianceSnapshot> AllegianceSnapshot,
    AcDream.Core.Social.FriendsState Friends,
    AcDream.Core.Social.SquelchState Squelch,
    Func<IEnumerable<AcDream.Runtime.RuntimeFellowMemberSnapshot>> FellowshipMembers,
    Func<string, bool, RuntimeCommandResult> FellowshipCreate,
    Func<uint, RuntimeCommandResult> FellowshipRecruit,
    Func<uint, RuntimeCommandResult> FellowshipDismiss,
    Func<bool, RuntimeCommandResult> FellowshipQuit,
    Func<uint, RuntimeCommandResult> FellowshipAssignLeader,
    Func<bool, RuntimeCommandResult> FellowshipSetOpen,
    Func<bool, RuntimeCommandResult> FellowshipSetPanelOpen,
    SelectionState Selection,
    Func<uint> LocalPlayerGuid,
    Func<AcDream.Runtime.RuntimeAllegianceMemberSnapshot?> AllegianceMonarch,
    Func<uint, AcDream.Runtime.RuntimeAllegianceMemberSnapshot?> AllegiancePatron,
    Func<uint, AcDream.Runtime.RuntimeAllegianceMemberSnapshot?> AllegianceMember,
    Func<uint, IEnumerable<AcDream.Runtime.RuntimeAllegianceMemberSnapshot>> AllegianceVassals,
    Func<uint, RuntimeCommandResult> AllegianceSwear,
    Func<uint, RuntimeCommandResult> AllegianceBreak,
    Func<uint, RuntimeCommandResult> AllegianceKick,
    Func<bool, RuntimeCommandResult> AllegianceSetUpdateSubscription,
    AcDream.Runtime.Gameplay.IRuntimeTradeView? Trade = null);

public sealed record MapHouseRuntimeBindings(
    Func<AcDream.Core.World.DerethDateTime.Calendar> CurrentCalendar,
    Func<uint> PlayerCellId,
    Func<CreateObject.ServerPosition?>? HousePosition = null,
    Func<IReadOnlyList<string>>? HouseLines = null,
    Action? HouseShown = null,
    Func<IReadOnlyList<AcDream.Runtime.Gameplay.HousePanelLine>>? HousePanelLines = null);

public sealed record QuestRuntimeBindings(
    AcDream.Runtime.Gameplay.IRuntimeContractView Contracts,
    Func<AcDream.Core.Quests.ContractCatalog> Catalog,
    AcDream.Runtime.Gameplay.IRuntimeJournalView Journal,
    AcDream.Runtime.Gameplay.RuntimeJournalState JournalCommands,
    Func<uint> PlayerCell,
    Action<uint> AbandonContract,
    string JournalDirectory,
    /// <summary>How a load or save failure reaches the player.</summary>
    Action<string> Report);

public sealed record InventoryRuntimeBindings(
    ClientObjectTable Objects,
    Func<uint> PlayerGuid,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveIcon,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveDragIcon,
    Func<int?> Strength,
    Spellbook Spellbook,
    Action<uint>? SendUse,
    Action<uint, uint, int>? SendPutItemInContainer,
    Action<uint, uint, uint, uint>? SendStackableSplitToContainer,
    Action<uint, uint, uint>? SendStackableMerge,
    ItemInteractionController ItemInteraction,
    SelectionState Selection);

public sealed record ExternalContainerRuntimeBindings(
    ExternalContainerState State,
    ClientObjectTable Objects,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveIcon,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveDragIcon,
    ItemInteractionController ItemInteraction,
    SelectionState Selection,
    Action<uint> SendUse,
    Action<uint, uint, int> SendPutItemInContainer,
    Action<uint, uint, uint, uint> SendStackableSplitToContainer,
    Func<uint, bool> IsWithinUseRange);

public sealed record RetailUiPersistenceBindings(
    SettingsStore Store,
    Func<string> CharacterKey,
    Func<(int Width, int Height)> ScreenSize);

public sealed record RetailUiProbeBindings(
    bool Enabled,
    string? ScriptPath,
    bool DumpOnStart,
    Action<string> Log,
    Func<InputAction, bool> PressInput,
    Func<InputAction, bool, bool> SetInputHeld,
    Testing.IRetailUiAutomationRuntime? Runtime = null,
    Action<float, float>? QueueMouseLookDelta = null);

public sealed record RetailUiCursorBindings(
    CursorFeedbackController Feedback,
    RetailCursorManager Manager);

public sealed record WorldTooltipRuntimeBindings(
    Func<uint?> HoverGuidAtCursor,
    Func<uint, string?> ResolveName,
    Func<bool> Enabled);

public sealed record ConfirmationRuntimeBindings(
    Action<uint, uint, bool> SendResponse);

public sealed record AppraisalRuntimeBindings(
    Func<string> PlayerName,
    Action<uint, string> SendSetInscription,
    Action<string> DisplaySystemMessage,
    Func<int> LocalFactionBits);

public sealed record VendorRuntimeBindings(
    VendorState State,
    Func<ItemType, uint, uint, uint, uint, uint> ResolveIcon,
    ItemInteractionController ItemInteraction,
    SelectionState Selection,
    Action<string>? DisplaySystemMessage = null);

public sealed record KeyboardRuntimeBindings(
    InputDispatcher? Dispatcher,
    string KeyBindingsFilePath);

public sealed record CharacterSelectionRuntimeBindings(
    Func<IRuntimeCharacterSelectionView?> View,
    Func<uint, RuntimeCommandResult> Highlight,
    Func<RuntimeCommandResult> Enter,
    Func<RuntimeCommandResult> RequestDelete,
    Func<RuntimeCommandResult> ConfirmDelete,
    Func<RuntimeCommandResult> Restore,
    Func<RuntimeCommandResult> Cancel,
    Action RequestExit,
    Action? RequestCreate = null,
    bool DirectCharacterLaunch = false);

public sealed record ConnectionRuntimeBindings(
    Func<IRuntimeConnectionView?> View,
    Action RequestExit,
    bool ShowProgress = true);

public sealed record RetailUiRuntimeBindings(
    UiHost Host,
    RetailUiAssets Assets,
    VitalsRuntimeBindings Vitals,
    ChatRuntimeBindings Chat,
    RadarRuntimeBindings Radar,
    CombatRuntimeBindings Combat,
    MagicRuntimeBindings Magic,
    JumpPowerbarRuntimeBindings JumpPowerbar,
    FpsRuntimeBindings Fps,
    VividTargetRuntimeBindings VividTarget,
    IndicatorRuntimeBindings Indicators,
    ToolbarRuntimeBindings Toolbar,
    CharacterRuntimeBindings Character,
    InventoryRuntimeBindings Inventory,
    ExternalContainerRuntimeBindings ExternalContainer,
    VendorRuntimeBindings Vendor,
    RetailUiCursorBindings Cursor,
    WorldTooltipRuntimeBindings WorldTooltip,
    ConfirmationRuntimeBindings Confirmations,
    AppraisalRuntimeBindings Appraisal,
    OptionsRuntimeBindings Options,
    SocialRuntimeBindings Social,
    MapHouseRuntimeBindings MapHouse,
    QuestRuntimeBindings Quests,
    StackSplitQuantityState StackSplitQuantity,
    BufferedUiRegistry? Plugins,
    RetailUiPersistenceBindings? Persistence,
    RetailUiProbeBindings Probe,
    KeyboardRuntimeBindings? Keyboard = null,
    CharacterSelectionRuntimeBindings? CharacterSelection = null,
    CharacterCreationRuntimeBindings? CharacterCreation = null,
    Action? CaptureScreenshot = null,
    Func<IReadOnlyList<PluginProjectileDebugSample>>?
        ProjectileDebugSamples = null,
    ConnectionRuntimeBindings? Connection = null,
    Func<bool>? IsGameplayDisplay = null,
    Action? SynchronizeDisplayPhase = null);

public sealed class RetailUiRuntime : IDisposable
{
    private readonly RetailUiRuntimeBindings _bindings;
    private CreatureDisplayNameResolver? _creatureNames;
    private RetailAppraisalNameResolver? _itemNames;

    private RetailAppraisalNameResolver ItemNames
    {
        get
        {
            if (_itemNames is not null)
                return _itemNames;
            lock (_bindings.Assets.DatLock)
            {
                _creatureNames ??= CreatureDisplayNameResolver.Load(_bindings.Assets.Dats);
                return _itemNames ??= RetailAppraisalNameResolver.Load(
                    _bindings.Assets.Dats, _creatureNames);
            }
        }
    }

    private string? ResolveSelectedObjectName(uint guid) =>
        _bindings.Toolbar.Objects.Get(guid) is { } obj
            ? ItemNames.ResolveAppropriateName(obj)
            : _bindings.Toolbar.ResolveName(guid);

    private StackSplitQuantityState StackSplitQuantity => _bindings.StackSplitQuantity;
    private RetailWindowLayoutPersistence? _persistence;
    private RetailUiAutomationScriptRunner? _automation;
    private readonly RetailPanelUiController _panelUi;
    private ChatWindowController? _chatWindowController;
    private readonly FloatingChatWindowController?[] _floatingChatControllers = new FloatingChatWindowController?[4];
    private GameplayConfirmationController? _gameplayConfirmationController;
    private RetailItemConfirmationController? _itemConfirmationController;
    private RetailSkillTrainingConfirmationController? _skillTrainingConfirmationController;
    private UiShortcutDigitGraphics? _shortcutDigitGraphics;
    private ItemCooldownUiController? _itemCooldownController;
    private VividTargetIndicatorController? _vividTargetIndicator;
    private ProjectileDebugOverlayController? _projectileDebugOverlay;
    private Layout.VitalsSideBySideController? _vitalsSideBySide;
    private CharacterManagementUiMountCoordinator? _characterManagementMount;
    private ConnectionUiMountCoordinator? _connectionMount;
    private CreditsUiController? _creditsController;
    private CharacterCreationUiMountCoordinator? _characterCreationMount;
    private PluginSidePanel? _pluginSidePanel;
    private IDisposable? _characterSheetSubscription;
    private Layout.CharacterTitlesController? _characterTitlesController;
    private ResourceShutdownTransaction? _shutdown;
    private bool _disposed;

    internal bool IsDisposalComplete => _disposed;

    private RetailUiRuntime(RetailUiRuntimeBindings bindings)
    {
        _bindings = bindings;
        _panelUi = new RetailPanelUiController(
            bindings.Host.IsWindowVisible,
            bindings.Host.ShowWindow,
            bindings.Host.HideWindow);

        ChatSettings chatSettings = bindings.Chat.Store?.LoadChat() ?? ChatSettings.Default;
        WindowLockPresentation = new RetailWindowLockPresentationController(
            bindings.Host.Root.WindowManager);
        WindowOpacity = new RetailWindowOpacityController(
            bindings.Host.Root.WindowManager,
            chatSettings.DefaultOpacity,
            chatSettings.ActiveOpacity);
    }

    internal static RetailUiRuntime CreateUninitialized(
        RetailUiRuntimeBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return new RetailUiRuntime(bindings);
    }

    internal void InitializeForLease() => Initialize();

    private void Initialize()
    {
        RetailUiRuntimeBindings bindings = _bindings;
        lock (_bindings.Assets.DatLock)
        {
            ElementInfo? mainPanelFrame = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats, 0x2100006Eu, 0x100005FEu);
            if (mainPanelFrame is not null)
                _panelUi.ConfigureMainPanelFrame(mainPanelFrame);
        }
        MountFpsDisplay();
        MountVividTargetIndicator();
        MountProjectileDebugOverlay();
        MountVitals();
        MountRadar();
        MountChat();
        MountFloatingChatWindows();
        MountToolbar();
        MountCombat();
        MountSpellbook();
        MountAppraisal();
        MountEffects();
        MountIndicatorDetailPanels();
        MountOptionsPanel();
        MountKeyboardConfig();
        MountIndicators();
        MountJumpPowerbar();
        MountDialogFactory();
        MountTooltipPresenter();
        MountSocialPanel();
        MountMapHousePanel();
        MountJournalPanel();
        MountCharacter();
        MountPlugins();
        MountInventory();
        MountExternalContainer();
        MountVendor();
        MountSecureTrade();
        MountSalvage();
        MountItemCooldowns();
        if (bindings.Connection is { } connection)
        {
            _connectionMount = new ConnectionUiMountCoordinator(Host.Root, bindings.Assets, connection);
            _connectionMount.Tick();
        }
        ConfigureCharacterManagement();
        _characterManagementMount?.Tick();
        ConfigureCharacterCreation();
        _characterCreationMount?.Tick();
        Host.WindowManager.WindowVisibilityChanged += OnWindowVisibilityChanged;
        BindToolbarPanelButtons();
        SyncToolbarWindowButtons();

        {
            var persistence = bindings.Persistence;
            _persistence = new RetailWindowLayoutPersistence(
                Host.WindowManager,
                persistence?.Store,
                persistence?.CharacterKey ?? (() => "default"),
                persistence?.ScreenSize ?? (() => ((int)Host.Root.Width, (int)Host.Root.Height)),
                stateManagedVisibilityWindows:
                [
                    WindowNames.Combat,
                    WindowNames.JumpPowerbar,
                    WindowNames.ExternalContainer,
                    WindowNames.Vendor,
                    WindowNames.Vitals,
                    WindowNames.SideVitals,
                    WindowNames.SecureTrade,
                    WindowNames.Salvage,
                    WindowNames.PluginShelf,
                ]);
            _persistence.ResetToDefaults();
            _persistence.SetGameplayActive(_bindings.IsGameplayDisplay?.Invoke() ?? true);
        }

        if (bindings.Probe.Enabled)
        {
            var probe = new RetailUiAutomationProbe(
                Host.Root,
                bindings.Inventory.Objects,
                bindings.Probe.Log);
            _automation = new RetailUiAutomationScriptRunner(
                probe,
                bindings.Probe.ScriptPath,
                bindings.Probe.DumpOnStart,
                bindings.Probe.Log,
                text => ChatCommandRouter.Submit(
                    text,
                    bindings.Chat.ViewModel,
                    bindings.Chat.CommandBus(),
                    ChatChannelKind.Say),
                bindings.Probe.PressInput,
                bindings.Probe.SetInputHeld,
                bindings.Probe.Runtime,
                bindings.Probe.QueueMouseLookDelta);
        }
    }

    public UiHost Host => _bindings.Host;

    public RetailWindowOpacityController WindowOpacity { get; }

    public RetailWindowLockPresentationController WindowLockPresentation { get; }

    public RetailUiAssets Assets => _bindings.Assets;

    public ItemInteractionController ItemInteraction => _bindings.Inventory.ItemInteraction;
    public CharacterSheetProvider CharacterSheetProvider => _bindings.Character.Provider;
    public ToolbarController? ToolbarController { get; private set; }
    public ToolbarInputController? ToolbarInputController { get; private set; }
    public CombatUiController? CombatUiController { get; private set; }
    public SpellcastingUiController? SpellcastingUiController { get; private set; }
    public SpellbookWindowController? SpellbookWindowController { get; private set; }
    public AppraisalUiController? AppraisalController { get; private set; }
    public UiViewport? CreatureAppraisalViewportWidget { get; private set; }
    public UiElement? ExaminationFrame { get; private set; }
    public EffectsUiController? PositiveEffectsController { get; private set; }
    public EffectsUiController? NegativeEffectsController { get; private set; }
    public LinkStatusUiController? LinkStatusUiController { get; private set; }
    public VitaeUiController? VitaeUiController { get; private set; }
    public MiniGameUiController? MiniGameUiController { get; private set; }
    public IndicatorBarController? IndicatorBarController { get; private set; }
    public JumpPowerbarController? JumpPowerbarController { get; private set; }
    public RetailFpsController? FpsController { get; private set; }
    public SelectedObjectController? SelectedObjectController { get; private set; }
    public UiViewport? PaperdollViewportWidget { get; private set; }
    public UiNineSlicePanel? InventoryFrame { get; private set; }
    public InventoryController? InventoryPanelController { get; private set; }
    public RetailDialogFactory? DialogFactory { get; private set; }
    public RetailTooltipPresenter? TooltipPresenter { get; private set; }
    public ExternalContainerController? ExternalContainerController { get; private set; }
    public VendorUiController? VendorController { get; private set; }
    public OptionsPanelController? OptionsPanelController { get; private set; }
    public SocialPanelController? SocialPanelController { get; private set; }
    private CharacterStatController.Binding? _characterStatBinding;

    public Layout.JournalPanelController? JournalPanelController { get; private set; }

    private JournalPersistence? _journalFile;

    private JournalPersistence JournalFile =>
        _journalFile ??= new JournalPersistence(
            _bindings.Quests.JournalCommands,
            _bindings.Quests.JournalDirectory,
            _bindings.Quests.Report);

    public void LoadJournal(string characterName) => JournalFile.Load(characterName);

    public void SaveJournal() => JournalFile.Save(DateTime.UtcNow);
    public MapHousePanelController? MapHousePanelController { get; private set; }
    internal CharacterManagementUiController? CharacterManagementController =>
        _characterManagementMount?.Controller;
    internal CreditsUiController? CreditsController => _creditsController;
    internal CharacterCreationUiController? CharacterCreationController =>
        _characterCreationMount?.Controller;

    internal UiViewport? ChargenPreviewViewportWidget =>
        CharacterCreationController?.AppearanceViewport;

    internal AcDream.App.Rendering.IChargenPreviewControl? ChargenPreviewControl
    {
        get => CharacterCreationController?.AppearancePreviewControl;
        set
        {
            if (CharacterCreationController is { } controller)
                controller.AppearancePreviewControl = value;
        }
    }

    internal AcDream.Core.CharGen.IChargenPalSetSource? ChargenPalSetSource
    {
        get => CharacterCreationController?.AppearancePalSetSource;
        set
        {
            if (CharacterCreationController is { } controller)
                controller.AppearancePalSetSource = value;
        }
    }

    internal AcDream.Core.CharGen.IChargenClothingTableSource? ChargenClothingTableSource
    {
        get => CharacterCreationController?.AppearanceClothingTableSource;
        set
        {
            if (CharacterCreationController is { } controller)
                controller.AppearanceClothingTableSource = value;
        }
    }

    internal AcDream.Core.CharGen.IChargenPaletteColorSource? ChargenPaletteColorSource
    {
        get => CharacterCreationController?.AppearancePaletteColorSource;
        set
        {
            if (CharacterCreationController is { } controller)
                controller.AppearancePaletteColorSource = value;
        }
    }

    internal AcDream.App.UI.Layout.IChargenSwatchTextureSource? ChargenSwatchTextureSource
    {
        get => CharacterCreationController?.AppearanceSwatchTextureSource;
        set
        {
            if (CharacterCreationController is { } controller)
                controller.AppearanceSwatchTextureSource = value;
        }
    }

    internal bool IsChargenPreviewPageVisible =>
        CharacterCreationController?.IsAppearancePageVisible ?? false;

    internal UiViewport? SummaryPreviewViewportWidget =>
        CharacterCreationController?.SummaryViewport;

    internal AcDream.App.Rendering.IChargenPreviewControl? SummaryPreviewControl
    {
        get => CharacterCreationController?.SummaryPreviewControl;
        set
        {
            if (CharacterCreationController is { } controller)
                controller.SummaryPreviewControl = value;
        }
    }

    internal bool IsSummaryPreviewPageVisible =>
        CharacterCreationController?.IsSummaryPageVisible ?? false;

    public static RetailUiRuntime Mount(RetailUiRuntimeBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var runtime = CreateUninitialized(bindings);
        try
        {
            runtime.Initialize();
            return runtime;
        }
        catch (Exception initializationFailure)
        {
            try
            {
                runtime.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Retail UI initialization failed and its partially constructed ownership could not be fully released.",
                    initializationFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    public void Tick(double deltaSeconds)
    {
        _bindings.SynchronizeDisplayPhase?.Invoke();
        _persistence?.SetGameplayActive(_bindings.IsGameplayDisplay?.Invoke() ?? true);
        Layout.UiMediaClock.Advance(deltaSeconds);
        FpsController?.Tick();
        _vividTargetIndicator?.Tick();
        _projectileDebugOverlay?.Tick();
        _vitalsSideBySide?.Tick();
        SpellbookWindowController?.Tick();
        AppraisalController?.Tick(deltaSeconds);
        SpellcastingUiController?.Tick();
        PositiveEffectsController?.Tick();
        NegativeEffectsController?.Tick();
        LinkStatusUiController?.Tick();
        IndicatorBarController?.Tick();
        _chatWindowController?.UpdateUnreadIndicator();
        JumpPowerbarController?.Tick();
        SecureTradeController?.Tick();
        SelectedObjectController?.Tick(deltaSeconds);
        ExternalContainerController?.Tick();
        SocialPanelController?.Tick();
        JournalPanelController?.Tick();
        MapHousePanelController?.Tick(deltaSeconds);
        _itemCooldownController?.Tick();
        _connectionMount?.Tick();
        _characterManagementMount?.Tick();
        CharacterManagementController?.Tick(_connectionMount?.IsVisible == true);
        _creditsController?.Tick();
        _characterCreationMount?.Tick();
        CharacterCreationController?.Tick();
        DialogFactory?.Tick();
        Host.Tick(deltaSeconds);
        TooltipPresenter?.Tick();
        _automation?.Tick(deltaSeconds);
    }

    private System.Numerics.Vector2 _lastScreenSize;

    public void Draw(System.Numerics.Vector2 screenSize)
    {
        if (screenSize != _lastScreenSize)
        {
            Host.Root.Width = screenSize.X;
            Host.Root.Height = screenSize.Y;
            bool gameplay = _bindings.IsGameplayDisplay?.Invoke() ?? true;
            _persistence?.SetGameplayActive(gameplay);
            if (gameplay) _persistence?.ReflowToScreen();
            _lastScreenSize = screenSize;
        }

        Host.Draw(screenSize);
    }

    public bool HandleInputAction(AcDream.UI.Abstractions.Input.InputAction action)
    {
        if (AppraisalController?.HandleInputAction(action) == true)
            return true;

        if (SpellcastingUiController?.Handle(action) == true)
            return true;

        switch (action)
        {
            case AcDream.UI.Abstractions.Input.InputAction.CaptureScreenshot:
                _bindings.CaptureScreenshot?.Invoke();
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleHelp:
                _bindings.Options.DisplaySystemMessage(
                    "In-game help is unavailable because the retail help plugin is not installed.");
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.TogglePluginManager:
                if (_pluginSidePanel is not { EntryCount: > 0 } shelf)
                {
                    _bindings.Options.DisplaySystemMessage(
                        "No plugin windows are registered.");
                    return true;
                }
                if (shelf.Visible)
                {
                    shelf.Hide();
                    _bindings.Options.DisplaySystemMessage(
                        PluginShelfHiddenMessage());
                }
                else
                {
                    shelf.Show();
                }
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleAbuseReportingPanel:
                _bindings.Options.DisplaySystemMessage(OptionsPanelText.ReportAbuseUnavailable);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleUrgentAssistancePanel:
                _bindings.Options.DisplaySystemMessage(OptionsPanelText.UrgentAssistanceUnavailable);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ChatReply:
                _chatWindowController?.StartReply(_bindings.Chat.ViewModel.LastIncomingTellSender);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ChatMonarchReply:
                _chatWindowController?.StartReply(_bindings.Chat.ViewModel.LastMonarchSender);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ChatPatronReply:
                _chatWindowController?.StartReply(_bindings.Chat.ViewModel.LastPatronSender);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ChatStartCommand:
                _chatWindowController?.StartCommand();
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ChatTellToSelected:
            {
                uint selected = _bindings.Toolbar.Selection.SelectedObjectId ?? 0u;
                if (selected is >= 0x50000001u and <= 0x6FFFFFFFu)
                {
                    string? name = _bindings.Toolbar.ResolveName(selected);
                    if (!string.IsNullOrEmpty(name))
                        _chatWindowController?.StartTell(name);
                }
                return true;
            }
            case AcDream.UI.Abstractions.Input.InputAction.EnterChatMode:
                _chatWindowController?.EnterChatMode(
                    _bindings.Keyboard?.Dispatcher?.CurrentPhysicalChord);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleChatEntry:
                _chatWindowController?.ToggleChatEntry(
                    _bindings.Keyboard?.Dispatcher?.CurrentPhysicalChord);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleCharacterInfoPanel:
                ToggleWindow(WindowNames.CharacterInformation);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.TogglePositiveMagicPanel:
                ToggleWindow(WindowNames.PositiveEffects);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleNegativeMagicPanel:
                ToggleWindow(WindowNames.NegativeEffects);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleLinkStatusPanel:
                ToggleWindow(WindowNames.LinkStatus);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleVitaePanel:
                ToggleWindow(WindowNames.Vitae);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleSocialPanel:
                ToggleWindow(WindowNames.SocialPanel);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleAllegiancePanel:
                OpenSocialPanel(SocialPanelPage.Allegiance);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleFellowshipPanel:
                OpenSocialPanel(SocialPanelPage.Fellowship);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleFriendsPage:
                OpenSocialPanel(SocialPanelPage.Friends);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleSpellManagementPanel:
                ToggleWindow(WindowNames.Spellbook);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleSpellbookPanel:
                OpenSpellbook(SpellbookWindowPage.Spells);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleSpellComponentsPanel:
                OpenSpellbook(SpellbookWindowPage.Components);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleCharacterDetailPanel:
                ToggleWindow(WindowNames.Character);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleAttributesPanel:
                OpenCharacterPanel(CharacterStatController.CharacterStatTab.Attributes);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleSkillsPanel:
                OpenCharacterPanel(CharacterStatController.CharacterStatTab.Skills);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleCharacterTitlesPage:
                OpenCharacterPanel(CharacterStatController.CharacterStatTab.Titles);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleWorldPanel:
                ToggleWindow(WindowNames.MapHouse);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleMapPage:
                OpenWorldPanel(showHouse: false);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleHousePage:
                OpenWorldPanel(showHouse: true);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleOptionsPanel:
                ToggleWindow(WindowNames.Options);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleGameplayOptionsPage:
                OpenOptionsPage(OptionsPanelPage.Gameplay);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleCharacterSettingsPage:
                OpenOptionsPage(OptionsPanelPage.Character);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleConfigurationPage:
                OpenOptionsPage(OptionsPanelPage.Configuration);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleCompass:
                Host.ToggleWindow(WindowNames.Radar);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleKeyboardConfiguration:
                ToggleWindow(WindowNames.KeyboardConfig);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleQuestJournalPage:
                OpenJournalPanel(JournalPanelPage.Notes);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleQuestDetailPanel:
                OpenJournalPanel(JournalPanelPage.Contracts);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleJournalPageList:
                OpenJournalPanel(JournalPanelPage.PageList);
                return true;
            case AcDream.UI.Abstractions.Input.InputAction.ToggleContractsPage:
                OpenJournalPanel(JournalPanelPage.Contracts);
                return true;
        }

        return ToolbarInputController?.Handle(action) == true;
    }

    private void OpenCharacterPanel(CharacterStatController.CharacterStatTab tab)
    {
        bool visible = Host.IsWindowVisible(WindowNames.Character);
        bool onTargetTab = _characterStatBinding?.CurrentTab() == tab;
        if (visible && onTargetTab)
        {
            CloseWindow(WindowNames.Character);
            return;
        }

        _characterStatBinding?.ShowTab(tab);
        _panelUi.SetPanelVisibility(RetailPanelCatalog.Character, visible: true);
    }

    private void OpenWorldPanel(bool showHouse)
    {
        bool visible = Host.IsWindowVisible(WindowNames.MapHouse);
        bool onTargetTab = showHouse
            ? MapHousePanelController?.IsShowingHouse == true
            : MapHousePanelController?.IsShowingMap == true;
        if (visible && onTargetTab)
        {
            CloseWindow(WindowNames.MapHouse);
            return;
        }

        if (showHouse)
            MapHousePanelController?.ShowHouse();
        else
            MapHousePanelController?.ShowMap();
        _panelUi.SetPanelVisibility(RetailPanelCatalog.MapHouse, visible: true);
    }

    private enum OptionsPanelPage { Gameplay, Character, Configuration }

    private void OpenOptionsPage(OptionsPanelPage page)
    {
        bool visible = Host.IsWindowVisible(WindowNames.Options);
        bool onTargetTab = page switch
        {
            OptionsPanelPage.Gameplay => OptionsPanelController?.IsShowingGameplay == true,
            OptionsPanelPage.Character => OptionsPanelController?.IsShowingCharacter == true,
            OptionsPanelPage.Configuration => OptionsPanelController?.IsShowingConfiguration == true,
            _ => false,
        };
        if (visible && onTargetTab)
        {
            CloseWindow(WindowNames.Options);
            return;
        }

        switch (page)
        {
            case OptionsPanelPage.Gameplay: OptionsPanelController?.ShowGameplay(); break;
            case OptionsPanelPage.Character: OptionsPanelController?.ShowCharacter(); break;
            case OptionsPanelPage.Configuration: OptionsPanelController?.ShowConfiguration(); break;
        }
        _panelUi.SetPanelVisibility(RetailPanelCatalog.Options, visible: true);
    }

    public void ToggleGameplayOptionsPage()
        => OpenOptionsPage(OptionsPanelPage.Gameplay);

    public void FocusChatEntry()
    {
        if (Host.Root.DefaultTextInput is { } input)
            Host.Root.SetKeyboardFocus(input);
    }

    public void LogOutCharacter() => EndCharacterSessionWithRetailGates();

    private enum SocialPanelPage { Friends, Allegiance, Fellowship }

    private void OpenSocialPanel(SocialPanelPage page)
    {
        bool visible = Host.IsWindowVisible(WindowNames.SocialPanel);
        bool onTargetTab = page switch
        {
            SocialPanelPage.Friends => SocialPanelController?.IsShowingFriends == true,
            SocialPanelPage.Allegiance => SocialPanelController?.IsShowingAllegiance == true,
            SocialPanelPage.Fellowship => SocialPanelController?.IsShowingFellowship == true,
            _ => false,
        };
        if (visible && onTargetTab)
        {
            CloseWindow(WindowNames.SocialPanel);
            return;
        }

        switch (page)
        {
            case SocialPanelPage.Friends: SocialPanelController?.ShowFriends(); break;
            case SocialPanelPage.Allegiance: SocialPanelController?.ShowAllegiance(); break;
            case SocialPanelPage.Fellowship: SocialPanelController?.ShowFellowship(); break;
        }
        _panelUi.SetPanelVisibility(RetailPanelCatalog.SocialPanel, visible: true);
    }

    private enum JournalPanelPage { Contracts, Notes, PageList }

    private void OpenJournalPanel(JournalPanelPage page)
    {
        switch (page)
        {
            case JournalPanelPage.Contracts: JournalPanelController?.ShowContracts(); break;
            case JournalPanelPage.Notes: JournalPanelController?.ShowNotes(); break;
            case JournalPanelPage.PageList: JournalPanelController?.ShowPageList(); break;
        }
        _panelUi.SetPanelVisibility(RetailPanelCatalog.Journal, visible: true);
    }

    private void OpenSpellbook(SpellbookWindowPage page)
    {
        bool visible = Host.IsWindowVisible(WindowNames.Spellbook);
        if (visible && SpellbookWindowController?.CurrentPage == page)
            CloseWindow(WindowNames.Spellbook);
        else
        {
            SpellbookWindowController?.ShowPage(page);
            _panelUi.SetPanelVisibility(RetailPanelCatalog.Magic, visible: true);
        }
    }

    public bool HandleConfirmationRequest(GameEvents.CharacterConfirmationRequest request)
        => _gameplayConfirmationController?.HandleRequest(request) == true;

    public bool HandleConfirmationDone(GameEvents.CharacterConfirmationDone done)
        => _gameplayConfirmationController?.HandleDone(done) == true;

    public bool HandleAppraisal(AppraiseInfoParser.Parsed appraisal)
        => AppraisalController is { } controller
            ? controller.Apply(appraisal)
            : ItemInteraction.AcceptAppraisalResponse(appraisal.Guid).Accepted;

    public uint ShowConfirmation(string message, Action<bool> completed)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(completed);
        return DialogFactory?.MakeConfirmation(
            message,
            data => completed(data.GetBoolean(RetailDialogProperty.ConfirmationResult))) ?? 0u;
    }

    public void ResetSessionDialogs()
    {
        try
        {
            _creditsController?.ResetSession();
            CharacterManagementController?.ResetSession();
            DialogFactory?.Reset();
            TooltipPresenter?.HideCurrent();
            Host.Root.ResetTooltipTracking();
        }
        finally
        {
            _gameplayConfirmationController?.ResetSession();
        }
    }

    public void ResetSessionTransientUi()
    {
        ResetSessionDialogs();
        AppraisalController?.ResetSession();
        Host.HideWindow(WindowNames.Examination);
        SocialPanelController?.ResetSessionDeclaration();
    }

    public void RedeclareSocialPanelAfterWorldEntry() =>
        SocialPanelController?.RedeclareAfterWorldEntry();

    public void UpdateCursor(IEnumerable<IMouse> mice)
    {
        CursorFeedback feedback = _bindings.Cursor.Feedback.Update(Host.Root);
        _bindings.Cursor.Manager.Apply(mice, feedback);
    }

    public void AttachNativeCursorWindow(nint glfwWindowHandle)
    {
        _bindings.Cursor.Manager.AttachNativeWindow(glfwWindowHandle);
    }

    public void RestoreLayout()
    {
        _persistence?.SetGameplayActive(_bindings.IsGameplayDisplay?.Invoke() ?? true);
        if (_bindings.Persistence is { } persistence)
        {
            var screen = persistence.ScreenSize();
            Host.Root.Width = screen.Width;
            Host.Root.Height = screen.Height;
            _lastScreenSize = new System.Numerics.Vector2(screen.Width, screen.Height);
        }
        _persistence?.RestoreAll();
    }

    public void SaveLayout()
    {
        _persistence?.SaveAll();
        SaveChatWindowFilters();
    }

    public void SaveNamedLayout(string profileName) => _persistence?.SaveNamed(profileName);

    public void RestoreNamedLayout(string profileName) => _persistence?.RestoreNamed(profileName);

    public bool ToggleWindow(string name)
        => RetailPanelCatalog.TryGetPanelId(name, out uint panelId)
            ? _panelUi.TogglePanel(panelId)
            : Host.ToggleWindow(name);

    public IReadOnlyList<FloatingChatWindowController?> FloatingChatWindows => _floatingChatControllers;

    public bool ToggleFloatingChatWindow(int windowId)
        => Host.ToggleWindow(FloatingChatWindowName(windowId));

    private static string FloatingChatWindowName(int windowId) => windowId switch
    {
        1 => WindowNames.ChatWindow1,
        2 => WindowNames.ChatWindow2,
        3 => WindowNames.ChatWindow3,
        4 => WindowNames.ChatWindow4,
        _ => throw new ArgumentOutOfRangeException(nameof(windowId), windowId, "floating chat window id must be 1-4."),
    };

    private void SaveChatWindowFilters()
    {
        if (_bindings.Chat.Store is not { } store) return;
        ChatWindowState windows = _bindings.Chat.Windows;
        ChatSettings current = store.LoadChat();
        store.SaveChat(current with
        {
            ChatWindowMainFilter = windows.GetFilter(ChatWindowState.MainWindowId),
            ChatWindow1Filter = windows.GetFilter(1),
            ChatWindow2Filter = windows.GetFilter(2),
            ChatWindow3Filter = windows.GetFilter(3),
            ChatWindow4Filter = windows.GetFilter(4),
        });
    }

    private void SaveChatOpacity()
    {
        if (_bindings.Chat.Store is not { } store) return;
        ChatSettings current = store.LoadChat();
        store.SaveChat(current with
        {
            DefaultOpacity = WindowOpacity.DefaultOpacity,
            ActiveOpacity = WindowOpacity.ActiveOpacity,
        });
    }

    public void CloseWindow(string name)
    {
        if (RetailPanelCatalog.TryGetPanelId(name, out uint panelId))
            _panelUi.SetPanelVisibility(panelId, visible: false);
        else
            Host.HideWindow(name);
    }

    public void SyncToolbarWindowButtons()
    {
        if (ToolbarController is null) return;
        foreach (var (panelId, windowName) in RetailPanelCatalog.ToolbarPanels)
            ToolbarController.SetPanelOpen(panelId, Host.IsWindowVisible(windowName));
    }

    private void BindToolbarPanelButtons()
    {
        ToolbarController?.BindPanelButtons(
            panelId => RetailPanelCatalog.TryGetWindowName(panelId, out string name)
                && Host.WindowManager.TryGet(name, out _),
            panelId =>
            {
                if (RetailPanelCatalog.TryGetWindowName(panelId, out string name))
                    ToggleWindow(name);
            });
    }

    private void OnWindowVisibilityChanged(string windowName, bool visible)
    {
        _panelUi.ObserveWindowVisibility(windowName, visible);
        if (RetailPanelCatalog.TryGetPanelId(windowName, out uint panelId))
            ToolbarController?.SetPanelOpen(panelId, visible);

        if (TryGetFloatingChatWindowId(windowName, out int chatWindowId))
        {
            _bindings.Chat.Windows.SetOpen(chatWindowId, visible);
            _chatWindowController?.SetIndicatorOpen(chatWindowId, visible);
        }
    }

    private static bool TryGetFloatingChatWindowId(string windowName, out int windowId)
    {
        windowId = windowName switch
        {
            WindowNames.ChatWindow1 => 1,
            WindowNames.ChatWindow2 => 2,
            WindowNames.ChatWindow3 => 3,
            WindowNames.ChatWindow4 => 4,
            _ => 0,
        };
        return windowId != 0;
    }

    private UiElement? BuildSwallowedChild(ElementInfo info)
    {
        lock (_bindings.Assets.DatLock)
            return LayoutImporter.Build(
                info,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont).Root;
    }

    private ImportedLayout? Import(uint layoutId)
    {
        lock (_bindings.Assets.DatLock)
            return LayoutImporter.Import(
                _bindings.Assets.Dats,
                layoutId,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
    }

    private ImportedLayout? Import(uint layoutId, uint rootElementId)
    {
        lock (_bindings.Assets.DatLock)
            return LayoutImporter.Import(
                _bindings.Assets.Dats,
                layoutId,
                rootElementId,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
    }

    private void MountFpsDisplay()
    {
        ImportedLayout? layout = Import(
            RetailFpsController.LayoutId,
            RetailFpsController.DisplayElementId);
        if (layout is null)
        {
            Console.WriteLine("[UI] FPS display: SmartBox element 0x10000047 not found.");
            return;
        }

        FpsRuntimeBindings b = _bindings.Fps;
        FpsController = RetailFpsController.Bind(
            layout,
            b.FramesPerSecond,
            b.DegradeMultiplier,
            b.IsVisible);
        if (FpsController is null)
        {
            Console.WriteLine("[UI] FPS display: SmartBox element is not UIElement_Text.");
            return;
        }

        Host.Root.AddChild(layout.Root);
        Console.WriteLine("[UI] retail FPS display from SmartBox LayoutDesc 0x2100000F.");
    }

    private void MountVividTargetIndicator()
    {
        _vividTargetIndicator = VividTargetIndicatorController.Mount(
            Host.Root,
            _bindings.Assets,
            _bindings.VividTarget);
        Console.WriteLine(_vividTargetIndicator is null
            ? "[UI] vivid target indicator DAT surfaces unavailable."
            : "[UI] vivid target indicator mounted from client-enum category 0x10000009.");
    }

    private void MountProjectileDebugOverlay()
    {
        if (_bindings.ProjectileDebugSamples is not { } samples)
            return;
        _projectileDebugOverlay = ProjectileDebugOverlayController.Mount(
            Host.Root,
            samples,
            _bindings.VividTarget.Camera);
        Console.WriteLine(
            "[PluginUI] projectile collision debug overlay mounted.");
    }

    private void MountVitals()
    {
        ImportedLayout? layout = Import(0x2100006Cu);
        if (layout is null)
        {
            Console.WriteLine("[UI] vitals: LayoutDesc 0x2100006C not found — vitals unavailable.");
            return;
        }

        BindVitalsLayout(layout);
        RetailWindowFrame.Mount(Host.Root, layout.Root, _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Vitals,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f,
                Top = 30f,
                ResizeX = true,
                ResizeY = false,
                MinWidth = 40f,
                ContentClickThrough = false,
            });
        Console.WriteLine("[UI] retail UI active — vitals window from LayoutDesc importer (0x2100006C).");

        MountSideVitals();
    }

    private void MountSideVitals()
    {
        ElementInfo? info;
        ImportedLayout? layout;
        lock (_bindings.Assets.DatLock)
        {
            info = LayoutImporter.ImportInfos(_bindings.Assets.Dats, 0x21000075u);
            layout = info is null ? null : LayoutImporter.Build(
                info,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
        }
        if (info is null || layout is null)
        {
            Console.WriteLine("[UI] side vitals: LayoutDesc 0x21000075 not found — SideBySideVitals unavailable.");
            return;
        }

        BindVitalsLayout(layout);
        RetailWindowFrame.Mount(Host.Root, layout.Root, _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.SideVitals,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f,
                Top = 30f,
                ResizeX = true,
                ResizeY = false,
                DatConstraintSource = info,
                Visible = false,
                ContentClickThrough = false,
            });
        _vitalsSideBySide = new Layout.VitalsSideBySideController(
            Host.Root,
            () => _bindings.Options.CurrentCharacterOption(
                (uint)CharacterOptionId.SideBySideVitals),
            WindowNames.Vitals,
            WindowNames.SideVitals);
        Console.WriteLine("[UI] side-by-side vitals window from LayoutDesc importer (0x21000075).");
    }

    private void BindVitalsLayout(ImportedLayout layout)
    {
        VitalsVM vm = _bindings.Vitals.ViewModel;
        VitalsController.Bind(layout,
            () => vm.HealthPercent,
            () => vm.StaminaPercent ?? 0f,
            () => vm.ManaPercent ?? 0f,
            () => (vm.HealthCurrent, vm.HealthMax) is (uint c, uint m) ? $"{c}/{m}" : "",
            () => (vm.StaminaCurrent, vm.StaminaMax) is (uint c, uint m) ? $"{c}/{m}" : "",
            () => (vm.ManaCurrent, vm.ManaMax) is (uint c, uint m) ? $"{c}/{m}" : "");
    }

    private void MountRadar()
    {
        ImportedLayout? layout = Import(RadarController.LayoutId);
        if (layout is null || layout.Root is not UiRadar radarRoot)
        {
            Console.WriteLine("[UI] radar: LayoutDesc 0x21000074 not found or root class mismatch.");
            return;
        }

        RadarController controller = RadarController.Bind(
            layout,
            _bindings.Radar.Snapshot,
            guid =>
            {
                if (_bindings.Toolbar.ItemInteraction.OfferPrimaryClick(guid)
                    != ItemPrimaryClickResult.NotActive)
                    return;
                _bindings.Radar.Selection.Select(guid, SelectionChangeSource.Radar);
            },
            setUiLocked: _bindings.Radar.SetUiLocked,
            datFont: _bindings.Assets.DefaultFont);
        RetailWindowFrame.Mount(Host.Root, radarRoot, _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Radar,
                Chrome = RetailWindowChrome.Imported,
                Left = Math.Max(0f, Host.Root.Width - radarRoot.Width - 10f),
                Top = 10f,
                Resizable = false,
                ResizeX = false,
                ResizeY = false,
                ConstrainDragToParent = true,
                ContentClickThrough = false,
                Controller = controller,
            });
        Console.WriteLine("[UI] retail radar/compass from LayoutDesc 0x21000074.");
    }

    private void MountChat()
    {
        ElementInfo? info;
        ImportedLayout? layout;
        lock (_bindings.Assets.DatLock)
        {
            info = LayoutImporter.ImportInfos(_bindings.Assets.Dats, ChatWindowController.LayoutId);
            var strings = new DatStringResolver(_bindings.Assets.Dats);
            layout = info is null ? null : LayoutImporter.Build(
                info,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont,
                strings.Resolve);
        }
        if (info is null || layout is null)
        {
            Console.WriteLine("[UI] chat: LayoutDesc 0x2100006F not found.");
            return;
        }

        if (_bindings.Chat.Store is { } chatStore)
            _bindings.Chat.Windows.SetFilter(
                ChatWindowState.MainWindowId, chatStore.LoadChat().ChatWindowMainFilter);

        ChatWindowController? controller = ChatWindowController.Bind(
            info,
            layout,
            _bindings.Chat.ViewModel,
            _bindings.Chat.CommandBus,
            _bindings.Chat.Windows,
            _bindings.Assets.DefaultFont,
            _bindings.Assets.DebugFont,
            _bindings.Assets.ResolveSprite,
            selectedTargetName: () =>
            {
                uint selected = _bindings.Toolbar.Selection.SelectedObjectId ?? 0u;
                if (selected == 0u)
                    return null;
                string? name = _bindings.Toolbar.ResolveName(selected);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            },
            chatStrings: key => new DatStringResolver(_bindings.Assets.Dats)
                .Resolve(0x23000001u, DatStringResolver.ComputeHash(key)),
            resolveFont: _bindings.Assets.ResolveFont);
        if (controller is null)
        {
            Console.WriteLine("[UI] chat: required role elements missing in 0x2100006F.");
            return;
        }

        controller.Transcript.Keyboard = Host.Keyboard;
        controller.Input.Keyboard = Host.Keyboard;
        UiElement root = controller.Root;
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Chat,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f,
                Top = 440f,
                DatConstraintSource = controller.DatWindowInfo,
                AuthoredGeometryRevision = 1,
                ResizeX = true,
                ResizeY = true,
                Controller = controller,
                StateController = controller,
            });
        controller.AttachWindow(handle);
        Host.Root.DefaultTextInput = controller.Input;
        _chatWindowController = controller;
        controller.BindIndicatorClicks(ToggleFloatingChatWindow);
        Console.WriteLine("[UI] retail chat window from LayoutDesc importer (0x2100006F).");
    }

    private void MountFloatingChatWindows()
    {
        ElementInfo? info;
        lock (_bindings.Assets.DatLock)
        {
            info = LayoutImporter.ImportInfos(_bindings.Assets.Dats, FloatingChatWindowController.LayoutId);
        }
        if (info is null)
        {
            Console.WriteLine("[UI] floating chat: LayoutDesc 0x2100005B not found.");
            return;
        }

        ChatWindowState windowFilters = _bindings.Chat.Windows;
        if (_bindings.Chat.Store is { } store)
        {
            ChatSettings chat = store.LoadChat();
            windowFilters.SetFilter(1, chat.ChatWindow1Filter);
            windowFilters.SetFilter(2, chat.ChatWindow2Filter);
            windowFilters.SetFilter(3, chat.ChatWindow3Filter);
            windowFilters.SetFilter(4, chat.ChatWindow4Filter);
        }

        (string windowName, float left, float top)[] slots =
        {
            (WindowNames.ChatWindow1, 440f, 40f),
            (WindowNames.ChatWindow2, 440f, 170f),
            (WindowNames.ChatWindow3, 440f, 300f),
            (WindowNames.ChatWindow4, 440f, 430f),
        };

        var strings = new DatStringResolver(_bindings.Assets.Dats);
        for (int windowId = 1; windowId <= 4; windowId++)
        {
            ImportedLayout layout;
            lock (_bindings.Assets.DatLock)
            {
                layout = LayoutImporter.Build(
                    info,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    strings.Resolve);
            }

            FloatingChatWindowController? controller = FloatingChatWindowController.Bind(
                windowId,
                info,
                layout,
                _bindings.Chat.ViewModel,
                _bindings.Chat.CommandBus,
                windowFilters,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.DebugFont,
                _bindings.Assets.ResolveSprite);
            if (controller is null)
            {
                Console.WriteLine($"[UI] floating chat window {windowId}: required role elements missing in 0x2100005B.");
                continue;
            }

            controller.Transcript.Keyboard = Host.Keyboard;
            controller.Input.Keyboard = Host.Keyboard;
            (string windowName, float left, float top) = slots[windowId - 1];
            UiElement root = controller.Root;
            RetailWindowHandle handle = RetailWindowFrame.Mount(
                Host.Root,
                root,
                _bindings.Assets.ResolveSprite,
                new RetailWindowFrame.Options
                {
                    WindowName = windowName,
                    Chrome = RetailWindowChrome.Imported,
                    Left = left,
                    Top = top,
                    DatConstraintSource = controller.DatWindowInfo,
                    AuthoredGeometryRevision = 1,
                    ResizeX = true,
                    ResizeY = true,
                    Visible = false,
                    Controller = controller,
                });
            controller.AttachWindow(handle);
            _floatingChatControllers[windowId - 1] = controller;
        }

        Console.WriteLine("[UI] retail floating chat windows 1-4 from LayoutDesc importer (0x2100005B).");
    }

    private void MountToolbar()
    {
        ImportedLayout? layout = Import(0x21000016u);
        if (layout is null)
        {
            Console.WriteLine("[UI] toolbar: LayoutDesc 0x21000016 not found.");
            return;
        }

        UiShortcutDigitGraphics shortcutDigits = LoadShortcutDigitGraphics();
        ToolbarRuntimeBindings b = _bindings.Toolbar;
        ToolbarController = Layout.ToolbarController.Bind(
            layout, b.Objects, b.Shortcuts, b.ResolveIcon, b.UseItem, b.Combat,
            shortcutDigits.RegularDigits, shortcutDigits.GhostedDigits,
            shortcutDigits.EmptyDigits, b.ItemInteraction,
            b.SendAddShortcut, b.SendRemoveShortcut,
            toggleCombat: b.ToggleCombat,
            selectItem: guid => b.Selection.Select(guid, SelectionChangeSource.Toolbar),
            selectedObjectId: () => b.Selection.SelectedObjectId ?? 0u,
            selection: b.Selection,
            playerGuid: b.PlayerGuid,
            sendPutItemInContainer: b.SendPutItemInContainer,
            ammoFont: _bindings.Assets.DefaultFont,
            dragIconIds: b.ResolveDragIcon);
        ToolbarInputController = new ToolbarInputController(ToolbarController, b.Selection);
        SelectedObjectController = Layout.SelectedObjectController.Bind(
            layout,
            b.Selection,
            b.SubscribeHealthChanged,
            b.UnsubscribeHealthChanged,
            handler => b.ItemMana.ItemManaChanged += handler,
            handler => b.ItemMana.ItemManaChanged -= handler,
            b.IsHealthTarget,
            b.ItemInteraction.IsOwnedByPlayer,
            ResolveSelectedObjectName,
            b.HealthPercent,
            b.HasHealth,
            b.StackSize,
            b.SendQueryHealth,
            b.ItemMana.GetManaPercent,
            b.SendQueryItemMana,
            _bindings.Assets.DefaultFont,
            StackSplitQuantity,
            handler => b.Objects.ObjectUpdated += handler,
            handler => b.Objects.ObjectUpdated -= handler,
            b.IsVendorSplitExempt,
            isCoinstack: guid => b.Objects.Get(guid)?.WeenieClassId == 273u,
            coinTotal: () => b.Objects.Get(b.PlayerGuid())?.Properties.GetInt(
                (uint)PropertyInt.CoinValue) ?? 0);

        UiElement root = layout.Root;
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root, root, _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Toolbar,
                Chrome = RetailWindowChrome.CollapsibleNineSlice,
                Left = 10f,
                Top = 300f,
                ContentWidth = 300f,
                ContentHeight = root.Height,
                Resizable = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right,
                ContentClickThrough = true,
                Controller = new RetainedPanelControllerGroup(
                    ToolbarController,
                    SelectedObjectController),
            });
        ConfigureToolbarCollapse(layout, (UiCollapsibleFrame)handle.OuterFrame, root.Height);
        Console.WriteLine("[UI] retail toolbar window from LayoutDesc importer (0x21000016).");
    }

    private void MountCombat()
    {
        ElementInfo? info;
        ImportedLayout? layout;
        CombatUiLabels? labels;
        uint favoriteEmptySprite;
        lock (_bindings.Assets.DatLock)
        {
            info = LayoutImporter.ImportInfos(_bindings.Assets.Dats, CombatUiController.LayoutId);
            favoriteEmptySprite = info is null
                ? 0u
                : ItemListCellTemplate.ResolveEmptySprite(
                    _bindings.Assets.Dats,
                    info,
                    SpellcastingUiController.FavoriteListId);
            var strings = new DatStringResolver(_bindings.Assets.Dats);
            layout = info is null ? null : LayoutImporter.Build(
                info,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont,
                strings.Resolve);
            labels = info is null ? null : CombatUiLabels.Resolve(info, strings);
        }
        if (info is null || layout is null || labels is null)
        {
            Console.WriteLine("[UI] combat: LayoutDesc 0x21000073 not found.");
            return;
        }
        float combatWidth = RetailCombatLayout.FitFavoriteSlots(layout);

        CombatUiController? controller = Layout.CombatUiController.Bind(
            layout,
            _bindings.Combat.State,
            _bindings.Combat.Attacks,
            new Layout.CombatUiController.Bindings(
                CurrentValue: id => _bindings.Options.CurrentCharacterOption((uint)id),
                SetOption: (id, value) => _bindings.Options.CommandBus().Publish(
                    new SetSingleCharacterOptionRuntimeCmd((uint)id, value))),
            labels,
            visible =>
            {
                if (visible) Host.ShowWindow(WindowNames.Combat);
                else Host.HideWindow(WindowNames.Combat);
            });
        if (controller is null)
        {
            Console.WriteLine("[UI] combat: required controls missing in LayoutDesc 0x21000073.");
            return;
        }

        SpellcastingUiController? spellcasting = Layout.SpellcastingUiController.Bind(
            layout,
            _bindings.Magic.Spellbook,
            _bindings.Magic.Casting,
            _bindings.Magic.Objects,
            _bindings.Magic.PlayerGuid,
            _bindings.Magic.ResolveSpellIcon,
            item => _bindings.Magic.ResolveDragIcon(
                item.Type, item.IconId, item.IconUnderlayId,
                item.IconOverlayId, item.Effects),
            _bindings.Magic.UseItem,
            _bindings.Magic.Selection,
            _bindings.Magic.AddFavorite,
            _bindings.Magic.RemoveFavorite,
            LoadShortcutDigitGraphics(),
            favoriteEmptySprite,
            examineSpell: spellId => AppraisalController?.ExamineSpell(spellId));
        if (spellcasting is null)
            Console.WriteLine("[UI] spellcasting: required controls missing in LayoutDesc 0x21000073.");

        CombatUiController = controller;
        SpellcastingUiController = spellcasting;
        IRetainedPanelController controllerOwner = spellcasting is null
            ? controller
            : new RetainedPanelControllerGroup(controller, spellcasting);
        UiElement root = layout.Root;
        RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            RetailCombatLayout.WithHorizontalResize(layout, new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Combat,
                Chrome = RetailWindowChrome.Imported,
                Left = 0f,
                Top = Math.Max(0f, Host.Root.Height - root.Height - 10f),
                ContentWidth = combatWidth,
                DatConstraintSource = info,
                OuterAnchors = AnchorEdges.Left | AnchorEdges.Bottom,
                Visible = false,
                Draggable = false,
                ConstrainDragToParent = true,
                ContentClickThrough = false,
                Controller = controllerOwner,
            }));
        controller.SyncVisibility();
        Console.WriteLine(spellcasting is null
            ? "[UI] retail combat from LayoutDesc 0x21000073; magic binding unavailable."
            : $"[UI] retail combat + spell bar from LayoutDesc 0x21000073; " +
              $"favorite empty=0x{favoriteEmptySprite:X8}.");
    }

    private void MountSpellbook()
    {
        ImportedLayout? layout;
        SpellbookRowStyle? rowStyle;
        ComponentBookTemplateFactory? componentTemplates;
        lock (_bindings.Assets.DatLock)
        {
            layout = LayoutImporter.Import(
                _bindings.Assets.Dats,
                SpellbookWindowController.LayoutId,
                SpellbookWindowController.RootId,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
            rowStyle = SpellbookRowStyle.TryLoad(_bindings.Assets.Dats);
            componentTemplates = ComponentBookTemplateFactory.TryLoad(
                _bindings.Assets.Dats,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
        }
        if (layout is null || rowStyle is null || componentTemplates is null)
        {
            Console.WriteLine(
                "[UI] spellbook: layout, spell row, or component list templates not found.");
            return;
        }

        UiDatFont? rowFont = rowStyle.Value.FontDid == 0u
            ? _bindings.Assets.DefaultFont
            : _bindings.Assets.ResolveFont(rowStyle.Value.FontDid)
                ?? _bindings.Assets.DefaultFont;

        SpellbookWindowController? controller = Layout.SpellbookWindowController.Bind(
            layout,
            _bindings.Magic.Spellbook,
            _bindings.Magic.Objects,
            _bindings.Magic.PlayerGuid,
            _bindings.Magic.Components,
            _bindings.Magic.Selection,
            _bindings.Magic.ResolveSpellIcon,
            _bindings.Magic.ResolveComponentIcon,
            _bindings.Magic.SpellLevel,
            _bindings.Magic.SelectObject,
            spellId => SpellcastingUiController?.AddFavorite(spellId),
            _bindings.Magic.SendSpellbookFilter,
            _bindings.Magic.RemoveSpell,
            spellId => AppraisalController?.ExamineSpell(spellId),
            (message, completed) => ShowConfirmation(message, completed),
            _bindings.Magic.SetDesiredComponent,
            () => CloseWindow(WindowNames.Spellbook),
            componentTemplates,
            rowStyle.Value,
            rowFont);
        if (controller is null)
        {
            Console.WriteLine("[UI] spellbook: required controls missing in LayoutDesc 0x21000034.");
            return;
        }

        SpellbookWindowController = controller;
        UiElement root = layout.Root;
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Spellbook,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 18f,
                Top = 18f,
                Visible = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
                ContentClickThrough = false,
                Controller = controller,
            });
        _panelUi.RegisterMainPanel(
            RetailPanelCatalog.Magic,
            WindowNames.Spellbook,
            handle);
        Console.WriteLine("[UI] retail spellbook/component book from LayoutDesc 0x21000034.");
    }

    private void MountAppraisal()
    {
        ImportedLayout? layout;
        CreatureAppraisalRowTemplateFactory? creatureRows;
        SpellExamineComponentTemplateFactory? spellComponentTemplates;
        CreatureDisplayNameResolver? creatureNames;
        RetailAppraisalNameResolver? itemNames;
        lock (_bindings.Assets.DatLock)
        {
            layout = LayoutImporter.Import(
                _bindings.Assets.Dats,
                AppraisalUiController.LayoutId,
                AppraisalUiController.RootId,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
            creatureRows = CreatureAppraisalRowTemplateFactory.TryLoad(
                _bindings.Assets.Dats,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
            spellComponentTemplates =
                SpellExamineComponentTemplateFactory.TryLoad(
                    _bindings.Assets.Dats,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont);
            itemNames = ItemNames;
            creatureNames = _creatureNames;
        }
        if (layout is null)
        {
            Console.WriteLine(
                "[UI] examination: LayoutDesc 0x2100006B root 0x100005F2 not found.");
            return;
        }

        string? ResolveCharacterTitle(uint titleId)
        {
            lock (_bindings.Assets.DatLock)
                return _bindings.Character.TitleResolver.Resolve(titleId);
        }

        AppraisalUiController? controller = AppraisalUiController.Bind(
            layout,
            _bindings.Inventory.Objects,
            _bindings.Inventory.ItemInteraction,
            _bindings.Inventory.Selection,
            _bindings.Combat.State,
            _bindings.Magic.Spellbook,
            _bindings.Appraisal.PlayerName,
            _bindings.Appraisal.SendSetInscription,
            _bindings.Appraisal.DisplaySystemMessage,
            show: () => Host.ShowWindow(WindowNames.Examination),
            close: () => CloseWindow(WindowNames.Examination),
            creatureRowTemplates: creatureRows,
            creatureNames: creatureNames,
            itemNames: itemNames,
            resolveSpellIcon: _bindings.Magic.ResolveSpellIcon,
            resolveComponentIcon: _bindings.Magic.ResolveComponentIcon,
            spellComponents: _bindings.Magic.SpellComponents,
            magicSkill: _bindings.Magic.MagicSkill,
            spellComponentTemplates: spellComponentTemplates,
            resolveCharacterTitle: ResolveCharacterTitle,
            localFactionBits: _bindings.Appraisal.LocalFactionBits);
        if (controller is null)
        {
            Console.WriteLine(
                "[UI] examination: required authored controls are missing.");
            return;
        }

        AppraisalController = controller;
        UiElement root = layout.Root;
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Examination,
                Chrome = RetailWindowChrome.Imported,
                Left = root.Left,
                Top = root.Top,
                ContentWidth = root.Width,
                ContentHeight = root.Height,
                AuthoredGeometryRevision = 1,
                Visible = false,
                ResizeX = true,
                ResizeY = true,
                MinWidth = 310f,
                MinHeight = 400f,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentClickThrough = false,
                Controller = controller,
            });
        CreatureAppraisalViewportWidget =
            layout.FindElement(AppraisalUiController.CreatureViewportId)
                as UiViewport;
        ExaminationFrame = handle.OuterFrame;
        Console.WriteLine(
            "[UI] retail examination window from LayoutDesc 0x2100006B.");
    }

    private void MountEffects()
    {
        MountEffectsInstance(positive: true);
        MountEffectsInstance(positive: false);
    }

    private void MountEffectsInstance(bool positive)
    {
        uint rootId = positive ? EffectsUiController.PositiveRootId : EffectsUiController.NegativeRootId;
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        EffectRowTemplateFactory? rowTemplates;
        string selectPrompt;
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                EffectsUiController.LayoutId,
                rootId);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    new DatStringResolver(_bindings.Assets.Dats).Resolve);
            rowTemplates = EffectRowTemplateFactory.TryLoad(
                _bindings.Assets.Dats,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
            var strings = new DatStringResolver(_bindings.Assets.Dats);
            selectPrompt = strings.Resolve(
                    0x23000001u,
                    DatStringResolver.ComputeHash("ID_Effects_Info_SelectASpell"))
                ?? "SELECT A SPELL";
        }
        if (rootInfo is null || layout is null || rowTemplates is null)
        {
            Console.WriteLine($"[UI] effects: root or row template for 0x{rootId:X8} not found.");
            return;
        }

        EffectsUiController? controller = Layout.EffectsUiController.Bind(
            layout,
            _bindings.Magic.Spellbook,
            positive,
            _bindings.Magic.ServerTime,
            _bindings.Assets.ResolveSprite,
            _bindings.Magic.ResolveSpellIcon,
            rowTemplates,
            selectPrompt,
            close: () => CloseWindow(
                positive ? WindowNames.PositiveEffects : WindowNames.NegativeEffects));
        if (controller is null)
        {
            Console.WriteLine($"[UI] effects: list missing under root 0x{rootId:X8}.");
            return;
        }

        if (positive) PositiveEffectsController = controller;
        else NegativeEffectsController = controller;
        UiElement root = layout.Root;
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = positive ? WindowNames.PositiveEffects : WindowNames.NegativeEffects,
                Chrome = RetailWindowChrome.NineSlice,
                Left = Math.Max(0f, Host.Root.Width - root.Width - 12f),
                Top = 18f,
                Visible = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top
                    | AnchorEdges.Right | AnchorEdges.Bottom,
                ContentClickThrough = false,
                DrawChromeCenter = !AuthorsFullPanelCenter(rootInfo),
                Controller = controller,
            });
        _panelUi.RegisterMainPanel(
            positive ? RetailPanelCatalog.PositiveEffects : RetailPanelCatalog.NegativeEffects,
            positive ? WindowNames.PositiveEffects : WindowNames.NegativeEffects,
            handle,
            rootInfo.TryGetEffectiveBool(
                RetailPanelUiController.RestorePreviousPropertyId,
                out bool restorePrevious)
                && restorePrevious);
    }

    private void MountIndicatorDetailPanels()
    {
        MountLinkStatusPanel();
        MountVitaePanel();
        MountMiniGamePanel();
        MountCharacterInformationPanel();
    }

    private void MountCharacterInformationPanel()
    {
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        CharacterInfoStrings strings;
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                CharacterController.LayoutId,
                CharacterController.RootId);
            var resolver = new DatStringResolver(_bindings.Assets.Dats);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    resolver.Resolve);
            strings = CharacterInfoStrings.FromDat(key => resolver.ResolveAll(
                0x23000001u, DatStringResolver.ComputeHash(key)));
        }
        if (rootInfo is null || layout is null) return;

        CharacterInformationUiController controller = CharacterController.Bind(
            layout,
            _bindings.Character.Provider.BuildSheet,
            _bindings.Assets.DefaultFont,
            () => CloseWindow(WindowNames.CharacterInformation),
            strings,
            _bindings.Character.Provider.SubscribeChanged);
        RegisterIndicatorDetailPanel(
            RetailPanelCatalog.CharacterInformation,
            WindowNames.CharacterInformation,
            rootInfo,
            layout.Root,
            controller);
    }

    private void MountLinkStatusPanel()
    {
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        LinkStatusStrings strings;
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                LinkStatusUiController.LayoutId,
                LinkStatusUiController.RootId);
            var resolver = new DatStringResolver(_bindings.Assets.Dats);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    resolver.Resolve);
            LinkStatusStrings fallback = LinkStatusStrings.English;
            strings = new LinkStatusStrings(
                resolver.Resolve(0x23000001u, 0x0D632F7Fu) ?? fallback.Description,
                resolver.Resolve(0x23000001u, 0x038FABF3u) ?? fallback.Legend,
                resolver.Resolve(0x23000001u, 0x05481084u) ?? fallback.DisconnectWarning,
                resolver.Resolve(0x23000001u, 0x0CADBA93u) ?? fallback.PacketLossPrefix,
                resolver.Resolve(0x23000001u, 0x0D6485F7u) ?? fallback.PingPrefix);
        }
        if (rootInfo is null || layout is null) return;

        LinkStatusUiController? controller = Layout.LinkStatusUiController.Bind(
            layout,
            _bindings.Indicators.LinkStatus,
            _bindings.Indicators.CurrentTime,
            _bindings.Indicators.RequestLinkStatusPing,
            strings,
            () => CloseWindow(WindowNames.LinkStatus));
        if (controller is null) return;
        LinkStatusUiController = controller;
        RegisterIndicatorDetailPanel(
            RetailPanelCatalog.LinkStatus,
            WindowNames.LinkStatus,
            rootInfo,
            layout.Root,
            controller);
    }

    private void MountVitaePanel()
    {
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        VitaeStrings strings;
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                VitaeUiController.LayoutId,
                VitaeUiController.RootId);
            var resolver = new DatStringResolver(_bindings.Assets.Dats);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    resolver.Resolve);
            VitaeStrings fallback = VitaeStrings.English;
            strings = new VitaeStrings(
                Resolve("ID_Vitae_Text_Full", 0, fallback.FullStrength),
                Resolve("ID_Vitae_Text_Vitae", 0, fallback.LostPrefix),
                Resolve("ID_Vitae_Text_Vitae", 1, fallback.LostSuffix),
                Resolve("ID_Vitae_Text_Skills", 0, fallback.SkillsPrefix),
                Resolve("ID_Vitae_Text_Skills", 1, fallback.SkillsSuffix),
                Resolve("ID_Vitae_Text_Experience", 0, fallback.RecoveryPrefix),
                Resolve("ID_Vitae_Text_Experience", 1, fallback.RecoverySuffix));

            string Resolve(string key, int token, string fallbackValue)
                => resolver.Resolve(
                    0x23000001u,
                    DatStringResolver.ComputeHash(key),
                    token) ?? fallbackValue;
        }
        if (rootInfo is null || layout is null) return;

        VitaeUiController? controller = Layout.VitaeUiController.Bind(
            layout,
            _bindings.Indicators.Spellbook,
            _bindings.Indicators.Objects,
            _bindings.Indicators.PlayerGuid,
            strings,
            () => CloseWindow(WindowNames.Vitae));
        if (controller is null) return;
        VitaeUiController = controller;
        RegisterIndicatorDetailPanel(
            RetailPanelCatalog.Vitae,
            WindowNames.Vitae,
            rootInfo,
            layout.Root,
            controller);
    }

    private void MountMiniGamePanel()
    {
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                MiniGameUiController.LayoutId,
                MiniGameUiController.RootId);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    new DatStringResolver(_bindings.Assets.Dats).Resolve);
        }
        if (rootInfo is null || layout is null) return;

        MiniGameUiController? controller = Layout.MiniGameUiController.Bind(
            layout,
            () => CloseWindow(WindowNames.MiniGame));
        if (controller is null) return;
        MiniGameUiController = controller;
        RegisterIndicatorDetailPanel(
            RetailPanelCatalog.MiniGame,
            WindowNames.MiniGame,
            rootInfo,
            layout.Root,
            controller);
    }

    private void RegisterIndicatorDetailPanel(
        uint panelId,
        string windowName,
        ElementInfo rootInfo,
        UiElement root,
        IRetainedPanelController? controller)
    {
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = windowName,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 18f,
                Top = 18f,
                Visible = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top
                    | AnchorEdges.Right | AnchorEdges.Bottom,
                ContentClickThrough = false,
                DrawChromeCenter = !AuthorsFullPanelCenter(rootInfo),
                Controller = controller,
            });
        _panelUi.RegisterMainPanel(
            panelId,
            windowName,
            handle,
            rootInfo.TryGetEffectiveBool(
                RetailPanelUiController.RestorePreviousPropertyId,
                out bool restorePrevious)
                && restorePrevious);
    }

    internal static bool AuthorsFullPanelCenter(ElementInfo rootInfo)
    {
        const float RetailTitleHeight = 25f;
        return Visit(rootInfo);

        bool Visit(ElementInfo info)
        {
            bool coversBody = info.Width >= rootInfo.Width
                && info.Height >= rootInfo.Height - RetailTitleHeight;
            if (coversBody && info.StateMedia.Values.Any(
                    media => media.File == RetailChromeSprites.CenterFill))
                return true;
            return info.Children.Any(Visit);
        }
    }

    private void MountIndicators()
    {
        ImportedLayout? layout = Import(IndicatorBarController.LayoutId);
        if (layout is null)
        {
            Console.WriteLine("[UI] indicator bar: LayoutDesc 0x21000071 not found.");
            return;
        }

        IndicatorRuntimeBindings b = _bindings.Indicators;
        IndicatorBarController? controller = Layout.IndicatorBarController.Bind(
            layout,
            new IndicatorBarBindings(
                b.Spellbook,
                b.Objects,
                b.PlayerGuid,
                b.Strength,
                b.LinkStatus,
                b.CurrentTime,
                panelId => _panelUi.TogglePanel(panelId),
                RequestEndCharacterSession));
        if (controller is null)
        {
            Console.WriteLine("[UI] indicator bar: one or more authored controls are missing.");
            return;
        }

        IndicatorBarController = controller;

        lock (_bindings.Assets.DatLock)
        {
            ElementInfo? infos = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats, IndicatorBarController.LayoutId);
            if (infos is not null)
                controller.AttachPressHighlights(infos, BuildSwallowedChild);
        }

        RetailWindowFrame.Mount(
            Host.Root,
            layout.Root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Indicators,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f,
                Top = 96f,
                Visible = true,
                Resizable = false,
                ResizeX = false,
                ResizeY = false,
                ConstrainDragToParent = true,
                ContentClickThrough = false,
                Controller = controller,
            });
        Console.WriteLine("[UI] retail seven-control indicator bar from LayoutDesc 0x21000071.");
    }

    private string ResolveEndCharacterSessionConfirmMessage()
    {
        const string fallback = "Are you sure you want to end this character session?";
        lock (_bindings.Assets.DatLock)
        {
            return new DatStringResolver(_bindings.Assets.Dats).Resolve(
                    0x23000001u,
                    DatStringResolver.ComputeHash("ID_Client_EndCharacterSessionConfirm"))
                ?? fallback;
        }
    }

    private void RequestEndCharacterSession()
    {
        ShowConfirmation(
            ResolveEndCharacterSessionConfirmMessage(),
            accepted =>
            {
                if (accepted)
                    EndCharacterSessionWithRetailGates();
            });
    }

    private void EndCharacterSessionWithRetailGates()
    {
        switch (_bindings.Options.IsGrounded())
        {
            case true:
                _bindings.Indicators.EndCharacterSession();
                break;
            case false:
                _bindings.Options.DisplaySystemMessage(
                    ClientTextRefusals.CantLogOffMidAir);
                break;
            case null:
                break;
        }
    }

    private void RequestExitToCharacterSelection()
    {
        ShowConfirmation(
            ResolveEndCharacterSessionConfirmMessage(),
            accepted =>
            {
                if (accepted)
                    EndCharacterSessionWithRetailGates();
            });
    }

    private void ApplyMouseTurningSettingsMacro()
    {
        CameraTurningSettings current = _bindings.Options.LoadCameraTurning();
        bool useMouseTurningCurrent = _bindings.Options.IsUseMouseTurningEnabled();
        MouseTurningSettingsMacro.Result result =
            MouseTurningSettingsMacro.Compute(current, useMouseTurningCurrent);

        _bindings.Options.SaveCameraTurning(result.Updated);

        if (result.UseMouseTurningChanged)
        {
            _bindings.Options.CommandBus().Publish(
                new SetSingleCharacterOptionRuntimeCmd(
                    (uint)CharacterOptionId.UseMouseTurning, true));
        }

        foreach (string line in result.ChatLines)
            _bindings.Options.DisplayMouseTurningMacroLine(line);

        OptionsPanelController?.ConfigPage.Apply();
    }

    private void MountOptionsPanel()
    {
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                Layout.OptionsPanelController.HostLayoutId,
                Layout.OptionsPanelController.SlotElementId);
            var resolver = new DatStringResolver(_bindings.Assets.Dats);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    resolver.Resolve);
        }
        if (rootInfo is null || layout is null)
        {
            Console.WriteLine("[UI] options panel: LayoutDesc 0x2100006E slot 0x1000018D not found.");
            return;
        }

        var callbacks = new Layout.OptionsPanelController.Callbacks(
            Toggle: () => ToggleWindow(WindowNames.Options),
            RequestExitToCharacterSelection: RequestExitToCharacterSelection,
            ExitGame: _bindings.Indicators.ExitGame,
            UseMouseTurningSettings: ApplyMouseTurningSettingsMacro,
            DisplaySystemMessage: _bindings.Options.DisplaySystemMessage,
            AfterApply: () => _bindings.Options.CommandBus().Publish(
                new SaveCharacterOptionsRuntimeCmd()),
            OpenConfigureKeyboard: () => ToggleWindow(WindowNames.KeyboardConfig));

        Layout.OptionsPanelController? controller =
            Layout.OptionsPanelController.Bind(layout, callbacks, _bindings.Assets.ResolveSprite);
        if (controller is null)
        {
            Console.WriteLine("[UI] options panel: required root did not build as UiTabPanel.");
            return;
        }

        OptionsPanelController = controller;

        lock (_bindings.Assets.DatLock)
        {
            var strings = new DatStringResolver(_bindings.Assets.Dats);
            bool bound = Layout.CharacterOptionsPageController.Bind(
                layout,
                controller.CharacterPage,
                templateResolver: (templateLayoutId, templateElementId) =>
                {
                    ElementInfo? info = LayoutImporter.ImportInfos(
                        _bindings.Assets.Dats, templateLayoutId, templateElementId);
                    return info is null
                        ? null
                        : LayoutImporter.Build(
                            info,
                            _bindings.Assets.ResolveSprite,
                            _bindings.Assets.DefaultFont,
                            _bindings.Assets.ResolveFont,
                            strings.Resolve,
                            templateLayoutId).Root;
                },
                resolveString: (tableId, stringId) => strings.Resolve(tableId, stringId),
                new Layout.CharacterOptionsPageController.Bindings(
                    CurrentValue: id => _bindings.Options.CurrentCharacterOption((uint)id),
                    SetOption: (id, value) => _bindings.Options.CommandBus().Publish(
                        new SetSingleCharacterOptionRuntimeCmd((uint)id, value))));
            if (!bound)
                Console.WriteLine("[UI] options panel: Character tab rows did not bind.");
        }

        lock (_bindings.Assets.DatLock)
        {
            var strings = new DatStringResolver(_bindings.Assets.Dats);

            if (!Layout.ChatOptionsDatDefaults.TryRead(
                    _bindings.Assets.Dats, out float datDefaultOpacity, out float datActiveOpacity))
            {
                Console.WriteLine(
                    "[UI] options panel: Chat tab opacity DAT defaults did not resolve "
                    + "(DID 0x78000001) — falling back to retail's ChatInterface base-"
                    + "constructor values (0.5/1.0).");
            }

            if (!Layout.ChatOptionsDatCaptions.TryRead(
                    _bindings.Assets.Dats, strings,
                    out Layout.ChatOptionsDatCaptions.Caption defaultOpacityCaption,
                    out Layout.ChatOptionsDatCaptions.Caption activeOpacityCaption))
            {
                Console.WriteLine(
                    "[UI] options panel: Chat tab opacity slider captions did not resolve "
                    + "(DID 0x78000000) — rows render with no caption rather than invented "
                    + "English.");
            }

            bool chatBound = Layout.ChatOptionsPageController.Bind(
                layout,
                controller.ChatPage,
                templateResolver: (templateLayoutId, templateElementId) =>
                {
                    ElementInfo? info = LayoutImporter.ImportInfos(
                        _bindings.Assets.Dats, templateLayoutId, templateElementId);
                    return info is null
                        ? null
                        : LayoutImporter.Build(
                            info,
                            _bindings.Assets.ResolveSprite,
                            _bindings.Assets.DefaultFont,
                            _bindings.Assets.ResolveFont,
                            strings.Resolve,
                            templateLayoutId).Root;
                },
                resolveString: (tableId, stringId) => strings.Resolve(tableId, stringId),
                new Layout.ChatOptionsPageController.Bindings(
                    CurrentDefaultOpacity: () => WindowOpacity.DefaultOpacity,
                    CurrentActiveOpacity: () => WindowOpacity.ActiveOpacity,
                    SetDefaultOpacity: WindowOpacity.SetDefaultOpacity,
                    SetActiveOpacity: WindowOpacity.SetActiveOpacity,
                    FlushOpacity: SaveChatOpacity,
                    DefaultOpacityDatDefault: datDefaultOpacity,
                    ActiveOpacityDatDefault: datActiveOpacity,
                    DefaultOpacityCaption: defaultOpacityCaption,
                    ActiveOpacityCaption: activeOpacityCaption,
                    CurrentFilter: windowId => _bindings.Chat.Windows.GetFilter(windowId),
                    SetFilter: (windowId, value) =>
                    {
                        _bindings.Chat.Windows.SetFilter(windowId, value);
                        SaveChatWindowFilters();
                    }));
            if (!chatBound)
                Console.WriteLine("[UI] options panel: Chat tab rows did not bind.");
        }

        lock (_bindings.Assets.DatLock)
        {
            var strings = new DatStringResolver(_bindings.Assets.Dats);

            bool configBound = Layout.ConfigOptionsPageController.Bind(
                layout,
                controller.ConfigPage,
                templateResolver: (templateLayoutId, templateElementId) =>
                {
                    ElementInfo? info = LayoutImporter.ImportInfos(
                        _bindings.Assets.Dats, templateLayoutId, templateElementId);
                    return info is null
                        ? null
                        : LayoutImporter.Build(
                            info,
                            _bindings.Assets.ResolveSprite,
                            _bindings.Assets.DefaultFont,
                            _bindings.Assets.ResolveFont,
                            strings.Resolve,
                            templateLayoutId).Root;
                },
                resolveString: (tableId, stringId) => strings.Resolve(tableId, stringId),
                new Layout.ConfigOptionsPageController.Bindings(
                    LoadDisplay: _bindings.Options.LoadDisplay,
                    SaveDisplay: _bindings.Options.SaveDisplay,
                    LoadAudio: _bindings.Options.LoadAudio,
                    SaveAudio: _bindings.Options.SaveAudio,
                    LoadCameraTurning: _bindings.Options.LoadCameraTurning,
                    SaveCameraTurning: _bindings.Options.SaveCameraTurning,
                    LoadChat: () => _bindings.Chat.Store?.LoadChat() ?? ChatSettings.Default,
                    SaveChat: chat => _bindings.Chat.Store?.SaveChat(chat))
                {
                    RenderPacks = _bindings.Options.LoadRenderPackChoices is { } load
                        ? new Layout.ConfigOptionsPageController.RenderPackBindings(load)
                        {
                            LoadRevision =
                                _bindings.Options.LoadRenderPackCatalogRevision,
                            LoadFailureNotice =
                                _bindings.Options.LoadRenderPackFailureNotice,
                        }
                        : null,
                },
                resolveSprite: _bindings.Assets.ResolveSprite,
                datFont: _bindings.Assets.DefaultFont,
                debugFont: _bindings.Assets.DebugFont,
                availableResolutions: Rendering.DisplayModeCatalog.WindowedResolutions,
                resolutionDefault: Rendering.DisplayModeCatalog.DesktopResolution);
            if (!configBound)
                Console.WriteLine("[UI] options panel: Config tab rows did not bind.");
        }

        controller.ActivateTabs();

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            controller.Root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Options,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 150f,
                Top = 80f,
                Visible = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top
                    | AnchorEdges.Right | AnchorEdges.Bottom,
                ContentClickThrough = false,
                DrawChromeCenter = !AuthorsFullPanelCenter(rootInfo),
                Controller = controller,
            });
        _panelUi.RegisterMainPanel(
            RetailPanelCatalog.Options,
            WindowNames.Options,
            handle,
            rootInfo.TryGetEffectiveBool(
                RetailPanelUiController.RestorePreviousPropertyId,
                out bool restorePrevious)
                && restorePrevious);
        Console.WriteLine("[UI] retail Options panel from LayoutDesc importer (0x2100006E slot 0x1000018D).");
    }

    private void MountKeyboardConfig()
    {
        KeyboardRuntimeBindings? keyboard = _bindings.Keyboard;
        if (keyboard is null || keyboard.Dispatcher is null)
        {
            Console.WriteLine(
                "[UI] keyboard config: no InputDispatcher wired — Configure Keyboard "
                + "screen will not open (button stays inert).");
            return;
        }
        InputDispatcher dispatcher = keyboard.Dispatcher;

        ElementInfo? info;
        ImportedLayout? layout;
        RetailActionMapSnapshot? snapshot;
        DatStringResolver strings;
        lock (_bindings.Assets.DatLock)
        {
            info = LayoutImporter.ImportInfos(_bindings.Assets.Dats, Layout.KeyboardConfigController.LayoutId);
            strings = new DatStringResolver(_bindings.Assets.Dats);
            layout = info is null
                ? null
                : LayoutImporter.Build(
                    info,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    strings.Resolve);
            snapshot = RetailActionMapReader.Read(_bindings.Assets.Dats);
        }
        if (layout is null || snapshot is null)
        {
            Console.WriteLine(
                "[UI] keyboard config: LayoutDesc 0x21000009 or the DAT ActionMap "
                + "singleton (0x26000000) not found — Configure Keyboard will not open.");
            return;
        }

        string unmappedPath = UnmappedKeyBindingsPath(keyboard.KeyBindingsFilePath);
        var unmapped = RetailUnmappedKeyBindings.LoadOrEmpty(unmappedPath);
        var keymaps = new RetailKeymapProfileStore(keyboard.KeyBindingsFilePath);

        string? ResolveKeymapTemplate(string key, string fileName)
        {
            var variables = new Dictionary<uint, string>
            {
                [DatStringResolver.ComputeHash("LABEL")] = fileName,
                [DatStringResolver.ComputeHash("KEYMAP")] = fileName,
                [DatStringResolver.ComputeHash("FILENAME")] = fileName,
                [DatStringResolver.ComputeHash("NAME")] = fileName,
                [DatStringResolver.ComputeHash("VALUE")] = fileName,
            };
            lock (_bindings.Assets.DatLock)
                return strings.ResolveTemplate(0x23000004u, key, variables);
        }

        void ShowKeymapMessage(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message) && DialogFactory is not null)
                DialogFactory.MakeMessage(message, queueKey: 0x10000001u, priority: true);
        }

        void SaveMirrors()
        {
            dispatcher.Bindings.SaveToFile(keyboard.KeyBindingsFilePath);
            unmapped.SaveToFile(unmappedPath);
        }

        void HandleSaveResult(
            RetailKeymapSaveResult result,
            string requestedName,
            Action onSaved)
        {
            switch (result.Status)
            {
                case RetailKeymapSaveStatus.Saved:
                    try
                    {
                        SaveMirrors();
                    }
                    catch (Exception failure)
                    {
                        Console.WriteLine($"keyboard config: JSON mirror save failed: {failure.Message}");
                    }
                    onSaved();
                    return;

                case RetailKeymapSaveStatus.Exists:
                    string? overwrite = ResolveKeymapTemplate(
                        "ID_KeyMapOverwriteKeymap_Label", result.FileName);
                    if (overwrite is null || DialogFactory is null) return;
                    DialogFactory.MakeConfirmation(
                        overwrite,
                        data =>
                        {
                            if (!data.GetBoolean(RetailDialogProperty.ConfirmationResult)) return;
                            HandleSaveResult(
                                keymaps.Save(requestedName, dispatcher.Bindings, overwrite: true),
                                requestedName,
                                onSaved);
                        },
                        queueKey: 0x10000001u,
                        priority: true);
                    return;

                case RetailKeymapSaveStatus.ReadOnly:
                    ShowKeymapMessage(ResolveKeymapTemplate(
                        "ID_KeyMapCantOverwriteReadOnlyKeymap_Label", result.FileName));
                    return;

                default:
                    Console.WriteLine(
                        $"keyboard config: keymap save failed ({result.Status}): {result.Error}");
                    return;
            }
        }

        Layout.KeyboardConfigController? controller = Layout.KeyboardConfigController.Bind(
            layout,
            snapshot,
            templateResolver: (templateLayoutId, templateElementId) =>
            {
                lock (_bindings.Assets.DatLock)
                {
                    ElementInfo? templateInfo = LayoutImporter.ImportInfos(
                        _bindings.Assets.Dats, templateLayoutId, templateElementId);
                    return templateInfo is null
                        ? null
                        : LayoutImporter.Build(
                            templateInfo,
                            _bindings.Assets.ResolveSprite,
                            _bindings.Assets.DefaultFont,
                            _bindings.Assets.ResolveFont,
                            strings.Resolve,
                            templateLayoutId).Root;
                }
            },
            resolveString: (tableId, stringId) => strings.Resolve(tableId, stringId),
            new Layout.KeyboardConfigController.Bindings(
                CurrentForAction: action => dispatcher.Bindings.ForAction(action).ToArray(),
                SetForAction: (action, newBindings) =>
                {
                    KeyBindings updated = CloneWithout(dispatcher.Bindings, action);
                    foreach (Binding b in newBindings)
                        updated.Add(b);
                    dispatcher.SetBindings(updated);
                },
                CurrentForUnmapped: key => unmapped.Get(key.InputMapId, key.ActionId),
                SetForUnmapped: (key, chords) => unmapped.Set(key.InputMapId, key.ActionId, chords),
                BeginCapture: onResult => dispatcher.BeginCapture(
                    chord => onResult(chord == default ? null : chord)),
                Save: () =>
                {
                    try
                    {
                        HandleSaveResult(
                            keymaps.SaveActive(dispatcher.Bindings),
                            keymaps.CurrentFileName,
                            static () => { });
                    }
                    catch (Exception failure)
                    {
                        Console.WriteLine($"keyboard config: save failed: {failure.Message}");
                    }
                },
                Toggle: () => ToggleWindow(WindowNames.KeyboardConfig),
                ResolveTemplate: (key, variables) =>
                {
                    lock (_bindings.Assets.DatLock)
                    {
                        return strings.ResolveTemplate(0x23000004u, key, variables);
                    }
                },
                ShowMessage: message =>
                {
                    if (DialogFactory is null) return;
                    DialogFactory.MakeMessage(
                        message,
                        queueKey: 0x10000001u,
                        priority: true);
                },
                ConfirmOverwrite: (message, onResult) =>
                {
                    if (DialogFactory is null) { onResult(false); return; }
                    DialogFactory.MakeConfirmation(
                        message,
                        data => onResult(data.GetBoolean(RetailDialogProperty.ConfirmationResult)),
                        queueKey: 0x10000001u,
                        priority: true);
                },
                OpenCaptureInstructions: actionLabel =>
                {
                    if (DialogFactory is null) return 0u;
                    string? text = strings.ResolveTemplate(
                        0x23000004u,
                        "ID_ActionKeyMap_MapInstructions",
                        new Dictionary<uint, string>
                        {
                            [DatStringResolver.ComputeHash("ACTION")] = actionLabel,
                        });
                    if (text is null) return 0u; // no invented English
                    try
                    {
                        return DialogFactory.MakeWait(
                            text,
                            queueKey: 0x10000001u,
                            priority: true);
                    }
                    catch (Exception failure)
                    {
                        Console.WriteLine(
                            "[UI] keyboard config: capture-instruction dialog failed to "
                            + $"build — capture refused. {failure.Message}");
                        return 0u;
                    }
                },
                CloseCaptureInstructions: context =>
                    DialogFactory?.CloseDialog(context),
                CurrentKeymapFilename: () => keymaps.CurrentFileName,
                OpenLoadKeymap: onLoaded =>
                {
                    if (DialogFactory is null) return;
                    IReadOnlyList<string> files = keymaps.ListFiles();
                    int selected = files
                        .Select(static (name, index) => (name, index))
                        .FirstOrDefault(
                            pair => string.Equals(
                                pair.name,
                                keymaps.CurrentFileName,
                                StringComparison.OrdinalIgnoreCase),
                            (name: string.Empty, index: 0)).index;
                    DialogFactory.MakeConfirmationMenu(
                        files,
                        selected,
                        data =>
                        {
                            int choice = data.GetInt32(RetailDialogProperty.MenuSelection, -1);
                            if (choice < 0 || choice >= files.Count) return;
                            if (!keymaps.TryLoad(
                                    files[choice],
                                    dispatcher.Bindings,
                                    out KeyBindings loaded,
                                    out string? error))
                            {
                                Console.WriteLine($"keyboard config: keymap load failed: {error}");
                                return;
                            }
                            dispatcher.SetBindings(loaded);
                            try { SaveMirrors(); }
                            catch (Exception failure)
                            {
                                Console.WriteLine(
                                    $"keyboard config: loaded profile JSON mirror failed: {failure.Message}");
                            }
                            onLoaded();
                        },
                        queueKey: 0x10000001u);
                },
                OpenSaveKeymap: onSaved =>
                {
                    if (DialogFactory is null) return;
                    DialogFactory.MakeConfirmationTextInput(
                        string.Empty,
                        data =>
                        {
                            string name = data.GetString(RetailDialogProperty.TextInputResult)
                                ?? string.Empty;
                            if (name.Length == 0) return;
                            HandleSaveResult(
                                keymaps.Save(name, dispatcher.Bindings, overwrite: false),
                                name,
                                onSaved);
                        },
                        queueKey: 0x10000001u);
                }),
            resolveTemplateFont: (templateLayoutId, templateElementId) =>
            {
                lock (_bindings.Assets.DatLock)
                {
                    ElementInfo? templateInfo = LayoutImporter.ImportInfos(
                        _bindings.Assets.Dats, templateLayoutId, templateElementId);
                    return templateInfo is null || templateInfo.FontDid == 0u
                        ? null
                        : _bindings.Assets.ResolveFont(templateInfo.FontDid);
                }
            });

        if (controller is null)
        {
            Console.WriteLine("[UI] keyboard config: required window root did not build.");
            return;
        }

        KeyboardConfigController = controller;
        UiElement root = layout.Root;
        RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.KeyboardConfig,
                Chrome = RetailWindowChrome.Imported,
                Left = Math.Max(0f, (Host.Root.Width - root.Width) * 0.5f),
                Top = Math.Max(0f, (Host.Root.Height - root.Height) * 0.5f),
                Visible = false,
                DatConstraintSource = info,
                ContentClickThrough = false,
            });
        Console.WriteLine("[UI] retail Configure Keyboard screen from gmKeyboardUI LayoutDesc 0x21000009.");
    }

    private string PluginShelfHiddenMessage()
    {
        InputDispatcher? dispatcher = _bindings.Keyboard?.Dispatcher;
        Binding? bound = dispatcher?.Bindings
            .ForAction(AcDream.UI.Abstractions.Input.InputAction.TogglePluginManager)
            .Cast<Binding?>()
            .FirstOrDefault();
        if (bound is not { } binding)
        {
            return "Plugin shelf hidden. Bind Toggle Plugin Manager in "
                + "Configure Keyboard to show it again.";
        }

        var strings = new DatStringResolver(_bindings.Assets.Dats);
        string chordText = new Layout.RetailKeyNames((tableId, stringId) =>
            strings.Resolve(tableId, stringId)).Describe(binding.Chord);
        return $"Plugin shelf hidden. Press {chordText} to show it again.";
    }

    private static string UnmappedKeyBindingsPath(string keyBindingsFilePath)
    {
        string? dir = System.IO.Path.GetDirectoryName(keyBindingsFilePath);
        string name = System.IO.Path.GetFileNameWithoutExtension(keyBindingsFilePath);
        string ext = System.IO.Path.GetExtension(keyBindingsFilePath);
        string sibling = $"{name}-unmapped{ext}";
        return string.IsNullOrEmpty(dir) ? sibling : System.IO.Path.Combine(dir, sibling);
    }

    private static KeyBindings CloneWithout(KeyBindings source, InputAction action)
    {
        var result = new KeyBindings();
        foreach (Binding b in source.All)
            if (b.Action != action) result.Add(b);
        return result;
    }

    public Layout.KeyboardConfigController? KeyboardConfigController { get; private set; }

    private void MountJumpPowerbar()
    {
        ElementInfo? info;
        ImportedLayout? layout;
        lock (_bindings.Assets.DatLock)
        {
            info = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats, JumpPowerbarController.LayoutId);
            var powerbarStrings = new DatStringResolver(_bindings.Assets.Dats);
            layout = info is null ? null : LayoutImporter.Build(
                info,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont,
                powerbarStrings.Resolve);
        }
        if (layout is null)
        {
            Console.WriteLine("[UI] jump powerbar: LayoutDesc 0x21000072 not found.");
            return;
        }

        JumpPowerbarController? controller = Layout.JumpPowerbarController.Bind(
            layout,
            _bindings.JumpPowerbar.Snapshot,
            visible =>
            {
                if (visible) Host.ShowWindow(WindowNames.JumpPowerbar);
                else Host.HideWindow(WindowNames.JumpPowerbar);
            });
        if (controller is null)
        {
            Console.WriteLine("[UI] jump powerbar: required JumpMode meter missing in LayoutDesc 0x21000072.");
            return;
        }

        JumpPowerbarController = controller;
        UiElement root = layout.Root;
        RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.JumpPowerbar,
                Chrome = RetailWindowChrome.Imported,
                Left = Math.Max(0f, (Host.Root.Width - root.Width) * 0.5f),
                Top = Math.Max(0f, Host.Root.Height - root.Height - 105f),
                Visible = false,
                DatConstraintSource = info,
                Resizable = true,
                ResizeX = true,
                ResizeY = true,
                ConstrainDragToParent = true,
                ContentClickThrough = false,
                Controller = controller,
            });
        controller.SyncVisibility();
        Console.WriteLine("[UI] retail jump bar from gmFloatyPowerBarUI LayoutDesc 0x21000072.");
    }

    private void MountSocialPanel()
    {
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                Layout.SocialPanelController.HostLayoutId,
                Layout.SocialPanelController.SlotElementId);
            var resolver = new DatStringResolver(_bindings.Assets.Dats);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    resolver.Resolve);
        }
        if (rootInfo is null || layout is null)
        {
            Console.WriteLine("[UI] social panel: LayoutDesc 0x2100006E slot 0x1000018F not found.");
            return;
        }

        var rowTemplates = new Layout.RowTemplateResolver(
            (layoutId, elementId) => LayoutImporter.ImportInfos(
                _bindings.Assets.Dats, layoutId, elementId),
            info =>
            {
                var strings = new DatStringResolver(_bindings.Assets.Dats);
                return LayoutImporter.Build(
                    info,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    strings.Resolve).Root;
            });
        UiElement? TemplateResolver(uint templateLayoutId, uint templateElementId)
        {
            lock (_bindings.Assets.DatLock)
                return rowTemplates.Resolve(templateLayoutId, templateElementId);
        }

        var fellowshipStrings = new DatStringResolver(_bindings.Assets.Dats);

        var callbacks = new Layout.SocialPanelController.Callbacks(
            Toggle: () => ToggleWindow(WindowNames.SocialPanel),
            Fellowship: new Layout.SocialFellowshipPageController.Bindings(
                Snapshot: _bindings.Social.FellowshipSnapshot,
                Members: _bindings.Social.FellowshipMembers,
                TemplateResolver: TemplateResolver,
                Create: _bindings.Social.FellowshipCreate,
                Recruit: _bindings.Social.FellowshipRecruit,
                Dismiss: _bindings.Social.FellowshipDismiss,
                Quit: _bindings.Social.FellowshipQuit,
                AssignLeader: _bindings.Social.FellowshipAssignLeader,
                SetOpen: _bindings.Social.FellowshipSetOpen,
                SetPanelOpen: _bindings.Social.FellowshipSetPanelOpen,
                Selection: _bindings.Social.Selection,
                LocalPlayerGuid: _bindings.Social.LocalPlayerGuid,
                CurrentCharacterOption: id => _bindings.Options.CurrentCharacterOption((uint)id),
                SetCharacterOption: (id, value) => _bindings.Options.CommandBus().Publish(
                    new SetSingleCharacterOptionRuntimeCmd((uint)id, value)),
                ResolveString: (tableId, stringId) => fellowshipStrings.Resolve(tableId, stringId)),
            Allegiance: new Layout.SocialAllegiancePageController.Bindings(
                Snapshot: _bindings.Social.AllegianceSnapshot,
                Monarch: _bindings.Social.AllegianceMonarch,
                Patron: _bindings.Social.AllegiancePatron,
                Member: _bindings.Social.AllegianceMember,
                Vassals: _bindings.Social.AllegianceVassals,
                Swear: _bindings.Social.AllegianceSwear,
                Break: _bindings.Social.AllegianceBreak,
                Kick: _bindings.Social.AllegianceKick,
                SetUpdateSubscription: _bindings.Social.AllegianceSetUpdateSubscription,
                Selection: _bindings.Social.Selection,
                LocalPlayerGuid: _bindings.Social.LocalPlayerGuid,
                CurrentCharacterOption: id => _bindings.Options.CurrentCharacterOption((uint)id),
                SetCharacterOption: (id, value) => _bindings.Options.CommandBus().Publish(
                    new SetSingleCharacterOptionRuntimeCmd((uint)id, value)),
                TemplateResolver: TemplateResolver,
                ResolveString: (tableId, stringId) => fellowshipStrings.Resolve(tableId, stringId),
                ResolveWorldObjectName: guid => _bindings.Inventory.Objects.Get(guid)?.GetAppropriateName(),
                ShowConfirmation: (message, completed) => ShowConfirmation(message, completed),
                ResolvePlayerTemplate: (key, playerName) =>
                {
                    lock (_bindings.Assets.DatLock)
                    {
                        return fellowshipStrings.ResolveTemplate(
                            0x23000001u,
                            key,
                            new Dictionary<uint, string>
                            {
                                [Layout.DatStringResolver.PlayerVariable] = playerName,
                            });
                    }
                }),
            Friends: _bindings.Social.Friends,
            Squelch: _bindings.Social.Squelch,
            TemplateResolver: TemplateResolver,
            FriendsActions: new Layout.SocialFriendsPageController.Actions(
                AddFriend: name => _bindings.Options.CommandBus().Publish(
                    new AddFriendRuntimeCmd(name)),
                RemoveFriend: guid => _bindings.Options.CommandBus().Publish(
                    new RemoveFriendRuntimeCmd(guid)),
                CurrentAppearOffline: () => _bindings.Options.CurrentCharacterOption(
                    (uint)CharacterOptionId.AppearOffline),
                SetAppearOffline: value => _bindings.Options.CommandBus().Publish(
                    new SetSingleCharacterOptionRuntimeCmd(
                        (uint)CharacterOptionId.AppearOffline, value))),
            SquelchActions: new Layout.SocialSquelchPageController.Actions(
                SquelchCharacter: name => _bindings.Options.CommandBus().Publish(
                    new ModifyCharacterSquelchRuntimeCmd(true, 0u, name, 1u)),
                SquelchAccount: name => _bindings.Options.CommandBus().Publish(
                    new ModifyAccountSquelchRuntimeCmd(true, name)),
                RemoveCharacterSquelch: (guid, name) => _bindings.Options.CommandBus().Publish(
                    new ModifyCharacterSquelchRuntimeCmd(false, guid, name, 1u)),
                RemoveAccountSquelch: name => _bindings.Options.CommandBus().Publish(
                    new ModifyAccountSquelchRuntimeCmd(false, name))));

        Layout.SocialPanelController? controller;
        lock (_bindings.Assets.DatLock)
            controller = Layout.SocialPanelController.Bind(layout, callbacks);
        if (controller is null)
        {
            Console.WriteLine("[UI] social panel: required root did not build as UiTabPanel.");
            return;
        }

        controller.ActivateTabs();
        SocialPanelController = controller;

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            controller.Root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.SocialPanel,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 200f,
                Top = 140f,
                Visible = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top
                    | AnchorEdges.Right | AnchorEdges.Bottom,
                ContentClickThrough = false,
                DrawChromeCenter = !AuthorsFullPanelCenter(rootInfo),
                Controller = controller,
            });
        _panelUi.RegisterMainPanel(
            RetailPanelCatalog.SocialPanel,
            WindowNames.SocialPanel,
            handle,
            rootInfo.TryGetEffectiveBool(
                RetailPanelUiController.RestorePreviousPropertyId,
                out bool restorePrevious)
                && restorePrevious);
        Console.WriteLine("[UI] retail social panel from LayoutDesc importer (0x2100006E slot 0x1000018F).");
    }

    private void MountJournalPanel()
    {
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                Layout.JournalPanelController.HostLayoutId,
                Layout.JournalPanelController.SlotElementId);
            var resolver = new DatStringResolver(_bindings.Assets.Dats);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    resolver.Resolve);
        }
        if (rootInfo is null || layout is null)
        {
            Console.WriteLine(
                "[UI] journal panel: LayoutDesc 0x2100006E slot 0x10000559 not found.");
            return;
        }

        var rowTemplates = new Layout.RowTemplateResolver(
            (layoutId, elementId) => LayoutImporter.ImportInfos(
                _bindings.Assets.Dats, layoutId, elementId),
            info =>
            {
                var strings = new DatStringResolver(_bindings.Assets.Dats);
                return LayoutImporter.Build(
                    info,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    strings.Resolve).Root;
            });

        var callbacks = new Layout.JournalPanelController.Callbacks(
            Toggle: () => ToggleWindow(WindowNames.Journal),
            Contracts: new Layout.JournalContractsPageController.Bindings(
                Contracts: _bindings.Quests.Contracts,
                Catalog: _bindings.Quests.Catalog,
                Now: () => DateTime.UtcNow,
                TemplateResolver: (templateLayoutId, templateElementId) =>
                {
                    lock (_bindings.Assets.DatLock)
                        return rowTemplates.Resolve(templateLayoutId, templateElementId);
                },
                Abandon: _bindings.Quests.AbandonContract),
            Notes: new Layout.JournalNotesPageController.Bindings(
                Journal: _bindings.Quests.Journal,
                Commands: _bindings.Quests.JournalCommands,
                PlayerCell: _bindings.Quests.PlayerCell,
                Now: () => DateTime.UtcNow),
            SaveJournal: SaveJournal,
            PageList: openPage => new Layout.JournalPageListController.Bindings(
                Journal: _bindings.Quests.Journal,
                Commands: _bindings.Quests.JournalCommands,
                OpenPage: openPage,
                TemplateResolver: (templateLayoutId, templateElementId) =>
                {
                    lock (_bindings.Assets.DatLock)
                        return rowTemplates.Resolve(templateLayoutId, templateElementId);
                },
                Now: () => DateTime.UtcNow));

        Layout.JournalPanelController? controller;
        lock (_bindings.Assets.DatLock)
            controller = Layout.JournalPanelController.Bind(layout, callbacks);
        if (controller is null)
        {
            Console.WriteLine("[UI] journal panel: required root did not build as UiTabPanel.");
            return;
        }

        controller.ActivateTabs();
        JournalPanelController = controller;

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            controller.Root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Journal,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 230f,
                Top = 160f,
                Visible = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top
                    | AnchorEdges.Right | AnchorEdges.Bottom,
                ContentClickThrough = false,
                DrawChromeCenter = !AuthorsFullPanelCenter(rootInfo),
                Controller = controller,
            });
        _panelUi.RegisterMainPanel(
            RetailPanelCatalog.Journal,
            WindowNames.Journal,
            handle,
            rootInfo.TryGetEffectiveBool(
                RetailPanelUiController.RestorePreviousPropertyId,
                out bool restorePrevious)
                && restorePrevious);
        Console.WriteLine(
            "[UI] retail journal panel from LayoutDesc importer (0x2100006E slot 0x10000559).");
    }

    private void MountMapHousePanel()
    {
        ElementInfo? rootInfo;
        ImportedLayout? layout;
        var strings = new DatStringResolver(_bindings.Assets.Dats);
        lock (_bindings.Assets.DatLock)
        {
            rootInfo = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats,
                Layout.MapHousePanelController.HostLayoutId,
                Layout.MapHousePanelController.SlotElementId);
            layout = rootInfo is null
                ? null
                : LayoutImporter.Build(
                    rootInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    strings.Resolve);
        }
        if (rootInfo is null || layout is null)
        {
            Console.WriteLine("[UI] Map/House panel: LayoutDesc 0x2100006E slot 0x1000018C not found.");
            return;
        }

        var hotspotTemplate = new Layout.RowTemplateResolver(
            (templateLayoutId, templateElementId) => LayoutImporter.ImportInfos(
                _bindings.Assets.Dats, templateLayoutId, templateElementId),
            info => LayoutImporter.Build(
                info,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont).Root);
        UiElement? ResolveHotspotTemplate(uint templateLayoutId, uint templateElementId)
        {
            lock (_bindings.Assets.DatLock)
                return hotspotTemplate.Resolve(templateLayoutId, templateElementId);
        }
        ElementInfo? ResolveHotspotTemplateInfo(uint templateLayoutId, uint templateElementId)
        {
            lock (_bindings.Assets.DatLock)
                return hotspotTemplate.ResolveInfo(templateLayoutId, templateElementId);
        }

        UiElement? BuildSwallowedIcon(ElementInfo iconInfo)
        {
            lock (_bindings.Assets.DatLock)
                return LayoutImporter.Build(
                    iconInfo,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont).Root;
        }

        MapHouseRuntimeBindings mh = _bindings.MapHouse;
        var callbacks = new Layout.MapHousePanelController.Callbacks(
            Toggle: () => ToggleWindow(WindowNames.MapHouse),
            Map: new Layout.MapPageController.Bindings(
                CurrentCalendar: mh.CurrentCalendar,
                PlayerCellId: mh.PlayerCellId,
                HousePosition: mh.HousePosition ?? (static () => null),
                TemplateResolver: ResolveHotspotTemplate,
                IconBuilder: BuildSwallowedIcon,
                TemplateInfoResolver: ResolveHotspotTemplateInfo),
            House: new Layout.HousePageController.Bindings(
                Lines: mh.HouseLines ?? (static () => Array.Empty<string>()),
                OnShown: mh.HouseShown,
                TemplateResolver: ResolveHotspotTemplate,
                PanelLines: mh.HousePanelLines));

        Layout.MapHousePanelController? controller;
        lock (_bindings.Assets.DatLock)
            controller = Layout.MapHousePanelController.Bind(rootInfo, layout, callbacks);
        if (controller is null)
        {
            Console.WriteLine("[UI] Map/House panel: required root did not build as UiTabPanel.");
            return;
        }

        controller.ActivateTabs();
        MapHousePanelController = controller;

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            controller.Root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.MapHouse,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 240f,
                Top = 160f,
                Visible = false,
                ResizeX = false,
                ResizeY = false,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top
                    | AnchorEdges.Right | AnchorEdges.Bottom,
                ContentClickThrough = false,
                DrawChromeCenter = !AuthorsFullPanelCenter(rootInfo),
                Controller = controller,
            });
        _panelUi.RegisterMainPanel(
            RetailPanelCatalog.MapHouse,
            WindowNames.MapHouse,
            handle,
            rootInfo.TryGetEffectiveBool(
                RetailPanelUiController.RestorePreviousPropertyId,
                out bool restorePrevious)
                && restorePrevious);
        Console.WriteLine("[UI] retail Map/House panel from LayoutDesc importer (0x2100006E slot 0x1000018C).");
    }

    private void MountDialogFactory()
    {
        if (DialogFactory is not null)
            return;

        uint layoutId;
        try
        {
            lock (_bindings.Assets.DatLock)
            {
                layoutId = RetailDataIdResolver.Resolve(
                    _bindings.Assets.Dats,
                    2u,
                    5u);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[UI] retail dialog catalog will retry after resource "
                + $"recovery: {error.Message}");
            return;
        }

        if (layoutId == 0u)
        {
            Console.WriteLine("[UI] retail dialog catalog could not be resolved.");
            return;
        }

        ImportedLayout? CreateLayout(RetailDialogType type)
        {
            uint rootElementId = RetailDialogFactory.RootElementId(type);
            if (rootElementId == 0u)
                return null;
            lock (_bindings.Assets.DatLock)
            {
                return LayoutImporter.Import(
                    _bindings.Assets.Dats,
                    layoutId,
                    rootElementId,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont);
            }
        }

        DialogFactory = new RetailDialogFactory(Host.Root, CreateLayout);
        var confirmationStrings = new DatStringResolver(_bindings.Assets.Dats);
        string? ComposeConfirmation(uint type, string bareName)
        {
            string? key = type switch
            {
                1u => "ID_Allegiance_AcceptSwearConfirmation",
                4u => "ID_Fellowship_FellowshipRequest",
                _ => null,
            };
            if (key is null)
                return null;
            lock (_bindings.Assets.DatLock)
            {
                return confirmationStrings.ResolveTemplate(
                    0x23000001u,
                    key,
                    new Dictionary<uint, string>
                    {
                        [DatStringResolver.PlayerVariable] = bareName,
                    });
            }
        }
        _gameplayConfirmationController = new GameplayConfirmationController(
            DialogFactory,
            _bindings.Confirmations.SendResponse,
            ComposeConfirmation);
        _itemConfirmationController = new RetailItemConfirmationController(
            DialogFactory,
            ItemInteraction);
        _skillTrainingConfirmationController =
            new RetailSkillTrainingConfirmationController(DialogFactory);
        Console.WriteLine(
            $"[UI] retail DialogFactory from LayoutDesc 0x{layoutId:X8}; confirmation root 0x15.");
    }

    private void MountTooltipPresenter()
    {
        if (TooltipPresenter is not null)
            return;

        ImportedLayout? CreateTooltipLayout(uint layoutDid, uint rootElementId)
        {
            lock (_bindings.Assets.DatLock)
            {
                return LayoutImporter.Import(
                    _bindings.Assets.Dats,
                    layoutDid,
                    rootElementId,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont);
            }
        }

        TooltipPresenter = new RetailTooltipPresenter(Host.Root, CreateTooltipLayout)
        {
            WorldHoverGuidProvider = () => _bindings.WorldTooltip.HoverGuidAtCursor(),
            WorldHoverNameResolver = _bindings.WorldTooltip.ResolveName,
            WorldTooltipsEnabled = () => _bindings.WorldTooltip.Enabled(),
        };

        if (_bindings.Chat.Store is { } store)
        {
            MiscSettings misc = store.LoadMisc();
            TooltipPresenter.Enabled = misc.TooltipEnable;
            Host.Root.TooltipDelayMs = (int)(misc.TooltipDelaySeconds * 1000f);
        }
    }

    private UiShortcutDigitGraphics LoadShortcutDigitGraphics()
    {
        if (_shortcutDigitGraphics is not null)
            return _shortcutDigitGraphics;

        uint[]? regular = null, ghosted = null, empty = null;
        lock (_bindings.Assets.DatLock)
        {
            var layout = _bindings.Assets.Dats.Get<DatReaderWriter.DBObjs.LayoutDesc>(0x21000037u);
            if (layout is not null
                && layout.Elements.TryGetValue(0x10000346u, out var composite)
                && composite.Children.TryGetValue(0x1000034Au, out var number)
                && number.StateDesc?.Properties is { } props)
            {
                regular = ReadDataIds(props, 0x10000042u);
                ghosted = ReadDataIds(props, 0x10000043u);
            }
            if (layout is not null
                && layout.Elements.TryGetValue(0x10000341u, out var emptyComposite)
                && emptyComposite.Children.TryGetValue(0x1000034Au, out var emptyNumber)
                && emptyNumber.StateDesc?.Properties is { } emptyProps)
                empty = ReadDataIds(emptyProps, 0x1000005Eu);
        }

        regular ??=
        [
            0x0600109Eu, 0x0600109Fu, 0x060010A0u, 0x060010A1u, 0x060010A2u,
            0x060010A3u, 0x060010A4u, 0x060010A5u, 0x060010A6u,
        ];
        ghosted ??=
        [
            0x06001ACCu, 0x06001ACDu, 0x06001ACEu, 0x06001ACFu, 0x06001AD0u,
            0x06001AD1u, 0x06001AD2u, 0x06001AD3u, 0x06001AD4u,
        ];
        _shortcutDigitGraphics = new UiShortcutDigitGraphics(
            regular, ghosted, empty);
        Console.WriteLine(
            $"[UI] shared shortcut digits ready: regular={regular.Length}, " +
            $"ghosted={ghosted.Length}, empty={empty?.Length ?? 0}.");
        return _shortcutDigitGraphics;
    }

    private static uint[]? ReadDataIds(
        IReadOnlyDictionary<uint, DatReaderWriter.Types.BaseProperty> properties,
        uint key)
    {
        if (!properties.TryGetValue(key, out var raw)
            || raw is not DatReaderWriter.Types.ArrayBaseProperty array)
            return null;
        var result = new uint[array.Value.Count];
        for (int i = 0; i < array.Value.Count; i++)
            if (array.Value[i] is DatReaderWriter.Types.DataIdBaseProperty dataId)
                result[i] = dataId.Value;
        return result;
    }

    private static void ConfigureToolbarCollapse(
        ImportedLayout layout,
        UiCollapsibleFrame frame,
        float contentHeight)
    {
        uint[] ids =
        [
            0x100006B6u, 0x100006B7u, 0x100006B8u, 0x100006B9u,
            0x100006BAu, 0x100006BBu, 0x100006BCu, 0x100006BDu,
            0x100006BEu, 0x100006BFu, 0x100006C0u,
        ];
        var row = new List<UiElement>();
        float top = float.MaxValue;
        foreach (uint id in ids)
            if (layout.FindElement(id) is { } element)
            {
                row.Add(element);
                top = Math.Min(top, element.Top);
            }
        if (row.Count == 0) return;

        const int border = RetailChromeSprites.Border;
        frame.CollapsedHeight = top + 2 * border;
        frame.ExpandedHeight = contentHeight + 2 * border;
        frame.SecondRow = row;
        frame.Resizable = true;
        frame.ResizableEdges = ResizeEdges.Bottom;
        frame.MinHeight = frame.CollapsedHeight;
        frame.MaxHeight = frame.ExpandedHeight;
    }

    private void MountCharacter()
    {
        ImportedLayout? layout = Import(0x2100002Eu);
        if (layout is null)
        {
            Console.WriteLine("[UI] character: LayoutDesc 0x2100002E not found.");
            return;
        }
        CharacterSheetProvider provider = _bindings.Character.Provider;
        CharacterSheet currentSheet = provider.BuildSheet();
        uint IconDidResolve(uint enumValue, uint category)
        {
            lock (_bindings.Assets.DatLock)
                return RetailDataIdResolver.Resolve(_bindings.Assets.Dats, enumValue, category);
        }
        _characterStatBinding = CharacterStatController.Bind(
            layout,
            () => currentSheet,
            _bindings.Assets.DefaultFont,
            _bindings.Assets.ResolveFont(0x40000001u) ?? _bindings.Assets.DefaultFont,
            _bindings.Assets.ResolveSprite,
            (request, completed) => HandleCharacterRaise(provider, request, completed),
            () => CloseWindow(WindowNames.Character),
            IconDidResolve);
        _characterSheetSubscription = provider.SubscribeChanged(() =>
        {
            currentSheet = provider.BuildSheet();
            _characterStatBinding?.Refresh();
        });

        var titleRowTemplates = new Layout.RowTemplateResolver(
            (layoutId, elementId) => LayoutImporter.ImportInfos(
                _bindings.Assets.Dats, layoutId, elementId),
            info =>
            {
                var strings = new DatStringResolver(_bindings.Assets.Dats);
                return LayoutImporter.Build(
                    info,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont,
                    strings.Resolve).Root;
            });
        UiElement? TitleTemplateResolver(uint templateLayoutId, uint templateElementId)
        {
            lock (_bindings.Assets.DatLock)
                return titleRowTemplates.Resolve(templateLayoutId, templateElementId);
        }
        string? TitleResolver(uint titleId)
        {
            lock (_bindings.Assets.DatLock)
                return _bindings.Character.TitleResolver.Resolve(titleId);
        }
        _characterTitlesController = Layout.CharacterTitlesController.Bind(
            layout.Root,
            _bindings.Character.Titles,
            TitleResolver,
            TitleTemplateResolver,
            _bindings.Character.SendSetTitle);

        ElementInfo? hostConstraint;
        lock (_bindings.Assets.DatLock)
            hostConstraint = LayoutImporter.ImportInfos(
                _bindings.Assets.Dats, 0x2100006Eu, 0x100005FEu);

        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            layout.Root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Character,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 540f,
                Top = 18f,
                ContentHeight = 362f,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainResizeToParent = true,
                DatConstraintSource = hostConstraint,
                DatConstraintSourceIsOuterFrame = true,
                Visible = false,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
                ContentClickThrough = false,
            });
        _panelUi.RegisterMainPanel(
            RetailPanelCatalog.Character,
            WindowNames.Character,
            handle);
        Console.WriteLine("[UI] retail character window from LayoutDesc importer (0x2100002E).");
    }

    private void HandleCharacterRaise(
        CharacterSheetProvider provider,
        CharacterStatController.RaiseRequest request,
        Action completed)
    {
        if (request.Kind != CharacterStatController.RaiseTargetKind.TrainSkill)
        {
            provider.HandleRaiseRequest(request);
            completed();
            return;
        }

        RetailSkillTrainingConfirmationController confirmations =
            _skillTrainingConfirmationController
            ?? throw new InvalidOperationException(
                "The retail dialog factory must be mounted before the character panel.");

        confirmations.Request(
            request,
            provider.BuildSheet(),
            provider.HandleRaiseRequest,
            completed);
    }

    private void MountPlugins()
    {
        if (_bindings.Plugins is null) return;

        IMarkupIconResolver iconResolver = new RetailMarkupIconResolver(
            _bindings.Assets.Dats,
            _bindings.Assets.Icons,
            _bindings.Toolbar.Objects);

        foreach (var panel in _bindings.Plugins.Drain())
        {
            try
            {
                string xml = panel.MarkupContent
                    ?? File.ReadAllText(panel.MarkupPath);
                UiNineSlicePanel element = MarkupDocument.Build(
                    xml,
                    panel.Binding,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.Controls,
                    _bindings.Assets.DefaultFont,
                    iconResolver);

                if (Host.WindowManager.TryGet(panel.WindowName, out _))
                {
                    throw new InvalidOperationException(
                        $"Plugin window '{panel.WindowName}' is already registered. "
                        + "Window ids must be unique within one plugin.");
                }

                Func<bool>? availability = element.VisibleSource;
                var visibility = new PluginWindowVisibilityController(
                    availability,
                    panel.Descriptor.StartVisible);
                element.VisibleSource = visibility.ShouldBeVisible;
                element.Visible = visibility.ShouldBeVisible();

                Host.Root.AddChild(element);
                // Publish ownership immediately after the tree mutation. Any
                // later registration/sidepanel failure then rolls the mounted
                // subtree back through FailMount instead of leaking it.
                _bindings.Plugins.CompleteMount(panel, Host.Root, element);
                int authoredGeometryRevision = RetailWindowManager.ComputeAuthoredGeometryRevision(
                    element.Width, element.Height, element.MinWidth, element.MinHeight, element.Resizable);
                RetailWindowHandle handle = Host.WindowManager.Register(
                    panel.WindowName,
                    element,
                    element,
                    visibility,
                    authoredGeometryRevision: authoredGeometryRevision);
                _bindings.Plugins.CompleteWindowMount(
                    panel,
                    () => Host.WindowManager.Unregister(panel.WindowName));

                if (panel.Descriptor.ShowInSidePanel)
                {
                    if (_pluginSidePanel is null)
                    {
                        _pluginSidePanel = new PluginSidePanel(
                            Host.WindowManager,
                            iconResolver.ResolveDid,
                            _bindings.Assets.DefaultFont);
                        Host.Root.AddChild(_pluginSidePanel);
                        Host.WindowManager.Register(
                            WindowNames.PluginShelf,
                            _pluginSidePanel,
                            _pluginSidePanel,
                            controller: _pluginSidePanel);
                    }
                    _pluginSidePanel.Add(panel.Owner, panel.Descriptor, handle);
                }

                Console.WriteLine(
                    $"[UI] plugin UI window loaded: {panel.WindowName} "
                    + $"({panel.MarkupPath})");
            }
            catch (Exception ex)
            {
                _bindings.Plugins.FailMount(panel);
                Console.WriteLine($"[UI] plugin UI panel '{panel.MarkupPath}' failed to load: {ex.Message}");
            }
        }
    }

    private void MountInventory()
    {
        ImportedLayout? layout = Import(0x21000023u);
        if (layout is null)
        {
            Console.WriteLine("[UI] inventory: LayoutDesc 0x21000023 not found.");
            return;
        }

        UiElement root = layout.Root;
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root, root, _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Inventory,
                Chrome = RetailWindowChrome.NineSlice,
                Left = root.Left,
                Top = root.Top,
                ContentWidth = root.Width,
                ContentHeight = root.Height,
                Visible = false,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
            });
        _panelUi.RegisterMainPanel(
            RetailPanelCatalog.Inventory,
            WindowNames.Inventory,
            handle);

        uint contents, sideBag, mainPack;
        IReadOnlyDictionary<uint, uint> paperdollEmptySprites;
        PaperdollClickMap? paperdollClickMap;
        lock (_bindings.Assets.DatLock)
        {
            contents = ItemListCellTemplate.ResolveEmptySprite(_bindings.Assets.Dats, 0x21000021u, 0x100001C6u);
            sideBag = ItemListCellTemplate.ResolveEmptySprite(_bindings.Assets.Dats, 0x21000022u, 0x100001CAu);
            mainPack = ItemListCellTemplate.ResolveEmptySprite(_bindings.Assets.Dats, 0x21000022u, 0x100001C9u);
            paperdollEmptySprites = PaperdollSlotBackgrounds.ResolveEmptySprites(_bindings.Assets.Dats);
            paperdollClickMap = PaperdollClickMap.Load(_bindings.Assets.Dats);
        }

        InventoryRuntimeBindings b = _bindings.Inventory;
        Action<uint, uint>? notifyMergeAttempt = ToolbarController is null
            ? null
            : ToolbarController.ReplaceFullyMergedShortcut;
        InventoryController inventory = InventoryController.Bind(
            layout, b.Objects, b.PlayerGuid, b.ResolveIcon, b.Strength, b.Selection,
            _bindings.Assets.DefaultFont, _bindings.Character.Provider.CharacterName,
            contents, sideBag, mainPack, b.SendUse,
            b.SendPutItemInContainer, b.SendStackableSplitToContainer, b.SendStackableMerge,
            notifyMergeAttempt, b.ItemInteraction,
            () => CloseWindow(WindowNames.Inventory),
            StackSplitQuantity,
            b.ResolveDragIcon,
            b.Spellbook);
        InventoryPanelController = inventory;
        PaperdollController paperdoll = PaperdollController.Bind(
            layout, b.Objects, b.PlayerGuid, b.ResolveIcon, b.Selection, b.ItemInteraction,
            contents, _bindings.Assets.DefaultFont, paperdollClickMap,
            b.ResolveDragIcon, paperdollEmptySprites);
        Host.WindowManager.AttachController(
            WindowNames.Inventory,
            new RetainedPanelControllerGroup(inventory, paperdoll));

        PaperdollViewportWidget = layout.FindElement(0x100001D5u) as UiViewport;
        InventoryFrame = (UiNineSlicePanel)handle.OuterFrame;
        Console.WriteLine("[UI] retail inventory window from LayoutDesc importer (0x21000023).");
    }

    private void MountExternalContainer()
    {
        ImportedLayout? layout;
        lock (_bindings.Assets.DatLock)
        {
            layout = LayoutImporter.Import(
                _bindings.Assets.Dats,
                ExternalContainerController.LayoutId,
                ExternalContainerController.RootId,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
        }
        if (layout is null)
        {
            Console.WriteLine("[UI] external container: LayoutDesc 0x21000008 not found.");
            return;
        }

        UiElement root = layout.Root;
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            ExternalContainerController.CreateWindowOptions(root));

        uint contentsEmpty;
        uint containerEmpty;
        lock (_bindings.Assets.DatLock)
        {
            contentsEmpty = ItemListCellTemplate.ResolveEmptySprite(
                _bindings.Assets.Dats,
                ExternalContainerController.LayoutId,
                ExternalContainerController.ContentsListId);
            containerEmpty = ItemListCellTemplate.ResolveEmptySprite(
                _bindings.Assets.Dats,
                ExternalContainerController.LayoutId,
                ExternalContainerController.ContainerListId);
        }

        ExternalContainerRuntimeBindings b = _bindings.ExternalContainer;
        ExternalContainerController = ExternalContainerController.Bind(
            layout,
            b.State,
            b.Objects,
            b.Selection,
            b.ItemInteraction,
            StackSplitQuantity,
            b.ResolveIcon,
            b.ResolveDragIcon,
            b.SendUse,
            b.SendPutItemInContainer,
            b.SendStackableSplitToContainer,
            b.IsWithinUseRange,
            handle,
            contentsEmpty,
            containerEmpty);
        Host.WindowManager.AttachController(
            WindowNames.ExternalContainer,
            ExternalContainerController);
        Console.WriteLine(
            "[UI] retail external-container strip mounted from LayoutDesc 0x21000008.");
    }

    private void MountVendor()
    {
        ImportedLayout? layout;
        uint emptySlotSprite;
        uint buyingEmptySlotSprite;
        uint sellingEmptySlotSprite;
        lock (_bindings.Assets.DatLock)
        {
            layout = LayoutImporter.Import(
                _bindings.Assets.Dats,
                VendorUiController.LayoutId,
                VendorUiController.RootId,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
            emptySlotSprite = ItemListCellTemplate.ResolveEmptySprite(
                _bindings.Assets.Dats,
                VendorUiController.LayoutId,
                VendorUiController.ItemListId);
            buyingEmptySlotSprite = ItemListCellTemplate.ResolveEmptySprite(
                _bindings.Assets.Dats,
                VendorUiController.LayoutId,
                VendorUiController.BuyingListId);
            sellingEmptySlotSprite = ItemListCellTemplate.ResolveEmptySprite(
                _bindings.Assets.Dats,
                VendorUiController.LayoutId,
                VendorUiController.SellingListId);
        }
        if (layout is null)
        {
            Console.WriteLine("[UI] vendor: LayoutDesc 0x21000012 root 0x100000B7 not found.");
            return;
        }

        UiElement root = layout.Root;
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Vendor,
                Chrome = RetailWindowChrome.NineSlice,
                Left = root.Left,
                Top = root.Top,
                ContentWidth = root.Width,
                ContentHeight = root.Height,
                MinWidth = root.Width,
                MinHeight = root.Height,
                Visible = false,
                ResizeX = false,
                ResizeY = false,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                DrawChromeCenter = false,
            });

        VendorRuntimeBindings b = _bindings.Vendor;
        VendorController = VendorUiController.Bind(
            layout,
            b.State,
            handle,
            b.ResolveIcon,
            _bindings.Inventory.Objects,
            _bindings.Inventory.PlayerGuid,
            b.ItemInteraction,
            b.Selection,
            StackSplitQuantity,
            _bindings.Assets.DefaultFont,
            _bindings.Assets.DebugFont,
            _bindings.Assets.ResolveSprite,
            emptySlotSprite,
            buyingEmptySlotSprite,
            sellingEmptySlotSprite,
            DialogFactory,
            b.DisplaySystemMessage);
        if (VendorController is null)
        {
            Console.WriteLine("[UI] vendor: required authored controls are missing.");
            return;
        }

        Host.WindowManager.AttachController(WindowNames.Vendor, VendorController);
        Console.WriteLine("[UI] retail vendor browse panel mounted from LayoutDesc 0x21000012.");
    }

    public Layout.SecureTradeUiController? SecureTradeController { get; private set; }

    public SalvageUiController? SalvageController { get; private set; }

    private void MountSalvage()
    {
        ImportedLayout? layout;
        uint emptySlot;
        lock (_bindings.Assets.DatLock)
        {
            layout = LayoutImporter.Import(
                _bindings.Assets.Dats, SalvageUiController.LayoutId, SalvageUiController.RootId,
                _bindings.Assets.ResolveSprite, _bindings.Assets.DefaultFont, _bindings.Assets.ResolveFont);
            emptySlot = ItemListCellTemplate.ResolveEmptySprite(
                _bindings.Assets.Dats, SalvageUiController.LayoutId, SalvageUiController.ItemListId);
        }
        if (layout is null)
        {
            Console.WriteLine("[UI] salvage window layout is unavailable.");
            return;
        }
        InventoryRuntimeBindings inventory = _bindings.Inventory;
        SalvageUiController? controller = SalvageUiController.Bind(layout,
            new SalvageUiController.Bindings(
                inventory.Objects,
                inventory.ItemInteraction.IsOwnedByPlayer,
                inventory.ItemInteraction.TrySalvageItems,
                () => _bindings.Options.CurrentCharacterOption((uint)CharacterOptionId.SalvageMultiple),
                inventory.ResolveIcon,
                visible =>
                {
                    if (visible) Host.ShowWindow(WindowNames.Salvage);
                    else Host.HideWindow(WindowNames.Salvage);
                },
                _bindings.Options.DisplaySystemMessage,
                emptySlot));
        if (controller is null)
        {
            Console.WriteLine("[UI] salvage window controls are unavailable.");
            return;
        }
        UiElement root = layout.Root;
        const float frameInset = 2f * RetailChromeSprites.Border;
        float width = MathF.Min(root.Width, MathF.Max(240f, Host.Root.Width - frameInset));
        RetailWindowFrame.Mount(Host.Root, root, _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Salvage,
                Chrome = RetailWindowChrome.NineSlice,
                Left = MathF.Max(0f, (Host.Root.Width - width - frameInset) * 0.5f),
                Top = MathF.Max(0f, (Host.Root.Height - root.Height - frameInset) * 0.5f),
                ContentWidth = width,
                ContentHeight = root.Height,
                MinWidth = 240f,
                MinHeight = root.Height,
                Visible = false,
                ResizeX = true,
                ResizeY = false,
                ResizableEdges = ResizeEdges.Left | ResizeEdges.Right,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                Controller = controller,
            });
        SalvageController = controller;
        inventory.ItemInteraction.PolicyActionRequested += controller.HandlePolicyAction;
    }

    private void MountSecureTrade()
    {
        if (_bindings.Social.Trade is not { } tradeView)
        {
            Console.WriteLine("[UI] secure trade: no runtime trade view bound.");
            return;
        }

        ImportedLayout? layout;
        uint selfEmptySlotSprite;
        uint partnerEmptySlotSprite;
        lock (_bindings.Assets.DatLock)
        {
            layout = LayoutImporter.Import(
                _bindings.Assets.Dats,
                Layout.SecureTradeUiController.LayoutId,
                Layout.SecureTradeUiController.RootId,
                _bindings.Assets.ResolveSprite,
                _bindings.Assets.DefaultFont,
                _bindings.Assets.ResolveFont);
            selfEmptySlotSprite = ItemListCellTemplate.ResolveEmptySprite(
                _bindings.Assets.Dats,
                Layout.SecureTradeUiController.LayoutId,
                Layout.SecureTradeUiController.SelfListId);
            partnerEmptySlotSprite = ItemListCellTemplate.ResolveEmptySprite(
                _bindings.Assets.Dats,
                Layout.SecureTradeUiController.LayoutId,
                Layout.SecureTradeUiController.PartnerListId);
        }
        if (layout is null)
        {
            Console.WriteLine(
                "[UI] secure trade: LayoutDesc 0x2100000D root 0x1000007A not found.");
            return;
        }

        Layout.SecureTradeUiController? controller =
            Layout.SecureTradeUiController.Bind(
                layout,
                new Layout.SecureTradeUiController.Bindings(
                    Trade: tradeView,
                    Objects: _bindings.Inventory.Objects,
                    ResolveIcon: _bindings.Inventory.ResolveIcon,
                    OpenTrade: partner => _bindings.Options.CommandBus().Publish(
                        new OpenTradeNegotiationsRuntimeCmd(partner)),
                    CloseTrade: () => _bindings.Options.CommandBus().Publish(
                        new CloseTradeNegotiationsRuntimeCmd()),
                    AddToTrade: item => _bindings.Options.CommandBus().Publish(
                        new AddToTradeRuntimeCmd(item)),
                    AcceptTrade: (selfAccepted, partnerAccepted, partner) =>
                        _bindings.Options.CommandBus().Publish(new AcceptTradeRuntimeCmd(
                            partner, selfAccepted, partnerAccepted)),
                    DeclineTrade: () => _bindings.Options.CommandBus().Publish(
                        new DeclineTradeRuntimeCmd()),
                    ResetTrade: () => _bindings.Options.CommandBus().Publish(
                        new ResetTradeRuntimeCmd()),
                    SetWindowVisible: visible =>
                    {
                        if (visible) Host.ShowWindow(WindowNames.SecureTrade);
                        else Host.HideWindow(WindowNames.SecureTrade);
                    },
                    SelfEmptySlotSprite: selfEmptySlotSprite,
                    PartnerEmptySlotSprite: partnerEmptySlotSprite,
                    FormatTotalItems: count =>
                    {
                        lock (_bindings.Assets.DatLock)
                        {
                            var strings = new DatStringResolver(_bindings.Assets.Dats);
                            return strings.ResolveTemplate(
                                0x23000001u,
                                "ID_SecureTrade_TotalItemsLabel",
                                new Dictionary<uint, string>
                                {
                                    [DatStringResolver.ComputeHash("ITEMS")] =
                                        count.ToString(),
                                }) ?? count.ToString();
                        }
                    }));
        if (controller is null)
        {
            Console.WriteLine("[UI] secure trade: required authored grids are missing.");
            return;
        }

        UiElement root = layout.Root;
        RetailWindowFrame.Mount(
            Host.Root,
            root,
            _bindings.Assets.ResolveSprite,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.SecureTrade,
                Chrome = RetailWindowChrome.NineSlice,
                Left = MathF.Max(0f, (Host.Root.Width - root.Width) * 0.5f),
                Top = MathF.Max(0f, (Host.Root.Height - root.Height) * 0.5f),
                ContentWidth = root.Width,
                ContentHeight = root.Height,
                MinWidth = root.Width,
                MinHeight = root.Height,
                Visible = false,
                ResizeX = false,
                ResizeY = false,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
            });

        SecureTradeController = controller;
        Host.WindowManager.AttachController(WindowNames.SecureTrade, controller);
        _bindings.Inventory.ItemInteraction.SecureTradeRequested +=
            controller.RequestSecureTrade;
        Console.WriteLine(
            "[UI] retail secure trade panel mounted from LayoutDesc 0x2100000D.");
    }

    private void ConfigureCharacterManagement()
    {
        CharacterSelectionRuntimeBindings? bindings =
            _bindings.CharacterSelection;
        if (bindings is null || _characterManagementMount is not null)
            return;

        _characterManagementMount = new CharacterManagementUiMountCoordinator(
            Host.Root,
            bindings with { RequestCreate = () => CharacterCreationController?.Open() },
            EnsureDialogFactory,
            LoadCharacterManagementResources,
            OpenCredits);
    }

    private void OpenCredits()
    {
        if (_disposed)
            return;

        CharacterManagementUiController? characters =
            CharacterManagementController;
        if (characters is null)
            return;

        if (_creditsController is null)
        {
            RetailDialogFactory? dialogs = EnsureDialogFactory();
            CreditsUiResources? resources = LoadCreditsResources();
            if (dialogs is null || resources is null)
                return;

            _creditsController = CreditsUiController.CreateDetached(
                Host.Root,
                resources,
                dialogs,
                static () => System.Diagnostics.Stopwatch.GetTimestamp()
                    / (double)System.Diagnostics.Stopwatch.Frequency,
                _bindings.Assets.ResolveSprite,
                ReturnFromCredits);
            if (_creditsController is null)
                return;
        }

        characters.SetPresentationSuppressed(true);
        try
        {
            _creditsController.Activate();
        }
        catch (Exception error)
        {
            characters.SetPresentationSuppressed(false);
            Console.WriteLine(
                $"[UI] credits activation failed: {error.Message}");
        }
    }

    private void ReturnFromCredits()
        => CharacterManagementController?.SetPresentationSuppressed(false);

    private CreditsUiResources? LoadCreditsResources()
    {
        uint layoutId;
        ElementInfo? pictureInfo;
        ElementInfo? textInfo;
        ImportedLayout? pictureLayout;
        ImportedLayout? textLayout;
        var strings = new DatStringResolver(_bindings.Assets.Dats);
        lock (_bindings.Assets.DatLock)
        {
            layoutId = RetailDataIdResolver.Resolve(
                _bindings.Assets.Dats,
                CreditsUiController.RootEnum,
                5u);
            pictureInfo = layoutId == 0u
                ? null
                : LayoutImporter.ImportInfos(
                    _bindings.Assets.Dats,
                    layoutId,
                    CreditsUiController.PictureRootElementId);
            textInfo = layoutId == 0u
                ? null
                : LayoutImporter.ImportInfos(
                    _bindings.Assets.Dats,
                    layoutId,
                    CreditsUiController.TextRootElementId);
            pictureLayout = layoutId == 0u
                ? null
                : LayoutImporter.Import(
                    _bindings.Assets.Dats,
                    layoutId,
                    CreditsUiController.PictureRootElementId,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont);
            textLayout = layoutId == 0u
                ? null
                : LayoutImporter.Import(
                    _bindings.Assets.Dats,
                    layoutId,
                    CreditsUiController.TextRootElementId,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont);
        }

        if (pictureInfo is null
            || textInfo is null
            || pictureLayout is null
            || textLayout is null
            || !TryGetCreditsDataId(
                textInfo,
                0x10000002u,
                out uint textAreaId)
            || textAreaId != CreditsUiController.TextAreaElementId
            || !TryGetCreditsDataId(
                textInfo,
                0x10000003u,
                out uint stringTableId)
            || !textInfo.TryGetEffectiveFloat(
                0x10000004u,
                out float sectionSeconds)
            || !pictureInfo.TryGetEffectiveProperty(
                0x10000005u,
                out UiPropertyValue pictureProperty)
            || pictureProperty.Kind != UiPropertyKind.Array)
        {
            Console.WriteLine(
                "[UI] credits: enum-table-5 properties could not be imported.");
            return null;
        }

        uint[] pictureIds =
        [
            .. pictureProperty.ArrayValue
                .Where(static value => value.Kind is
                    UiPropertyKind.DataId or UiPropertyKind.Enum)
                .Select(static value => checked((uint)value.UnsignedValue))
                .Where(static value => value != 0u),
        ];
        if (pictureIds.Length == 0)
            return null;

        var textFragments = new List<string>();
        string? pleaseWait;
        lock (_bindings.Assets.DatLock)
        {
            for (int index = 1; index <= 4096; index++)
            {
                string? fragment = strings.Resolve(
                    stringTableId,
                    DatStringResolver.ComputeHash($"ID_Credits{index}"));
                if (fragment is null)
                    break;
                textFragments.Add(fragment);
            }

            pleaseWait = strings.Resolve(
                0x23000001u,
                DatStringResolver.ComputeHash("ID_Wait_PleaseWait"));
        }

        if (textFragments.Count == 0 || pleaseWait is null)
        {
            Console.WriteLine(
                "[UI] credits: localized credit/wait strings are unavailable.");
            return null;
        }

        Console.WriteLine(
            $"[UI] retail credits ready (layout 0x{layoutId:X8}, "
            + $"{textFragments.Count} text fragments, {pictureIds.Length} pictures). ");
        return new CreditsUiResources(
            layoutId,
            pictureLayout,
            textLayout,
            textFragments,
            pictureIds,
            sectionSeconds,
            pleaseWait);
    }

    private static bool TryGetCreditsDataId(
        ElementInfo info,
        uint propertyId,
        out uint value)
    {
        if (info.TryGetEffectiveProperty(propertyId, out UiPropertyValue property)
            && property.Kind is UiPropertyKind.DataId or UiPropertyKind.Enum
            && property.UnsignedValue <= uint.MaxValue)
        {
            value = (uint)property.UnsignedValue;
            return true;
        }

        value = 0u;
        return false;
    }

    private RetailDialogFactory? EnsureDialogFactory()
    {
        MountDialogFactory();
        return DialogFactory;
    }

    private CharacterManagementUiMountResources? LoadCharacterManagementResources()
    {
        const uint stringTableId = 0x23000002u;
        uint layoutId;
        ImportedLayout? layout;
        var strings = new DatStringResolver(_bindings.Assets.Dats);
        lock (_bindings.Assets.DatLock)
        {
            layoutId = RetailDataIdResolver.Resolve(
                _bindings.Assets.Dats,
                CharacterManagementUiController.RootEnum,
                5u);
            layout = layoutId == 0u
                ? null
                : LayoutImporter.Import(
                    _bindings.Assets.Dats,
                    layoutId,
                    CharacterManagementUiController.RootElementId,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont);
        }

        if (layout is null)
        {
            Console.WriteLine(
                "[UI] character management: enum-table-5 root could not be imported.");
            return null;
        }

        string? deleteResponse;
        string? deleteConfirmationProbe;
        string? pleaseWait;
        string? enteringWorld;
        string? confirmExit;
        lock (_bindings.Assets.DatLock)
        {
            deleteConfirmationProbe = strings.ResolveTemplate(
                stringTableId,
                "ID_CharacterManagement_DeleteCharacterConfirmation",
                new Dictionary<uint, string>
                {
                    [DatStringResolver.PlayerVariable] = string.Empty,
                });
            deleteResponse = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_CharacterManagement_DeleteCharacterResponse");
            pleaseWait = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_CharacterManagement_PleaseWait");
            enteringWorld = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_Character_EnteringWorld");
            confirmExit = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_CharacterManagement_ConfirmExit");
        }

        if (deleteConfirmationProbe is null
            || deleteResponse is null
            || pleaseWait is null
            || enteringWorld is null
            || confirmExit is null)
        {
            Console.WriteLine(
                "[UI] character management: required retail strings are unavailable.");
            return null;
        }

        UiElement? ResolveTemplate(uint templateLayoutId, uint templateElementId)
        {
            lock (_bindings.Assets.DatLock)
            {
                return LayoutImporter.Import(
                    _bindings.Assets.Dats,
                    templateLayoutId,
                    templateElementId,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont)?.Root;
            }
        }

        string ComposeDeleteConfirmation(string characterName)
        {
            lock (_bindings.Assets.DatLock)
            {
                return strings.ResolveTemplate(
                    stringTableId,
                    "ID_CharacterManagement_DeleteCharacterConfirmation",
                    new Dictionary<uint, string>
                    {
                        [DatStringResolver.PlayerVariable] = characterName,
                    })!;
            }
        }

        return new CharacterManagementUiMountResources(
            layoutId,
            layout,
            ResolveTemplate,
            new CharacterManagementUiController.DialogStrings(
                ComposeDeleteConfirmation,
                deleteResponse,
                pleaseWait,
                enteringWorld,
                confirmExit),
            _bindings.Assets.DebugFont);
    }

    private static string? ResolveCharacterManagementString(
        DatStringResolver strings,
        uint tableId,
        string key) =>
        strings.Resolve(tableId, DatStringResolver.ComputeHash(key));

    private void ConfigureCharacterCreation()
    {
        CharacterCreationRuntimeBindings? bindings = _bindings.CharacterCreation;
        if (bindings is null || _characterCreationMount is not null)
            return;

        _characterCreationMount = new CharacterCreationUiMountCoordinator(
            Host.Root,
            bindings,
            EnsureDialogFactory,
            LoadCharacterCreationResources);
    }

    private CharacterCreationUiMountResources? LoadCharacterCreationResources()
    {
        const uint stringTableId = 0x23000002u;
        uint layoutId;
        ImportedLayout? layout;
        var strings = new DatStringResolver(_bindings.Assets.Dats);
        lock (_bindings.Assets.DatLock)
        {
            layoutId = RetailDataIdResolver.Resolve(
                _bindings.Assets.Dats,
                CharacterCreationUiController.RootEnum,
                5u);
            layout = layoutId == 0u
                ? null
                : LayoutImporter.Import(
                    _bindings.Assets.Dats,
                    layoutId,
                    CharacterCreationUiController.RootElementId,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont);
        }

        if (layout is null)
        {
            Console.WriteLine(
                "[UI] character creation: enum-table-5 root could not be imported.");
            return null;
        }

        string? exitWarning;
        string? noNameWarning;
        string? creditWarning;
        string? randomizeWarning;
        string? nameTooLong;
        lock (_bindings.Assets.DatLock)
        {
            exitWarning = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_CharGen_ExitWarning");
            noNameWarning = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_CharGen_NoNameWarning");
            creditWarning = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_CharGen_CreditWarning");
            randomizeWarning = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_CharGen_RandomizeWarning");
            nameTooLong = ResolveCharacterManagementString(
                strings,
                stringTableId,
                "ID_CharGen_NameTooLong");
        }
        if (exitWarning is null
            || noNameWarning is null
            || creditWarning is null
            || randomizeWarning is null
            || nameTooLong is null)
        {
            Console.WriteLine(
                "[UI] character creation: required retail strings are unavailable.");
            return null;
        }

        UiElement? ResolveTemplate(uint templateLayoutId, uint templateElementId)
        {
            lock (_bindings.Assets.DatLock)
            {
                return LayoutImporter.Import(
                    _bindings.Assets.Dats,
                    templateLayoutId,
                    templateElementId,
                    _bindings.Assets.ResolveSprite,
                    _bindings.Assets.DefaultFont,
                    _bindings.Assets.ResolveFont)?.Root;
            }
        }

        return new CharacterCreationUiMountResources(
            layoutId,
            layout,
            ResolveTemplate,
            new CharacterCreationUiController.DialogStrings(
                exitWarning, noNameWarning, creditWarning, randomizeWarning, nameTooLong));
    }

    private void MountItemCooldowns()
    {
        ItemCooldownAssets? assets;
        lock (_bindings.Assets.DatLock)
            assets = ItemCooldownAssets.TryLoad(_bindings.Assets.Dats);

        if (assets is null)
        {
            Console.WriteLine(
                "[UI] shared UIItem cooldown overlays missing from LayoutDesc 0x21000037.");
            return;
        }

        _itemCooldownController = ItemCooldownUiController.Bind(
            Host.Root,
            _bindings.Magic.Spellbook,
            _bindings.Magic.Objects,
            _bindings.Magic.ServerTime,
            assets.Value);
        Console.WriteLine(
            "[UI] retail shared item cooldown overlays ready (10 DAT-authored steps).");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _shutdown ??= CreateShutdownTransaction(
            () => _automation?.Dispose(),
            () => _persistence?.Dispose(),
            () =>
            {
                _characterSheetSubscription?.Dispose();
                _characterTitlesController?.Dispose();
                _pluginSidePanel?.Dispose();
                Host.WindowManager.WindowVisibilityChanged -= OnWindowVisibilityChanged;
                WindowLockPresentation.Dispose();
                WindowOpacity.Dispose();
                if (SecureTradeController is { } trade)
                {
                    _bindings.Inventory.ItemInteraction.SecureTradeRequested -=
                        trade.RequestSecureTrade;
                }
                if (SalvageController is { } salvage)
                    _bindings.Inventory.ItemInteraction.PolicyActionRequested -= salvage.HandlePolicyAction;
            },
            () => _itemConfirmationController?.Dispose(),
            () =>
            {
                _creditsController?.Dispose();
                _connectionMount?.Dispose();
                _characterManagementMount?.Dispose();
                _characterCreationMount?.Dispose();
                _gameplayConfirmationController?.Dispose();
            },
            () =>
            {
                DialogFactory?.Dispose();
                TooltipPresenter?.Dispose();
            },
            _panelUi.Dispose,
            Host.Dispose);
        _shutdown.CompleteOrThrow();
        _disposed = _shutdown.IsComplete;
    }

    private sealed class PluginWindowVisibilityController(
        Func<bool>? availability,
        bool startVisible) : IRetainedPanelController
    {
        private bool _requestedVisible = startVisible;

        internal bool ShouldBeVisible() =>
            _requestedVisible && (availability?.Invoke() ?? true);

        public void OnShown() => _requestedVisible = true;

        public void OnHidden()
        {
            if (availability?.Invoke() ?? true)
                _requestedVisible = false;
        }

        public void Dispose()
        {
        }
    }

    internal static ResourceShutdownTransaction CreateShutdownTransaction(
        Action disposeAutomation,
        Action disposePersistence,
        Action unsubscribeWindowVisibility,
        Action disposeItemConfirmation,
        Action disposeGameplayConfirmation,
        Action disposeDialogFactory,
        Action disposePanelController,
        Action disposeHost)
    {
        ArgumentNullException.ThrowIfNull(disposeAutomation);
        ArgumentNullException.ThrowIfNull(disposePersistence);
        ArgumentNullException.ThrowIfNull(unsubscribeWindowVisibility);
        ArgumentNullException.ThrowIfNull(disposeItemConfirmation);
        ArgumentNullException.ThrowIfNull(disposeGameplayConfirmation);
        ArgumentNullException.ThrowIfNull(disposeDialogFactory);
        ArgumentNullException.ThrowIfNull(disposePanelController);
        ArgumentNullException.ThrowIfNull(disposeHost);

        return new ResourceShutdownTransaction(
            new ResourceShutdownStage("retail UI observers",
            [
                new("UI automation", disposeAutomation),
                new("window persistence", disposePersistence),
                new("window visibility", unsubscribeWindowVisibility),
            ]),
            new ResourceShutdownStage("retail UI semantic controllers",
            [
                new("item confirmation", disposeItemConfirmation),
                new("gameplay confirmation", disposeGameplayConfirmation),
            ]),
            new ResourceShutdownStage("retail UI panel composition",
            [
                new("dialog factory", disposeDialogFactory),
                new("panel controller", disposePanelController),
            ]),
            new ResourceShutdownStage("retained UI host",
            [
                new("host", disposeHost),
            ]));
    }
}
