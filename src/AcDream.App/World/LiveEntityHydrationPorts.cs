using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.App.World;

internal sealed class LiveEntityRelationshipProjection(
    EquippedChildRenderController children) : ILiveEntityRelationshipProjection
{
    private readonly EquippedChildRenderController _children =
        children ?? throw new ArgumentNullException(nameof(children));

    public void OnSpawn(WorldSession.EntitySpawn spawn) => _children.OnSpawn(spawn);
    public void OnParent(ParentEvent.Parsed update) => _children.OnParentEvent(update);
    public void OnCreateParentAccepted(CreateParentUpdate update) =>
        _children.OnCreateParentAccepted(update);
    public ChildUnparentDisposition OnChildBecameUnparented(uint childGuid) =>
        _children.OnChildBecameUnparented(childGuid);
    public bool TryApplyAttachedAppearance(
        LiveEntityRecord record,
        ulong objDescAuthorityVersion) =>
        _children.TryApplyAttachedAppearance(record, objDescAuthorityVersion);
}

internal sealed class DeferredLiveEntityParentAcceptance
{
    private Func<ParentEvent.Parsed, bool>? _accept;

    public void Bind(Func<ParentEvent.Parsed, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        if (Interlocked.CompareExchange(ref _accept, accept, null) is not null)
            throw new InvalidOperationException("Live parent acceptance is already bound.");
    }

    public IDisposable BindOwned(Func<ParentEvent.Parsed, bool> accept)
    {
        Bind(accept);
        return new Binding(this, accept);
    }

    private void Unbind(Func<ParentEvent.Parsed, bool> expected)
    {
        _ = Interlocked.CompareExchange(ref _accept, null, expected);
    }

    public bool TryAccept(ParentEvent.Parsed update) =>
        (_accept ?? throw new InvalidOperationException(
            "Live parent acceptance must be bound before a session starts."))(update);

    private sealed class Binding : IDisposable
    {
        private DeferredLiveEntityParentAcceptance? _owner;
        private readonly Func<ParentEvent.Parsed, bool> _expected;

        public Binding(
            DeferredLiveEntityParentAcceptance owner,
            Func<ParentEvent.Parsed, bool> expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}

internal sealed class LiveEntityReadyPublisher : ILiveEntityReadyPublisher
{
    private readonly LiveEntityRuntime _runtime;
    private readonly Func<LiveEntityRecord, bool> _prepareEffects;
    private readonly Func<LiveEntityRecord, bool> _presentState;
    private readonly Func<LiveEntityRecord, bool> _replayEffects;
    private readonly ILiveRenderProjectionSink? _renderProjection;

    public LiveEntityReadyPublisher(
        LiveEntityRuntime runtime,
        EntityEffectController effects,
        LiveEntityPresentationController presentation,
        ILiveRenderProjectionSink? renderProjection = null)
        : this(
            runtime,
            record => effects.PrepareLiveEntityOwner(record.ServerGuid),
            record => presentation.OnLiveEntityReady(record.ServerGuid),
            record => effects.ReplayPendingForLiveEntity(record.ServerGuid),
            renderProjection)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(presentation);
    }

    internal LiveEntityReadyPublisher(
        LiveEntityRuntime runtime,
        Func<LiveEntityRecord, bool> prepareEffects,
        Func<LiveEntityRecord, bool> presentState,
        Func<LiveEntityRecord, bool> replayEffects,
        ILiveRenderProjectionSink? renderProjection = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _prepareEffects = prepareEffects
            ?? throw new ArgumentNullException(nameof(prepareEffects));
        _presentState = presentState
            ?? throw new ArgumentNullException(nameof(presentState));
        _replayEffects = replayEffects
            ?? throw new ArgumentNullException(nameof(replayEffects));
        _renderProjection = renderProjection;
    }

