using System;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Input;

public interface IMouseSource
{
    /// <summary>Fires on every transition from up → down for any button.</summary>
    event Action<MouseButton, ModifierMask>? MouseDown;

    /// <summary>Fires on every transition from down → up for any button.</summary>
    event Action<MouseButton, ModifierMask>? MouseUp;

    event Action<float, float>? MouseMove;

    /// <summary>Fires on every wheel-tick. Positive = up, negative = down.</summary>
    event Action<float>? Scroll;

    bool IsHeld(MouseButton button);

    bool WantCaptureMouse { get; }

    bool WantCaptureKeyboard { get; }
}
