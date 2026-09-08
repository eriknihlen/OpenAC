using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.UI.Testing;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions;
using Silk.NET.Windowing;

namespace AcDream.App.Composition;

internal sealed class DeferredGameRuntimeStateCommands
{
    private readonly object _gate = new();
    private IGameRuntimeView? _view;
    private IGameRuntimeCommands? _commands;
    private bool _deactivated;

    public bool IsInWorld
    {
        get
        {
            lock (_gate)
                return !_deactivated
                    && _view?.Lifecycle.State == RuntimeLifecycleState.InWorld;
        }
    }

    public IRuntimeCharacterSelectionView? CharacterSelection
    {
        get
        {
            lock (_gate)
                return !_deactivated && _view is not null
                    ? _view.CharacterSelection
                    : null;
        }
    }

    public IRuntimeCharacterCreationView? CharacterCreation
    {
        get
        {
            lock (_gate)
                return !_deactivated && _view is not null
                    ? _view.CharacterCreation
                    : null;
        }
    }

    public IDisposable Bind(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deactivated, this);
            if (_view is not null || _commands is not null)
            {
                throw new InvalidOperationException(
                    "The retained-UI game-runtime command seam is already bound.");
            }
            _view = view;
            _commands = commands;
        }
        return new ExpectedRuntimeBinding(this, view, commands);
    }

    public RuntimeCommandResult AddShortcut(ShortcutEntry entry) =>
        Invoke((commands, generation) => commands.InventoryState.AddShortcut(
            generation,
            new RuntimeShortcutCommand(
                entry.Index,
                entry.ObjectId,
                entry.SpellId)));

    public RuntimeCommandResult RemoveShortcut(uint index)
    {
        if (index > int.MaxValue)
            return CurrentResult(RuntimeCommandStatus.Rejected);
        return Invoke((commands, generation) =>
            commands.InventoryState.RemoveShortcut(
                generation,
                (int)index));
    }

    public RuntimeCommandResult AddFavorite(
        int tab,
        int position,
        uint spellId) =>
        Invoke((commands, generation) => commands.Spellbook.AddFavorite(
            generation,
            tab,
            position,
            spellId));

    public RuntimeCommandResult RemoveFavorite(int tab, uint spellId) =>
        Invoke((commands, generation) => commands.Spellbook.RemoveFavorite(
            generation,
            tab,
            spellId));

    public RuntimeCommandResult SetSpellbookFilter(uint filters) =>
        Invoke((commands, generation) => commands.Spellbook.SetFilter(
            generation,
            filters));

    public RuntimeCommandResult ForgetSpell(uint spellId) =>
        Invoke((commands, generation) => commands.Spellbook.ForgetSpell(
            generation,
            spellId));

    public RuntimeCommandResult SetDesiredComponent(
        uint componentId,
        uint amount) =>
        Invoke((commands, generation) =>
            commands.Spellbook.SetDesiredComponent(
                generation,
                componentId,
                amount));

    public RuntimeCommandResult Advance(
        RuntimeAdvancementKind kind,
        uint statId,
        ulong cost) =>
        Invoke((commands, generation) => commands.Character.Advance(
            generation,
            new RuntimeAdvancementCommand(kind, statId, cost)));

    public RuntimeCommandResult SetTitle(uint titleId) =>
        Invoke((commands, generation) => commands.Character.SetTitle(
            generation,
            titleId));


    public RuntimeCommandResult CharacterSelectionHighlight(uint characterId) =>
        Invoke((commands, generation) =>
            commands.CharacterSelection.Highlight(generation, characterId));

    public RuntimeCommandResult CharacterSelectionEnter() =>
        Invoke((commands, generation) =>
            commands.CharacterSelection.Enter(generation));

    public RuntimeCommandResult CharacterSelectionRequestDelete() =>
        Invoke((commands, generation) =>
            commands.CharacterSelection.RequestDelete(generation));

    public RuntimeCommandResult CharacterSelectionConfirmDelete() =>
        Invoke((commands, generation) =>
            commands.CharacterSelection.ConfirmDelete(generation));

    public RuntimeCommandResult CharacterSelectionRestore() =>
        Invoke((commands, generation) =>
            commands.CharacterSelection.Restore(generation));

    public RuntimeCommandResult CharacterSelectionCancel() =>
        Invoke((commands, generation) =>
            commands.CharacterSelection.Cancel(generation));


    public RuntimeCommandResult CharacterCreationSelectHeritage(uint heritageId) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SelectHeritage(generation, heritageId));

    public RuntimeCommandResult CharacterCreationSelectGender(uint genderKey) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SelectGender(generation, genderKey));

    public RuntimeCommandResult CharacterCreationSelectTemplate(uint templateIndex) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SelectTemplate(generation, templateIndex));

    public RuntimeCommandResult CharacterCreationSetAttribute(
        ChargenAttributeId attributeId,
        int value) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SetAttribute(generation, attributeId, value));

    public RuntimeCommandResult CharacterCreationSetAttributeLock(
        ChargenAttributeId attributeId,
        bool locked) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SetAttributeLock(generation, attributeId, locked));

    public RuntimeCommandResult CharacterCreationTrainSkill(uint skillId) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.TrainSkill(generation, skillId));

    public RuntimeCommandResult CharacterCreationSpecializeSkill(uint skillId) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SpecializeSkill(generation, skillId));

    public RuntimeCommandResult CharacterCreationUntrainSkill(uint skillId) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.UntrainSkill(generation, skillId));

    public RuntimeCommandResult CharacterCreationSelectStartArea(int startAreaIndex) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SelectStartArea(generation, startAreaIndex));

    public RuntimeCommandResult CharacterCreationFinish(bool confirmUnspentCredits) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.Finish(generation, confirmUnspentCredits));


    public RuntimeCommandResult CharacterCreationSetAppearanceIndex(
        ChargenAppearanceSlot slot,
        uint index) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SetAppearanceIndex(generation, slot, index));

    public RuntimeCommandResult CharacterCreationSetShade(
        ChargenShadeSlot slot,
        double value) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SetShade(generation, slot, value));


    public RuntimeCommandResult CharacterCreationSetName(string name) =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.SetName(generation, name));

    public RuntimeCommandResult CharacterCreationAcknowledgeRejection() =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.AcknowledgeRejection(generation));

    public RuntimeCommandResult CharacterCreationRandomizeCharacter() =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.RandomizeCharacter(generation));

    public RuntimeCommandResult CharacterCreationRandomizeAppearance() =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.RandomizeAppearance(generation));

    public RuntimeCommandResult CharacterCreationRandomizeClothing() =>
        Invoke((commands, generation) =>
            commands.CharacterCreation.RandomizeClothing(generation));


    public RuntimeCommandResult FellowshipCreate(string fellowshipName, bool shareXp) =>
        Invoke((commands, generation) => commands.Fellowship.Create(
            generation, fellowshipName, shareXp));

    public RuntimeCommandResult FellowshipRecruit(uint targetGuid) =>
        Invoke((commands, generation) => commands.Fellowship.Recruit(
            generation, targetGuid));

    public RuntimeCommandResult FellowshipDismiss(uint targetGuid) =>
        Invoke((commands, generation) => commands.Fellowship.Dismiss(
            generation, targetGuid));

    public RuntimeCommandResult FellowshipQuit(bool disband) =>
        Invoke((commands, generation) => commands.Fellowship.Quit(
            generation, disband));

    public RuntimeCommandResult FellowshipAssignLeader(uint newLeaderGuid) =>
        Invoke((commands, generation) => commands.Fellowship.AssignLeader(
            generation, newLeaderGuid));

    public RuntimeCommandResult FellowshipSetOpen(bool isOpen) =>
        Invoke((commands, generation) => commands.Fellowship.SetOpen(
            generation, isOpen));

    public RuntimeCommandResult FellowshipSetPanelOpen(bool panelOpen) =>
        Invoke((commands, generation) => commands.Fellowship.SetPanelOpen(
            generation, panelOpen));


    public RuntimeCommandResult AllegianceSwear(uint patronGuid) =>
        Invoke((commands, generation) => commands.Allegiance.Swear(
            generation, patronGuid));

    public RuntimeCommandResult AllegianceBreak(uint targetGuid) =>
        Invoke((commands, generation) => commands.Allegiance.Break(
            generation, targetGuid));

    public RuntimeCommandResult AllegianceKick(uint vassalGuid) =>
        Invoke((commands, generation) => commands.Allegiance.Kick(
            generation, vassalGuid));

    public RuntimeCommandResult AllegianceSetUpdateSubscription(bool on) =>
        Invoke((commands, generation) => commands.Allegiance.SetUpdateSubscription(
            generation, on));

    public void Deactivate()
    {
        lock (_gate)
        {
            _deactivated = true;
            _view = null;
            _commands = null;
        }
    }

    private RuntimeCommandResult Invoke(
        Func<
            IGameRuntimeCommands,
            RuntimeGenerationToken,
            RuntimeCommandResult> invoke)
    {
        IGameRuntimeCommands commands;
        RuntimeGenerationToken generation;
        lock (_gate)
        {
            if (_deactivated || _view is null || _commands is null)
            {
                return new RuntimeCommandResult(
                    RuntimeCommandStatus.Inactive,
                    _view?.Generation ?? default);
            }
            commands = _commands;
            generation = _view.Generation;
        }
        return invoke(commands, generation);
    }

    private RuntimeCommandResult CurrentResult(RuntimeCommandStatus status)
    {
        lock (_gate)
        {
            return new RuntimeCommandResult(
                status,
                _view?.Generation ?? default);
        }
    }

    private void Release(
        IGameRuntimeView expectedView,
        IGameRuntimeCommands expectedCommands)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_view, expectedView)
                || !ReferenceEquals(_commands, expectedCommands))
            {
                return;
            }
            _view = null;
            _commands = null;
        }
    }

    private sealed class ExpectedRuntimeBinding(
        DeferredGameRuntimeStateCommands owner,
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
        : IDisposable
    {
        private DeferredGameRuntimeStateCommands? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(view, commands);
    }
}

