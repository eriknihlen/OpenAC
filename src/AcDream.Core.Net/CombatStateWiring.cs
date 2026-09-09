using AcDream.Core.Combat;

namespace AcDream.Core.Net;

public static class CombatStateWiring
{
    public const uint CombatModePropertyId = 40u;

    public static IDisposable Wire(
        WorldSession session,
        CombatState combat,
        Func<bool>? accepting = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(combat);

        Action<WorldSession.PlayerIntPropertyUpdate> handler = update =>
        {
            if (accepting?.Invoke() == false) return;
            ApplyPlayerIntProperty(combat, update.Property, update.Value);
        };
        session.PlayerIntPropertyUpdated += handler;
        return new EventSubscription(session, handler);
    }

    public static bool ApplyPlayerIntProperty(
        CombatState combat,
        uint property,
        int value)
    {
        ArgumentNullException.ThrowIfNull(combat);
        if (property != CombatModePropertyId)
            return false;

        CombatMode mode = (CombatMode)value;
        if (mode is not (CombatMode.NonCombat
            or CombatMode.Melee
            or CombatMode.Missile
            or CombatMode.Magic))
            return false;

        combat.SetCombatMode(mode);
        return true;
    }

    private sealed class EventSubscription(
        WorldSession session,
        Action<WorldSession.PlayerIntPropertyUpdate> handler) : IDisposable
    {
        private Action? _unsubscribe =
            () => session.PlayerIntPropertyUpdated -= handler;

        public void Dispose() =>
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