    public bool Publish(LiveEntityReadyCandidate candidate)
    {
        LiveEntityRecord expectedRecord = candidate.Record;
        ArgumentNullException.ThrowIfNull(expectedRecord);
        if (!candidate.IsCurrent(_runtime)
            || !_prepareEffects(expectedRecord)
            || !candidate.IsCurrent(_runtime))
            return false;

        if (!_presentState(expectedRecord)
            || !candidate.IsCurrent(_runtime))
        {
            return false;
        }

        if (!_replayEffects(expectedRecord)
            || !candidate.IsCurrent(_runtime))
        {
            return false;
        }

        return (_renderProjection?.OnEntityReady(candidate) ?? true)
            && candidate.IsCurrent(_runtime);
    }
}

internal sealed class LiveEntityWorldOriginCoordinator : ILiveEntityWorldOriginCoordinator
{
    private readonly LiveWorldOriginState _origin;
    private readonly StreamingController _streaming;
    private readonly GpuWorldState _worldState;
    private readonly WorldRevealCoordinator _worldReveal;
    private sealed class DelegateIdentitySource : ILocalPlayerIdentitySource
    {
        private readonly Func<uint> _read;

        public DelegateIdentitySource(Func<uint> read) =>
            _read = read ?? throw new ArgumentNullException(nameof(read));

        public uint ServerGuid => _read();
    }

    private readonly ILocalPlayerIdentitySource _identity;
    private readonly ISealedDungeonCellClassifier _sealedDungeonCells;
    private readonly Action<string>? _diagnostic;

    public LiveEntityWorldOriginCoordinator(
        LiveWorldOriginState origin,
        StreamingController streaming,
        GpuWorldState worldState,
        WorldRevealCoordinator worldReveal,
        ILocalPlayerIdentitySource identity,
        ISealedDungeonCellClassifier sealedDungeonCells,
        Action<string>? diagnostic = null)
    {
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _worldReveal = worldReveal ?? throw new ArgumentNullException(nameof(worldReveal));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _sealedDungeonCells = sealedDungeonCells
            ?? throw new ArgumentNullException(nameof(sealedDungeonCells));
        _diagnostic = diagnostic;
    }

    internal LiveEntityWorldOriginCoordinator(
        LiveWorldOriginState origin,
        StreamingController streaming,
        GpuWorldState worldState,
        WorldRevealCoordinator worldReveal,
        Func<uint> playerGuid,
        ISealedDungeonCellClassifier sealedDungeonCells,
        Action<string>? diagnostic = null)
        : this(
            origin,
            streaming,
            worldState,
            worldReveal,
            new DelegateIdentitySource(playerGuid),
            sealedDungeonCells,
            diagnostic)
    {
    }

    public bool IsKnown => _origin.IsKnown;

    public LiveEntityOriginInitialization TryInitialize(
        WorldSession.EntitySpawn spawn)
    {
        if (spawn.Guid != _identity.ServerGuid
            || spawn.Position is not { } position)
        {
            return new(_origin.IsKnown, Array.Empty<uint>());
        }

        int lbX = (int)((position.LandblockId >> 24) & 0xFFu);
        int lbY = (int)((position.LandblockId >> 16) & 0xFFu);
        int oldCenterX = _origin.CenterX;
        int oldCenterY = _origin.CenterY;
        if (!_origin.TryInitialize(lbX, lbY))
            return new(true, Array.Empty<uint>());

        if (lbX != oldCenterX || lbY != oldCenterY)
        {
            _diagnostic?.Invoke(
                $"live: first player position — recentering streaming from ({oldCenterX},{oldCenterY}) "
                + $"to ({lbX},{lbY}) @0x{position.LandblockId:X8}");
        }

        _streaming.InitializeKnownLoginCenter(
            lbX,
            lbY,
            isSealedDungeon: _sealedDungeonCells.IsSealedDungeon(
                position.LandblockId));
        _worldReveal.BeginLogin(position.LandblockId);

        return new(
            true,
            _worldState.LoadedLandblockIds.ToArray());
    }
}
