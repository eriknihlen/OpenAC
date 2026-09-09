using AcDream.Core.Chat;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessConsoleSpewBoxPump
{
    private readonly SpewBoxState _spewBox;
    private readonly Func<double> _nowSeconds;
    private readonly Action<string> _writeInterfaceText;
    private SpewBoxEntry[] _lastSeen = [];

    internal HeadlessConsoleSpewBoxPump(
        SpewBoxState spewBox,
        Func<double> nowSeconds,
        Action<string> writeInterfaceText)
    {
        _spewBox = spewBox ?? throw new ArgumentNullException(nameof(spewBox));
        _nowSeconds = nowSeconds
            ?? throw new ArgumentNullException(nameof(nowSeconds));
        _writeInterfaceText = writeInterfaceText
            ?? throw new ArgumentNullException(nameof(writeInterfaceText));
    }

    internal void Pump()
    {
        _spewBox.Tick(_nowSeconds());
        SpewBoxEntry[] current = _spewBox.Snapshot();
        for (int i = current.Length - 1; i >= 0; i--)
        {
            if (Array.IndexOf(_lastSeen, current[i]) < 0)
                _writeInterfaceText(current[i].Text);
        }
        _lastSeen = current;
    }
}
