using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanGpuTimerPool : IGpuTimerPool, IDisposable
{
    /// <summary>Distinct named scopes measurable per frame. Two queries each.</summary>
    internal const int MaxScopesPerFrame = 16;

    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly double _timestampPeriodNanoseconds;
    private readonly QueryPool[] _pools;
    private readonly List<string>[] _scopeNames;
    private readonly Dictionary<string, double> _resolved = new(StringComparer.Ordinal);

    private int _currentSlot;
    private bool _disposed;

    internal VulkanGpuTimerPool(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        int flightCount,
        bool isSupported)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        IsSupported = isSupported;

        vk.GetPhysicalDeviceProperties(physicalDevice, out PhysicalDeviceProperties properties);
        _timestampPeriodNanoseconds = properties.Limits.TimestampPeriod;

        _pools = new QueryPool[flightCount];
        _scopeNames = new List<string>[flightCount];
        for (int slot = 0; slot < flightCount; slot++)
        {
            _scopeNames[slot] = [];
            if (!isSupported)
                continue;

            var create = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = MaxScopesPerFrame * 2,
            };
            VulkanInterop.Check(
                _vk.CreateQueryPool(_device, &create, null, out QueryPool pool),
                "vkCreateQueryPool (timer pool)");
            _pools[slot] = pool;
        }
    }

    public bool IsSupported { get; }

    internal void BeginSlot(int slotIndex)
    {
        if (!IsSupported || _disposed)
            return;

        _currentSlot = slotIndex;
        List<string> names = _scopeNames[slotIndex];
        if (names.Count > 0)
        {
            Resolve(slotIndex, names);
            names.Clear();
        }

        _vk.ResetQueryPool(_device, _pools[slotIndex], 0, MaxScopesPerFrame * 2);
    }

    internal IDisposable BeginScope(CommandBuffer commands, string scopeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeName);
        if (!IsSupported || _disposed)
            return NullScope.Instance;

        List<string> names = _scopeNames[_currentSlot];
        if (names.Count >= MaxScopesPerFrame)
            return NullScope.Instance;
        if (names.Contains(scopeName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"GPU timer scope '{scopeName}' has already been measured this frame. Two ranges " +
                "sharing a name would silently report whichever finished last.");
        }

        int index = names.Count;
        names.Add(scopeName);
        _vk.CmdWriteTimestamp2(
            commands,
            PipelineStageFlags2.TopOfPipeBit,
            _pools[_currentSlot],
            (uint)(index * 2));
        return new ActiveScope(this, commands, _currentSlot, index);
    }

    private void EndScope(CommandBuffer commands, int slotIndex, int index)
    {
        if (!IsSupported || _disposed)
            return;
        _vk.CmdWriteTimestamp2(
            commands,
            PipelineStageFlags2.BottomOfPipeBit,
            _pools[slotIndex],
            (uint)((index * 2) + 1));
    }

    private void Resolve(int slotIndex, List<string> names)
    {
        int queryCount = names.Count * 2;
        Span<ulong> results = stackalloc ulong[MaxScopesPerFrame * 2];
        fixed (ulong* first = results)
        {
            Result status = _vk.GetQueryPoolResults(
                _device,
                _pools[slotIndex],
                0,
                (uint)queryCount,
                (nuint)(queryCount * sizeof(ulong)),
                first,
                sizeof(ulong),
                QueryResultFlags.Result64Bit);
            // NotReady is normal and expected the first time a slot recurs on a
            // fast GPU; the previous value simply stands. It is never worth a
            // wait, which is the whole design.
            if (status != Result.Success)
                return;
        }

        for (int i = 0; i < names.Count; i++)
        {
            ulong start = results[i * 2];
            ulong end = results[(i * 2) + 1];
            if (end <= start)
                continue;
            double nanoseconds = (end - start) * _timestampPeriodNanoseconds;
            _resolved[names[i]] = nanoseconds / 1_000_000d;
        }
    }

    public bool TryResolve(string scopeName, out double milliseconds) =>
        _resolved.TryGetValue(scopeName, out milliseconds);

    public bool TryTakeResolved(string scopeName, out double milliseconds)
    {
        if (!_resolved.TryGetValue(scopeName, out milliseconds))
            return false;
        _resolved.Remove(scopeName);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (QueryPool pool in _pools)
        {
            if (pool.Handle != 0)
                _vk.DestroyQueryPool(_device, pool, null);
        }
    }

    private sealed class ActiveScope(
        VulkanGpuTimerPool pool,
        CommandBuffer commands,
        int slotIndex,
        int index) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended)
                return;
            _ended = true;
            pool.EndScope(commands, slotIndex, index);
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
