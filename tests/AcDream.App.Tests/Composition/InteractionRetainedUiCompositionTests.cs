using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Combat;
using AcDream.App.Composition;
using AcDream.App.Diagnostics;
using AcDream.App.Rendering;
using AcDream.App.Settings;
using AcDream.App.Spells;
using AcDream.Content;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.App.Tests.Architecture;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Spells;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Panels.Chat;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Composition;

public sealed class InteractionRetainedUiCompositionTests
{
    private static readonly InteractionRetainedUiCompositionPoint[] UiPoints =
    [
        InteractionRetainedUiCompositionPoint.UiHostAcquired,
        InteractionRetainedUiCompositionPoint.InputCaptureBound,
        InteractionRetainedUiCompositionPoint.CursorAssetsCreated,
        InteractionRetainedUiCompositionPoint.CharacterSheetCreated,
        InteractionRetainedUiCompositionPoint.MouseInputWired,
        InteractionRetainedUiCompositionPoint.KeyboardInputWired,
        InteractionRetainedUiCompositionPoint.UiAssetsCreated,
        InteractionRetainedUiCompositionPoint.UiProbeCreated,
        InteractionRetainedUiCompositionPoint.UiRuntimeMounted,
        InteractionRetainedUiCompositionPoint.InventoryContainerBound,
    ];

    [Fact]
    public void FpsPanelBorrowsTheExactRendererLifetimeDegradeOwner()
    {
        DisplaySettings settings = DisplaySettings.Default with
        {
            AutomaticDegrades = false,
            GraphicsPerformance = -0.35f,
        };
        var owner = new BuildingDegradeController(() => settings);
        for (int i = 0; i < 21; i++)
            owner.Tick(0.05);

        FpsRuntimeBindings bindings =
            RetailInteractionRetainedUiCompositionFactory.CreateFpsBindings(
                owner, () => true);

        Assert.Equal(owner.Fps, bindings.FramesPerSecond());
        Assert.Equal(-0.35, bindings.DegradeMultiplier(), 6);
        settings = settings with { GraphicsPerformance = 0.65f };
        Assert.Equal(0.65, bindings.DegradeMultiplier(), 6);
        Assert.True(bindings.IsVisible());
    }

    [Fact]
    public void RadarLockBindingUsesAuthoritativeRequestInsteadOfPresentationOnlySetter()
    {
        MethodInfo compose = typeof(RetailInteractionRetainedUiCompositionFactory)
            .GetMethod(nameof(
                RetailInteractionRetainedUiCompositionFactory.CreateRetainedUi))!;
        IReadOnlyList<CompiledCall> references =
            CompiledCallGraph.ReadMethodReferences(compose);

        Assert.Contains(
            references,
            call => call.Target.DeclaringType == typeof(RuntimeSettingsController)
                && call.Target.Name == nameof(RuntimeSettingsController.RequestUiLocked));
        Assert.DoesNotContain(
            references,
            call => call.Target.DeclaringType == typeof(RuntimeSettingsController)
                && call.Target.Name == nameof(RuntimeSettingsController.SetUiLocked));
    }

    [Fact]
    public void EnabledUiPublishesOneExactResultAfterFrozenConstructionOrder()
    {
        using var fixture = new Fixture(retailUi: true);

        InteractionRetainedUiResult result = fixture.Compose();

        Assert.Same(result, fixture.Publication.Result);
        Assert.Equal(
        [
            InteractionRetainedUiCompositionPoint.LateBindingsCreated,
            InteractionRetainedUiCompositionPoint.CombatTargetCreated,
            InteractionRetainedUiCompositionPoint.ExternalContainerLifecycleCreated,
            InteractionRetainedUiCompositionPoint.ItemInteractionCreated,
            InteractionRetainedUiCompositionPoint.MagicRuntimeCreated,
            .. UiPoints,
            InteractionRetainedUiCompositionPoint.ResultPublished,
        ], fixture.Points);
        Assert.NotNull(result.RetainedUi);
        Assert.NotNull(result.Magic);
        Assert.Empty(fixture.Factory.Releases);
    }