internal sealed class DeferredLiveSessionUiAuthority
    : ILiveInWorldSource,
      ILiveWorldSessionSource
{
    private readonly object _gate = new();
    private ILiveUiSessionTarget? _target;
    private bool _deactivated;

    public bool IsInWorld
    {
        get
        {
            lock (_gate)
                return !_deactivated && _target?.IsInWorld == true;
        }
    }

    public WorldSession? CurrentSession
    {
        get
        {
            lock (_gate)
                return !_deactivated ? _target?.CurrentSession : null;
        }
    }

    public ICommandBus Commands
    {
        get
        {
            lock (_gate)
                return !_deactivated
                    ? _target?.Commands ?? NullCommandBus.Instance
                    : NullCommandBus.Instance;
        }
    }

    public string? AccountName => CurrentSession?.Characters?.AccountName;

    public LinkStatusSnapshot LinkStatus =>
        CurrentSession?.LinkStatus ?? LinkStatusSnapshot.Disconnected;

    public IDisposable Bind(ILiveUiSessionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deactivated, this);
            if (_target is not null)
            {
                throw new InvalidOperationException(
                    "The retained-UI live-session authority is already bound.");
            }

            _target = target;
        }

        return new ExpectedOwnerBinding<ILiveUiSessionTarget>(
            target,
            Release);
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _deactivated = true;
            _target = null;
        }
    }

    public bool TryUseItem(uint guid, Action<string>? log = null)
    {
        WorldSession? session = CurrentSession;
        if (!IsInWorld || session is null)
            return false;

        uint sequence = session.NextGameActionSequence();
        session.SendGameAction(InteractRequests.BuildUse(sequence, guid));
        log?.Invoke($"[D.5.1] toolbar use-item guid=0x{guid:X8} seq={sequence}");
        return true;
    }

    private void Release(ILiveUiSessionTarget expected)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_target, expected))
                _target = null;
        }
    }
}

