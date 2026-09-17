using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Gameplay;

// Projects RuntimeTradeState onto the plugin trade contract and sends every
// outbound trade command through the same WorldSession builders the
// retail-look secure-trade window's own buttons use. Shared by the
// graphical and headless hosts: both bind one instance over the same
// GameRuntime.
public sealed class RuntimeTradeAutomation : ITradeAutomation
{
    private readonly GameRuntime _runtime;
    private readonly object _gate = new();

    private long _lastRevision = long.MinValue;
    private bool _wasOpen;
    private uint _lastPartnerGuid;
    private bool _wasPartnerAccepted;
    private HashSet<uint> _lastSelfItems = [];
    private HashSet<uint> _lastPartnerItems = [];

    private Action<PluginTradeOpened>? _opened;
    private Action? _closed;
    private Action<uint>? _partnerTradeAccepted;
    private Action<PluginTradeItemAdded>? _itemAdded;

    public RuntimeTradeAutomation(GameRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public bool IsAvailable =>
        _runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;

    private RuntimeTradeSnapshot Snapshot => _runtime.Trade.Snapshot;

    public bool IsOpen => Snapshot.IsOpen;
    public uint PartnerObjectId => Snapshot.PartnerGuid;

    public string PartnerName
    {
        get
        {
            uint partner = Snapshot.PartnerGuid;
            return partner == 0u
                ? string.Empty
                : _runtime.InventoryOwner.Objects.Get(partner)?.Name
                    ?? string.Empty;
        }
    }

    public IReadOnlyList<uint> MyItems =>
        _runtime.Trade.GetItems(RuntimeTradeSide.Self);

    public IReadOnlyList<uint> PartnerItems =>
        _runtime.Trade.GetItems(RuntimeTradeSide.Partner);

    public bool MyAccepted => Snapshot.SelfAccepted;
    public bool PartnerAccepted => Snapshot.PartnerAccepted;

    private AcDream.Core.Net.WorldSession? Session => _runtime.Session.CurrentSession;

    public PluginTradeCommandResult Add(uint itemObjectId)
    {
        if (!IsAvailable)
            return new(PluginTradeCommandStatus.Unavailable);
        if (!Snapshot.IsOpen)
            return new(PluginTradeCommandStatus.NotOpen);
        if (itemObjectId == 0u)
            return new(PluginTradeCommandStatus.InvalidItem);
        if (Session is not { } session)
            return new(PluginTradeCommandStatus.Unavailable);
        session.SendAddToTrade(itemObjectId);
        return new(PluginTradeCommandStatus.Sent);
    }

    // A no-op when the local side already accepted -- the retail-look
    // window's own Accept button disables itself the moment MyAccepted
    // flips true, so a second press (or a plugin racing the same click)
    // never re-sends the wire command. Decline()/End() carry no such
    // guard: they are meant to be resendable (a partner-declined round can
    // decline again; ending an already-closing trade is harmless).
    public PluginTradeCommandResult Accept()
    {
        if (!IsAvailable)
            return new(PluginTradeCommandStatus.Unavailable);
        RuntimeTradeSnapshot snapshot = Snapshot;
        if (!snapshot.IsOpen)
            return new(PluginTradeCommandStatus.NotOpen);
        if (snapshot.SelfAccepted)
            return new(PluginTradeCommandStatus.AlreadyAccepted);
        if (Session is not { } session)
            return new(PluginTradeCommandStatus.Unavailable);
        session.SendAcceptTrade(
            snapshot.PartnerGuid,
            0d,
            0u,
            snapshot.PartnerGuid,
            true,
            snapshot.PartnerAccepted);
        return new(PluginTradeCommandStatus.Sent);
    }

    public PluginTradeCommandResult Decline()
    {
        if (!IsAvailable)
            return new(PluginTradeCommandStatus.Unavailable);
        if (!Snapshot.IsOpen)
            return new(PluginTradeCommandStatus.NotOpen);
        if (Session is not { } session)
            return new(PluginTradeCommandStatus.Unavailable);
        session.SendDeclineTrade();
        return new(PluginTradeCommandStatus.Sent);
    }

    public PluginTradeCommandResult Reset()
    {
        if (!IsAvailable)
            return new(PluginTradeCommandStatus.Unavailable);
        if (!Snapshot.IsOpen)
            return new(PluginTradeCommandStatus.NotOpen);
        if (Session is not { } session)
            return new(PluginTradeCommandStatus.Unavailable);
        session.SendResetTrade();
        return new(PluginTradeCommandStatus.Sent);
    }

    public PluginTradeCommandResult End()
    {
        if (!IsAvailable)
            return new(PluginTradeCommandStatus.Unavailable);
        if (!Snapshot.IsOpen)
            return new(PluginTradeCommandStatus.NotOpen);
        if (Session is not { } session)
            return new(PluginTradeCommandStatus.Unavailable);
        session.SendCloseTradeNegotiations();
        return new(PluginTradeCommandStatus.Sent);
    }

    public event Action<PluginTradeOpened> Opened
    {
        add { lock (_gate) _opened += value; }
        remove { lock (_gate) _opened -= value; }
    }

    public event Action Closed
    {
        add { lock (_gate) _closed += value; }
        remove { lock (_gate) _closed -= value; }
    }

    public event Action<uint> PartnerTradeAccepted
    {
        add { lock (_gate) _partnerTradeAccepted += value; }
        remove { lock (_gate) _partnerTradeAccepted -= value; }
    }

    public event Action<PluginTradeItemAdded> ItemAdded
    {
        add { lock (_gate) _itemAdded += value; }
        remove { lock (_gate) _itemAdded -= value; }
    }

    // Diffs the trade owner's latest snapshot against what was last seen
    // and raises events for the differences. Call once per host tick.
    public void Poll()
    {
        RuntimeTradeSnapshot snapshot = Snapshot;

        if (snapshot.IsOpen && !_wasOpen)
        {
            _wasOpen = true;
            _lastPartnerGuid = snapshot.PartnerGuid;
            _lastSelfItems = [];
            _lastPartnerItems = [];
            _wasPartnerAccepted = false;
            Raise(
                _opened,
                new PluginTradeOpened(
                    _runtime.PlayerIdentity.ServerGuid,
                    snapshot.PartnerGuid));
        }
        else if (!snapshot.IsOpen && _wasOpen)
        {
            _wasOpen = false;
            _lastPartnerGuid = 0u;
            _lastSelfItems = [];
            _lastPartnerItems = [];
            _wasPartnerAccepted = false;
            Raise(_closed);
        }
        else if (snapshot.IsOpen
            && _wasOpen
            && snapshot.PartnerGuid != _lastPartnerGuid)
        {
            // The trade owner reused the same open window for a different
            // partner without an intervening Closed report -- one tick
            // between two distinct trades. A caller only ever sees Closed
            // then Opened, one pair per Poll() call: two swaps landing
            // inside the same poll interval coalesce into a single
            // close+open for the final partner (documented in the plugin
            // API doc).
            _lastPartnerGuid = snapshot.PartnerGuid;
            _lastSelfItems = [];
            _lastPartnerItems = [];
            _wasPartnerAccepted = false;
            Raise(_closed);
            Raise(
                _opened,
                new PluginTradeOpened(
                    _runtime.PlayerIdentity.ServerGuid,
                    snapshot.PartnerGuid));
        }

        if (snapshot.Revision == _lastRevision)
            return;
        _lastRevision = snapshot.Revision;

        if (snapshot.PartnerAccepted && !_wasPartnerAccepted)
            Raise(_partnerTradeAccepted, snapshot.PartnerGuid);
        _wasPartnerAccepted = snapshot.PartnerAccepted;

        var selfNow = new HashSet<uint>(
            _runtime.Trade.GetItems(RuntimeTradeSide.Self));
        foreach (uint itemId in selfNow)
        {
            if (!_lastSelfItems.Contains(itemId))
                Raise(_itemAdded, new PluginTradeItemAdded(itemId, true));
        }

        var partnerNow = new HashSet<uint>(
            _runtime.Trade.GetItems(RuntimeTradeSide.Partner));
        foreach (uint itemId in partnerNow)
        {
            if (!_lastPartnerItems.Contains(itemId))
                Raise(_itemAdded, new PluginTradeItemAdded(itemId, false));
        }

        _lastSelfItems = selfNow;
        _lastPartnerItems = partnerNow;
    }

    private static void Raise(Action<PluginTradeOpened>? handlers, PluginTradeOpened value)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginTradeOpened>)handler)(value); }
            catch { }
        }
    }

    private static void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch { }
        }
    }

    private static void Raise(Action<uint>? handlers, uint value)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<uint>)handler)(value); }
            catch { }
        }
    }

    private static void Raise(
        Action<PluginTradeItemAdded>? handlers, PluginTradeItemAdded value)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginTradeItemAdded>)handler)(value); }
            catch { }
        }
    }
}
