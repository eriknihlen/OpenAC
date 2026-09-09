using System.Numerics;
using System.Runtime.CompilerServices;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions;

namespace AcDream.App.Tests.Composition;

public sealed class InteractionUiRuntimeSourcesTests
{
    [Fact]
    public void SessionAuthorityDefaultsBindUnbindRebindAndDeactivateExactly()
    {
        var source = new DeferredLiveSessionUiAuthority();
        var first = new SessionTarget(true);
        var second = new SessionTarget(false);

        Assert.False(source.IsInWorld);
        Assert.Same(NullCommandBus.Instance, source.Commands);

        IDisposable stale = source.Bind(first);
        Assert.True(source.IsInWorld);
        Assert.Same(first.Commands, source.Commands);
        Assert.Throws<InvalidOperationException>(() => source.Bind(second));

        stale.Dispose();
        IDisposable current = source.Bind(second);
        stale.Dispose();
        Assert.False(source.IsInWorld);
        Assert.Same(second.Commands, source.Commands);

        source.Deactivate();
        current.Dispose();
        Assert.Same(NullCommandBus.Instance, source.Commands);
        Assert.Throws<ObjectDisposedException>(() => source.Bind(first));
    }

    [Fact]
    public void SelectionAuthorityUsesRetailSafeDefaultsAndExactBindingLifetime()
    {
        var source = new DeferredSelectionUiAuthority();
        var query = new SelectionQuery();
        SelectionInteractionController interactions =
            Stub<SelectionInteractionController>();

        Assert.True(source.IsWithinExternalContainerUseRange(1u));
        Assert.False(source.ShouldShowHealth(1u));
        Assert.Null(source.ResolveVividTargetInfo(1u));

        IDisposable binding = source.Bind(query, interactions);
        Assert.False(source.IsWithinExternalContainerUseRange(1u));
        Assert.True(source.ShouldShowHealth(1u));
        Assert.Equal(3f, source.ResolveVividTargetInfo(1u)?.SelectionSphereRadius);
        Assert.Throws<InvalidOperationException>(() => source.Bind(query, interactions));

        binding.Dispose();
        Assert.True(source.IsWithinExternalContainerUseRange(1u));
        source.Deactivate();
        Assert.Throws<ObjectDisposedException>(() => source.Bind(query, interactions));
    }

    [Fact]
    public void GameRuntimeCommandsCaptureTheExactCurrentGeneration()
    {
        var source = new DeferredGameRuntimeStateCommands();
        var first = new RuntimeTarget(new RuntimeGenerationToken(7));
        var second = new RuntimeTarget(new RuntimeGenerationToken(9));

        Assert.False(source.IsInWorld);
        Assert.Equal(
            RuntimeCommandStatus.Inactive,
            source.Advance(RuntimeAdvancementKind.Skill, 6u, 100u).Status);

        IDisposable stale = source.Bind(first, first);
        Assert.True(source.IsInWorld);
        Assert.True(source.Advance(
            RuntimeAdvancementKind.Skill,
            6u,
            100u).Accepted);
        Assert.Equal(new RuntimeGenerationToken(7), first.LastGeneration);
        Assert.Throws<InvalidOperationException>(() =>
            source.Bind(second, second));

        stale.Dispose();
        using IDisposable current = source.Bind(second, second);
        stale.Dispose();
        Assert.True(source.AddFavorite(0, 2, 42u).Accepted);
        Assert.Equal(new RuntimeGenerationToken(9), second.LastGeneration);
        Assert.Equal(
            RuntimeCommandStatus.Rejected,
            source.RemoveShortcut(uint.MaxValue).Status);

        source.Deactivate();
        Assert.Equal(
            RuntimeCommandStatus.Inactive,
            source.RemoveShortcut(3u).Status);
        Assert.Throws<ObjectDisposedException>(() => source.Bind(first, first));
    }

