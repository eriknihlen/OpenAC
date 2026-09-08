using AcDream.App.UI.Layout;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailKeyNamesTests
{
    private static string? NoStrings(uint table, uint hash) => null;

    private static Func<uint, uint, string?> Table(
        params (uint Table, string Key, string Value)[] entries)
        => (table, hash) =>
        {
            foreach ((uint t, string key, string value) in entries)
                if (t == table && DatStringResolver.ComputeHash(key) == hash)
                    return value;
            return null;
        };

    [Fact]
    public void DatTableOverride_WinsOverOsName()
    {
        var names = new RetailKeyNames(
            Table((RetailKeyNames.KeyNameTableId, "DIK_LCONTROL", "Left Ctrl")),
            osKeyName: (_, _) => throw new InvalidOperationException("OS lookup must not run"));

        Assert.Equal("Left Ctrl", names.Describe(new KeyChord(Key.ControlLeft, ModifierMask.None)));
    }

    [Fact]
    public void OsLocalizedName_UsedWhenTheDatTableMisses()
    {
        var names = new RetailKeyNames(
            NoStrings,
            osKeyName: (scan, extended) =>
                scan == 0x2A && !extended ? "SKIFT" : null);

        Assert.Equal("SKIFT", names.Describe(new KeyChord(Key.ShiftLeft, ModifierMask.None)));
    }

    [Fact]
    public void SelfModifier_ShowsOnlyTheKeyName_NeverShiftPlusShiftLeft()
    {
        var names = new RetailKeyNames(
            NoStrings,
            osKeyName: (scan, _) => scan == 0x2A ? "SKIFT" : null);

        Assert.Equal("SKIFT", names.Describe(new KeyChord(Key.ShiftLeft, ModifierMask.Shift)));
        Assert.Equal("RSHIFT", names.Describe(new KeyChord(Key.ShiftRight, ModifierMask.Shift)));
    }

    [Fact]
    public void ModifierPrefixes_JoinWithTheAuthoredDelimiter_InMetaBitOrder()
    {
        var names = new RetailKeyNames(
            Table((RetailKeyNames.DelimiterTableId, "ID_KeyDescDelimiter", "+")),
            osKeyName: (scan, _) => scan switch
            {
                0x2A => "SKIFT",
                0x1D => "CTRL",
                0x38 => "ALT",
                0x32 => "M",
                _ => null,
            });

        Assert.Equal(
            "SKIFT+CTRL+ALT+M",
            names.Describe(new KeyChord(
                Key.M, ModifierMask.Shift | ModifierMask.Ctrl | ModifierMask.Alt)));
    }

    [Fact]
    public void MetaTableOverride_WinsForTheModifierPrefix()
    {
        var names = new RetailKeyNames(
            Table(
                (RetailKeyNames.DelimiterTableId, "ID_KeyDescDelimiter", "+"),
                (RetailKeyNames.MetaKeyNameTableId, "DIK_LSHIFT", "Shift")),
            osKeyName: (scan, _) => scan == 0x32 ? "M" : null);

        Assert.Equal("Shift+M", names.Describe(new KeyChord(Key.M, ModifierMask.Shift)));
    }

    [Fact]
    public void ExtendedKeys_PassTheExtendedFlagToTheOsLookup()
    {
        // DIK_UP = 0xC8: scan 0x48 + the extended bit — the same split
        // GetKeyNameText expects in lParam bit 24.
        (byte Scan, bool Extended)? seen = null;
        var names = new RetailKeyNames(
            NoStrings,
            osKeyName: (scan, extended) =>
            {
                seen = (scan, extended);
                return "UP ARROW";
            });

        Assert.Equal("UP ARROW", names.Describe(new KeyChord(Key.Up, ModifierMask.None)));
        Assert.Equal(((byte)0x48, true), seen);
    }

    [Fact]
    public void DikSuffixSpelling_WhenBothDatAndOsMiss()
    {
        var names = new RetailKeyNames(NoStrings, osKeyName: (_, _) => null);

        Assert.Equal("W", names.Describe(new KeyChord(Key.W, ModifierMask.None)));
        Assert.Equal("NUMPADENTER", names.Describe(new KeyChord(Key.KeypadEnter, ModifierMask.None)));
    }

    [Fact]
    public void KeymapInterchangeControls_UseTheirRetailDikNames()
    {
        var names = new RetailKeyNames(NoStrings, osKeyName: (_, _) => null);

        Assert.Equal("F13", names.Describe(new KeyChord(Key.F13, ModifierMask.None)));
        Assert.Equal(
            "LSHIFT+F13",
            names.Describe(new KeyChord(Key.F13, ModifierMask.Shift)));
        Assert.Equal(
            "LWIN+F13",
            names.Describe(new KeyChord(Key.F13, ModifierMask.Win)));
    }

    [Fact]
    public void MouseButtonUsesRetailSemanticTableThenReadableFallback()
    {
        var authored = new RetailKeyNames(
            Table((RetailKeyNames.KeyNameTableId, "DIMOFS_BUTTON0", "Primary Mouse")),
            osKeyName: (_, _) => null);
        var fallback = new RetailKeyNames(NoStrings, osKeyName: (_, _) => null);
        var left = new KeyChord(
            InputDispatcher.MouseButtonToKey(MouseButton.Left),
            ModifierMask.None,
            Device: 1);
        var rightWithCtrl = new KeyChord(
            InputDispatcher.MouseButtonToKey(MouseButton.Right),
            ModifierMask.Ctrl,
            Device: 1);

        Assert.Equal("Primary Mouse", authored.Describe(left));
        Assert.Equal("LCONTROL+Mouse Button 2", fallback.Describe(rightWithCtrl));
    }

    [Fact]
    public void DefaultChord_DescribesAsEmpty()
    {
        var names = new RetailKeyNames(NoStrings, osKeyName: (_, _) => null);
        Assert.Equal(string.Empty, names.Describe(default));
    }
}
