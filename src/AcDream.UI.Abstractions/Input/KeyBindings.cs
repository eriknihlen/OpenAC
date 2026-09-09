using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Input;

public sealed class KeyBindings
{
    private const int CurrentSchemaVersion = 7;

    private readonly List<Binding> _bindings = new();

    /// <summary>All bindings in insertion order.</summary>
    public IReadOnlyList<Binding> All => _bindings;

    /// <summary>Append a binding. Duplicates are allowed; first match wins.</summary>
    public void Add(Binding b) => _bindings.Add(b);

    /// <summary>Remove the first occurrence of the given binding (structural equality).</summary>
    public bool Remove(Binding b) => _bindings.Remove(b);

    /// <summary>Drop every binding.</summary>
    public void Clear() => _bindings.Clear();

    public Binding? Find(KeyChord chord, ActivationType activation)
    {
        for (int i = 0; i < _bindings.Count; i++)
        {
            Binding binding = _bindings[i];
            if (binding.Chord == chord && binding.Activation == activation)
                return binding;
        }
        return null;
    }

    public Binding? Find(KeyChord chord, ActivationType activation, InputScope scope)
    {
        for (int i = 0; i < _bindings.Count; i++)
        {
            var b = _bindings[i];
            if (b.Chord == chord && b.Activation == activation && b.Scope == scope) return b;
        }
        return null;
    }

    public IEnumerable<Binding> ForAction(InputAction action)
    {
        for (int i = 0; i < _bindings.Count; i++)
            if (_bindings[i].Action == action) yield return _bindings[i];
    }

