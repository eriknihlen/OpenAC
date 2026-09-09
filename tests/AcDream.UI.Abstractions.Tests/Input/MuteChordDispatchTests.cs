using System.Collections.Generic;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public sealed class MuteChordDispatchTests
{
    private static (InputDispatcher dispatcher, FakeKeyboardSource kb, FakeMouseSource mouse, List<(InputAction Action, ActivationType Activation)> fired)
        BuildWithRetailDefaults()
    {
        var kb = new FakeKeyboardSource();
        var mouse = new FakeMouseSource(); // WantCaptureKeyboard defaults to false — no widget focused.
        KeyBindings bindings = KeyBindings.RetailDefaults();
        InputDispatcher dispatcher = InputDispatcher.CreateDetached(kb, mouse, bindings);
        dispatcher.Attach();
                              // production default; nothing in src/AcDream.App ever pushes
                              // another scope on top of it.
        var fired = new List<(InputAction, ActivationType)>();
        dispatcher.Fired += (a, t) => fired.Add((a, t));
        return (dispatcher, kb, mouse, fired);
    }

    [Fact]
    public void CtrlM_WithNoWidgetFocused_FiresAcdreamToggleAudioMute()
    {
        (_, FakeKeyboardSource kb, _, var fired) = BuildWithRetailDefaults();

        kb.EmitKeyDown(Key.M, ModifierMask.Ctrl);

        Assert.Contains((InputAction.AcdreamToggleAudioMute, ActivationType.Press), fired);
    }

    [Fact]
    public void CtrlM_WhileAnyWidgetHoldsKeyboardFocus_IsSuppressed()
    {
        (_, FakeKeyboardSource kb, FakeMouseSource mouse, var fired) = BuildWithRetailDefaults();
        mouse.WantCaptureKeyboard = true;

        kb.EmitKeyDown(Key.M, ModifierMask.Ctrl);

        Assert.DoesNotContain((InputAction.AcdreamToggleAudioMute, ActivationType.Press), fired);
    }
}
