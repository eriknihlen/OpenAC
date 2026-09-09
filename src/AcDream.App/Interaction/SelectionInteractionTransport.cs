using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Interaction;

internal sealed class WorldSessionSelectionInteractionTransport(
    Func<WorldSession?> session)
    : IRuntimeInteractionTransport
{
    private readonly Func<WorldSession?> _session = session
        ?? throw new ArgumentNullException(nameof(session));

    public bool IsInWorld =>
        _session()?.CurrentState == WorldSession.State.InWorld;

    public bool TrySendUse(uint serverGuid, out uint sequence)
    {
        WorldSession? live = _session();
        if (live?.CurrentState != WorldSession.State.InWorld)
        {
            sequence = 0u;
            return false;
        }

        sequence = live.NextGameActionSequence();
        live.SendGameAction(InteractRequests.BuildUse(sequence, serverGuid));
        return true;
    }

    public bool TrySendPickup(
        uint itemGuid,
        uint destinationContainerId,
        int placement,
        out uint sequence)
    {
        WorldSession? live = _session();
        if (live?.CurrentState != WorldSession.State.InWorld)
        {
            sequence = 0u;
            return false;
        }

        sequence = live.NextGameActionSequence();
        live.SendGameAction(InteractRequests.BuildPickUp(
            sequence,
            itemGuid,
            destinationContainerId,
            placement));
        return true;
    }
}