internal sealed class DeferredSelectionUiAuthority
{
    private readonly object _gate = new();
    private IRetainedUiSelectionQuery? _query;
    private SelectionInteractionController? _interactions;
    private bool _deactivated;

    public IDisposable Bind(
        IRetainedUiSelectionQuery query,
        SelectionInteractionController interactions)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(interactions);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deactivated, this);
            if (_query is not null || _interactions is not null)
            {
                throw new InvalidOperationException(
                    "The retained-UI selection authority is already bound.");
            }

            _query = query;
            _interactions = interactions;
        }

        return new ExpectedSelectionBinding(this, query, interactions);
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _deactivated = true;
            _query = null;
            _interactions = null;
        }
    }

    public uint? PickAtCursor(bool includeSelf)
    {
        SelectionInteractionController? target;
        lock (_gate)
            target = !_deactivated ? _interactions : null;
        return target?.PickAtCursor(includeSelf);
    }

    public void SendUse(uint guid)
    {
        SelectionInteractionController? target;
        lock (_gate)
            target = !_deactivated ? _interactions : null;
        target?.SendUse(guid);
    }

    public void RequestUse(uint guid, ItemUseRequestReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        SelectionInteractionController? target;
        lock (_gate)
            target = !_deactivated ? _interactions : null;
        if (target is null)
            reservation.CancelBeforeDispatch();
        else
            target.RequestUse(guid, reservation);
    }

    public void SendPickup(uint itemGuid, uint destinationContainerId, int placement)
    {
        SelectionInteractionController? target;
        lock (_gate)
            target = !_deactivated ? _interactions : null;
        target?.SendPickup(itemGuid, destinationContainerId, placement);
    }

    public uint? SelectClosestCombatTarget(bool showToast)
    {
        SelectionInteractionController? target;
        lock (_gate)
            target = !_deactivated ? _interactions : null;
        return target?.SelectClosestCombatTarget(showToast);
    }

    public bool IsWithinExternalContainerUseRange(uint targetGuid)
    {
        IRetainedUiSelectionQuery? query;
        lock (_gate)
            query = !_deactivated ? _query : null;
        return query?.IsWithinExternalContainerUseRange(targetGuid) != false;
    }

    public bool ShouldShowHealth(uint guid)
    {
        IRetainedUiSelectionQuery? query;
        lock (_gate)
            query = !_deactivated ? _query : null;
        return query?.ShouldShowHealth(guid) == true;
    }

    public VividTargetInfo? ResolveVividTargetInfo(uint guid)
    {
        IRetainedUiSelectionQuery? query;
        lock (_gate)
            query = !_deactivated ? _query : null;
        return query?.ResolveVividTargetInfo(guid);
    }

    private void Release(
        IRetainedUiSelectionQuery expectedQuery,
        SelectionInteractionController expectedInteractions)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_query, expectedQuery)
                && ReferenceEquals(_interactions, expectedInteractions))
            {
                _query = null;
                _interactions = null;
            }
        }
    }

    private sealed class ExpectedSelectionBinding : IDisposable
    {
        private DeferredSelectionUiAuthority? _owner;
        private readonly IRetainedUiSelectionQuery _query;
        private readonly SelectionInteractionController _interactions;

        public ExpectedSelectionBinding(
            DeferredSelectionUiAuthority owner,
            IRetainedUiSelectionQuery query,
            SelectionInteractionController interactions)
        {
            _owner = owner;
            _query = query;
            _interactions = interactions;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(_query, _interactions);
    }
}

