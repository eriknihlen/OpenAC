using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.World;
using AcDream.Core.Physics;

namespace AcDream.App.Rendering;

internal sealed class LiveAnimationPresentationContext
    : ILiveAnimationPresentationContext
{
    private readonly LiveEntityRuntime _runtime;
    private readonly ILocalPlayerIdentitySource _localPlayer;
    private readonly ILocalPlayerMotionSource _playerMotion;

    public LiveAnimationPresentationContext(
        LiveEntityRuntime runtime,
        ILocalPlayerIdentitySource localPlayer,
        ILocalPlayerMotionSource playerMotion)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _localPlayer = localPlayer ?? throw new ArgumentNullException(nameof(localPlayer));
        _playerMotion = playerMotion
            ?? throw new ArgumentNullException(nameof(playerMotion));
    }

    public uint LocalPlayerGuid => _localPlayer.ServerGuid;

    public MotionInterpreter? ResolveMotionInterpreter(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_runtime.TryGetRecord(record.ServerGuid, out LiveEntityRecord current)
            || !ReferenceEquals(current, record))
        {
            return null;
        }

        if (record.ServerGuid == _localPlayer.ServerGuid)
            return _playerMotion.Motion;
        return record.RemoteMotionRuntime is RemoteMotion remote
            ? remote.Motion
            : null;
    }
}
