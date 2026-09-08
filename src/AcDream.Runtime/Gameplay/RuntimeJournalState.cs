using System;
using System.Collections.Generic;
using AcDream.Core.Journal;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeJournalOwnershipSnapshot(
    bool IsDisposed,
    int PageCount)
{
    public bool IsConverged => IsDisposed && PageCount == 0;
}

public readonly record struct RuntimeJournalSnapshot(
    long Revision,
    int PageCount,
    int CurrentPage,
    bool IsDirty);

public interface IRuntimeJournalView
{
    RuntimeJournalSnapshot Snapshot { get; }

    /// <summary>Every page, in file order.</summary>
    IReadOnlyList<JournalPage> Pages { get; }

    JournalPage Current { get; }

    double RemainingTimerSeconds(DateTime now);
}

public sealed class RuntimeJournalState : IDisposable
{
    private readonly object _gate = new();
    private readonly List<JournalPage> _pages = [];
    private int _currentPage;
    private long _revision;
    private bool _dirty;
    private bool _disposed;

    private DateTime? _timerStartedAt;
    private double _timerSecondsAtStart;

    public RuntimeJournalState() => View = new JournalView(this);

    public IRuntimeJournalView View { get; }

    public bool IsDirty
    {
        get { lock (_gate) return _dirty; }
    }

    /// <summary>Replaces the whole journal, as a file load does.</summary>
    public void Load(IReadOnlyList<JournalPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        lock (_gate)
        {
            if (_disposed) return;

            _pages.Clear();
            _pages.AddRange(pages);
            _currentPage = _pages.Count == 0 ? 0 : 1;
            _timerStartedAt = null;
            _timerSecondsAtStart = 0d;

            // A load is not an edit: the file already says this.
            _dirty = false;
            Bump();
        }
    }

    public IReadOnlyList<JournalPage> CaptureForSave(DateTime now)
    {
        lock (_gate)
        {
            var saved = new JournalPage[_pages.Count];
            for (int i = 0; i < _pages.Count; i++)
            {
                saved[i] = i + 1 == _currentPage
                    ? _pages[i] with { RunningTimerSeconds = RemainingLocked(now) }
                    : _pages[i];
            }

            return saved;
        }
    }

    public void MarkSaved()
    {
        lock (_gate) _dirty = false;
    }

    public void NewPage()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pages.Add(JournalPage.Empty);
            _currentPage = _pages.Count;
            ClearTimerLocked();
            _dirty = true;
            Bump();
        }
    }

    public bool DeletePage(int pageNumber)
    {
        lock (_gate)
        {
            if (_disposed || pageNumber < 1 || pageNumber > _pages.Count)
                return false;

            _pages.RemoveAt(pageNumber - 1);
            if (_currentPage > _pages.Count)
                _currentPage = _pages.Count;
            if (_pages.Count == 0)
                _currentPage = 0;

            ClearTimerLocked();
            _dirty = true;
            Bump();
            return true;
        }
    }

    public bool GotoPage(int pageNumber)
    {
        lock (_gate)
        {
            if (_disposed || pageNumber < 1 || pageNumber > _pages.Count)
                return false;
            if (_currentPage == pageNumber)
                return true;

            _currentPage = pageNumber;
            ClearTimerLocked();
            Bump();
            return true;
        }
    }

    public void UpdateCurrent(string label, string title, string notes)
    {
        lock (_gate)
        {
            if (_disposed || _currentPage == 0) return;

            JournalPage updated = (_pages[_currentPage - 1] with
            {
                Label = label ?? string.Empty,
                Title = title ?? string.Empty,
                Notes = notes ?? string.Empty,
            }).Clipped();

            if (updated == _pages[_currentPage - 1])
                return;

            _pages[_currentPage - 1] = updated;
            _dirty = true;
            Bump();
        }
    }

    public void RecordLocation(float x, float y)
    {
        lock (_gate)
        {
            if (_disposed || _currentPage == 0) return;
            _pages[_currentPage - 1] = _pages[_currentPage - 1] with
            {
                LocationX = x,
                LocationY = y,
                HasLocation = true,
            };
            _dirty = true;
            Bump();
        }
    }

    public void SetTimer(int days, int hours, int minutes)
    {
        lock (_gate)
        {
            if (_disposed || _currentPage == 0) return;
            _pages[_currentPage - 1] = _pages[_currentPage - 1] with
            {
                TimerDays = Math.Max(0, days),
                TimerHours = Math.Max(0, hours),
                TimerMinutes = Math.Max(0, minutes),
            };
            _dirty = true;
            Bump();
        }
    }

    public bool StartTimer(DateTime now)
    {
        lock (_gate)
        {
            if (_disposed || _currentPage == 0) return false;

            JournalPage page = _pages[_currentPage - 1];
            double seconds = page.TimerDuration.TotalSeconds;
            if (seconds <= 0d)
                return false;

            _timerStartedAt = now;
            _timerSecondsAtStart = seconds;
            _pages[_currentPage - 1] = page with { RunningTimerSeconds = seconds };
            _dirty = true;
            Bump();
            return true;
        }
    }

    public void ResetTimer()
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool wasRunning = _timerStartedAt is not null;
            ClearTimerLocked();
            if (_currentPage != 0)
            {
                _pages[_currentPage - 1] =
                    _pages[_currentPage - 1] with { RunningTimerSeconds = 0d };
            }

            if (wasRunning) _dirty = true;
            Bump();
        }
    }

    public bool IsLastPage
    {
        get { lock (_gate) return _currentPage != 0 && _currentPage == _pages.Count; }
    }

    public RuntimeJournalOwnershipSnapshot CaptureOwnership()
    {
        lock (_gate) return new RuntimeJournalOwnershipSnapshot(_disposed, _pages.Count);
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
        bool changed = _pages.Count != 0 || _currentPage != 0;
        _pages.Clear();
        _currentPage = 0;
        _dirty = false;
        ClearTimerLocked();
        if (changed) Bump();
    }

    private void ClearTimerLocked()
    {
        _timerStartedAt = null;
        _timerSecondsAtStart = 0d;
    }

    private double RemainingLocked(DateTime now)
    {
        if (_timerStartedAt is not { } started)
            return _currentPage == 0 ? 0d : _pages[_currentPage - 1].RunningTimerSeconds;

        double remaining = _timerSecondsAtStart - (now - started).TotalSeconds;
        return remaining > 0d ? remaining : 0d;
    }

    private void Bump() => _revision++;

    private sealed class JournalView(RuntimeJournalState owner) : IRuntimeJournalView
    {
        public RuntimeJournalSnapshot Snapshot
        {
            get
            {
                lock (owner._gate)
                    return new RuntimeJournalSnapshot(
                        owner._revision,
                        owner._pages.Count,
                        owner._currentPage,
                        owner._dirty);
            }
        }

        public IReadOnlyList<JournalPage> Pages
        {
            get { lock (owner._gate) return owner._pages.ToArray(); }
        }

        public JournalPage Current
        {
            get
            {
                lock (owner._gate)
                    return owner._currentPage == 0
                        ? JournalPage.Empty
                        : owner._pages[owner._currentPage - 1];
            }
        }

        public double RemainingTimerSeconds(DateTime now)
        {
            lock (owner._gate) return owner.RemainingLocked(now);
        }
    }
}
