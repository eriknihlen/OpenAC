using AcDream.Runtime.Chat;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessConsoleController : IDisposable
{
    private readonly HeadlessConsoleInputReader _reader;
    private readonly TextWriter _output;
    private readonly Func<string, SubmitOutcome> _submit;
    private readonly Func<string> _statusText;
    private readonly CancellationTokenSource _quitRequested;

    internal HeadlessConsoleController(
        TextReader input,
        TextWriter output,
        Func<string, SubmitOutcome> submit,
        Func<string> statusText,
        CancellationTokenSource quitRequested)
    {
        ArgumentNullException.ThrowIfNull(input);
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        _statusText = statusText ?? throw new ArgumentNullException(nameof(statusText));
        _quitRequested = quitRequested
            ?? throw new ArgumentNullException(nameof(quitRequested));
        _reader = new HeadlessConsoleInputReader(input);
    }

    internal int LastDrainCount { get; private set; }

    internal HeadlessConsoleInputReader Reader => _reader;

    internal void DrainDue()
    {
        int count = 0;
        while (_reader.TryDequeue(out string line))
        {
            Handle(line);
            count++;
        }
        LastDrainCount = count;
    }

    private void Handle(string rawLine)
    {
        string trimmed = rawLine.Trim();
        if (trimmed.Length == 0)
            return;

        if (trimmed.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            WriteLine("quitting (graceful logout)");
            _quitRequested.Cancel();
            return;
        }

        if (trimmed.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            WriteLine(_statusText());
            return;
        }

        try
        {
            SubmitOutcome outcome = _submit(rawLine);
            if (outcome is SubmitOutcome.UnknownCommand or SubmitOutcome.Dropped)
                WriteLine($"not handled ({outcome}): {rawLine}");
        }
        catch (Exception error)
        {
            WriteLine($"command failed: {error.GetBaseException().Message}");
        }
    }

    private void WriteLine(string text)
    {
        _output.WriteLine(text);
        _output.Flush();
    }

    public void Dispose() => _reader.Dispose();
}
