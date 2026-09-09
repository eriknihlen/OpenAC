using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Input;

public readonly record struct KeyChord(Key Key, ModifierMask Modifiers, byte Device = 0);