internal sealed class DeferredSelectionViewPlaneSource
{
    private ISelectionViewPlaneSource? _target;
    private bool _deactivated;

    public IDisposable Bind(ISelectionViewPlaneSource target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_target is not null)
        {
            throw new InvalidOperationException(
                "The retained-UI selection view plane is already bound.");
        }

        _target = target;
        return new ExpectedOwnerBinding<ISelectionViewPlaneSource>(target, Release);
    }

    public ICamera Apply(ICamera camera) =>
        !_deactivated && _target is { } target
            ? target.ApplyViewPlane(camera)
            : camera;

    public void Deactivate()
    {
        _deactivated = true;
        _target = null;
    }

    private void Release(ISelectionViewPlaneSource expected)
    {
        if (ReferenceEquals(_target, expected))
            _target = null;
    }
}

internal sealed class SelectionCameraSource
{
    private readonly CameraController _camera;
    private readonly IView _window;
    private readonly DeferredSelectionViewPlaneSource _viewPlane;

    public SelectionCameraSource(
        CameraController camera,
        IView window,
        DeferredSelectionViewPlaneSource viewPlane)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _viewPlane = viewPlane ?? throw new ArgumentNullException(nameof(viewPlane));
    }

    public SelectionCameraSnapshot Snapshot()
    {
        ICamera camera = _viewPlane.Apply(_camera.Active);
        return new SelectionCameraSnapshot(
            camera.View,
            camera.Projection,
            new Vector2(_window.Size.X, _window.Size.Y));
    }

    public (Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport) UiSnapshot()
    {
        SelectionCameraSnapshot snapshot = Snapshot();
        return (snapshot.View, snapshot.Projection, snapshot.Viewport);
    }
}

