using AcDream.App.Input;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

public sealed class DispatcherMovementInputSourceTests
{
    [Fact]
    public void UnboundSourceCapturesNeutralInput()
    {
        var source = CreateSource();

        Assert.Equal(default, source.Capture());
    }

    [Fact]
    public void CapturesDispatcherHeldStateAndRetailWalkModifier()
    {
        var (dispatcher, _, _) = CreateDispatcher();
        var source = CreateSource();
        source.Bind(dispatcher);

        Assert.True(dispatcher.TrySetAutomationActionHeld(
            InputAction.MovementForward,
            held: true));
        Assert.True(dispatcher.TrySetAutomationActionHeld(
            InputAction.MovementWalkMode,
            held: true));

        MovementInput captured = source.Capture();

        Assert.True(captured.Forward);
        Assert.False(captured.Run);
    }

    [Fact]
    public void CommandInputIsMarkedPersistentWithoutChangingStoredSnapshot()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        var command = new MovementInput(TurnRight: true);
        movement.SetCommandInput(command);
        var source = new DispatcherMovementInputSource(movement);

        MovementInput captured = source.Capture();

        Assert.True(captured.TurnRight);
        Assert.True(captured.IsPersistentCommand);
        Assert.Equal(command, movement.CommandInput);
    }

    [Fact]
    public void RetainedKeyboardCaptureSilencesHeldKeysButDoesNotCancelAutorun()
    {
        var (dispatcher, _, mouse) = CreateDispatcher();
        var source = CreateSource();
        source.Bind(dispatcher);
        dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, held: true);
        Assert.True(source.HandlePressedAction(InputAction.MovementRunLock));

        mouse.WantCaptureKeyboard = true;
        MovementInput captured = source.Capture();

        Assert.True(captured.Forward);
        Assert.True(source.AutoRunActive);

        source.HandlePressedAction(InputAction.MovementRunLock);
        captured = source.Capture();
        Assert.False(captured.Forward);
    }

    [Fact]
    public void DevToolsKeyboardCapturePausesAutorunWithoutClearingItsLatch()
    {
        var capture = new FakeCapture();
        var (dispatcher, _, _) = CreateDispatcher();
        var source = CreateSource(capture);
        source.Bind(dispatcher);
        source.HandlePressedAction(InputAction.MovementRunLock);

        capture.DevToolsWantCaptureKeyboard = true;
        Assert.Equal(default, source.Capture());
        Assert.True(source.AutoRunActive);

        capture.DevToolsWantCaptureKeyboard = false;
        Assert.True(source.Capture().Forward);
    }

    [Theory]
    [InlineData(InputAction.MovementBackup)]
    [InlineData(InputAction.MovementStop)]
    [InlineData(InputAction.MovementStrafeLeft)]
    [InlineData(InputAction.MovementStrafeRight)]
    public void RetailCancelActionsClearAutorun(InputAction action)
    {
        var source = CreateSource();
        source.HandlePressedAction(InputAction.MovementRunLock);

        Assert.False(source.HandlePressedAction(action));

        Assert.False(source.AutoRunActive);
    }

    [Fact]
    public void ForwardCancelsAutorun_AndResetClearsIt()
    {
        var source = CreateSource();
        source.HandlePressedAction(InputAction.MovementRunLock);
        Assert.True(source.AutoRunActive);

        source.HandlePressedAction(InputAction.MovementForward);
        Assert.False(source.AutoRunActive);

        source.HandlePressedAction(InputAction.MovementRunLock);
        Assert.True(source.AutoRunActive);
        source.ResetSession();
        Assert.False(source.AutoRunActive);
    }


    [Theory]
    [InlineData(true, false, true)]   // run-by-default, no walk modifier -> runs
    [InlineData(true, true, false)]   // run-by-default, walk modifier held -> walks
    [InlineData(false, false, false)] // walk-by-default, no modifier -> walks
    [InlineData(false, true, true)]   // walk-by-default, modifier held -> runs
    public void Capture_RunReflectsOptionXorWalkModifier(
        bool runAsDefault, bool walkModifierHeld, bool expectedRun)
    {
        var (dispatcher, _, _) = CreateDispatcher();
        var movement = new RuntimeLocalPlayerMovementState
        {
            RunAsDefaultMovementSource = () => runAsDefault,
        };
        var source = new DispatcherMovementInputSource(movement);
        source.Bind(dispatcher);
        dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, held: true);
        if (walkModifierHeld)
            dispatcher.TrySetAutomationActionHeld(InputAction.MovementWalkMode, held: true);

        MovementInput captured = source.Capture();

        Assert.Equal(expectedRun, captured.Run);
    }

    [Fact]
    public void Capture_AutoRunActive_ForcesRun_EvenWhenOptionIsOff()
    {
        var (dispatcher, _, _) = CreateDispatcher();
        var movement = new RuntimeLocalPlayerMovementState
        {
            RunAsDefaultMovementSource = () => false,
        };
        var source = new DispatcherMovementInputSource(movement);
        source.Bind(dispatcher);
        source.HandlePressedAction(InputAction.MovementRunLock); // arm autorun
        Assert.True(source.AutoRunActive);

        MovementInput captured = source.Capture();

        Assert.True(captured.Run);
    }

    [Fact]
    public void BindingIsIdempotentOnlyForTheSameDispatcher()
    {
        var source = CreateSource();
        var (first, _, _) = CreateDispatcher();
        var (second, _, _) = CreateDispatcher();

        source.Bind(first);
        source.Bind(first);

        Assert.Throws<InvalidOperationException>(() => source.Bind(second));
    }

    [Fact]
    public void UnbindRequiresExactDispatcherAndRestoresNeutralCapture()
    {
        var source = CreateSource();
        var (first, _, _) = CreateDispatcher();
        var (other, _, _) = CreateDispatcher();
        source.Bind(first);
        first.TrySetAutomationActionHeld(InputAction.MovementForward, held: true);

        source.Unbind(other);
        Assert.True(source.Capture().Forward);
        source.Unbind(first);

        Assert.False(source.IsAvailable);
        Assert.Equal(default, source.Capture());
    }

    private static (InputDispatcher Dispatcher, FakeKeyboard Keyboard, FakeMouse Mouse)
        CreateDispatcher()
    {
        var keyboard = new FakeKeyboard();
        var mouse = new FakeMouse();
        var dispatcher = InputDispatcher.CreateDetached(
            keyboard,
            mouse,
            new KeyBindings());
        dispatcher.Attach();
        return (dispatcher, keyboard, mouse);
    }

    private static DispatcherMovementInputSource CreateSource(
        IInputCaptureSource? capture = null) =>
        new(new RuntimeLocalPlayerMovementState(), capture);

    private sealed class FakeKeyboard : IKeyboardSource
    {
#pragma warning disable CS0067
        public event Action<Key, ModifierMask>? KeyDown;
        public event Action<Key, ModifierMask>? KeyUp;
#pragma warning restore CS0067
        public bool IsHeld(Key key) => false;
        public ModifierMask CurrentModifiers => ModifierMask.None;
    }

    private sealed class FakeMouse : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool WantCaptureKeyboard { get; set; }
        public bool WantCaptureMouse { get; set; }
        public bool IsHeld(MouseButton button) => false;
    }

    private sealed class FakeCapture : IInputCaptureSource
    {
        public bool WantCaptureMouse { get; set; }
        public bool WantCaptureKeyboard { get; set; }
        public bool DevToolsWantCaptureKeyboard { get; set; }
    }
}