    [Fact]
    public void CharacterSelectionProjectionBorrowsExactAdapterAndEveryRouteBecomesInert()
    {
        var source = new DeferredGameRuntimeStateCommands();
        var target = new RuntimeTarget(new RuntimeGenerationToken(11));

        Assert.Null(source.CharacterSelection);
        Assert.Equal(RuntimeCommandStatus.Inactive,
            source.CharacterSelectionEnter().Status);

        IDisposable binding = source.Bind(target, target);
        Assert.Same(target, source.CharacterSelection);
        RuntimeCommandResult[] accepted =
        [
            source.CharacterSelectionHighlight(0x50000001u),
            source.CharacterSelectionEnter(),
            source.CharacterSelectionRequestDelete(),
            source.CharacterSelectionConfirmDelete(),
            source.CharacterSelectionRestore(),
            source.CharacterSelectionCancel(),
        ];
        Assert.All(accepted, result =>
        {
            Assert.True(result.Accepted);
            Assert.Equal(new RuntimeGenerationToken(11), result.Generation);
        });

        binding.Dispose();
        Assert.Null(source.CharacterSelection);
        Assert.Equal(RuntimeCommandStatus.Inactive,
            source.CharacterSelectionRestore().Status);

        source.Deactivate();
        Assert.Null(source.CharacterSelection);
        Assert.Equal(RuntimeCommandStatus.Inactive,
            source.CharacterSelectionCancel().Status);
    }

    [Fact]
    public void RadarNeverCachesAnUnboundBootstrapSnapshot()
    {
        var source = new DeferredRadarSnapshotSource();
        Assert.Same(UiRadarSnapshot.Empty, source.Snapshot());

        var provider = new RadarSnapshotProvider(
            new AcDream.Core.Items.ClientObjectTable(),
            EmptyRadarSource.Instance,
            static () => new Dictionary<uint, WorldSession.EntitySpawn>(),
            static () => 0u,
            static () => 0f,
            static () => 0u,
            static () => null,
            static () => false,
            static () => true);
        IDisposable binding = source.Bind(provider);

        Assert.True(source.Snapshot().UiLocked);
        binding.Dispose();
        Assert.False(source.Snapshot().UiLocked);
    }

    [Fact]
    public void AutomationProxyIsInertUntilExactRuntimeBinds()
    {
        var source = new DeferredWorldLifecycleAutomationRuntime();
        Assert.False(source.TryRequestCheckpoint(
            "early", out _, out string earlyError));
        Assert.Contains("not bound", earlyError);

        var target = new AutomationRuntime();
        IDisposable binding = source.Bind(target);
        Assert.True(source.IsWorldReady);
        Assert.True(source.TryRequestCheckpoint("ready", out _, out _));
        Assert.Equal("ready", target.LastCheckpoint);
        Assert.Equal(target.RenderPackStatus, source.RenderPackStatus);
        Assert.Equal(1280, source.FramebufferWidth);
        Assert.Equal(720, source.FramebufferHeight);
        Assert.True(source.TrySelectRenderPack("high", out _));
        Assert.Equal("high", target.LastRenderPackPreset);
        Assert.True(source.TryDisableRenderPack(out _));
        Assert.True(target.RenderPackDisabled);
        Assert.True(source.TryReenableRenderPack(out _));
        Assert.True(target.RenderPackReenabled);
        Assert.True(source.TryResizeFramebuffer(1024, 768, out _));
        Assert.Equal((1024, 768), target.LastFramebufferSize);

        binding.Dispose();
        Assert.False(source.IsWorldReady);
        Assert.Equal(
            AcDream.App.UI.Testing.RetailUiAutomationRenderPackStatus.Retail,
            source.RenderPackStatus);
        Assert.False(source.TrySelectRenderPack("high", out string unboundError));
        Assert.Contains("not bound", unboundError);
        source.Deactivate();
        Assert.Throws<ObjectDisposedException>(() => source.Bind(target));
    }

    [Fact]
    public void ExpectedOwnerBindingReleasesOnce()
    {
        object target = new();
        int releases = 0;
        var binding = new ExpectedOwnerBinding<object>(target, value =>
        {
            Assert.Same(target, value);
            releases++;
        });

        binding.Dispose();
        binding.Dispose();

        Assert.Equal(1, releases);
    }

