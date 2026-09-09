using System.Collections.Generic;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public sealed class InputDispatcherClickTests
{
    [Fact]
    public void MouseClickFiresOnReleaseAfterStationaryPress()
    {
        var (dispatcher, mouse, fired) = BuildMouse();

        mouse.EmitMouseDown(MouseButton.Right, ModifierMask.None);
        Assert.Empty(fired);

        mouse.EmitMouseUp(MouseButton.Right, ModifierMask.None);

        Assert.Equal(
            [(InputAction.SelectRight, ActivationType.Click)],
            fired);
        dispatcher.Dispose();
    }

    [Fact]
    public void MouseClickDoesNotFireAfterCrossingRetailDragThreshold()
    {
        var (dispatcher, mouse, fired) = BuildMouse();

        mouse.EmitMouseDown(MouseButton.Right, ModifierMask.None);
        mouse.EmitMouseMove(4f, 0f);
        mouse.EmitMouseUp(MouseButton.Right, ModifierMask.None);

        Assert.Empty(fired);
        dispatcher.Dispose();
    }

    [Fact]
    public void MouseClickAllowsMovementAtRetailDragThreshold()
    {
        var (dispatcher, mouse, fired) = BuildMouse();

        mouse.EmitMouseDown(MouseButton.Right, ModifierMask.None);
        mouse.EmitMouseMove(3f, 0f);
        mouse.EmitMouseUp(MouseButton.Right, ModifierMask.None);

        Assert.Single(fired);
        dispatcher.Dispose();
    }

    [Fact]
    public void CapturedMouseNeverStartsWorldClick()
    {
        var (dispatcher, mouse, fired) = BuildMouse();
        mouse.WantCaptureMouse = true;

        mouse.EmitMouseDown(MouseButton.Right, ModifierMask.None);
        mouse.WantCaptureMouse = false;
        mouse.EmitMouseUp(MouseButton.Right, ModifierMask.None);

        Assert.Empty(fired);
        dispatcher.Dispose();
    }

    [Fact]
    public void KeyboardRebindOfClickActionFiresOnKeyDown()
    {
        var keyboard = new FakeKeyboardSource();
        var mouse = new FakeMouseSource();
        var bindings = new KeyBindings();
        bindings.Add(new Binding(
            new KeyChord(Key.Q, ModifierMask.None),
            InputAction.SelectRight,
            ActivationType.Click));
        var dispatcher = InputDispatcher.CreateDetached(keyboard, mouse, bindings);
        var fired = new List<(InputAction, ActivationType)>();
        dispatcher.Fired += (action, activation) => fired.Add((action, activation));
        dispatcher.Attach();

        keyboard.EmitKeyDown(Key.Q, ModifierMask.None);

        Assert.Equal(
            [(InputAction.SelectRight, ActivationType.Click)],
            fired);
        dispatcher.Dispose();
    }

    private static (
        InputDispatcher Dispatcher,
        FakeMouseSource Mouse,
        List<(InputAction, ActivationType)> Fired) BuildMouse()
    {
        var keyboard = new FakeKeyboardSource();
        var mouse = new FakeMouseSource();
        var bindings = new KeyBindings();
        bindings.Add(new Binding(
            new KeyChord(
                InputDispatcher.MouseButtonToKey(MouseButton.Right),
                ModifierMask.None,
                Device: 1),
            InputAction.SelectRight,
            ActivationType.Click));
        var dispatcher = InputDispatcher.CreateDetached(keyboard, mouse, bindings);
        var fired = new List<(InputAction, ActivationType)>();
        dispatcher.Fired += (action, activation) => fired.Add((action, activation));
        dispatcher.Attach();
        return (dispatcher, mouse, fired);
    }
}
