using System;
using System.Threading;

namespace AcDream.Core.Chat;

public sealed class ChatWindowState
{
    public const int MainWindowId = 0;
    public const int MinFloatingWindowId = 1;
    public const int MaxFloatingWindowId = 4;

    public const ulong MainWindowDefaultFilter = 0xFBFFFFFFul;
    public const ulong Floaty1DefaultFilter = 0x0000101Cul;
    public const ulong Floaty2DefaultFilter = 0x00040C00ul;
    public const ulong Floaty3DefaultFilter = 0x00080000ul;
    public const ulong Floaty4DefaultFilter = 0x78000000ul;

    public const uint BroadcastTargetWindow = uint.MaxValue;

    private const int WindowCount = MaxFloatingWindowId + 1;

    private readonly object _gate = new();
    private readonly ulong[] _filters = new ulong[WindowCount];
    private readonly bool[] _open = new bool[WindowCount];
    private long _revision;

    public ChatWindowState() => ResetToDefaults();

    public long Revision => Interlocked.Read(ref _revision);

    public void ResetToDefaults()
    {
        lock (_gate)
        {
            _filters[0] = MainWindowDefaultFilter;
            // Speech, Tell, Speech_Direct_Send, Emote (m_oldState 2).
            _filters[1] = Floaty1DefaultFilter;
            // Social, Social_Send, Allegiance (m_oldState 3).
            _filters[2] = Floaty2DefaultFilter;
            // Fellowship (m_oldState 4).
            _filters[3] = Floaty3DefaultFilter;
            _filters[4] = Floaty4DefaultFilter;

            _open[0] = true;
            for (int i = MinFloatingWindowId; i <= MaxFloatingWindowId; i++)
                _open[i] = false;

            Interlocked.Increment(ref _revision);
        }
    }

    public ulong GetFilter(int windowId)
    {
        ValidateWindowId(windowId);
        lock (_gate) return _filters[windowId];
    }

    public void SetFilter(int windowId, ulong filter)
    {
        ValidateWindowId(windowId);
        lock (_gate)
        {
            if (_filters[windowId] == filter) return;
            _filters[windowId] = filter;
            Interlocked.Increment(ref _revision);
        }
    }

    /// <summary>Main window (id 0) is always open.</summary>
    public bool IsOpen(int windowId)
    {
        ValidateWindowId(windowId);
        if (windowId == MainWindowId) return true;
        lock (_gate) return _open[windowId];
    }

    public void SetOpen(int windowId, bool open)
    {
        ValidateWindowId(windowId);
        if (windowId == MainWindowId) return;
        lock (_gate)
        {
            if (_open[windowId] == open) return;
            _open[windowId] = open;
            Interlocked.Increment(ref _revision);
        }
    }

    public bool Toggle(int windowId)
    {
        ValidateWindowId(windowId);
        if (windowId == MainWindowId) return true;
        lock (_gate)
        {
            bool next = !_open[windowId];
            _open[windowId] = next;
            Interlocked.Increment(ref _revision);
            return next;
        }
    }

    public bool TypeIsActive(int windowId, uint logTextType)
    {
        ValidateWindowId(windowId);
        if (logTextType >= 64u) return false;
        ulong filter;
        lock (_gate) filter = _filters[windowId];
        return ((1UL << (int)logTextType) & filter) != 0UL;
    }

    public bool ShouldDisplay(int windowId, uint targetWindowId, uint logTextType)
    {
        ValidateWindowId(windowId);
        if (targetWindowId == (uint)windowId) return true;
        return targetWindowId == BroadcastTargetWindow && TypeIsActive(windowId, logTextType);
    }

    private static void ValidateWindowId(int windowId)
    {
        if (windowId < MainWindowId || windowId > MaxFloatingWindowId)
            throw new ArgumentOutOfRangeException(
                nameof(windowId), windowId, "chat window id must be 0 (main) through 4 (floating).");
    }
}
