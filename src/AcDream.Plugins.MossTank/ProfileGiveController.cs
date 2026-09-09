using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class ProfileGiveController
{
    private const double GiveTimeoutSeconds = 10d;
    private const int MaximumAttemptsPerItem = 5;

    private static readonly PluginItemProperties EmptyProperties = new(
        new Dictionary<uint, int>(),
        new Dictionary<uint, long>(),
        new Dictionary<uint, bool>(),
        new Dictionary<uint, double>(),
        new Dictionary<uint, string>(),
        new Dictionary<uint, uint>(),
        new Dictionary<uint, uint>());

    private readonly IPluginHost _host;
    private readonly MossTankLootProfileStore _profiles;
    private readonly Queue<uint> _pending = new();
    private uint _targetObjectId;
    private uint _waitingObjectId;
    private long _completionRevision;
    private double _waitingSeconds;
    private int _attempts;
    private int _given;
    private string _profileName = string.Empty;
    private string _targetName = string.Empty;

    public ProfileGiveController(
        IPluginHost host,
        MossTankLootProfileStore profiles)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Item giver idle.";

    public bool TryStart(string? profileName, string? targetName)
    {
        if (IsRunning || !_host.Automation.IsAvailable)
            return false;

        string requestedProfile = profileName?.Trim() ?? string.Empty;
        string requestedTarget = targetName?.Trim() ?? string.Empty;
        PluginWorldObject target = _host.Automation.Objects.CaptureObjects()
            .Where(obj => obj.ObjectClass is PluginObjectClass.Player
                or PluginObjectClass.Npc)
            .Where(obj => obj.Name.Equals(
                requestedTarget,
                StringComparison.OrdinalIgnoreCase))
            .Where(obj => obj.ObjectId != _host.Automation.Character.ObjectId)
            .OrderBy(obj => DistanceFromPlayer(obj))
            .ThenBy(static obj => obj.ObjectId)
            .FirstOrDefault();
        if (target.ObjectId == 0u)
        {
            Status = $"Item giver target not found: {requestedTarget}.";
            return false;
        }

        var rules = new List<LootRule>();
        if (!_profiles.TryLoadNamed(requestedProfile, rules))
        {
            Status = $"Item giver profile not found: {requestedProfile}.";
            return false;
        }

        IReadOnlyList<PluginInventoryItem> owned =
            _host.Automation.Items.CaptureOwnedItems();
        _pending.Clear();
        foreach (PluginInventoryItem item in owned
            .Where(static item => !item.IsEquipped && item.WielderObjectId == 0u)
            .OrderBy(static item => item.ObjectId))
        {
            PluginItemProperties properties = _host.Automation.Items
                .TryCaptureProperties(item.ObjectId, out PluginItemProperties value)
                    ? value
                    : EmptyProperties;
            if (MatchesGiveProfile(item, properties, rules))
                _pending.Enqueue(item.ObjectId);
        }

        _targetObjectId = target.ObjectId;
        _profileName = requestedProfile;
        _targetName = target.Name;
        _waitingObjectId = 0u;
        _attempts = 0;
        _given = 0;
        _waitingSeconds = 0d;
        IsRunning = true;
        Status = _pending.Count == 0
            ? $"No items match {_profileName}."
            : $"Giving {_pending.Count} item(s) to {_targetName}.";
        return true;
    }

    public bool Tick(double elapsedSeconds, bool canAct)
    {
        if (!IsRunning)
            return false;
        if (!_host.Automation.IsAvailable
            || !_host.Automation.Objects.TryGet(
                _targetObjectId,
                out PluginWorldObject target)
            || target.ObjectClass is not (PluginObjectClass.Player
                or PluginObjectClass.Npc))
        {
            Stop("Item giver stopped: target vanished.");
            return false;
        }

        if (_waitingObjectId != 0u)
        {
            _waitingSeconds += Math.Max(0d, elapsedSeconds);
            PluginInventoryCompletion completion =
                _host.Automation.Items.LastInventoryCompletion;
            bool itemStillOwned = _host.Automation.Items.CaptureOwnedItems()
                .Any(item => item.ObjectId == _waitingObjectId);
            if (!itemStillOwned
                || (completion.Revision > _completionRevision
                    && completion.Kind == PluginInventoryCommandKind.Give
                    && completion.SourceObjectId == _waitingObjectId))
            {
                if (!itemStillOwned || completion.IsSuccess)
                    _given++;
                _pending.Dequeue();
                _waitingObjectId = 0u;
                _attempts = 0;
                _waitingSeconds = 0d;
            }
            else if (_waitingSeconds >= GiveTimeoutSeconds)
            {
                if (_attempts >= MaximumAttemptsPerItem)
                {
                    _pending.Dequeue();
                    _waitingObjectId = 0u;
                    _attempts = 0;
                    _waitingSeconds = 0d;
                }
                else
                {
                    _waitingObjectId = 0u;
                    _waitingSeconds = 0d;
                }
            }
            return true;
        }

        if (_pending.Count == 0)
        {
            Stop($"Item giver finished: {_given} item(s) given to {_targetName}.");
            return false;
        }
        if (!canAct || _host.Automation.Items.IsBusy)
            return true;

        uint objectId = _pending.Peek();
        if (!_host.Automation.Items.CaptureOwnedItems()
            .Any(item => item.ObjectId == objectId))
        {
            _pending.Dequeue();
            return true;
        }

        long baselineRevision =
            _host.Automation.Items.LastInventoryCompletion.Revision;
        PluginItemCommandResult result = _host.Automation.Items.Give(
            objectId,
            _targetObjectId);
        if (result.Accepted)
        {
            _waitingObjectId = objectId;
            _completionRevision = baselineRevision;
            _waitingSeconds = 0d;
            _attempts++;
            Status = $"Giving item {_given + 1} to {_targetName}…";
        }
        else if (result.Status is PluginItemCommandStatus.InvalidItem
            or PluginItemCommandStatus.InvalidTarget
            or PluginItemCommandStatus.Refused
            or PluginItemCommandStatus.Unavailable)
        {
            _pending.Dequeue();
            _attempts = 0;
        }
        return true;
    }

    public void Reset()
    {
        _pending.Clear();
        _targetObjectId = 0u;
        _waitingObjectId = 0u;
        _completionRevision = 0;
        _waitingSeconds = 0d;
        _attempts = 0;
        _given = 0;
        IsRunning = false;
        Status = "Item giver idle.";
    }

    private bool MatchesGiveProfile(
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        IReadOnlyList<LootRule> rules)
    {
        foreach (LootRule rule in rules)
        {
            if (!rule.IsMatch(item, properties, _host, out _))
                continue;
            return rule.Action is LootAction.Keep or LootAction.KeepUpTo;
        }
        return false;
    }

    private double DistanceFromPlayer(in PluginWorldObject target)
    {
        PluginNavigationSnapshot player = _host.Automation.Navigation.Snapshot;
        if (!player.IsAvailable || !target.HasPosition)
            return double.MaxValue;
        double dx = target.Position.NorthSouth - player.Position.NorthSouth;
        double dy = target.Position.EastWest - player.Position.EastWest;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private void Stop(string status)
    {
        _pending.Clear();
        _targetObjectId = 0u;
        _waitingObjectId = 0u;
        _completionRevision = 0;
        _waitingSeconds = 0d;
        _attempts = 0;
        IsRunning = false;
        Status = status;
    }
}
