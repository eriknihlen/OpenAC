namespace AcDream.App.Tests.Plugins;

#pragma warning disable CS0067 // Events required by IKeyboard, unused by these tests.

/// <summary>
/// A minimal stand-in for Silk.NET's IKeyboard, used only to exercise
/// WindowPluginClipboard. <see cref="StoredText"/> and the Get/Set call
/// counters are deliberately kept separate from the interface's explicit
/// ClipboardText accessor: a test asserting through StoredText/GetCount
/// is checking what actually happened through the interface, not just
/// reading back the same field it wrote -- the tautology the interface
/// property alone would produce.
/// </summary>
internal sealed class FakeKeyboard : Silk.NET.Input.IKeyboard
{
    private string _stored = string.Empty;

    /// <summary>
    /// When set, models what the OS clipboard actually does with a write
    /// attempt: return true to let it land, false to silently swallow it
    /// -- the exact hazard WindowPluginClipboard's read-back verification
    /// exists to catch. Left null, every write lands.
    /// </summary>
    public Func<string, bool>? OnSetClipboardText { get; set; }

    /// <summary>How many times the interface's ClipboardText getter ran.</summary>
    public int GetCount { get; private set; }

    /// <summary>How many times the interface's ClipboardText setter ran.</summary>
    public int SetCount { get; private set; }

    /// <summary>
    /// The backing store, read directly rather than through the
    /// interface's ClipboardText getter -- an independent view a test can
    /// assert against without exercising the same accessor
    /// WindowPluginClipboard's own read-back verification calls.
    /// </summary>
    public string StoredText => _stored;

    string Silk.NET.Input.IInputDevice.Name => "fake-keyboard";
    int Silk.NET.Input.IInputDevice.Index => 0;
    bool Silk.NET.Input.IInputDevice.IsConnected => true;
    IReadOnlyList<Silk.NET.Input.Key> Silk.NET.Input.IKeyboard.SupportedKeys => [];

    string Silk.NET.Input.IKeyboard.ClipboardText
    {
        get
        {
            GetCount++;
            return _stored;
        }
        set
        {
            SetCount++;
            if (OnSetClipboardText is null || OnSetClipboardText(value))
                _stored = value;
        }
    }

    public bool IsKeyPressed(Silk.NET.Input.Key key) => false;
    public bool IsScancodePressed(int scancode) => false;
    public void BeginInput() { }
    public void EndInput() { }

    public event Action<Silk.NET.Input.IKeyboard, Silk.NET.Input.Key, int>? KeyDown;
    public event Action<Silk.NET.Input.IKeyboard, Silk.NET.Input.Key, int>? KeyUp;
    public event Action<Silk.NET.Input.IKeyboard, char>? KeyChar;
}
#pragma warning restore CS0067
