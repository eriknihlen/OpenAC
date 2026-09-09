using System;
using System.Collections.Generic;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class InputDispatcherDoubleClickTests
{
    /// <summary>
    /// Build a dispatcher wired with LMB Press → SelectLeft,
    /// LMB DoubleClick → SelectDblLeft, and RMB Press → SelectRight.
    /// </summary>
    private static (InputDispatcher dispatcher, FakeMouseSource mouse, ManualTickClock clock, List<(InputAction, ActivationType)> fired)
        Build()
    {
        var kb = new FakeKeyboardSource();
        var mouse = new FakeMouseSource();
        var bindings = new KeyBindings();

        var lmbChord = new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Left),  ModifierMask.None, Device: 1);
        var rmbChord = new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Right), ModifierMask.None, Device: 1);

        bindings.Add(new Binding(lmbChord, InputAction.SelectLeft));
        bindings.Add(new Binding(lmbChord, InputAction.SelectDblLeft, ActivationType.DoubleClick));
        bindings.Add(new Binding(rmbChord, InputAction.SelectRight));

        var clock = new ManualTickClock();
        var dispatcher = InputDispatcher.CreateDetached(kb, mouse, bindings, clock.Read);
        dispatcher.Attach();
        var fired = new List<(InputAction, ActivationType)>();
        dispatcher.Fired += (a, t) => fired.Add((a, t));
        return (dispatcher, mouse, clock, fired);
    }

    [Fact]
    public void SecondClick_WithinThreshold_FiresDoubleClick()
    {
        var (_, mouse, clock, fired) = Build();

        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);
        clock.Advance(10);
        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);

        Assert.Equal(2, fired.FindAll(e => e == (InputAction.SelectLeft, ActivationType.Press)).Count);

        Assert.Single(fired, e => e == (InputAction.SelectDblLeft, ActivationType.DoubleClick));
    }

    [Fact]
    public void SecondClick_BeyondThreshold_DoesNotFireDoubleClick()
    {
        var (_, mouse, clock, fired) = Build();

        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);
        clock.Advance(600);
        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);

        Assert.Equal(2, fired.FindAll(e => e == (InputAction.SelectLeft, ActivationType.Press)).Count);
        Assert.Empty(fired.FindAll(e => e.Item2 == ActivationType.DoubleClick));
    }

    /// <summary>
    /// LMB then RMB in rapid succession → no DoubleClick (different buttons).
    /// </summary>
    [Fact]
    public void DifferentButtons_DoNotFireDoubleClick()
    {
        var (_, mouse, clock, fired) = Build();

        mouse.EmitMouseDown(MouseButton.Left,  ModifierMask.None);
        clock.Advance(10);
        mouse.EmitMouseDown(MouseButton.Right, ModifierMask.None);

        Assert.Empty(fired.FindAll(e => e.Item2 == ActivationType.DoubleClick));
    }

    [Fact]
    public void ThirdClick_AfterDoubleClick_RequiresFreshPair()
    {
        var (_, mouse, clock, fired) = Build();

        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);
        clock.Advance(10);
        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);
        clock.Advance(10);
        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);

        // Three Press events total.
        Assert.Equal(3, fired.FindAll(e => e == (InputAction.SelectLeft, ActivationType.Press)).Count);

        Assert.Single(fired.FindAll(e => e == (InputAction.SelectDblLeft, ActivationType.DoubleClick)));
    }

    private sealed class ManualTickClock
    {
        private long _tickCount64;

        public long Read() => _tickCount64;

        public void Advance(long milliseconds) =>
            _tickCount64 = checked(_tickCount64 + milliseconds);
    }
}
