using System;
using System.Collections.Generic;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Input;

public sealed class InputDispatcher : IDisposable
{
    private readonly IKeyboardSource _keyboard;
    private readonly IMouseSource _mouse;
    private readonly Func<long> _getTickCount64;
    private KeyBindings _bindings;
    private readonly Stack<InputScope> _scopes = new();
    private InputScope? _combatScope;
    private bool _cameraAlternateScope;
    private readonly HashSet<KeyChord> _heldHoldChords = new();
    private readonly HashSet<InputAction> _automationHeldActions = new();
    private readonly Dictionary<MouseButton, float> _mouseClickTravel = new();
    private readonly bool[] _sourceAttached = new bool[6];
    private bool _attachStarted;
    private int _disposeRequested;
    private int _active;

    private MouseButton? _lastMouseDownButton;
    private long _lastMouseDownTickMs;
    private const long DoubleClickThresholdMs = 500;
    private const float ClickDragThresholdPixels = 3f;

    private Action<KeyChord>? _captureCallback;
    private Key? _captureModifierCandidate;
    private KeyChord? _currentPhysicalChord;

    public event Action<InputAction, ActivationType>? Fired;

    public KeyChord? CurrentPhysicalChord => _currentPhysicalChord;

    private InputDispatcher(
        IKeyboardSource keyboard,
        IMouseSource mouse,
        KeyBindings bindings,
        Func<long> getTickCount64)
    {
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _getTickCount64 = getTickCount64
            ?? throw new ArgumentNullException(nameof(getTickCount64));

        _scopes.Push(InputScope.Always); // bottom of the stack
        _scopes.Push(InputScope.Game);   // default top for normal play

    }

    public static InputDispatcher CreateDetached(
        IKeyboardSource keyboard,
        IMouseSource mouse,
        KeyBindings bindings) =>
        new(keyboard, mouse, bindings, static () => Environment.TickCount64);

    internal static InputDispatcher CreateDetached(
        IKeyboardSource keyboard,
        IMouseSource mouse,
        KeyBindings bindings,
        Func<long> getTickCount64) =>
        new(keyboard, mouse, bindings, getTickCount64);

    public bool IsDisposalComplete =>
        _sourceAttached.All(static attached => !attached);

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        if (_attachStarted)
            throw new InvalidOperationException(
                "Input dispatcher attachment has already started.");
        _attachStarted = true;

