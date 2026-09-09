using AcDream.App.Input;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

public sealed class RetailKeymapFileTests
{
    [Fact]
    public void WriteThenParse_RoundTripsAll306RetailActionIdentities()
    {
        var source = new KeyBindings();
        foreach (((uint inputMapId, uint actionId), InputAction action) in
            RetailActionIdentityTable.Map)
        {
            source.Add(new Binding(
                new KeyChord(
                    Key.SuperRight,
                    ModifierMask.Shift | ModifierMask.Ctrl | ModifierMask.Win),
                action,
                RetailActionIdentityTable.ActivationFor(inputMapId, actionId),
                RetailActionIdentityTable.ScopeForInputMap(inputMapId)));
        }

        string text = RetailKeymapFile.Write(source);
        KeyBindings loaded = RetailKeymapFile.Parse(text, new KeyBindings());

        Assert.Equal(306, loaded.All.Count);
        foreach (Binding expected in source.All)
            Assert.Contains(expected, loaded.All);
        Assert.Contains("CharacterOptionCommands", text, StringComparison.Ordinal);
        Assert.Contains("AutoRepeatAttacks", text, StringComparison.Ordinal);
        Assert.Contains("AFKState", text, StringComparison.Ordinal);
        Assert.Contains("DIK_RWIN", text, StringComparison.Ordinal);
        Assert.Contains("EscapeKey [ \"\" [ 0 DIK_ESCAPE ] ]", text, StringComparison.Ordinal);
        Assert.Contains("TargetedUsage", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileStore_SaveAsSelectsProfile_AndLoadReplacesRetailRows()
    {
        string root = Path.Combine(Path.GetTempPath(), "acdream-keymap-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "config");
        string documents = Path.Combine(root, "documents", "Asheron's Call");
        string json = Path.Combine(config, "keybinds.json");
        try
        {
            var source = KeyBindings.RetailDefaults();
            var store = new RetailKeymapProfileStore(json, documents);
            RetailKeymapSaveResult saved = store.Save("friends", source, overwrite: false);

            Assert.Equal(RetailKeymapSaveStatus.Saved, saved.Status);
            Assert.Equal("friends.keymap", store.CurrentFileName);
            Assert.Equal(new[] { "friends.keymap" }, store.ListFiles());
            Assert.True(store.TryLoad(
                "friends.keymap", new KeyBindings(), out KeyBindings loaded, out string? error),
                error);
            Assert.Equal(
                source.ForAction(InputAction.MovementForward).ToArray(),
                loaded.ForAction(InputAction.MovementForward).ToArray());
            Assert.Equal(
                RetailKeymapSaveStatus.Exists,
                store.Save("friends", source, overwrite: false).Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "AcDream.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate acdream.sln.");
    }
}