    public static KeyBindings AcdreamCurrentDefaults()
    {
        var b = new KeyBindings();

        b.Add(new(new KeyChord(Key.W, ModifierMask.None),  InputAction.MovementForward));
        b.Add(new(new KeyChord(Key.W, ModifierMask.Shift), InputAction.MovementForward));
        b.Add(new(new KeyChord(Key.S, ModifierMask.None),  InputAction.MovementBackup));
        b.Add(new(new KeyChord(Key.S, ModifierMask.Shift), InputAction.MovementBackup));
        b.Add(new(new KeyChord(Key.A, ModifierMask.None),  InputAction.MovementTurnLeft));
        b.Add(new(new KeyChord(Key.A, ModifierMask.Shift), InputAction.MovementTurnLeft));
        b.Add(new(new KeyChord(Key.D, ModifierMask.None),  InputAction.MovementTurnRight));
        b.Add(new(new KeyChord(Key.D, ModifierMask.Shift), InputAction.MovementTurnRight));
        b.Add(new(new KeyChord(Key.Z, ModifierMask.None),  InputAction.MovementStrafeLeft));
        b.Add(new(new KeyChord(Key.Z, ModifierMask.Shift), InputAction.MovementStrafeLeft));
        b.Add(new(new KeyChord(Key.X, ModifierMask.None),  InputAction.MovementStrafeRight));
        b.Add(new(new KeyChord(Key.X, ModifierMask.Shift), InputAction.MovementStrafeRight));
        b.Add(new(new KeyChord(Key.ShiftLeft, ModifierMask.Shift),  InputAction.MovementRunLock, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.ShiftRight, ModifierMask.Shift), InputAction.MovementRunLock, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.Space, ModifierMask.None),  InputAction.MovementJump));
        b.Add(new(new KeyChord(Key.Space, ModifierMask.Shift), InputAction.MovementJump));

        b.Add(new(new KeyChord(Key.ControlLeft,  ModifierMask.Ctrl), InputAction.AcdreamFlyDown, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.ControlRight, ModifierMask.Ctrl), InputAction.AcdreamFlyDown, ActivationType.Hold));

        b.Add(new(new KeyChord(Key.F1, ModifierMask.None), InputAction.AcdreamToggleDebugPanel));
        b.Add(new(new KeyChord(Key.F2, ModifierMask.None), InputAction.AcdreamToggleCollisionWires));
        b.Add(new(new KeyChord(Key.F3, ModifierMask.None), InputAction.AcdreamDumpNearby));
        b.Add(new(new KeyChord(Key.F7, ModifierMask.None), InputAction.AcdreamCycleTimeOfDay));
        b.Add(new(new KeyChord(Key.F8, ModifierMask.None), InputAction.AcdreamSensitivityDown));
        b.Add(new(new KeyChord(Key.F9, ModifierMask.None), InputAction.AcdreamSensitivityUp));
        b.Add(new(new KeyChord(Key.M,  ModifierMask.Ctrl), InputAction.AcdreamToggleAudioMute));
        b.Add(new(new KeyChord(Key.F10, ModifierMask.None), InputAction.AcdreamCycleWeather));
        b.Add(new(new KeyChord(Key.Tab, ModifierMask.None), InputAction.AcdreamTogglePlayerMode));
        b.Add(new(new KeyChord(Key.Escape, ModifierMask.None), InputAction.EscapeKey));

        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(Silk.NET.Input.MouseButton.Right), ModifierMask.None, Device: 1),
            InputAction.AcdreamRmbOrbitHold,
            ActivationType.Hold));

        return b;
    }

    public static KeyBindings RetailDefaults()
    {
        var b = new KeyBindings();

        // ── MovementCommands ───────────────────────────────────
        b.Add(new(new KeyChord(Key.W,         ModifierMask.None), InputAction.MovementForward));
        b.Add(new(new KeyChord(Key.Up,        ModifierMask.None), InputAction.MovementForward));
        b.Add(new(new KeyChord(Key.X,         ModifierMask.None), InputAction.MovementBackup));
        b.Add(new(new KeyChord(Key.Down,      ModifierMask.None), InputAction.MovementBackup));
        b.Add(new(new KeyChord(Key.A,         ModifierMask.None), InputAction.MovementTurnLeft));
        b.Add(new(new KeyChord(Key.Left,      ModifierMask.None), InputAction.MovementTurnLeft));
        b.Add(new(new KeyChord(Key.D,         ModifierMask.None), InputAction.MovementTurnRight));
        b.Add(new(new KeyChord(Key.Right,     ModifierMask.None), InputAction.MovementTurnRight));
        b.Add(new(new KeyChord(Key.Z,         ModifierMask.None), InputAction.MovementStrafeLeft));
        b.Add(new(new KeyChord(Key.A,         ModifierMask.Alt),  InputAction.MovementStrafeLeft));
        b.Add(new(new KeyChord(Key.Left,      ModifierMask.Alt),  InputAction.MovementStrafeLeft));
        b.Add(new(new KeyChord(Key.C,         ModifierMask.None), InputAction.MovementStrafeRight));
        b.Add(new(new KeyChord(Key.D,         ModifierMask.Alt),  InputAction.MovementStrafeRight));
        b.Add(new(new KeyChord(Key.Right,     ModifierMask.Alt),  InputAction.MovementStrafeRight));
        b.Add(new(new KeyChord(Key.ShiftLeft, ModifierMask.None), InputAction.MovementWalkMode, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.Q,         ModifierMask.None), InputAction.MovementRunLock));
        b.Add(new(new KeyChord(Key.S,         ModifierMask.None), InputAction.MovementStop));
        b.Add(new(new KeyChord(Key.Y,         ModifierMask.None), InputAction.Ready));
        b.Add(new(new KeyChord(Key.G,         ModifierMask.None), InputAction.Sitting));
        b.Add(new(new KeyChord(Key.H,         ModifierMask.None), InputAction.Crouch));
        b.Add(new(new KeyChord(Key.B,         ModifierMask.None), InputAction.Sleeping));
        b.Add(new(new KeyChord(Key.Space,     ModifierMask.None), InputAction.MovementJump));

        b.Add(new(new KeyChord(Key.F,            ModifierMask.None), InputAction.SelectionPlaceInInventory));
        b.Add(new(new KeyChord(Key.T,            ModifierMask.None), InputAction.SelectionSplitStack));
        b.Add(new(new KeyChord(Key.P,            ModifierMask.None), InputAction.SelectionPreviousSelection));
        b.Add(new(new KeyChord(Key.Backspace,    ModifierMask.None), InputAction.SelectionClosestCompassItem));
        b.Add(new(new KeyChord(Key.Minus,        ModifierMask.None), InputAction.SelectionPreviousCompassItem));
        b.Add(new(new KeyChord(Key.Equal,        ModifierMask.None), InputAction.SelectionNextCompassItem));
        b.Add(new(new KeyChord(Key.BackSlash,    ModifierMask.None), InputAction.SelectionClosestItem));
        b.Add(new(new KeyChord(Key.LeftBracket,  ModifierMask.None), InputAction.SelectionPreviousItem));
        b.Add(new(new KeyChord(Key.RightBracket, ModifierMask.None), InputAction.SelectionNextItem));
        b.Add(new(new KeyChord(Key.Apostrophe,   ModifierMask.None), InputAction.SelectionClosestMonster));
        b.Add(new(new KeyChord(Key.L,            ModifierMask.None), InputAction.SelectionPreviousMonster));
        // Silk.NET names this Semicolon (lowercase c), not SemiColon.
        b.Add(new(new KeyChord(Key.Semicolon,    ModifierMask.None), InputAction.SelectionNextMonster));
        b.Add(new(new KeyChord(Key.Home,         ModifierMask.None), InputAction.SelectionLastAttacker));
        b.Add(new(new KeyChord(Key.Slash,        ModifierMask.None), InputAction.SelectionClosestPlayer));
        b.Add(new(new KeyChord(Key.Comma,        ModifierMask.None), InputAction.SelectionPreviousPlayer));
        b.Add(new(new KeyChord(Key.Period,       ModifierMask.None), InputAction.SelectionNextPlayer));
        b.Add(new(new KeyChord(Key.N,            ModifierMask.None), InputAction.SelectionPreviousFellow));
        b.Add(new(new KeyChord(Key.M,            ModifierMask.None), InputAction.SelectionNextFellow));

        // ── UICommands ─────────────────────────────────────────
        b.Add(new(new KeyChord(Key.E,              ModifierMask.None),                       InputAction.SelectionExamine));
        b.Add(new(new KeyChord(Key.KeypadMultiply, ModifierMask.None),                       InputAction.CaptureScreenshot));
        b.Add(new(new KeyChord(Key.F1,             ModifierMask.None),                       InputAction.ToggleHelp));
        b.Add(new(new KeyChord(Key.F1,             ModifierMask.Shift | ModifierMask.Ctrl),  InputAction.TogglePluginManager));
        b.Add(new(new KeyChord(Key.F3,             ModifierMask.None),                       InputAction.ToggleAllegiancePanel));
        b.Add(new(new KeyChord(Key.F4,             ModifierMask.None),                       InputAction.ToggleFellowshipPanel));
        b.Add(new(new KeyChord(Key.F5,             ModifierMask.None),                       InputAction.ToggleSpellbookPanel));
        b.Add(new(new KeyChord(Key.F6,             ModifierMask.None),                       InputAction.ToggleSpellComponentsPanel));
        b.Add(new(new KeyChord(Key.F8,             ModifierMask.None),                       InputAction.ToggleAttributesPanel));
        b.Add(new(new KeyChord(Key.F9,             ModifierMask.None),                       InputAction.ToggleSkillsPanel));
        b.Add(new(new KeyChord(Key.F10,            ModifierMask.None),                       InputAction.ToggleWorldPanel));
        b.Add(new(new KeyChord(Key.F11,            ModifierMask.None),                       InputAction.ToggleOptionsPanel));
        b.Add(new(new KeyChord(Key.F12,            ModifierMask.None),                       InputAction.ToggleInventoryPanel));
        b.Add(new(new KeyChord(Key.Number1,        ModifierMask.Alt),                        InputAction.ToggleFloatingChatWindow1));
        b.Add(new(new KeyChord(Key.Number2,        ModifierMask.Alt),                        InputAction.ToggleFloatingChatWindow2));
        b.Add(new(new KeyChord(Key.Number3,        ModifierMask.Alt),                        InputAction.ToggleFloatingChatWindow3));
        b.Add(new(new KeyChord(Key.Number4,        ModifierMask.Alt),                        InputAction.ToggleFloatingChatWindow4));
        b.Add(new(new KeyChord(Key.R,              ModifierMask.None),                       InputAction.UseSelected));
        b.Add(new(new KeyChord(Key.Escape,         ModifierMask.None),                       InputAction.EscapeKey));
        b.Add(new(new KeyChord(Key.Escape,         ModifierMask.Shift),                      InputAction.LOGOUT));

        for (int i = 1; i <= 9; i++)
        {
            var k = (Key)((int)Key.Number0 + i); // Number1..Number9
            var useAction = (InputAction)((int)InputAction.UseQuickSlot_1 + i - 1);
            b.Add(new(new KeyChord(k, ModifierMask.None), useAction));
            b.Add(new(new KeyChord(k, ModifierMask.Ctrl), useAction));
        }
        // Alt+1..4 → slots 10..13; Alt+5..9 → slots 14..18.
        b.Add(new(new KeyChord(Key.Number1, ModifierMask.Alt), InputAction.UseQuickSlot_10));
        b.Add(new(new KeyChord(Key.Number2, ModifierMask.Alt), InputAction.UseQuickSlot_11));
        b.Add(new(new KeyChord(Key.Number3, ModifierMask.Alt), InputAction.UseQuickSlot_12));
        b.Add(new(new KeyChord(Key.Number4, ModifierMask.Alt), InputAction.UseQuickSlot_13));
        for (int i = 5; i <= 9; i++)
        {
            var k = (Key)((int)Key.Number0 + i);
            var action = (InputAction)((int)InputAction.UseQuickSlot_14 + i - 5);
            b.Add(new(new KeyChord(k, ModifierMask.Alt), action));
        }
        b.Add(new(new KeyChord(Key.Number0, ModifierMask.None), InputAction.CreateShortcut));
        b.Add(new(new KeyChord(Key.Number0, ModifierMask.Ctrl), InputAction.CreateShortcut));

        b.Add(new(new KeyChord(Key.Tab,   ModifierMask.None), InputAction.ToggleChatEntry));
        b.Add(new(new KeyChord(Key.Enter, ModifierMask.None), InputAction.EnterChatMode));

        b.Add(new(new KeyChord(Key.GraveAccent, ModifierMask.None), InputAction.CombatToggleCombat));
        // Melee mode (active when MeleeCombat scope pushed).
        b.Add(new(new KeyChord(Key.Insert,   ModifierMask.None), InputAction.CombatDecreaseAttackPower, Scope: InputScope.MeleeCombat));
        b.Add(new(new KeyChord(Key.PageUp,   ModifierMask.None), InputAction.CombatIncreaseAttackPower, Scope: InputScope.MeleeCombat));
        b.Add(new(new KeyChord(Key.Delete,   ModifierMask.None), InputAction.CombatLowAttack, ActivationType.Hold, InputScope.MeleeCombat));
        b.Add(new(new KeyChord(Key.End,      ModifierMask.None), InputAction.CombatMediumAttack, ActivationType.Hold, InputScope.MeleeCombat));
        b.Add(new(new KeyChord(Key.PageDown, ModifierMask.None), InputAction.CombatHighAttack, ActivationType.Hold, InputScope.MeleeCombat));
        b.Add(new(new KeyChord(Key.Insert,   ModifierMask.None), InputAction.CombatDecreaseMissileAccuracy, Scope: InputScope.MissileCombat));
        b.Add(new(new KeyChord(Key.PageUp,   ModifierMask.None), InputAction.CombatIncreaseMissileAccuracy, Scope: InputScope.MissileCombat));
        b.Add(new(new KeyChord(Key.Delete,   ModifierMask.None), InputAction.CombatAimLow, ActivationType.Hold, InputScope.MissileCombat));
        b.Add(new(new KeyChord(Key.End,      ModifierMask.None), InputAction.CombatAimMedium, ActivationType.Hold, InputScope.MissileCombat));
        b.Add(new(new KeyChord(Key.PageDown, ModifierMask.None), InputAction.CombatAimHigh, ActivationType.Hold, InputScope.MissileCombat));
        b.Add(new(new KeyChord(Key.Insert,   ModifierMask.None), InputAction.CombatPrevSpellTab, Scope: InputScope.MagicCombat));
        b.Add(new(new KeyChord(Key.PageUp,   ModifierMask.None), InputAction.CombatNextSpellTab, Scope: InputScope.MagicCombat));
        b.Add(new(new KeyChord(Key.Delete,   ModifierMask.None), InputAction.CombatPrevSpell, Scope: InputScope.MagicCombat));
        b.Add(new(new KeyChord(Key.End,      ModifierMask.None), InputAction.CombatCastCurrentSpell, Scope: InputScope.MagicCombat));
        b.Add(new(new KeyChord(Key.PageDown, ModifierMask.None), InputAction.CombatNextSpell, Scope: InputScope.MagicCombat));
        b.Add(new(new KeyChord(Key.Insert,   ModifierMask.Ctrl), InputAction.CombatFirstSpellTab, Scope: InputScope.MagicCombat));
        b.Add(new(new KeyChord(Key.PageUp,   ModifierMask.Ctrl), InputAction.CombatLastSpellTab, Scope: InputScope.MagicCombat));
        b.Add(new(new KeyChord(Key.Delete,   ModifierMask.Ctrl), InputAction.CombatFirstSpell, Scope: InputScope.MagicCombat));
        b.Add(new(new KeyChord(Key.PageDown, ModifierMask.Ctrl), InputAction.CombatLastSpell, Scope: InputScope.MagicCombat));
        for (int i = 1; i <= 9; i++)
        {
            var k = (Key)((int)Key.Number0 + i);
            var action = (InputAction)((int)InputAction.UseSpellSlot_1 + i - 1);
            b.Add(new(new KeyChord(k, ModifierMask.None), action, Scope: InputScope.MagicCombat));
        }

        // ── Emotes ──────────────────────────────────────────────
        b.Add(new(new KeyChord(Key.U, ModifierMask.None), InputAction.Cry));
        b.Add(new(new KeyChord(Key.I, ModifierMask.None), InputAction.Laugh));
        b.Add(new(new KeyChord(Key.J, ModifierMask.None), InputAction.Wave));
        b.Add(new(new KeyChord(Key.O, ModifierMask.None), InputAction.Cheer));
        b.Add(new(new KeyChord(Key.K, ModifierMask.None), InputAction.PointState));

        b.Add(new(new KeyChord(Key.KeypadDivide,   ModifierMask.None), InputAction.CameraActivateAlternateMode, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.F2,             ModifierMask.None), InputAction.CameraActivateAlternateMode, ActivationType.Hold));
        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Middle), ModifierMask.None, Device: 1),
            InputAction.CameraInstantMouseLook,
            ActivationType.Hold));
        b.Add(new(new KeyChord(Key.Keypad4,        ModifierMask.None), InputAction.CameraRotateLeft, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.Keypad6,        ModifierMask.None), InputAction.CameraRotateRight, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.Keypad8,        ModifierMask.None), InputAction.CameraRotateUp, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.Keypad2,        ModifierMask.None), InputAction.CameraRotateDown, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.KeypadSubtract, ModifierMask.None), InputAction.CameraMoveToward, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.KeypadAdd,      ModifierMask.None), InputAction.CameraMoveAway, ActivationType.Hold));
        b.Add(new(new KeyChord(Key.Keypad0,        ModifierMask.None), InputAction.CameraViewDefault));
        b.Add(new(new KeyChord(Key.KeypadDecimal,  ModifierMask.None), InputAction.CameraViewFirstPerson));
        b.Add(new(new KeyChord(Key.Keypad5,        ModifierMask.None), InputAction.CameraViewLookDown));
        b.Add(new(new KeyChord(Key.KeypadEnter,    ModifierMask.None), InputAction.CameraViewMapMode));
        b.Add(new(new KeyChord(Key.Left,  ModifierMask.None), InputAction.CameraAlternateRotateLeft, ActivationType.Hold, InputScope.Camera));
        b.Add(new(new KeyChord(Key.Right, ModifierMask.None), InputAction.CameraAlternateRotateRight, ActivationType.Hold, InputScope.Camera));
        b.Add(new(new KeyChord(Key.Up,    ModifierMask.None), InputAction.CameraAlternateRotateUp, ActivationType.Hold, InputScope.Camera));
        b.Add(new(new KeyChord(Key.Down,  ModifierMask.None), InputAction.CameraAlternateRotateDown, ActivationType.Hold, InputScope.Camera));

        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Left), ModifierMask.None, Device: 1),
            InputAction.SelectLeft));
        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Right), ModifierMask.None, Device: 1),
            InputAction.SelectRight,
            ActivationType.Click));
        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Middle), ModifierMask.None, Device: 1),
            InputAction.SelectMid));
        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Left), ModifierMask.None, Device: 1),
            InputAction.SelectDblLeft, ActivationType.DoubleClick));
        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Right), ModifierMask.None, Device: 1),
            InputAction.SelectDblRight, ActivationType.DoubleClick));
        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Middle), ModifierMask.None, Device: 1),
            InputAction.SelectDblMid, ActivationType.DoubleClick));

        // ── Scrollable ─────────────────────────────────────────
        // Mouse wheel → ScrollUp/Down handled by dispatcher's OnScroll path.
        b.Add(new(new KeyChord(Key.Up,   ModifierMask.Ctrl), InputAction.ScrollUp));
        b.Add(new(new KeyChord(Key.Down, ModifierMask.Ctrl), InputAction.ScrollDown));

        b.Add(new(new KeyChord(Key.F1,  ModifierMask.Ctrl), InputAction.AcdreamToggleDebugPanel));
        b.Add(new(new KeyChord(Key.F2,  ModifierMask.Ctrl), InputAction.AcdreamToggleCollisionWires));
        b.Add(new(new KeyChord(Key.F3,  ModifierMask.Ctrl), InputAction.AcdreamDumpNearby));
        b.Add(new(new KeyChord(Key.F7,  ModifierMask.Ctrl), InputAction.AcdreamCycleTimeOfDay));
        b.Add(new(new KeyChord(Key.F8,  ModifierMask.Ctrl), InputAction.AcdreamSensitivityDown));
        b.Add(new(new KeyChord(Key.F9,  ModifierMask.Ctrl), InputAction.AcdreamSensitivityUp));
        b.Add(new(new KeyChord(Key.F10, ModifierMask.Ctrl), InputAction.AcdreamCycleWeather));
        b.Add(new(new KeyChord(Key.M,   ModifierMask.Ctrl), InputAction.AcdreamToggleAudioMute));


        b.Add(new(
            new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Right), ModifierMask.None, Device: 1),
            InputAction.AcdreamRmbOrbitHold,
            ActivationType.Hold));

        return b;
    }

    public static KeyBindings LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return RetailDefaults();
        try
        {
            using var stream = File.OpenRead(path);
            var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            // Missing/older versions are valid and drive narrow migrations;
            // the field's presence remains non-fatal.
            int version = root.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : 0;

            var defaults = RetailDefaults();
            var loaded = new KeyBindings();
            var explicitlyStoredActions = new HashSet<InputAction>();

            if (root.TryGetProperty("actions", out var actionsEl)
                && actionsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var actionProp in actionsEl.EnumerateObject())
                {
                    if (!Enum.TryParse<InputAction>(actionProp.Name, out var action))
                        continue; // unknown action → skip
                    if (actionProp.Value.ValueKind != JsonValueKind.Array) continue;
                    explicitlyStoredActions.Add(action);
                    foreach (var bindingEl in actionProp.Value.EnumerateArray())
                    {
                        if (!bindingEl.TryGetProperty("key", out var keyEl)) continue;
                        var key = keyEl.GetString();
                        if (key is null) continue;
                        if (!Enum.TryParse<Key>(key, out var silkKey))
                            continue; // unknown key → skip
                        var mods = ParseModifiers(bindingEl);
                        var activation = ActivationType.Press;
                        if (bindingEl.TryGetProperty("activation", out var actEl)
                            && actEl.ValueKind == JsonValueKind.String
                            && Enum.TryParse<ActivationType>(actEl.GetString(), out var parsedAct))
                        {
                            activation = parsedAct;
                        }
                        byte device = 0;
                        if (bindingEl.TryGetProperty("device", out var dEl)
                            && dEl.ValueKind == JsonValueKind.Number)
                        {
                            device = (byte)dEl.GetInt32();
                        }
                        var chord = new KeyChord(silkKey, mods, device);
                        action = MigrateQuickSlotIntent(version, action, chord, activation);
                        explicitlyStoredActions.Add(action);
                        activation = MigrateCombatAttackActivation(version, action, activation);
                        activation = MigrateSelectRightActivation(version, action, activation);
                        InputScope scope = defaults.ForAction(action)
                            .Select(binding => binding.Scope)
                            .DefaultIfEmpty(InputScope.Game)
                            .First();
                        if (bindingEl.TryGetProperty("scope", out var scopeEl)
                            && scopeEl.ValueKind == JsonValueKind.String
                            && Enum.TryParse(scopeEl.GetString(), out InputScope parsedScope))
                        {
                            scope = parsedScope;
                        }
                        loaded.Add(new(chord, action, activation, scope));
                    }
                }
            }

            foreach (var actionInDefaults in Enum.GetValues<InputAction>())
            {
                if (!explicitlyStoredActions.Contains(actionInDefaults)
                    && defaults.ForAction(actionInDefaults).Any())
                {
                    foreach (var def in defaults.ForAction(actionInDefaults))
                        loaded.Add(def);
                }
            }

            return loaded;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"keybinds: failed to load {path}: {ex.Message} — using retail defaults");
            return RetailDefaults();
        }
    }

    /// <summary>
    /// Persist this binding set to <paramref name="path"/> as JSON.
    /// Format: <c>{ "version": N, "actions": { "ActionName": [ {key,mod,activation,device}... ] } }</c>.
    /// Sorted keys for deterministic diffs.
    /// </summary>
    public void SaveToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var actions = new SortedDictionary<string, List<object>>(StringComparer.Ordinal);
        foreach (InputAction action in RetailActionIdentityTable.Map.Values)
            actions.TryAdd(action.ToString(), new List<object>());
        foreach (var binding in _bindings)
        {
            if (!actions.TryGetValue(binding.Action.ToString(), out var list))
            {
                list = new List<object>();
                actions[binding.Action.ToString()] = list;
            }
            var entry = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["key"] = binding.Chord.Key.ToString(),
            };
            if (binding.Chord.Modifiers != ModifierMask.None)
                entry["mod"] = binding.Chord.Modifiers.ToString();
            if (binding.Chord.Device != 0)
                entry["device"] = (int)binding.Chord.Device;
            if (binding.Activation != ActivationType.Press)
                entry["activation"] = binding.Activation.ToString();
            if (binding.Scope != InputScope.Game)
                entry["scope"] = binding.Scope.ToString();
            list.Add(entry);
        }

        var root = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["version"] = CurrentSchemaVersion,
            ["actions"] = actions,
        };
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static InputAction MigrateQuickSlotIntent(
        int version,
        InputAction action,
        KeyChord chord,
        ActivationType activation)
    {
        if (version >= 6
            || activation != ActivationType.Press
            || chord.Device != 0
            || chord.Modifiers != ModifierMask.Ctrl)
            return action;

        int offset = (int)action - (int)InputAction.SelectQuickSlot_1;
        if ((uint)offset >= 9u)
            return action;

        var expectedKey = (Key)((int)Key.Number1 + offset);
        return chord.Key == expectedKey
            ? (InputAction)((int)InputAction.UseQuickSlot_1 + offset)
            : action;
    }

    private static ActivationType MigrateCombatAttackActivation(
        int version,
        InputAction action,
        ActivationType activation)
    {
        if (version >= 3 || activation != ActivationType.Press)
            return activation;
        return action is InputAction.CombatLowAttack
            or InputAction.CombatMediumAttack
            or InputAction.CombatHighAttack
                ? ActivationType.Hold
                : activation;
    }

    private static ActivationType MigrateSelectRightActivation(
        int version,
        InputAction action,
        ActivationType activation)
        => version < 5
            && action == InputAction.SelectRight
            && activation == ActivationType.Press
                ? ActivationType.Click
                : activation;

    private static ModifierMask ParseModifiers(JsonElement bindingEl)
    {
        if (!bindingEl.TryGetProperty("mod", out var modEl)) return ModifierMask.None;
        if (modEl.ValueKind != JsonValueKind.String) return ModifierMask.None;
        var modString = modEl.GetString();
        if (string.IsNullOrEmpty(modString)) return ModifierMask.None;
        var result = ModifierMask.None;
        foreach (var part in modString.Split(
                     new[] { '|', ',', ' ' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<ModifierMask>(part, ignoreCase: true, out var single))
                result |= single;
        }
        return result;
    }

}
