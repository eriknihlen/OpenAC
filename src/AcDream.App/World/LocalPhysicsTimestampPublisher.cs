using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.Runtime.Entities;

namespace AcDream.App.World;

internal interface IAcceptedLocalPhysicsTimestampPublisher
{
    void Publish(uint serverGuid, AcceptedPhysicsTimestamps timestamps);
}

internal sealed class LiveSessionLocalPhysicsTimestampPublisher
    : IAcceptedLocalPhysicsTimestampPublisher
{
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly ILiveWorldSessionSource _session;

    public LiveSessionLocalPhysicsTimestampPublisher(
        ILocalPlayerIdentitySource identity,
        ILiveWorldSessionSource session)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void Publish(uint serverGuid, AcceptedPhysicsTimestamps timestamps)
    {
        if (serverGuid != _identity.ServerGuid
            || _session.CurrentSession is not { } session)
        {
            return;
        }

        session.PublishAcceptedLocalPhysicsTimestamps(
            timestamps.Instance,
            timestamps.ServerControlledMove,
            timestamps.Teleport,
            timestamps.ForcePosition);
    }
}