    [Fact]
    public void RetainedInputCaptureBindingCannotClearAReplacement()
    {
        var slot = new RetainedUiInputCaptureSlot();
        var first = new UiRoot();
        var second = new UiRoot();

        IDisposable stale = slot.Bind(first);
        Assert.Same(first, slot.Root);
        Assert.Throws<InvalidOperationException>(() => slot.Bind(second));
        stale.Dispose();
        IDisposable current = slot.Bind(second);
        stale.Dispose();
        Assert.Same(second, slot.Root);
        current.Dispose();
        Assert.Null(slot.Root);
    }

    [Fact]
    public void LateBindingCleanupRetriesOnlyTheFailedSuffix()
    {
        var bindings = new InteractionUiLateBindings();
        var first = new RetryBinding(failures: 0);
        var second = new RetryBinding(failures: 1);
        bindings.AdoptLateOwnerBinding("first", first);
        bindings.AdoptLateOwnerBinding("second", second);

        Assert.Throws<AggregateException>(bindings.Dispose);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);

        bindings.Dispose();
        Assert.Equal(1, first.Calls);
        Assert.Equal(2, second.Calls);
        Assert.Throws<ObjectDisposedException>(() =>
            bindings.AdoptLateOwnerBinding("late", new RetryBinding(0)));
    }

    private sealed class SessionTarget(bool inWorld) : ILiveUiSessionTarget
    {
        public bool IsInWorld { get; } = inWorld;
        public WorldSession? CurrentSession => null;
        public ICommandBus Commands { get; } = new LiveCommandBus();
    }

    private sealed class RuntimeTarget
        : IGameRuntimeView,
          IGameRuntimeCommands,
          IRuntimeInventoryStateCommands,
          IRuntimeSpellbookCommands,
          IRuntimeCharacterCommands,
          IRuntimeCharacterSelectionView,
          IRuntimeCharacterSelectionCommands
    {
        public RuntimeTarget(RuntimeGenerationToken generation)
        {
            Generation = generation;
        }

        public RuntimeGenerationToken LastGeneration { get; private set; }
        public RuntimeGenerationToken Generation { get; }
        public RuntimeLifecycleSnapshot Lifecycle =>
            new(Generation, RuntimeLifecycleState.InWorld, 1u, true);
        public IGameRuntimeClock Clock => null!;
        public IRuntimeEntityView Entities => null!;
        public IRuntimeInventoryView Inventory => null!;
        public IRuntimeInventoryStateView InventoryState => null!;
        public IRuntimeCharacterView Character => null!;
        public IRuntimeSocialView Social => null!;
        public IRuntimeChatView Chat => null!;
        public IRuntimeCharacterSelectionView CharacterSelection => this;
        public IRuntimeFellowshipView Fellowship => null!;
        public IRuntimeAllegianceView Allegiance => null!;
        public IRuntimeActionView Actions => null!;
        public IRuntimeMovementView Movement => null!;
        public AcDream.Runtime.World.IRuntimeWorldEnvironmentView Environment =>
            null!;
        public IRuntimePortalView Portal => null!;
        public IRuntimeSessionCommands Session => null!;
        IRuntimeCharacterSelectionCommands IGameRuntimeCommands.CharacterSelection => this;
        public IRuntimeSelectionCommands Selection => null!;
        public IRuntimeCombatCommands Combat => null!;
        public IRuntimeMagicCommands Magic => null!;
        IRuntimeMovementCommands IGameRuntimeCommands.Movement => null!;
        IRuntimeChatCommands IGameRuntimeCommands.Chat => null!;
        IRuntimePortalCommands IGameRuntimeCommands.Portal => null!;
        IRuntimeInventoryStateCommands IGameRuntimeCommands.InventoryState => this;
        IRuntimeSpellbookCommands IGameRuntimeCommands.Spellbook => this;
        IRuntimeCharacterCommands IGameRuntimeCommands.Character => this;
        IRuntimeSocialCommands IGameRuntimeCommands.Social => null!;
        IRuntimeFellowshipCommands IGameRuntimeCommands.Fellowship => null!;
        IRuntimeAllegianceCommands IGameRuntimeCommands.Allegiance => null!;

        public RuntimeStateCheckpoint CaptureCheckpoint() => default;

        RuntimeCharacterSelectionSnapshot IRuntimeCharacterSelectionView.Snapshot =>
            new(
                Generation,
                RuntimeCharacterSelectionLifecycle.AwaitingSelection,
                Revision: 1,
                AccountName: "account",
                SlotCount: 0,
                RosterCount: 0,
                WorldName: string.Empty,
                HighlightedCharacterId: 0u,
                HighlightedDisplayIndex: -1,
                PendingDeleteCharacterId: 0u,
                LastRestoreRequestedCharacterId: 0u,
                Operation: RuntimeCharacterSelectionOperation.None,
                Error: null,
                Buttons: RuntimeCharacterSelectionButtons.None);

        public bool TryGetAt(
            int displayIndex,
            out RuntimeCharacterSelectionEntry character)
        {
            character = default;
            return false;
        }

        public bool TryGet(
            uint characterId,
            out RuntimeCharacterSelectionEntry character)
        {
            character = default;
            return false;
        }

        public void Visit(IRuntimeCharacterSelectionVisitor visitor) { }

        public IDisposable Subscribe(IRuntimeCharacterSelectionObserver observer) =>
            EmptyDisposable.Instance;

        public RuntimeCommandResult Highlight(
            RuntimeGenerationToken expectedGeneration,
            uint characterId) => Accepted(expectedGeneration, characterId);

        public RuntimeCommandResult Enter(
            RuntimeGenerationToken expectedGeneration) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult RequestDelete(
            RuntimeGenerationToken expectedGeneration) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult ConfirmDelete(
            RuntimeGenerationToken expectedGeneration) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult Restore(
            RuntimeGenerationToken expectedGeneration) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult Cancel(
            RuntimeGenerationToken expectedGeneration) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult AddShortcut(
            RuntimeGenerationToken expectedGeneration,
            in RuntimeShortcutCommand command) =>
            Accepted(expectedGeneration, command.ObjectId);

        public RuntimeCommandResult RemoveShortcut(
            RuntimeGenerationToken expectedGeneration,
            int index) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult AddFavorite(
            RuntimeGenerationToken expectedGeneration,
            int tabIndex,
            int position,
            uint spellId) =>
            Accepted(expectedGeneration, spellId);

        public RuntimeCommandResult RemoveFavorite(
            RuntimeGenerationToken expectedGeneration,
            int tabIndex,
            uint spellId) =>
            Accepted(expectedGeneration, spellId);

        public RuntimeCommandResult SetFilter(
            RuntimeGenerationToken expectedGeneration,
            uint filters) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult ForgetSpell(
            RuntimeGenerationToken expectedGeneration,
            uint spellId) =>
            Accepted(expectedGeneration, spellId);

        public RuntimeCommandResult SetDesiredComponent(
            RuntimeGenerationToken expectedGeneration,
            uint componentId,
            uint amount) =>
            Accepted(expectedGeneration, componentId);

        public RuntimeCommandResult ClearDesiredComponents(
            RuntimeGenerationToken expectedGeneration) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult Advance(
            RuntimeGenerationToken expectedGeneration,
            in RuntimeAdvancementCommand command) =>
            Accepted(expectedGeneration, command.StatId);

        public RuntimeCommandResult SetSingleOption(
            RuntimeGenerationToken expectedGeneration,
            uint optionId,
            bool value) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult SaveOptions(
            RuntimeGenerationToken expectedGeneration) =>
            Accepted(expectedGeneration);

        public RuntimeCommandResult SetTitle(
            RuntimeGenerationToken expectedGeneration,
            uint titleId) =>
            Accepted(expectedGeneration, titleId);

        private RuntimeCommandResult Accepted(
            RuntimeGenerationToken generation,
            uint objectId = 0u)
        {
            LastGeneration = generation;
            return new RuntimeCommandResult(
                RuntimeCommandStatus.Accepted,
                generation,
                objectId);
        }
    }

    private sealed class SelectionQuery : IRetainedUiSelectionQuery
    {
        public bool ShouldShowHealth(uint serverGuid) => true;
        public VividTargetInfo? ResolveVividTargetInfo(uint serverGuid) =>
            new(Vector3.Zero, 3f, 0u, 0u);
        public bool IsWithinExternalContainerUseRange(uint serverGuid) => false;
    }

    private sealed class AutomationRuntime
        : AcDream.App.UI.Testing.IRetailUiAutomationRuntime
    {
        private sealed record CompletedCheckpoint(int Sequence, string Name)
            : AcDream.App.UI.Testing.IRetailUiAutomationCheckpoint
        {
            public AcDream.App.UI.Testing.RetailUiAutomationCheckpointStatus Status =>
                AcDream.App.UI.Testing.RetailUiAutomationCheckpointStatus.Succeeded;
            public string? Error => null;
        }

        public bool IsWorldReady => true;
        public bool IsWorldViewportVisible => true;
        public int PortalMaterializationCount => 2;
        public AcDream.App.UI.Testing.RetailUiAutomationRenderPackStatus
            RenderPackStatus { get; } = new(
                AcDream.App.UI.Testing.RetailUiAutomationRenderPackState.Active,
                "acdream.atmospheric",
                "high",
                7,
                null);
        public int FramebufferWidth => 1280;
        public int FramebufferHeight => 720;
        public string? LastCheckpoint { get; private set; }
        public string? LastRenderPackPreset { get; private set; }
        public bool RenderPackDisabled { get; private set; }
        public bool RenderPackReenabled { get; private set; }
        public (int Width, int Height)? LastFramebufferSize { get; private set; }

        public bool TrySelectRenderPack(string presetId, out string error)
        {
            LastRenderPackPreset = presetId;
            error = string.Empty;
            return true;
        }

        public bool TryDisableRenderPack(out string error)
        {
            RenderPackDisabled = true;
            error = string.Empty;
            return true;
        }

        public bool TryReenableRenderPack(out string error)
        {
            RenderPackReenabled = true;
            error = string.Empty;
            return true;
        }

        public bool TryResizeFramebuffer(int width, int height, out string error)
        {
            LastFramebufferSize = (width, height);
            error = string.Empty;
            return true;
        }

        public bool TryRequestCheckpoint(
            string name,
            out AcDream.App.UI.Testing.IRetailUiAutomationCheckpoint? checkpoint,
            out string error)
        {
            LastCheckpoint = name;
            checkpoint = new CompletedCheckpoint(1, name);
            error = string.Empty;
            return true;
        }

        public void CancelCheckpoint(
            AcDream.App.UI.Testing.IRetailUiAutomationCheckpoint checkpoint)
        {
        }

        public bool TryRequestScreenshot(string name, out string error)
        {
            error = string.Empty;
            return true;
        }

        public bool IsScreenshotComplete(string name) => true;
    }

    private sealed class RetryBinding(int failures) : IDisposable
    {
        private int _failures = failures;
        public int Calls { get; private set; }

        public void Dispose()
        {
            Calls++;
            if (_failures > 0)
            {
                _failures--;
                throw new InvalidOperationException("retry");
            }
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class EmptyRadarSource : ILiveEntityRadarSource
    {
        public static EmptyRadarSource Instance { get; } = new();

        public bool TryGetMaterialized(
            uint serverGuid,
            out WorldEntity entity)
        {
            entity = null!;
            return false;
        }

        public bool TryGetVisible(uint serverGuid, out WorldEntity entity)
        {
            entity = null!;
            return false;
        }

        public void CopyVisibleTo(
            List<KeyValuePair<uint, WorldEntity>> destination) =>
            destination.Clear();
    }

    private static T Stub<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
