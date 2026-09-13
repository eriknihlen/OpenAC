using AcDream.App.UI;

namespace AcDream.App.Input;

internal interface IInputCaptureSource
{
    bool WantCaptureMouse { get; }
    bool WantCaptureKeyboard { get; }
    bool DevToolsWantCaptureKeyboard { get; }
}

/// <summary>Something outside the retained UI that can claim the pointer or keyboard - the ImGui overlay.</summary>
internal interface IExternalInputCapture
{
    bool WantCaptureMouse { get; }

    bool WantCaptureKeyboard { get; }
}

/// <summary>
/// The overlay's capture claim, bound once the overlay exists. Created with the
/// window so gameplay input can hold a reference before the renderer is up.
/// </summary>
internal sealed class DevToolsInputCaptureSource : IExternalInputCapture
{
    private IExternalInputCapture? _source;

    public IDisposable Bind(IExternalInputCapture source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_source is not null)
            throw new InvalidOperationException("Dev-tools input capture is already bound.");
        _source = source;
        return new Binding(this, source);
    }

    public bool WantCaptureMouse => _source?.WantCaptureMouse ?? false;

    public bool WantCaptureKeyboard => _source?.WantCaptureKeyboard ?? false;

    private void Unbind(IExternalInputCapture expected)
    {
        if (ReferenceEquals(_source, expected))
            _source = null;
    }

    private sealed class Binding(DevToolsInputCaptureSource owner, IExternalInputCapture expected) : IDisposable
    {
        private DevToolsInputCaptureSource? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(expected);
    }
}

internal sealed class RetainedUiInputCaptureSlot
{
    public UiRoot? Root { get; private set; }

    public IDisposable Bind(UiRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Root is not null)
            throw new InvalidOperationException("Retained UI input capture is already bound.");
        Root = root;
        return new Binding(this, root);
    }

    public bool WantCaptureMouse => Root?.WantsMouse ?? false;
    public bool WantCaptureKeyboard => Root?.WantsKeyboard ?? false;

    private void Unbind(UiRoot expected)
    {
        if (ReferenceEquals(Root, expected))
            Root = null;
    }

    private sealed class Binding : IDisposable
    {
        private RetainedUiInputCaptureSlot? _owner;
        private readonly UiRoot _expected;

        public Binding(RetainedUiInputCaptureSlot owner, UiRoot expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}

internal sealed class CompositeInputCaptureSource : IInputCaptureSource
{
    private readonly DevToolsInputCaptureSource _devTools;
    private readonly RetainedUiInputCaptureSlot _retained;

    public CompositeInputCaptureSource(
        DevToolsInputCaptureSource devTools,
        RetainedUiInputCaptureSlot retained)
    {
        _devTools = devTools ?? throw new ArgumentNullException(nameof(devTools));
        _retained = retained ?? throw new ArgumentNullException(nameof(retained));
    }

    public bool WantCaptureMouse =>
        _devTools.WantCaptureMouse || _retained.WantCaptureMouse;

    public bool WantCaptureKeyboard =>
        DevToolsWantCaptureKeyboard || _retained.WantCaptureKeyboard;

    public bool DevToolsWantCaptureKeyboard =>
        _devTools.WantCaptureKeyboard;
}
