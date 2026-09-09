using AcDream.App.Input;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

public sealed class DispatcherCameraInputSourceTests
{
    [Fact]
    public void ExactUnbindRestoresNeutralCameraSampling()
    {
        var keyboard = new Keyboard();
        var mouse = new Mouse();
        var dispatcher = InputDispatcher.CreateDetached(keyboard, mouse, new KeyBindings());
        dispatcher.Attach();
        var other = InputDispatcher.CreateDetached(new Keyboard(), new Mouse(), new KeyBindings());
        other.Attach();
        var source = new DispatcherCameraInputSource();
        source.Bind(dispatcher);
        dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, held: true);

        source.Unbind(other);
        Assert.True(source.CaptureFly().Forward);
        source.Unbind(dispatcher);

        Assert.False(source.IsAvailable);
        Assert.Equal(default, source.CaptureFly());
        Assert.Equal(default, source.CaptureChaseAdjustment());
    }

    private sealed class Keyboard : IKeyboardSource
    {
#pragma warning disable CS0067
        public event Action<Key, ModifierMask>? KeyDown;
        public event Action<Key, ModifierMask>? KeyUp;
#pragma warning restore CS0067
        public bool IsHeld(Key key) => false;
        public ModifierMask CurrentModifiers => ModifierMask.None;
    }

    private sealed class Mouse : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool IsHeld(MouseButton button) => false;
        public bool WantCaptureMouse => false;
        public bool WantCaptureKeyboard => false;
    }
}
