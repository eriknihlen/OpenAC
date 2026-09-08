using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeContractOwnershipSnapshot(
    bool IsDisposed,
    int ContractCount,
    uint DisplayContractId)
{
    public bool IsConverged =>
        IsDisposed
        && ContractCount == 0
        && DisplayContractId == 0u;
}

/// <summary>Whole-tracker state at one revision.</summary>
public readonly record struct RuntimeContractsSnapshot(
    long Revision,
    int ContractCount,
    uint DisplayContractId);

public interface IRuntimeContractView
{
    RuntimeContractsSnapshot Snapshot { get; }

    bool TryGetContract(uint contractId, out ContractTracker tracker);

    IReadOnlyList<ContractTracker> GetContracts();
}

public sealed class RuntimeContractState : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, ContractTracker> _contracts = [];
    private uint _displayContractId;
    private long _revision;
    private bool _disposed;

    public RuntimeContractState() => View = new ContractView(this);

    public IRuntimeContractView View { get; }

    public void ApplyTable(IReadOnlyDictionary<uint, ContractTracker> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        lock (_gate)
        {
            if (_disposed) return;

            _contracts.Clear();
            foreach ((uint id, ContractTracker tracker) in table)
                _contracts[id] = tracker;

            if (_displayContractId != 0u && !_contracts.ContainsKey(_displayContractId))
                _displayContractId = 0u;

            Bump();
        }
    }

    public void ApplyUpdate(ContractTrackerUpdate update)
    {
        lock (_gate)
        {
            if (_disposed) return;

            uint id = update.Tracker.ContractId;
            if (update.Delete)
            {
                bool removed = _contracts.Remove(id);
                if (_displayContractId == id)
                    _displayContractId = 0u;
                if (removed) Bump();
                return;
            }

            _contracts[id] = update.Tracker;
            if (update.SetAsDisplay)
                _displayContractId = id;
            Bump();
        }
    }

    public RuntimeContractOwnershipSnapshot CaptureOwnership()
    {
        lock (_gate)
            return new RuntimeContractOwnershipSnapshot(
                _disposed,
                _contracts.Count,
                _displayContractId);
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
        bool changed = _contracts.Count != 0 || _displayContractId != 0u;
        _contracts.Clear();
        _displayContractId = 0u;
        if (changed) Bump();
    }

    private void Bump() => _revision++;

    private sealed class ContractView(RuntimeContractState owner) : IRuntimeContractView
    {
        public RuntimeContractsSnapshot Snapshot
        {
            get
            {
                lock (owner._gate)
                    return new RuntimeContractsSnapshot(
                        owner._revision,
                        owner._contracts.Count,
                        owner._displayContractId);
            }
        }

        public bool TryGetContract(uint contractId, out ContractTracker tracker)
        {
            lock (owner._gate)
                return owner._contracts.TryGetValue(contractId, out tracker);
        }

        public IReadOnlyList<ContractTracker> GetContracts()
        {
            lock (owner._gate)
            {
                var result = new ContractTracker[owner._contracts.Count];
                int i = 0;
                foreach (ContractTracker tracker in owner._contracts.Values)
                    result[i++] = tracker;
                Array.Sort(result, static (a, b) => a.ContractId.CompareTo(b.ContractId));
                return result;
            }
        }
    }
}