    [Fact]
    public void DisabledUiAcquiresNoRetainedFrontendResource()
    {
        using var fixture = new Fixture(retailUi: false);

        InteractionRetainedUiResult result = fixture.Compose();

        Assert.Null(result.RetainedUi);
        Assert.NotNull(result.Magic);
        Assert.Equal(
        [
            InteractionRetainedUiCompositionPoint.LateBindingsCreated,
            InteractionRetainedUiCompositionPoint.CombatTargetCreated,
            InteractionRetainedUiCompositionPoint.ExternalContainerLifecycleCreated,
            InteractionRetainedUiCompositionPoint.ItemInteractionCreated,
            InteractionRetainedUiCompositionPoint.MagicRuntimeCreated,
            InteractionRetainedUiCompositionPoint.RetainedUiDisabled,
            InteractionRetainedUiCompositionPoint.ResultPublished,
        ], fixture.Points);
        Assert.Equal(0, fixture.Factory.RetainedUiCalls);
    }

    [Theory]
    [MemberData(nameof(EnabledFailurePoints))]
    public void FaultAtEveryEnabledBoundaryStopsSuffixAndRollsBackUnpublishedPrefix(
        int pointValue)
    {
        var point = (InteractionRetainedUiCompositionPoint)pointValue;
        using var fixture = new Fixture(retailUi: true, failurePoint: point);

        Assert.Throws<InvalidOperationException>(fixture.Compose);

        Assert.Equal(point, fixture.Points[^1]);
        Assert.False(
            fixture.Dependencies.Runtime.CaptureOwnership().IsDisposeRequested);
        if (point == InteractionRetainedUiCompositionPoint.ResultPublished)
        {
            Assert.Empty(fixture.Factory.Releases);
            Assert.NotNull(fixture.Publication.Result);
        }
        else
        {
            Assert.Null(fixture.Publication.Result);
            Assert.Equal(ExpectedRollback(point), fixture.Factory.Releases);
        }
    }

    public static TheoryData<int> EnabledFailurePoints()
    {
        var data = new TheoryData<int>();
        foreach (InteractionRetainedUiCompositionPoint point in
                 Enum.GetValues<InteractionRetainedUiCompositionPoint>())
        {
            if (point != InteractionRetainedUiCompositionPoint.RetainedUiDisabled)
                data.Add((int)point);
        }
        return data;
    }

    private static string[] ExpectedRollback(
        InteractionRetainedUiCompositionPoint point)
    {
        var acquired = new List<string> { "late bindings" };
        if (point >= InteractionRetainedUiCompositionPoint.ExternalContainerLifecycleCreated)
            acquired.Add("external container");
        if (point >= InteractionRetainedUiCompositionPoint.ItemInteractionCreated)
            acquired.Add("item interaction");
        if (point >= InteractionRetainedUiCompositionPoint.MagicRuntimeCreated)
            acquired.Add("magic runtime");
        if (point >= InteractionRetainedUiCompositionPoint.UiHostAcquired)
            acquired.Add("retained UI lease");
        acquired.Reverse();
        return acquired.ToArray();
    }

    [Fact]
    public void PublicationFailureRollsBackCompleteUnpublishedPrefix()
    {
        using var fixture = new Fixture(retailUi: true, publicationFailure: true);

        Assert.Throws<InvalidOperationException>(fixture.Compose);
        Assert.False(
            fixture.Dependencies.Runtime.CaptureOwnership().IsDisposeRequested);

        Assert.Equal(
        [
            "retained UI lease",
            "magic runtime",
            "item interaction",
            "external container",
            "late bindings",
        ], fixture.Factory.Releases);
    }

