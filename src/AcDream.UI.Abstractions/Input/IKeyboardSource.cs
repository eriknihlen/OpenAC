using System;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Input;

public interface IKeyboardSource
{
    /// <summary>Fires on every transition from up → down for any key.</summary>
    event Action<Key, ModifierMask>? KeyDown;

    /// <summary>Fires on every transition from down → up for any key.</summary>
    event Action<Key, ModifierMask>? KeyUp;

    bool IsHeld(Key key);

    ModifierMask CurrentModifiers { get; }
}
