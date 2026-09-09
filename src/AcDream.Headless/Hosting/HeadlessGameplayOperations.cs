using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using AcDream.Content;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessGameplayOperations
    : IRuntimeCombatAttackOperations,
      IRuntimeCombatTargetOperations,
      IRuntimeCombatModeOperations,
      IRuntimeSpellCastOperations
{
    private sealed class SessionRoute(
        HeadlessGameplayOperations owner,
        WorldSession session)
        : ILiveSessionCommandRouting
    {
        private bool _active;

        public void Activate()
        {
            if (_active)
                return;
            owner.Activate(session, this);
            _active = true;
        }

        public void Dispose()
        {
            owner.Deactivate(this);
            _active = false;
        }
    }

    private readonly object _gate = new();
    private GameRuntime? _runtime;
    private SpellComponentRequirementService? _componentRequirements;
    private WorldSession? _session;
    private SessionRoute? _route;

    internal void Bind(
        GameRuntime runtime,
        MagicCatalog? catalog,
        Func<string> accountName)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(accountName);
        if (_runtime is not null)
        {
            throw new InvalidOperationException(
                "Headless gameplay operations are already bound.");
        }

        _runtime = runtime;
        _componentRequirements = catalog?.CreateRequirementService(
            runtime.InventoryOwner.Objects,
            () => runtime.PlayerIdentity.ServerGuid,
            accountName);
    }

    internal ILiveSessionCommandRouting CreateRoute(
        WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionRoute(this, session);
    }

    public bool CanStartAttack()
    {
        GameRuntime runtime = RequireRuntime();
        if (!IsInWorld
            || !CombatInputPlanner.SupportsTargetedAttack(
                runtime.ActionOwner.Combat.CurrentMode))
        {
            return false;
        }
        return GetSelectedOrClosestTarget(runtime) is not null;
    }

    public void PrepareAttackRequest()
    {
        _ = RequireRuntime().MovementOwner.PrepareForAttackRequest();
    }

    public bool SendAttack(AttackHeight height, float power)
    {
        GameRuntime runtime = RequireRuntime();
        uint? target = GetSelectedOrClosestTarget(runtime);
        if (target is null || !TryGetSession(out WorldSession? session))
            return false;

        power = Math.Clamp(power, 0f, 1f);
        if (runtime.ActionOwner.Combat.CurrentMode == CombatMode.Missile)
            session!.SendMissileAttack(target.Value, height, power);
        else
            session!.SendMeleeAttack(target.Value, height, power);
        return true;
    }

    public void SendCancelAttack()
    {
        if (TryGetSession(out WorldSession? session))
            session!.SendCancelAttack();
    }

    public bool IsDualWield =>
        RequireRuntime().MovementOwner.IsDualWield;
    public bool PlayerReadyForAttack
    {
        get
        {
            GameRuntime runtime = RequireRuntime();
            return runtime.MovementOwner.IsReadyForAttack(
                runtime.ActionOwner.Combat.CurrentMode);
        }
    }
    public bool AutoRepeatAttack =>
        RequireRuntime().CharacterOwner.Options.GetOptionBit(CharacterOptionId.AutoRepeatAttack);
    public bool AutoTarget =>
        RequireRuntime().CharacterOwner.Options.GetOptionBit(CharacterOptionId.AutoTarget);

    public uint? SelectClosestTarget()
    {
        GameRuntime runtime = RequireRuntime();
        uint? closest = RuntimeHostileTargetQuery.FindClosest(runtime);
        if (closest is { } target)
        {
            runtime.ActionOwner.Selection.Select(
                target,
                AcDream.Core.Selection.SelectionChangeSource.Keyboard);
        }
        else
        {
            runtime.ActionOwner.Selection.Clear(
                AcDream.Core.Selection.SelectionChangeSource.Keyboard);
        }
        return closest;
    }

    public bool IsInWorld => _runtime?.Session.IsInWorld == true;
    public IReadOnlyList<ClientObject> GetOrderedEquipment()
    {
        GameRuntime runtime = RequireRuntime();
        return runtime.InventoryOwner.Objects.GetEquippedBy(
            runtime.PlayerIdentity.ServerGuid);
    }

    public void NotifyExplicitCombatModeRequest()
    {
    }

    public void SendChangeCombatMode(CombatMode mode)
    {
        if (TryGetSession(out WorldSession? session))
            session!.SendChangeCombatMode(mode);
    }

    public uint LocalPlayerId =>
        _runtime?.PlayerIdentity.ServerGuid ?? 0u;
    public bool CanSend => TryGetSession(out _);

    public bool HasRequiredComponents(uint spellId)
    {
        if (_componentRequirements is { } requirements)
            return requirements.HasRequiredComponents(spellId);

        GameRuntime runtime = RequireRuntime();
        ClientObject? player = runtime.InventoryOwner.Objects.Get(
            runtime.PlayerIdentity.ServerGuid);
        return player is not null
            && !player.Properties.GetBool(
                SpellComponentRequirementService
                    .SpellComponentsRequiredProperty,
                true);
    }

    public bool IsTargetCompatible(
        uint targetId,
        SpellMetadata spell,
        bool showMessage)
    {
        GameRuntime runtime = RequireRuntime();
        ClientObject? target =
            runtime.InventoryOwner.Objects.Get(targetId);
        if (target is null)
            return false;
        SpellTargetPolicyResult result =
            RetailSpellTargetPolicy.Evaluate(
                runtime.PlayerIdentity.ServerGuid,
                target,
                spell);
        if (showMessage
            && !result.Allowed
            && result.Message is { } message)
        {
            DisplayMessage(message);
        }
        return result.Allowed;
    }

    public void StopCompletely()
    {
        _ = RequireRuntime().MovementOwner.PrepareForAttackRequest();
    }

    public void SendUntargeted(uint spellId)
    {
        if (TryGetSession(out WorldSession? session))
            session!.SendCastUntargetedSpell(spellId);
    }

    public void SendTargeted(uint targetId, uint spellId)
    {
        if (TryGetSession(out WorldSession? session))
            session!.SendCastTargetedSpell(targetId, spellId);
    }

    public void DisplayMessage(string message) =>
        RequireRuntime().CommunicationOwner.Chat.OnSystemMessage(
            message,
            chatType: 0x00u);

    public void IncrementBusy() =>
        RequireRuntime().ActionOwner.Transactions
            .IncrementBusyCount();

    private uint? GetSelectedOrClosestTarget(GameRuntime runtime)
    {
        uint? selected =
            runtime.ActionOwner.Selection.SelectedObjectId;
        if (selected is { } target
            && RuntimeHostileTargetQuery.IsHostile(runtime, target))
        {
            return target;
        }
        return AutoTarget ? SelectClosestTarget() : null;
    }

    private GameRuntime RequireRuntime() =>
        _runtime
        ?? throw new InvalidOperationException(
            "Headless gameplay operations are not bound.");

    private bool TryGetSession(out WorldSession? session)
    {
        lock (_gate)
        {
            session = _session;
            return _route is not null
                && session is not null
                && IsInWorld;
        }
    }

    private void Activate(
        WorldSession session,
        SessionRoute route)
    {
        lock (_gate)
        {
            if (_route is not null)
            {
                throw new InvalidOperationException(
                    "Headless gameplay operations already have an active session.");
            }
            _session = session;
            _route = route;
        }
    }

    private void Deactivate(SessionRoute route)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_route, route))
                return;
            _session = null;
            _route = null;
        }
    }
}
