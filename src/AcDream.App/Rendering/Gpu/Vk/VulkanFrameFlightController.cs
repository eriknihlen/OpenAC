using AcDream.App.Rendering;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace AcDream.App.Rendering.Gpu.Vk;

internal interface IVulkanTimelineApi
{
    /// <summary>Highest value the timeline has signalled.</summary>
    ulong CurrentValue { get; }

    /// <summary>Blocks until the timeline reaches <paramref name="value"/>.</summary>
    void Wait(ulong value);
}

/// <summary>Live implementation over one <c>VkSemaphore</c> of type timeline.</summary>
internal sealed unsafe class VulkanTimelineApi(
    Silk.NET.Vulkan.Vk vk,
    Device device,
    Semaphore timeline) : IVulkanTimelineApi
{
    private readonly Silk.NET.Vulkan.Vk _vk = vk ?? throw new ArgumentNullException(nameof(vk));

    public ulong CurrentValue
    {
        get
        {
            VulkanInterop.Check(
                _vk.GetSemaphoreCounterValue(device, timeline, out ulong value),
                "vkGetSemaphoreCounterValue");
            return value;
        }
    }

    public void Wait(ulong value)
    {
        Semaphore semaphore = timeline;
        ulong target = value;
        var wait = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &target,
        };
        VulkanInterop.Check(
            _vk.WaitSemaphores(device, &wait, ulong.MaxValue),
            $"vkWaitSemaphores (frame flight, value {value})");
    }
}

internal sealed class VulkanFrameFlightController : IGpuResourceRetirementQueue, IDisposable
{
    /// <summary>Plan §4.8: two frames in flight.</summary>
    internal const int DefaultFramesInFlight = 2;

    private readonly IVulkanTimelineApi _timeline;
    private readonly SortedDictionary<long, List<Action>> _retirements = [];
    private readonly object _sync = new();

    private long _openSerial;
    private long _submittedSerial;
    private bool _disposed;

    internal VulkanFrameFlightController(
        IVulkanTimelineApi timeline,
        int framesInFlight = DefaultFramesInFlight)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);
        SlotCount = framesInFlight;
    }

    internal int SlotCount { get; }

    internal long OpenSerial
    {
        get
        {
            lock (_sync)
                return _openSerial;
        }
    }

    internal long SubmittedSerial
    {
        get
        {
            lock (_sync)
                return _submittedSerial;
        }
    }

    internal int CurrentSlot
    {
        get
        {
            lock (_sync)
                return SlotIndexOf(_openSerial);
        }
    }

    internal int PendingRetirementCount
    {
        get
        {
            lock (_sync)
                return _retirements.Sum(entry => entry.Value.Count);
        }
    }

    /// <summary>Maps a frame serial onto its flight slot. Serials are 1-based.</summary>
    internal int SlotIndexOf(long serial) =>
        serial <= 0 ? 0 : (int)((serial - 1) % SlotCount);

    internal long BeginFrame()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_openSerial != 0)
            {
                throw new InvalidOperationException(
                    $"Frame {_openSerial} is still open; call EndFrame before beginning another.");
            }

            long serial = _submittedSerial + 1;
            long mustComplete = serial - SlotCount;
            if (mustComplete > 0)
                _timeline.Wait((ulong)mustComplete);

            _openSerial = serial;
            RunRetirements();
            return serial;
        }
    }

    /// <summary>Records that the open frame has been submitted with its serial as the timeline signal value.</summary>
    internal void EndFrame()
    {
        lock (_sync)
        {
            if (_openSerial == 0)
                return;
            _submittedSerial = _openSerial;
            _openSerial = 0;
        }
    }

    public void Retire(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_sync)
        {
            if (_disposed)
            {
                // Teardown already drained the ledger; running immediately is the
                // only way this release ever happens, and by then the device is idle.
                release();
                return;
            }

            long key = _openSerial != 0 ? _openSerial : _submittedSerial + 1;
            if (!_retirements.TryGetValue(key, out List<Action>? actions))
            {
                actions = [];
                _retirements.Add(key, actions);
            }

            actions.Add(release);
        }
    }

    internal void RunRetirements()
    {
        lock (_sync)
        {
            if (_retirements.Count == 0)
                return;

            var completed = (long)_timeline.CurrentValue;
            while (_retirements.Count > 0)
            {
                KeyValuePair<long, List<Action>> first = _retirements.First();
                if (first.Key > completed)
                    break;

                _retirements.Remove(first.Key);
                foreach (Action release in first.Value)
                    release();
            }
        }
    }

    internal void WaitForSubmittedWork()
    {
        lock (_sync)
        {
            if (_submittedSerial > 0)
                _timeline.Wait((ulong)_submittedSerial);
            DrainAll();
        }
    }

    /// <summary>Runs every pending retirement regardless of serial. Only legal when the device is idle.</summary>
    internal void DrainAll()
    {
        lock (_sync)
        {
            while (_retirements.Count > 0)
            {
                KeyValuePair<long, List<Action>> first = _retirements.First();
                _retirements.Remove(first.Key);
                foreach (Action release in first.Value)
                    release();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            DrainAll();
            _disposed = true;
        }
    }
}