internal sealed class DeferredRadarSnapshotSource
{
    private RadarSnapshotProvider? _target;
    private bool _deactivated;

    public UiRadarSnapshot Snapshot() =>
        !_deactivated && _target is { } target
            ? target.BuildSnapshot()
            : UiRadarSnapshot.Empty;

    public IDisposable Bind(RadarSnapshotProvider target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_target is not null)
            throw new InvalidOperationException("The retained-UI radar is already bound.");
        _target = target;
        return new ExpectedOwnerBinding<RadarSnapshotProvider>(target, Release);
    }

    public void Deactivate()
    {
        _deactivated = true;
        _target = null;
    }

    private void Release(RadarSnapshotProvider expected)
    {
        if (ReferenceEquals(_target, expected))
            _target = null;
    }
}

internal sealed class DeferredInventoryContainerSource
{
    private RetailUiRuntime? _runtime;
    private bool _deactivated;

    public uint Current(uint playerGuid) =>
        !_deactivated
            ? _runtime?.InventoryPanelController?.CurrentOpenContainerId ?? playerGuid
            : playerGuid;

    public IDisposable Bind(RetailUiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_runtime is not null)
            throw new InvalidOperationException("The retained inventory container is already bound.");
        _runtime = runtime;
        return new ExpectedOwnerBinding<RetailUiRuntime>(runtime, Release);
    }

    public void Deactivate()
    {
        _deactivated = true;
        _runtime = null;
    }

    private void Release(RetailUiRuntime expected)
    {
        if (ReferenceEquals(_runtime, expected))
            _runtime = null;
    }
}

