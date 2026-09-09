using System.IO;
using System.Linq;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class KeyBindingsJsonTests
{
    private static string TempFile(string suffix = ".json")
    {
        var f = Path.Combine(Path.GetTempPath(),
            $"acdream_kb_test_{System.Guid.NewGuid():N}{suffix}");
        return f;
    }

    [Fact]
    public void LoadOrDefault_missing_file_returns_RetailDefaults()
    {
        var path = TempFile();
        // No file written.
        var loaded = KeyBindings.LoadOrDefault(path);
        var defaults = KeyBindings.RetailDefaults();
        Assert.Equal(defaults.All.Count, loaded.All.Count);
    }

    [Fact]
    public void Roundtrip_preserves_every_binding()
    {
        var path = TempFile();
        try
        {
            var original = KeyBindings.RetailDefaults();
            original.SaveToFile(path);

            var loaded = KeyBindings.LoadOrDefault(path);

            Assert.Equal(original.All.Count, loaded.All.Count);
            foreach (var b in original.All)
            {
                Assert.Contains(loaded.All, x =>
                    x.Chord == b.Chord
                    && x.Action == b.Action
                    && x.Activation == b.Activation
                    && x.Scope == b.Scope);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_corrupt_file_returns_RetailDefaults()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ this is not valid JSON :: garbage");

            var loaded = KeyBindings.LoadOrDefault(path);
            var defaults = KeyBindings.RetailDefaults();
            Assert.Equal(defaults.All.Count, loaded.All.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_corrupt_file_does_NOT_overwrite_user_file()
    {
        var path = TempFile();
        try
        {
            const string corrupt = "{ this is not valid JSON :: garbage";
            File.WriteAllText(path, corrupt);

            _ = KeyBindings.LoadOrDefault(path);

            Assert.Equal(corrupt, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Custom_binding_for_one_action_keeps_defaults_for_others()
    {
        var path = TempFile();
        try
        {
            const string legacyJson = """
                {
                  "version": 6,
                  "actions": {
                    "MovementForward": [
                      { "key": "Q" }
                    ]
                  }
                }
                """;
            File.WriteAllText(path, legacyJson);

            var loaded = KeyBindings.LoadOrDefault(path);

            var fwd = loaded.ForAction(InputAction.MovementForward).ToList();
            Assert.Single(fwd);
            Assert.Equal(new KeyChord(Key.Q, ModifierMask.None), fwd[0].Chord);

            var back = loaded.ForAction(InputAction.MovementBackup).ToList();
            Assert.Contains(back, x => x.Chord == new KeyChord(Key.X, ModifierMask.None));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Roundtrip_preserves_explicitly_unbound_retail_action()
    {
        var path = TempFile();
        try
        {
            KeyBindings defaults = KeyBindings.RetailDefaults();
            var customized = new KeyBindings();
            foreach (Binding binding in defaults.All)
            {
                if (binding.Action != InputAction.ToggleHelp)
                    customized.Add(binding);
            }

            customized.SaveToFile(path);
            KeyBindings loaded = KeyBindings.LoadOrDefault(path);

            Assert.Empty(loaded.ForAction(InputAction.ToggleHelp));
            Assert.NotEmpty(loaded.ForAction(InputAction.ToggleOptionsPanel));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_handles_version_zero_legacy_file()
    {
        // A pretend "old schema" file with version=0 + just one action.
        // Should still parse — unknown fields are ignored, missing
        // actions get default-merged.
        var path = TempFile();
        try
        {
            const string legacyJson = """
                {
                  "version": 0,
                  "actions": {
                    "MovementForward": [
                      { "key": "W", "mod": "None" }
                    ]
                  }
                }
                """;
            File.WriteAllText(path, legacyJson);

            var loaded = KeyBindings.LoadOrDefault(path);

            var fwd = loaded.ForAction(InputAction.MovementForward).ToList();
            Assert.Single(fwd);

            var back = loaded.ForAction(InputAction.MovementBackup).ToList();
            Assert.NotEmpty(back);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_preserves_Hold_activation()
    {
        var path = TempFile();
        try
        {
            var original = new KeyBindings();
            original.Add(new(
                new KeyChord(Key.ShiftLeft, ModifierMask.None),
                InputAction.MovementWalkMode,
                ActivationType.Hold));
            original.SaveToFile(path);

            var loaded = KeyBindings.LoadOrDefault(path);
            var binds = loaded.ForAction(InputAction.MovementWalkMode).ToList();
            // The user binding is preserved with Hold activation.
            Assert.Contains(binds, x =>
                x.Chord.Key == Key.ShiftLeft
                && x.Activation == ActivationType.Hold);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_migratesV5CtrlNumberQuickSlotFromSelectBackToRetailUse()
    {
        var path = TempFile();
        try
        {
            const string json = """
                {
                  "version": 5,
                  "actions": {
                    "SelectQuickSlot_5": [
                      { "key": "Number5", "mod": "Ctrl" }
                    ]
                  }
                }
                """;
            File.WriteAllText(path, json);

            var loaded = KeyBindings.LoadOrDefault(path);

            Assert.Equal(InputAction.UseQuickSlot_5,
                loaded.Find(new KeyChord(Key.Number5, ModifierMask.Ctrl), ActivationType.Press)?.Action);
            Assert.Empty(loaded.ForAction(InputAction.SelectQuickSlot_5));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_migratesV2CombatPressBindingToHold()
    {
        var path = TempFile();
        try
        {
            const string json = """
                {
                  "version": 2,
                  "actions": {
                    "CombatHighAttack": [
                      { "key": "F7" }
                    ]
                  }
                }
                """;
            File.WriteAllText(path, json);

            var loaded = KeyBindings.LoadOrDefault(path);

            Assert.Equal(InputAction.CombatHighAttack,
                loaded.Find(
                    new KeyChord(Key.F7, ModifierMask.None),
                    ActivationType.Hold)?.Action);
            Assert.Null(loaded.Find(
                new KeyChord(Key.F7, ModifierMask.None),
                ActivationType.Press));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_migratesV4SelectRightPressBindingToClick()
    {
        var path = TempFile();
        try
        {
            const string json = """
                {
                  "version": 4,
                  "actions": {
                    "SelectRight": [
                      { "key": "-1002", "device": 1 }
                    ]
                  }
                }
                """;
            File.WriteAllText(path, json);

            var loaded = KeyBindings.LoadOrDefault(path);
            Binding binding = Assert.Single(
                loaded.ForAction(InputAction.SelectRight));

            Assert.Equal((Key)(-1002), binding.Chord.Key);
            Assert.Equal(ActivationType.Click, binding.Activation);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_unknown_action_name_skipped_silently()
    {
        var path = TempFile();
        try
        {
            const string json = """
                {
                  "version": 1,
                  "actions": {
                    "NotARealActionName": [
                      { "key": "W", "mod": "None" }
                    ],
                    "MovementForward": [
                      { "key": "Q", "mod": "None" }
                    ]
                  }
                }
                """;
            File.WriteAllText(path, json);

            var loaded = KeyBindings.LoadOrDefault(path);
            // The known action loaded; unknown action skipped without
            // throwing.
            var fwd = loaded.ForAction(InputAction.MovementForward).ToList();
            Assert.Contains(fwd, x => x.Chord.Key == Key.Q);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

}
