using System;
using System.Collections.Generic;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

internal sealed class FakeKeyboardSource : IKeyboardSource
{
    public event Action<Key, ModifierMask>? KeyDown;
    public event Action<Key, ModifierMask>? KeyUp;

    private readonly HashSet<Key> _held = new();

    public ModifierMask CurrentModifiers { get; set; } = ModifierMask.None;

    public bool IsHeld(Key key) => _held.Contains(key);

    public void EmitKeyDown(Key key, ModifierMask mods)
    {
        CurrentModifiers = mods;
        _held.Add(key);
        KeyDown?.Invoke(key, mods);
    }

    public void EmitKeyUp(Key key, ModifierMask mods)
    {
        CurrentModifiers = mods;
        _held.Remove(key);
        KeyUp?.Invoke(key, mods);
    }
}
