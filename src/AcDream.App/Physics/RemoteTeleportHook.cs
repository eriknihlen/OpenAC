using AcDream.Core.Physics;

namespace AcDream.App.Physics;

public static class RemoteTeleportHook
{
    private const WeenieError TeleportCancelContext = WeenieError.ITeleported;

    public static bool Execute(
        RemoteTeleportHookActions actions,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(actions.CancelMoveTo);
        ArgumentNullException.ThrowIfNull(actions.UnStick);
        ArgumentNullException.ThrowIfNull(actions.StopInterpolating);
        ArgumentNullException.ThrowIfNull(actions.UnConstrain);
        ArgumentNullException.ThrowIfNull(actions.NotifyTeleported);
        ArgumentNullException.ThrowIfNull(actions.ReportCollisionEnd);

        bool Current() => isCurrent?.Invoke() ?? true;
        if (!Current())
            return false;
        actions.CancelMoveTo(TeleportCancelContext);
        if (!Current())
            return false;
        actions.UnStick();
        if (!Current())
            return false;
        actions.StopInterpolating();
        if (!Current())
            return false;
        actions.UnConstrain();
        if (!Current())
            return false;
        actions.NotifyTeleported();
        if (!Current())
            return false;
        actions.ReportCollisionEnd();
        return Current();
    }
}

public sealed record RemoteTeleportHookActions(
    Action<WeenieError> CancelMoveTo,
    Action UnStick,
    Action StopInterpolating,
    Action UnConstrain,
    Action NotifyTeleported,
    Action ReportCollisionEnd);
