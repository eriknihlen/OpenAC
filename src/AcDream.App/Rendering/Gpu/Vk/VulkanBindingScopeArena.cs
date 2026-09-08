namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanBindingScopeArena
{
    private readonly int _storageBindingCount;
    private readonly int _uniformBindingCount;
    private readonly Func<uint, bool> _isDynamicStorage;

    private readonly ulong[] _storageBuffers;
    private readonly uint[] _storageOffsets;
    private readonly uint[] _storageRanges;
    private readonly ulong[] _uniformBuffers;
    private readonly uint[] _uniformOffsets;
    private readonly uint[] _uniformRanges;

    private readonly List<Entry> _entries = [];
    private int _liveCount;
    private int _active = -1;
    private bool _dirty = true;

    private sealed class Entry(int storageBindingCount, int uniformBindingCount)
    {
        public ulong[] StorageBuffers { get; } = new ulong[storageBindingCount];
        public uint[] StorageOffsets { get; } = new uint[storageBindingCount];
        public uint[] StorageRanges { get; } = new uint[storageBindingCount];
        public ulong[] UniformBuffers { get; } = new ulong[uniformBindingCount];
        public uint[] UniformRanges { get; } = new uint[uniformBindingCount];
        public int Slot { get; set; } = -1;
    }

    internal VulkanBindingScopeArena(
        int storageBindingCount,
        int uniformBindingCount,
        Func<uint, bool> isDynamicStorage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storageBindingCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(uniformBindingCount);
        _storageBindingCount = storageBindingCount;
        _uniformBindingCount = uniformBindingCount;
        _isDynamicStorage = isDynamicStorage
            ?? throw new ArgumentNullException(nameof(isDynamicStorage));
        _storageBuffers = new ulong[storageBindingCount];
        _storageOffsets = new uint[storageBindingCount];
        _storageRanges = new uint[storageBindingCount];
        _uniformBuffers = new ulong[uniformBindingCount];
        _uniformOffsets = new uint[uniformBindingCount];
        _uniformRanges = new uint[uniformBindingCount];
    }

    /// <summary>Distinct descriptor states this slot has ever materialised.</summary>
    internal int Count => _entries.Count;

    internal int LiveCount => _liveCount;

    internal void SeedStorage(uint binding, ulong buffer, uint offsetBytes, uint rangeBytes)
    {
        _storageBuffers[binding] = buffer;
        _storageOffsets[binding] = offsetBytes;
        _storageRanges[binding] = rangeBytes;
    }

    internal void SeedUniform(uint binding, ulong buffer, uint rangeBytes)
    {
        _uniformBuffers[binding] = buffer;
        _uniformRanges[binding] = rangeBytes;
    }

    internal void BeginFrame()
    {
        _liveCount = 0;
        _active = -1;
        _dirty = true;
    }

    internal void SetStorage(uint binding, ulong buffer, uint offsetBytes, uint rangeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(binding, (uint)_storageBindingCount);
        bool dynamic = _isDynamicStorage(binding);
        if (_storageBuffers[binding] != buffer
            || _storageRanges[binding] != rangeBytes
            || (!dynamic && _storageOffsets[binding] != offsetBytes))
        {
            _storageBuffers[binding] = buffer;
            _storageRanges[binding] = rangeBytes;
            _dirty = true;
        }

        _storageOffsets[binding] = offsetBytes;
    }

    internal void SetUniform(uint binding, ulong buffer, uint offsetBytes, uint rangeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(binding, (uint)_uniformBindingCount);
        if (_uniformBuffers[binding] != buffer || _uniformRanges[binding] != rangeBytes)
        {
            _uniformBuffers[binding] = buffer;
            _uniformRanges[binding] = rangeBytes;
            _dirty = true;
        }

        _uniformOffsets[binding] = offsetBytes;
    }

    internal uint StorageOffset(uint binding) => _storageOffsets[binding];

    internal uint UniformOffset(uint binding) => _uniformOffsets[binding];

    internal ulong StorageBuffer(uint binding) => _storageBuffers[binding];

    internal uint StorageRange(uint binding) => _storageRanges[binding];

    internal uint StorageDescriptorOffset(uint binding) =>
        _isDynamicStorage(binding) ? 0u : _storageOffsets[binding];

    internal ulong UniformBuffer(uint binding) => _uniformBuffers[binding];

    internal uint UniformRange(uint binding) => _uniformRanges[binding];

    internal (int Index, int Slot, bool NeedsWrite) Resolve()
    {
        if (!_dirty && _active >= 0)
            return (_active, _entries[_active].Slot, false);

        for (int i = 0; i < _entries.Count; i++)
        {
            if (!Matches(_entries[i]))
                continue;
            if (i >= _liveCount)
            {
                (_entries[i], _entries[_liveCount]) = (_entries[_liveCount], _entries[i]);
                _active = _liveCount;
                _liveCount++;
            }
            else
            {
                _active = i;
            }

            _dirty = false;
            return (_active, _entries[_active].Slot, false);
        }

        while (_entries.Count <= _liveCount)
            _entries.Add(new Entry(_storageBindingCount, _uniformBindingCount));

        Entry target = _entries[_liveCount];
        Adopt(target);
        _active = _liveCount;
        _liveCount++;
        _dirty = false;
        return (_active, target.Slot, true);
    }

    internal void AssignSlot(int index, int slot) => _entries[index].Slot = slot;

    private bool Matches(Entry entry)
    {
        if (entry.Slot < 0)
            return false;
        for (uint binding = 0; binding < _storageBindingCount; binding++)
        {
            if (entry.StorageBuffers[binding] != _storageBuffers[binding])
                return false;
            if (entry.StorageRanges[binding] != _storageRanges[binding])
                return false;
            if (!_isDynamicStorage(binding) && entry.StorageOffsets[binding] != _storageOffsets[binding])
                return false;
        }

        for (uint binding = 0; binding < _uniformBindingCount; binding++)
        {
            if (entry.UniformBuffers[binding] != _uniformBuffers[binding])
                return false;
            if (entry.UniformRanges[binding] != _uniformRanges[binding])
                return false;
        }

        return true;
    }

    private void Adopt(Entry entry)
    {
        Array.Copy(_storageBuffers, entry.StorageBuffers, _storageBindingCount);
        Array.Copy(_storageOffsets, entry.StorageOffsets, _storageBindingCount);
        Array.Copy(_storageRanges, entry.StorageRanges, _storageBindingCount);
        Array.Copy(_uniformBuffers, entry.UniformBuffers, _uniformBindingCount);
        Array.Copy(_uniformRanges, entry.UniformRanges, _uniformBindingCount);
    }
}