internal sealed class DeferredWorldLifecycleAutomationRuntime
    : IRetailUiAutomationRuntime
{
    private IRetailUiAutomationRuntime? _target;
    private bool _deactivated;

    public bool IsWorldReady => !_deactivated && _target?.IsWorldReady == true;
    public bool IsWorldViewportVisible =>
        !_deactivated && _target?.IsWorldViewportVisible == true;
    public int PortalMaterializationCount =>
        !_deactivated ? _target?.PortalMaterializationCount ?? 0 : 0;
    public int RenderPackPerformanceSampleCount =>
        !_deactivated ? _target?.RenderPackPerformanceSampleCount ?? 0 : 0;
    public bool RenderPackFailedToRetail =>
        !_deactivated && _target?.RenderPackFailedToRetail == true;
    public RetailUiAutomationRenderPackStatus RenderPackStatus =>
        !_deactivated
            ? _target?.RenderPackStatus
                ?? RetailUiAutomationRenderPackStatus.Retail
            : RetailUiAutomationRenderPackStatus.Retail;
    public int FramebufferWidth =>
        !_deactivated ? _target?.FramebufferWidth ?? 0 : 0;
    public int FramebufferHeight =>
        !_deactivated ? _target?.FramebufferHeight ?? 0 : 0;

    public bool TrySelectRenderPack(string presetId, out string error)
    {
        if (!_deactivated && _target is { } target)
            return target.TrySelectRenderPack(presetId, out error);
        error = "world lifecycle automation is not bound";
        return false;
    }

    public bool TryDisableRenderPack(out string error)
    {
        if (!_deactivated && _target is { } target)
            return target.TryDisableRenderPack(out error);
        error = "world lifecycle automation is not bound";
        return false;
    }

    public bool TryReenableRenderPack(out string error)
    {
        if (!_deactivated && _target is { } target)
            return target.TryReenableRenderPack(out error);
        error = "world lifecycle automation is not bound";
        return false;
    }

    public bool TryResizeFramebuffer(int width, int height, out string error)
    {
        if (!_deactivated && _target is { } target)
            return target.TryResizeFramebuffer(width, height, out error);
        error = "world lifecycle automation is not bound";
        return false;
    }

    public bool TryResetRenderPackPerformance(out string error)
    {
        if (!_deactivated && _target is { } target)
            return target.TryResetRenderPackPerformance(out error);
        error = "world lifecycle automation is not bound";
        return false;
    }

    public bool TryRequestClientClose(out string error)
    {
        if (!_deactivated && _target is { } target)
            return target.TryRequestClientClose(out error);
        error = "world lifecycle automation is not bound";
        return false;
    }

    public IDisposable Bind(IRetailUiAutomationRuntime target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_target is not null)
            throw new InvalidOperationException("World lifecycle automation is already bound.");
        _target = target;
        return new ExpectedOwnerBinding<IRetailUiAutomationRuntime>(target, Release);
    }

    public bool TryRequestCheckpoint(
        string name,
        out IRetailUiAutomationCheckpoint? checkpoint,
        out string error)
    {
        if (!_deactivated && _target is { } target)
            return target.TryRequestCheckpoint(name, out checkpoint, out error);
        checkpoint = null;
        error = "world lifecycle automation is not bound";
        return false;
    }

    public void CancelCheckpoint(IRetailUiAutomationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!_deactivated && _target is { } target)
            target.CancelCheckpoint(checkpoint);
    }

    public bool TryRequestScreenshot(string name, out string error)
    {
        if (!_deactivated && _target is { } target)
            return target.TryRequestScreenshot(name, out error);
        error = "world lifecycle automation is not bound";
        return false;
    }

    public bool IsScreenshotComplete(string name) =>
        !_deactivated && _target?.IsScreenshotComplete(name) == true;

    public void Deactivate()
    {
        _deactivated = true;
        _target = null;
    }

    private void Release(IRetailUiAutomationRuntime expected)
    {
        if (ReferenceEquals(_target, expected))
            _target = null;
    }
}

internal sealed class ExpectedOwnerBinding<T> : IDisposable where T : class
{
    private Action<T>? _release;
    private readonly T _expected;

    public ExpectedOwnerBinding(T expected, Action<T> release)
    {
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(_expected);
}
