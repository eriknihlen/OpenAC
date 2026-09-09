using System.Collections.Concurrent;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessConsoleInputReader : IDisposable
{
    private readonly TextReader _input;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly Thread _thread;
    private volatile bool _stopRequested;

    internal HeadlessConsoleInputReader(TextReader input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "acdream-headless-console-reader",
        };
        _thread.Start();
    }

    /// <summary>Set once the reader loop has returned (EOF or stop request).
    /// Tests wait on this instead of sleeping/polling for a deterministic
    /// "every line the fixture will ever produce has been enqueued" signal.
    /// </summary>
    internal ManualResetEventSlim EndOfInput { get; } = new(initialState: false);

    /// <summary>Dequeues the next queued line in FIFO order, or returns
    /// <see langword="false"/> if none is queued yet. Never blocks.</summary>
    internal bool TryDequeue(out string line) => _queue.TryDequeue(out line!);

    private void ReadLoop()
    {
        try
        {
            while (!_stopRequested)
            {
                string? line = _input.ReadLine();
                if (line is null)
                    return;
                _queue.Enqueue(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // The input was disposed out from under a pending read (process
            // teardown racing the reader thread) — end the loop quietly,
            // same as EOF.
        }
        catch (IOException)
        {
        }
        finally
        {
            EndOfInput.Set();
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
    }
}
