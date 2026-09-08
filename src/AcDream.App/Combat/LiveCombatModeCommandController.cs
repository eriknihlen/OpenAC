using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.UI;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.App.Combat;

internal interface ILiveCombatModeAuthority
{
    bool IsInWorld { get; }

    void SendChangeCombatMode(CombatMode mode);
}

internal sealed class LiveSessionCombatModeAuthority(LiveSessionHost session)
    : ILiveCombatModeAuthority
{
    private readonly LiveSessionHost _session = session
        ?? throw new ArgumentNullException(nameof(session));

    public bool IsInWorld =>
        _session.IsInWorld && _session.CurrentSession is not null;

    public void SendChangeCombatMode(CombatMode mode)
    {
        if (!IsInWorld)
        {
            throw new InvalidOperationException(
                "A combat-mode request requires an active in-world session.");
        }

        _session.CurrentSession!.SendChangeCombatMode(mode);
    }
}

internal interface ICombatEquipmentSource
{
    IReadOnlyList<ClientObject> GetOrderedEquipment();
}

internal sealed class LocalPlayerCombatEquipmentSource(
    ClientObjectTable objects,
    ILocalPlayerIdentitySource identity) : ICombatEquipmentSource
{
    private readonly ClientObjectTable _objects = objects
        ?? throw new ArgumentNullException(nameof(objects));
    private readonly ILocalPlayerIdentitySource _identity = identity
        ?? throw new ArgumentNullException(nameof(identity));

    public IReadOnlyList<ClientObject> GetOrderedEquipment() =>
        _objects.GetEquippedBy(_identity.ServerGuid);
}

internal interface IExplicitCombatModeIntentSink
{
    void NotifyExplicitCombatModeRequest();
}

internal sealed class ItemInteractionCombatModeIntentSink(
    ItemInteractionController items) : IExplicitCombatModeIntentSink
{
    private readonly ItemInteractionController _items = items
        ?? throw new ArgumentNullException(nameof(items));

    public void NotifyExplicitCombatModeRequest() =>
        _items.NotifyExplicitCombatModeRequest();
}

internal interface ILiveCombatModeCommand
{
    void Toggle();
}

internal sealed class LiveCombatModeCommandSlot : ILiveCombatModeCommand
{
    private readonly object _gate = new();
    private ILiveCombatModeCommand? _target;
    private bool _deactivated;

    public void Bind(ILiveCombatModeCommand target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deactivated, this);
            if (_target is not null && !ReferenceEquals(_target, target))
            {
                throw new InvalidOperationException(
                    "Live combat-mode commands are already bound.");
            }

            _target = target;
        }
    }

    public void Unbind(ILiveCombatModeCommand target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            if (ReferenceEquals(_target, target))
                _target = null;
        }
    }

    public IDisposable BindOwned(ILiveCombatModeCommand target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deactivated, this);
            if (_target is not null)
            {
                throw new InvalidOperationException(
                    "Live combat-mode commands are already bound.");
            }

            _target = target;
        }

        return new Binding(this, target);
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _deactivated = true;
            _target = null;
        }
    }

    public void Toggle()
    {
        lock (_gate)
        {
            if (!_deactivated)
                _target?.Toggle();
        }
    }

    private sealed class Binding : IDisposable
    {
        private LiveCombatModeCommandSlot? _owner;
        private readonly ILiveCombatModeCommand _expected;

        public Binding(
            LiveCombatModeCommandSlot owner,
            ILiveCombatModeCommand expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}

internal sealed class RuntimeCombatModeOperationsSlot
    : IRuntimeCombatModeOperations
{
    private IRuntimeCombatModeOperations? _owner;

    public IDisposable BindOwned(IRuntimeCombatModeOperations owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null)
            throw new InvalidOperationException(
                "Runtime combat-mode operations are already bound.");
        _owner = owner;
        return new Binding(this, owner);
    }

    private void Unbind(IRuntimeCombatModeOperations expected)
    {
        if (ReferenceEquals(_owner, expected))
            _owner = null;
    }

    public bool IsInWorld => _owner?.IsInWorld == true;
    public IReadOnlyList<ClientObject> GetOrderedEquipment() =>
        _owner?.GetOrderedEquipment() ?? [];
    public void NotifyExplicitCombatModeRequest() =>
        _owner?.NotifyExplicitCombatModeRequest();
    public void SendChangeCombatMode(CombatMode mode) =>
        _owner?.SendChangeCombatMode(mode);

    private sealed class Binding : IDisposable
    {
        private RuntimeCombatModeOperationsSlot? _slot;
        private readonly IRuntimeCombatModeOperations _expected;

        public Binding(
            RuntimeCombatModeOperationsSlot slot,
            IRuntimeCombatModeOperations expected)
        {
            _slot = slot;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _slot, null)?.Unbind(_expected);
    }
}

internal sealed class LiveCombatModeOperations
    : IRuntimeCombatModeOperations
{
    private readonly ILiveCombatModeAuthority _authority;
    private readonly ICombatEquipmentSource _equipment;
    private readonly IExplicitCombatModeIntentSink _itemIntent;

    public LiveCombatModeOperations(
        ILiveCombatModeAuthority authority,
        ICombatEquipmentSource equipment,
        IExplicitCombatModeIntentSink itemIntent)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        _itemIntent = itemIntent ?? throw new ArgumentNullException(nameof(itemIntent));
    }

    public bool IsInWorld => _authority.IsInWorld;
    public IReadOnlyList<ClientObject> GetOrderedEquipment() =>
        _equipment.GetOrderedEquipment();
    public void NotifyExplicitCombatModeRequest() =>
        _itemIntent.NotifyExplicitCombatModeRequest();
    public void SendChangeCombatMode(CombatMode mode) =>
        _authority.SendChangeCombatMode(mode);
}

internal sealed class RuntimeCombatModeCommandAdapter : ILiveCombatModeCommand
{
    private readonly RuntimeCombatModeState _owner;
    private readonly Action<string> _log;
    private readonly Action<string>? _toast;
    private readonly Action<string> _systemMessage;

    public RuntimeCombatModeCommandAdapter(
        RuntimeCombatModeState owner,
        Action<string>? log = null,
        Action<string>? toast = null,
        Action<string>? systemMessage = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _log = log ?? (_ => { });
        _toast = toast;
        _systemMessage = systemMessage ?? (_ => { });
    }

    public void Toggle()
    {
        RuntimeCombatModeRequestResult result = _owner.Toggle();
        if (result.Status == RuntimeCombatModeRequestStatus.Inactive)
            return;

        if (result.Status == RuntimeCombatModeRequestStatus.Rejected)
        {
            string notice = result.Notice ?? string.Empty;
            _log($"combat: {notice}");
            _systemMessage(notice);
            return;
        }

        string message = $"Combat mode {result.Mode}";
        _log($"combat: {message}");
        _toast?.Invoke(message);
    }
}