    [Fact]
    public void ComposedChatViewModelWiresOnInterfaceTextToSpewBox()
    {
        using var fixture = new Fixture(retailUi: true);

        ChatVM chat = RetailInteractionRetainedUiCompositionFactory
            .CreateChatViewModel(fixture.Dependencies);

        Assert.NotNull(chat.OnInterfaceText);

        const string probeText = "consolidated-review SHOULD-FIX 2 probe";
        chat.ShowInterfaceText(probeText);
        fixture.Dependencies.Runtime.CommunicationOwner.SpewBox.Tick(0);

        Assert.Contains(
            fixture.Dependencies.Runtime.CommunicationOwner.SpewBox.Snapshot(),
            entry => entry.Text == probeText);
    }

    [Fact]
    public void GameWindowUsesPhaseAndContainsNoRetainedUiConstructionBody()
    {
        IReadOnlyList<CompiledCall> calls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));

        Assert.Single(
            calls,
            call => call.Target.DeclaringType
                    == typeof(InteractionRetainedUiCompositionPhase)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType
                    == typeof(ItemInteractionController)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(RetailUiRuntimeLease)
                && call.Target.Name == "AcquireHost");
        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(RetailUiRuntime)
                && call.Target.Name == nameof(RetailUiRuntime.CreateUninitialized));
        Assert.Null(typeof(GameWindow).GetMethod(
            "UseItemByGuid",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(GameWindow).GetMethod(
            "PickWorldGuidAtCursor",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly InteractionRetainedUiCompositionPoint? _failurePoint;

        public Fixture(
            bool retailUi,
            InteractionRetainedUiCompositionPoint? failurePoint = null,
            bool publicationFailure = false)
        {
            _failurePoint = failurePoint;
            Factory = new FakeFactory();
            Publication = new Publication(publicationFailure);
            RuntimeOptions options = RuntimeOptions.Parse("dat", static _ => null)
                with { RetailUi = retailUi };
            GameRuntime runtime = GameRuntimeTestFactory.Create(
                new NoopCombatOperations(),
                new NoopCombatTargetOperations(),
                new NoopCombatModeOperations(),
                new NoopSpellOperations());
            Dependencies = new InteractionRetainedUiDependencies(
                Options: options,
                Graphics: null!,
                BackbufferReader: static (_, _) => [],
                Window: null!,
                Input: null!,
                ShadersDirectory: "shaders",
                Dats: null!,
                DatLock: new object(),
                TextureCache: null!,
                DebugFont: null,
                HostQuiescence: null!,
                RetainedInputCapture: null!,
                InputDispatcher: null,
                TeleportSink:
                    new AcDream.App.Streaming.DeferredLocalPlayerTeleportNetworkSink(),
                KeyBindingsFilePath: "keybinds.json",
                Settings: null!,
                BuildingDegrades: new BuildingDegradeController(
                    () => DisplaySettings.Default),
                Runtime: runtime,
                CombatAttackOperations: new NoopCombatOperations(),
                CombatTargetOperations: new RuntimeCombatTargetOperationsSlot(),
                SpellCastOperations: new RuntimeSpellCastOperationsSlot(),
                MagicCatalog: null!,
                StackSplitQuantity: null!,
                UiRegistry: null,
                CombatModeCommands: null!,
                PlayerIdentity: null!,
                PlayerMode: null!,
                SelectionCameraFactory: static _ => Stub<SelectionCameraSource>(),
                FrameDiagnostics: new DeferredRenderFrameDiagnosticsSource(),
                ExistingVitals: null,
                Toast: null,
                ClientTime: static () => 0d,
                Log: static _ => { },
                GpuDevice: null!,
                GpuFrameSource: null!,
                CurrentCalendar: static () => default);
        }

        public InteractionRetainedUiDependencies Dependencies { get; }
        public FakeFactory Factory { get; }
        public Publication Publication { get; }
        public List<InteractionRetainedUiCompositionPoint> Points { get; } = [];

        public InteractionRetainedUiResult Compose() =>
            new InteractionRetainedUiCompositionPhase(
                Dependencies,
                new RetailUiRuntimeLease(),
                Publication,
                Factory,
                point =>
                {
                    Points.Add(point);
                    if (_failurePoint == point)
                        throw new InvalidOperationException($"fault at {point}");
                }).Compose();

        public void Dispose() => Dependencies.Runtime.Dispose();
    }

    private sealed class FakeFactory : IInteractionRetainedUiCompositionFactory
    {
        private readonly Dictionary<object, string> _names =
            new(ReferenceEqualityComparer.Instance);

        public List<string> Releases { get; } = [];
        public int RetainedUiCalls { get; private set; }

        public IDisposable BindCombatTarget(
            InteractionRetainedUiDependencies dependencies,
            DeferredSelectionUiAuthority selection) =>
            new NoopDisposable();

        public ExternalContainerLifecycleController CreateExternalContainerLifecycle(
            InteractionRetainedUiDependencies dependencies,
            DeferredLiveSessionUiAuthority session) =>
            Resource<ExternalContainerLifecycleController>("external container");

        public ItemInteractionController CreateItemInteraction(
            InteractionRetainedUiDependencies dependencies,
            InteractionUiLateBindings lateBindings) =>
            Resource<ItemInteractionController>("item interaction");

        public MagicRuntime CreateMagicRuntime(
            InteractionRetainedUiDependencies dependencies,
            InteractionUiLateBindings lateBindings,
            ItemInteractionController itemInteraction) =>
            Resource<MagicRuntime>("magic runtime");

        public RetainedUiComposition CreateRetainedUi(
            InteractionRetainedUiDependencies dependencies,
            InteractionUiLateBindings lateBindings,
            RetailUiRuntimeLease lease,
            RuntimeCombatAttackState combatAttack,
            ItemInteractionController itemInteraction,
            MagicRuntime magic,
            Action<InteractionRetainedUiCompositionPoint> checkpoint)
        {
            RetainedUiCalls++;
            _names.Add(lease, "retained UI lease");
            foreach (InteractionRetainedUiCompositionPoint point in UiPoints)
                checkpoint(point);
            return new RetainedUiComposition(
                Stub<UiHost>(),
                Stub<RetailUiRuntime>(),
                Stub<AcDream.UI.Abstractions.Panels.Vitals.VitalsVM>(),
                Stub<AcDream.UI.Abstractions.Panels.Chat.ChatVM>(),
                Stub<CharacterSheetProvider>(),
                null);
        }

        public void Release(IDisposable resource)
        {
            string name = _names.TryGetValue(resource, out string? found)
                ? found
                : resource switch
                {
                    InteractionUiLateBindings => "late bindings",
                    RetailUiRuntimeLease => "retained UI lease",
                    _ => throw new InvalidOperationException(
                        $"Unknown test resource {resource.GetType().Name}"),
                };
            Releases.Add(name);
        }

        private T Resource<T>(string name) where T : class
        {
            T value = Stub<T>();
            _names.Add(value, name);
            return value;
        }
    }

    private sealed class NoopCombatOperations
        : IRuntimeCombatAttackOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NoopSpellOperations : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }

    private sealed class NoopCombatTargetOperations
        : IRuntimeCombatTargetOperations
    {
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
    }

    private sealed class NoopCombatModeOperations
        : IRuntimeCombatModeOperations
    {
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
    }

    private sealed class Publication(bool fail)
        : IGameWindowInteractionRetainedUiPublication
    {
        public InteractionRetainedUiResult? Result { get; private set; }

        public void PublishInteractionRetainedUi(InteractionRetainedUiResult result)
        {
            if (fail)
                throw new InvalidOperationException("publication failed");
            Result = result;
        }
    }

    private static T Stub<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

}
