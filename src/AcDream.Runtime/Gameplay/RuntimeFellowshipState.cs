using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeFellowshipOwnershipSnapshot(
    bool IsDisposed,
    bool IsInFellowship,
    int MemberCount)
{
    public bool IsConverged =>
        IsDisposed
        && !IsInFellowship
        && MemberCount == 0;
}

public sealed class RuntimeFellowshipState : IDisposable
{
    private const int DepartedGraceSeconds = 900;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<uint, GameEvents.FellowMember> _members = [];
    private readonly Dictionary<uint, DateTimeOffset> _vitalsUpdatedAt = [];
    private readonly Dictionary<uint, int> _fellowsDeparted = [];
    private bool _panelVisible;
    private bool _vitalsRequested;
    private bool _sentVitalsSubscription;
    private string _name = string.Empty;
    private uint _leaderGuid;
    private bool _shareXp;
    private bool _evenXpSplit;
    private bool _isOpen;
    private bool _locked;
    private bool _isInFellowship;
    private long _revision;
    private bool _disposed;

    public RuntimeFellowshipState(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        View = new FellowshipView(this);
    }

    public IRuntimeFellowshipView View { get; }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    public void ApplyFullUpdate(GameEvents.FellowshipFullUpdate update)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _members.Clear();
            foreach (GameEvents.FellowMember member in update.Members)
                _members[member.Guid] = member;
            // A full roster carries vitals, but only a per-fellow update
            // counts as a live vitals sample.
            foreach (uint guid in _vitalsUpdatedAt.Keys.ToArray())
            {
                if (!_members.ContainsKey(guid))
                    _vitalsUpdatedAt.Remove(guid);
            }
            _fellowsDeparted.Clear();
            foreach (GameEvents.FellowshipDepartedMember departed in update.Departed)
                _fellowsDeparted[departed.Guid] = departed.DepartedTimestamp;
            _name = update.Name;
            _leaderGuid = update.LeaderGuid;
            _shareXp = update.ShareXp;
            _evenXpSplit = update.EvenXpSplit;
            _isOpen = update.OpenFellow;
            _locked = update.Locked;
            _isInFellowship = true;
            Bump();
        }
    }

    public void ApplyUpdateFellow(GameEvents.FellowshipUpdateFellow update)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isInFellowship) return;
            bool isNewMember = !_members.ContainsKey(update.MemberGuid);
            if (isNewMember && _locked && !IsAdmissibleWhileLocked(update.MemberGuid))
                return;
            _members[update.MemberGuid] = update.Member;
            _vitalsUpdatedAt[update.MemberGuid] = _timeProvider.GetUtcNow();
            RecalculateEvenXpSplit();
            Bump();
        }
    }

    /// <summary>
    /// The server streams fellow vitals only to a client that has declared
    /// its fellowship panel open. The panel and automation can each want that
    /// stream; the wire carries the OR of the two, sent once per change.
    /// Returns true when the caller must send <paramref name="subscribe"/>.
    /// </summary>
    public bool SetPanelVisible(bool visible, out bool subscribe)
    {
        lock (_gate)
        {
            _panelVisible = visible;
            return ResolveVitalsSubscription(out subscribe);
        }
    }

    public bool SetVitalsRequested(bool requested, out bool subscribe)
    {
        lock (_gate)
        {
            _vitalsRequested = requested;
            return ResolveVitalsSubscription(out subscribe);
        }
    }

    private bool ResolveVitalsSubscription(out bool subscribe)
    {
        subscribe = _panelVisible || _vitalsRequested;
        if (_sentVitalsSubscription == subscribe)
            return false;
        _sentVitalsSubscription = subscribe;
        return true;
    }

    private bool IsAdmissibleWhileLocked(uint guid)
    {
        if (!_fellowsDeparted.TryGetValue(guid, out int departedTimestamp))
            return false;
        long nowSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return nowSeconds - departedTimestamp <= DepartedGraceSeconds;
    }

    private void RecalculateEvenXpSplit()
    {
        if (!_shareXp) return;

        uint minLevel = uint.MaxValue;
        uint maxLevel = 0u;
        foreach (GameEvents.FellowMember member in _members.Values)
        {
            if (member.Level < minLevel) minLevel = member.Level;
            if (member.Level > maxLevel) maxLevel = member.Level;
        }

        if (!_members.TryGetValue(_leaderGuid, out GameEvents.FellowMember leader))
        {
            _evenXpSplit = true;
            return;
        }

        _evenXpSplit = true;
        if (minLevel < 50u)
        {
            if (maxLevel > leader.Level + 5u) _evenXpSplit = false;
            if (minLevel + 5u < leader.Level) _evenXpSplit = false;
        }
    }

    public void ApplyQuit(uint quitterGuid, uint selfGuid)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isInFellowship) return;
            if (quitterGuid == selfGuid)
            {
                ClearLocked();
                return;
            }
            if (_members.Remove(quitterGuid))
            {
                _vitalsUpdatedAt.Remove(quitterGuid);
                RecalculateEvenXpSplit();
                Bump();
            }
        }
    }

    public void ApplyDismiss(uint dismissedGuid, uint selfGuid)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isInFellowship) return;
            if (dismissedGuid == selfGuid)
            {
                ClearLocked();
                return;
            }
            if (_members.Remove(dismissedGuid))
            {
                _vitalsUpdatedAt.Remove(dismissedGuid);
                RecalculateEvenXpSplit();
                Bump();
            }
        }
    }

    public void ApplyDisband()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearLocked();
        }
    }

    public uint LeaderGuid
    {
        get { lock (_gate) return _isInFellowship ? _leaderGuid : 0u; }
    }

    public bool IsInFellowship
    {
        get { lock (_gate) return _isInFellowship; }
    }

    public bool RequiresLeaderHandoffBeforeQuit(
        uint selfGuid,
        bool disband,
        out uint newLeaderGuid)
    {
        lock (_gate)
        {
            if (disband || !_isInFellowship || _leaderGuid != selfGuid)
            {
                newLeaderGuid = 0u;
                return false;
            }
            foreach (uint guid in _members.Keys)
            {
                if (guid == selfGuid) continue;
                newLeaderGuid = guid;
                return true;
            }
            newLeaderGuid = 0u;
            return false;
        }
    }

    public RuntimeFellowshipOwnershipSnapshot CaptureOwnership()
    {
        lock (_gate)
            return new RuntimeFellowshipOwnershipSnapshot(
                _disposed,
                _isInFellowship,
                _members.Count);
    }

    public void ResetSession()
    {
        lock (_gate) ClearLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearLocked();
            _disposed = true;
        }
    }

    private void ClearLocked()
    {
        bool changed = _members.Count != 0
            || _isInFellowship
            || _name.Length != 0
            || _leaderGuid != 0u
            || _shareXp
            || _evenXpSplit
            || _isOpen
            || _locked;
        _members.Clear();
        _vitalsUpdatedAt.Clear();
        _fellowsDeparted.Clear();
        _panelVisible = false;
        _vitalsRequested = false;
        _sentVitalsSubscription = false;
        _name = string.Empty;
        _leaderGuid = 0u;
        _shareXp = false;
        _evenXpSplit = false;
        _isOpen = false;
        _locked = false;
        _isInFellowship = false;
        if (changed) Bump();
    }

    private void Bump() => _revision++;

    private sealed class FellowshipView(RuntimeFellowshipState owner)
        : IRuntimeFellowshipView
    {
        public RuntimeFellowshipSnapshot Snapshot
        {
            get
            {
                lock (owner._gate)
                    return new RuntimeFellowshipSnapshot(
                        owner._revision,
                        owner._isInFellowship,
                        owner._name,
                        owner._leaderGuid,
                        owner._shareXp,
                        owner._evenXpSplit,
                        owner._isOpen,
                        owner._locked,
                        owner._members.Count);
            }
        }

        public bool TryGetMember(uint guid, out RuntimeFellowMemberSnapshot member)
        {
            lock (owner._gate)
            {
                if (!owner._members.TryGetValue(guid, out GameEvents.FellowMember raw))
                {
                    member = default;
                    return false;
                }
                member = ToSnapshot(raw, owner._timeProvider.GetUtcNow());
                return true;
            }
        }

        public IEnumerable<RuntimeFellowMemberSnapshot> GetMembers()
        {
            lock (owner._gate)
            {
                DateTimeOffset now = owner._timeProvider.GetUtcNow();
                var result = new RuntimeFellowMemberSnapshot[owner._members.Count];
                int i = 0;
                foreach (GameEvents.FellowMember raw in owner._members.Values)
                    result[i++] = ToSnapshot(raw, now);
                return result;
            }
        }

        private RuntimeFellowMemberSnapshot ToSnapshot(
            GameEvents.FellowMember raw,
            DateTimeOffset now) =>
            new(
                raw.Guid,
                raw.Name,
                raw.Level,
                raw.MaxHealth,
                raw.MaxStamina,
                raw.MaxMana,
                raw.CurrentHealth,
                raw.CurrentStamina,
                raw.CurrentMana,
                raw.ShareLoot != 0u)
            {
                VitalsAgeSeconds = owner._vitalsUpdatedAt.TryGetValue(
                        raw.Guid, out DateTimeOffset updatedAt)
                    ? Math.Max(0d, (now - updatedAt).TotalSeconds)
                    : null,
            };
    }
}