        try
        {
            _sourceAttached[0] = true;
            _keyboard.KeyDown += OnKeyDown;
            _sourceAttached[1] = true;
            _keyboard.KeyUp += OnKeyUp;
            _sourceAttached[2] = true;
            _mouse.MouseDown += OnMouseDown;
            _sourceAttached[3] = true;
            _mouse.MouseUp += OnMouseUp;
            _sourceAttached[4] = true;
            _mouse.MouseMove += OnMouseMove;
            _sourceAttached[5] = true;
            _mouse.Scroll += OnScroll;
            Volatile.Write(ref _active, 1);
        }
        catch (Exception attachError)
        {
            Deactivate();
            List<Exception> rollbackErrors = DetachSources();
            if (rollbackErrors.Count != 0)
            {
                rollbackErrors.Insert(0, new InvalidOperationException(
                    "Input dispatcher source registration failed.",
                    attachError));
                throw new AggregateException(
                    "Input dispatcher registration and rollback both failed.",
                    rollbackErrors);
            }

            throw new InvalidOperationException(
                "Input dispatcher registration failed and was rolled back.",
                attachError);
        }
    }

    public void Deactivate()
    {
        Interlocked.Exchange(ref _active, 0);
        _captureCallback = null;
        _captureModifierCandidate = null;
        _heldHoldChords.Clear();
        _automationHeldActions.Clear();
        _mouseClickTravel.Clear();
        _cameraAlternateScope = false;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        Deactivate();
        List<Exception> failures = DetachSources();
        if (failures.Count != 0)
            throw new AggregateException(
                "One or more input dispatcher source callbacks could not be detached.",
                failures);
    }

    public bool TryInvokeAutomationAction(InputAction action)
    {
        if (action == InputAction.None || !Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action));
        if (Volatile.Read(ref _active) == 0
            || _captureCallback is not null
            || _mouse.WantCaptureKeyboard)
            return false;

        Fired?.Invoke(action, ActivationType.Press);
        return true;
    }

    public bool TrySetAutomationActionHeld(InputAction action, bool held)
    {
        if (action == InputAction.None || !Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action));

        if (Volatile.Read(ref _active) == 0)
            return false;

        if (held)
        {
            if (_captureCallback is not null || _mouse.WantCaptureKeyboard)
                return false;
            if (_automationHeldActions.Add(action))
                Fired?.Invoke(action, ActivationType.Press);
            return true;
        }

        if (_automationHeldActions.Remove(action))
            Fired?.Invoke(action, ActivationType.Release);
        return true;
    }

    /// <summary>Topmost scope on the stack — what the dispatcher looks up first.</summary>
    public InputScope ActiveScope => _cameraAlternateScope
        ? InputScope.Camera
        : _scopes.Peek() == InputScope.Game && _combatScope is { } combat
            ? combat
            : _scopes.Peek();

    public void SetCameraAlternateScope(bool active)
    {
        if (_cameraAlternateScope == active) return;
        ReleaseHeldHoldBindings();
        _cameraAlternateScope = active;
    }

    public void SetCombatScope(InputScope? scope)
    {
        if (scope is not null && scope is not (
            InputScope.MeleeCombat or InputScope.MissileCombat or InputScope.MagicCombat))
            throw new ArgumentOutOfRangeException(nameof(scope));
        if (_combatScope == scope) return;
        ReleaseHeldHoldBindings();
        _combatScope = scope;
    }

    private Binding? FindActive(KeyChord chord, ActivationType activation)
    {
        IReadOnlyList<Binding> bindings = FindActiveBindings(chord, activation);
        return bindings.Count == 0 ? null : bindings[0];
    }

    private IReadOnlyList<Binding> FindActiveBindings(
        KeyChord chord,
        ActivationType activation)
    {
        if (_cameraAlternateScope)
        {
            Binding[] camera = FindInScope(InputScope.Camera, chord, activation);
            if (camera.Length != 0)
                return camera;
        }

        foreach (InputScope scope in _scopes)
        {
            if (scope == InputScope.Game && _combatScope is { } combat)
            {
                Binding[] combatBindings = FindInScope(combat, chord, activation);
                if (combatBindings.Length != 0)
                    return combatBindings;
            }

            Binding[] bindings = FindInScope(scope, chord, activation);
            if (bindings.Length != 0)
                return bindings;
        }
        return Array.Empty<Binding>();
    }

    private Binding[] FindInScope(
        InputScope scope,
        KeyChord chord,
        ActivationType activation) =>
        _bindings.All
            .Where(binding =>
                binding.Scope == scope
                && binding.Chord == chord
                && binding.Activation == activation)
            .DistinctBy(static binding => binding.Action)
            .ToArray();

    public bool IsCapturing => _captureCallback is not null;

    public void BeginCapture(Action<KeyChord> onCaptured)
    {
        _captureCallback = onCaptured ?? throw new ArgumentNullException(nameof(onCaptured));
        _captureModifierCandidate = null;
    }

    public void CancelCapture()
    {
        var cb = _captureCallback;
        if (cb is null) return;
        _captureCallback = null;
        _captureModifierCandidate = null;
        cb(default);
    }

    public void SetBindings(KeyBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ReleaseHeldHoldBindings();
        _bindings = bindings;
    }

    public KeyBindings Bindings => _bindings;

    public bool IsActionHeld(InputAction action)
    {
        if (Volatile.Read(ref _active) == 0 || action == InputAction.None) return false;
        if (_mouse.WantCaptureKeyboard) return false;
        if (_automationHeldActions.Contains(action)) return true;
        foreach (var b in _bindings.ForAction(action))
        {
            if (!IsChordHeld(b.Chord)) continue;
            Binding? active = FindActiveHeld(b.Chord, b.Activation);
            if (active?.Action == action) return true;
        }
        return false;
    }

    private Binding? FindActiveHeld(KeyChord candidate, ActivationType activation)
    {
        var actual = candidate with { Modifiers = _keyboard.CurrentModifiers };
        Binding? active = FindActive(actual, activation);
        if (active is not null) return active;
        return candidate.Modifiers == ModifierMask.None
            && _keyboard.CurrentModifiers == ModifierMask.Shift
                ? FindActive(candidate, activation)
                : null;
    }

    private bool IsChordHeld(KeyChord chord)
    {
        if (chord.Device == 0)
        {
            if (!_keyboard.IsHeld(chord.Key)) return false;
        }
        else if (chord.Device == 1)
        {
            var btn = KeyToMouseButton(chord.Key);
            if (btn is null || !_mouse.IsHeld(btn.Value)) return false;
        }
        else
        {
            // Unknown device — never held.
            return false;
        }
        var current = _keyboard.CurrentModifiers;
        if (chord.Modifiers == ModifierMask.None)
        {
            current &= ~ModifierMask.Shift;
        }
        return current == chord.Modifiers;
    }

    private static MouseButton? KeyToMouseButton(Key key) => (int)key switch
    {
        -1001 => MouseButton.Left,
        -1002 => MouseButton.Right,
        -1003 => MouseButton.Middle,
        -1004 => MouseButton.Button4,
        -1005 => MouseButton.Button5,
        _ => null,
    };

    /// <summary>Push a scope onto the active stack. Top wins.</summary>
    public void PushScope(InputScope scope)
    {
        ReleaseHeldHoldBindings();
        _scopes.Push(scope);
    }

    public void PopScope(InputScope expected)
    {
        if (_scopes.Peek() != expected)
            throw new InvalidOperationException(
                $"PopScope expected {expected} but top is {_scopes.Peek()}");
        ReleaseHeldHoldBindings();
        _scopes.Pop();
    }

    private void ReleaseHeldHoldBindings()
    {
        if (_heldHoldChords.Count == 0) return;
        var releases = new List<Binding>(_heldHoldChords.Count);
        foreach (KeyChord chord in _heldHoldChords)
            releases.AddRange(FindActiveBindings(chord, ActivationType.Hold));
        _heldHoldChords.Clear();
        foreach (Binding binding in releases)
            Fired?.Invoke(binding.Action, ActivationType.Release);
    }

    public void Tick()
    {
        if (Volatile.Read(ref _active) == 0 || _mouse.WantCaptureKeyboard) return;

        // Snapshot to avoid issues if a subscriber mutates _heldHoldChords.
        if (_heldHoldChords.Count == 0) return;
        var snapshot = new KeyChord[_heldHoldChords.Count];
        _heldHoldChords.CopyTo(snapshot);
        for (int i = 0; i < snapshot.Length; i++)
        {
            var chord = snapshot[i];
            if (!_heldHoldChords.Contains(chord))
                continue;
            foreach (Binding hold in FindActiveBindings(chord, ActivationType.Hold))
                Fired?.Invoke(hold.Action, ActivationType.Hold);
        }
    }

    private void OnKeyDown(Key key, ModifierMask mods)
    {
        if (Volatile.Read(ref _active) == 0) return;
        if (_captureCallback is not null)
        {
            if (key == Key.Escape)
            {
                var cb = _captureCallback;
                _captureCallback = null;
                _captureModifierCandidate = null;
                cb(default);
                return;
            }
            if (IsModifierKey(key))
            {
                _captureModifierCandidate = key;
                return;
            }

            var captured = new KeyChord(key, mods, Device: 0);
            var cb2 = _captureCallback;
            _captureCallback = null;
            _captureModifierCandidate = null;
            cb2(captured);
            return; // SUPPRESS the action — don't run binding lookup below
        }

        if (_mouse.WantCaptureKeyboard) return;
        var chord = KeyboardChord(key, mods);
        _currentPhysicalChord = chord;
        try
        {
            foreach (Binding press in FindActiveBindings(chord, ActivationType.Press))
                Fired?.Invoke(press.Action, ActivationType.Press);

            foreach (Binding click in FindActiveBindings(chord, ActivationType.Click))
                Fired?.Invoke(click.Action, ActivationType.Click);

            IReadOnlyList<Binding> holds = FindActiveBindings(chord, ActivationType.Hold);
            if (holds.Count != 0)
            {
                foreach (Binding hold in holds)
                    Fired?.Invoke(hold.Action, ActivationType.Press);
                _heldHoldChords.Add(chord);
            }
        }
        finally
        {
            _currentPhysicalChord = null;
        }
    }

    private static bool IsModifierKey(Key key) => key switch
    {
        Key.ShiftLeft or Key.ShiftRight       => true,
        Key.ControlLeft or Key.ControlRight   => true,
        Key.AltLeft or Key.AltRight           => true,
        Key.SuperLeft or Key.SuperRight       => true,
        _                                     => false,
    };

    private void OnKeyUp(Key key, ModifierMask mods)
    {
        if (Volatile.Read(ref _active) == 0) return;
        if (_captureCallback is not null)
        {
            if (_captureModifierCandidate == key)
            {
                Action<KeyChord> callback = _captureCallback;
                _captureCallback = null;
                _captureModifierCandidate = null;
                callback(KeyboardChord(key, mods));
            }
            return;
        }
        var chord = KeyboardChord(key, mods);

        foreach (Binding release in FindActiveBindings(chord, ActivationType.Release))
            Fired?.Invoke(release.Action, ActivationType.Release);

        var toRemove = new List<KeyChord>();
        foreach (var held in _heldHoldChords)
        {
            if (held.Key == key && held.Device == 0)
                toRemove.Add(held);
        }
        foreach (var held in toRemove)
        {
            _heldHoldChords.Remove(held);
            foreach (Binding hold in FindActiveBindings(held, ActivationType.Hold))
                Fired?.Invoke(hold.Action, ActivationType.Release);
        }
    }

    private void OnMouseDown(MouseButton button, ModifierMask mods)
    {
        if (Volatile.Read(ref _active) == 0) return;
        _mouseClickTravel.Remove(button);
        if (_captureCallback is not null)
        {
            var captured = new KeyChord(
                MouseButtonToKey(button),
                mods,
                Device: 1);
            Action<KeyChord> callback = _captureCallback;
            _captureCallback = null;
            _captureModifierCandidate = null;
            callback(captured);
            return;
        }
        if (_mouse.WantCaptureMouse) return;
        var chord = new KeyChord(MouseButtonToKey(button), mods, Device: 1);

        foreach (Binding press in FindActiveBindings(chord, ActivationType.Press))
            Fired?.Invoke(press.Action, ActivationType.Press);

        IReadOnlyList<Binding> holds = FindActiveBindings(chord, ActivationType.Hold);
        if (holds.Count != 0)
        {
            foreach (Binding hold in holds)
                Fired?.Invoke(hold.Action, ActivationType.Press);
            _heldHoldChords.Add(chord);
        }

        if (FindActive(chord, ActivationType.Click) is not null)
            _mouseClickTravel[button] = 0f;

        long nowMs = _getTickCount64();
        if (_lastMouseDownButton == button
            && nowMs - _lastMouseDownTickMs <= DoubleClickThresholdMs)
        {
            foreach (Binding dbl in FindActiveBindings(chord, ActivationType.DoubleClick))
                Fired?.Invoke(dbl.Action, ActivationType.DoubleClick);
            _lastMouseDownButton = null;
        }
        else
        {
            _lastMouseDownButton = button;
            _lastMouseDownTickMs = nowMs;
        }
    }

    private void OnMouseUp(MouseButton button, ModifierMask mods)
    {
        if (Volatile.Read(ref _active) == 0) return;
        var chord = new KeyChord(MouseButtonToKey(button), mods, Device: 1);
        bool wasClickCandidate = _mouseClickTravel.Remove(button, out float travel);

        foreach (Binding release in FindActiveBindings(chord, ActivationType.Release))
            Fired?.Invoke(release.Action, ActivationType.Release);

        var keyForLookup = MouseButtonToKey(button);
        var toRemove = new List<KeyChord>();
        foreach (var held in _heldHoldChords)
        {
            if (held.Key == keyForLookup && held.Device == 1)
                toRemove.Add(held);
        }
        foreach (var held in toRemove)
        {
            _heldHoldChords.Remove(held);
            foreach (Binding hold in FindActiveBindings(held, ActivationType.Hold))
                Fired?.Invoke(hold.Action, ActivationType.Release);
        }

        if (wasClickCandidate
            && !_mouse.WantCaptureMouse
            && travel <= ClickDragThresholdPixels
            && FindActiveBindings(chord, ActivationType.Click) is { Count: > 0 } clicks)
        {
            foreach (Binding click in clicks)
                Fired?.Invoke(click.Action, ActivationType.Click);
        }
    }

    private void OnMouseMove(float dx, float dy)
    {
        if (Volatile.Read(ref _active) == 0 || _mouseClickTravel.Count == 0)
            return;

        if (_mouse.WantCaptureMouse)
        {
            _mouseClickTravel.Clear();
            return;
        }

        float distance = MathF.Sqrt((dx * dx) + (dy * dy));
        foreach (MouseButton button in _mouseClickTravel.Keys.ToArray())
        {
            if (_mouse.IsHeld(button))
                _mouseClickTravel[button] += distance;
            else
                _mouseClickTravel.Remove(button);
        }
    }

    private void OnScroll(float delta)
    {
        if (Volatile.Read(ref _active) == 0) return;
        if (_mouse.WantCaptureMouse) return;
        // K.1b: wheel ticks emit ScrollUp / ScrollDown depending on the
        // sign of the delta. Magnitude is dropped — the action is a
        // discrete press transition; subscribers apply a fixed-size step.
        // (Plan-agent: rebindable in K.1c when KeyChord+wheel-axis support
        // lands.)
        if (delta > 0f) Fired?.Invoke(InputAction.ScrollUp, ActivationType.Press);
        else if (delta < 0f) Fired?.Invoke(InputAction.ScrollDown, ActivationType.Press);
    }

    public static Key MouseButtonToKey(MouseButton button) => button switch
    {
        MouseButton.Left   => (Key)(-1001),
        MouseButton.Right  => (Key)(-1002),
        MouseButton.Middle => (Key)(-1003),
        MouseButton.Button4 => (Key)(-1004),
        MouseButton.Button5 => (Key)(-1005),
        _ => (Key)(-1000 - (int)button),
    };

    private static KeyChord KeyboardChord(Key key, ModifierMask modifiers)
    {
        modifiers &= key switch
        {
            Key.ShiftLeft or Key.ShiftRight => ~ModifierMask.Shift,
            Key.ControlLeft or Key.ControlRight => ~ModifierMask.Ctrl,
            Key.AltLeft or Key.AltRight => ~ModifierMask.Alt,
            Key.SuperLeft or Key.SuperRight => ~ModifierMask.Win,
            _ => ~ModifierMask.None,
        };
        return new KeyChord(key, modifiers, Device: 0);
    }

    private List<Exception> DetachSources()
    {
        var failures = new List<Exception>();
        for (int index = _sourceAttached.Length - 1; index >= 0; index--)
        {
            if (!_sourceAttached[index])
                continue;
            try
            {
                switch (index)
                {
                    case 0: _keyboard.KeyDown -= OnKeyDown; break;
                    case 1: _keyboard.KeyUp -= OnKeyUp; break;
                    case 2: _mouse.MouseDown -= OnMouseDown; break;
                    case 3: _mouse.MouseUp -= OnMouseUp; break;
                    case 4: _mouse.MouseMove -= OnMouseMove; break;
                    case 5: _mouse.Scroll -= OnScroll; break;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
                _sourceAttached[index] = false;
            }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException(
                    $"Input dispatcher source callback {index} could not be detached.",
                    error));
            }
        }
        return failures;
    }
}
