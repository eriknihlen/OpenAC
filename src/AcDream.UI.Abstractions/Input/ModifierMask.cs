using System;

namespace AcDream.UI.Abstractions.Input;

[Flags]
public enum ModifierMask : uint
{
    /// <summary>No modifier held — bare key.</summary>
    None  = 0,
    Shift = 0x01,
    Ctrl  = 0x02,
    Alt   = 0x04,
    Win   = 0x08,
}
