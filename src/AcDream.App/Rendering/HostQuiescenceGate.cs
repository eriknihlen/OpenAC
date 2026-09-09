namespace AcDream.App.Rendering;

internal sealed class HostQuiescenceGate
{
    private readonly object _sync = new();
    private bool _accepting = true;

    public bool IsAccepting
    {
        get
        {
            lock (_sync)
                return _accepting;
        }
    }

    internal bool IsEnteredByCurrentThread => Monitor.IsEntered(_sync);

    public void StopAccepting()
    {
        lock (_sync)
            _accepting = false;
    }

    public void Invoke(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            if (_accepting)
                callback();
        }
    }

    public void Invoke<T>(Action<T> callback, T value)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            if (_accepting)
                callback(value);
        }
    }
}
