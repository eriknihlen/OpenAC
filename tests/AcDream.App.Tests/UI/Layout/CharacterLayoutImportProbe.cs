using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class CharacterLayoutImportProbe
{
    private const uint CharacterLayout = 0x2100002Eu;

    private static string? DatDir()
    {
        var d = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(d) ? d : null;
    }

    [Fact]
    public void Selected_attribute_shows_raise_buttons_on_visible_footer_state()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var layout = LayoutImporter.Import(dats, CharacterLayout, _ => (1u, 30, 26), null);
        Assert.NotNull(layout);

        CharacterStatController.Bind(layout!, SampleData.SampleCharacter, spriteResolve: _ => (1u, 30, 26));

        var rows = new List<UiClickablePanel>();
        CollectRows(layout!.Root, rows);
        Assert.True(rows.Count >= 9, $"expected at least 9 attribute rows, found {rows.Count}");

        rows[4].OnClick!();

        var buttons = new List<UiButton>();
        CollectButtons(layout.Root, buttons);

        Assert.Contains(buttons, b =>
            b.ElementId == CharacterStatController.RaiseOneId
            && b.ActiveState == "Normal"
            && IsEffectivelyVisible(b));
        Assert.Contains(buttons, b =>
            b.ElementId == CharacterStatController.RaiseTenId
            && b.ActiveState == "Normal"
            && IsEffectivelyVisible(b));
    }

    [Fact]
    public void Close_button_resolves_and_invokes_controller_close_callback()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var layout = LayoutImporter.Import(dats, CharacterLayout, _ => (1u, 30, 26), null);
        Assert.NotNull(layout);

        var close = layout!.FindElement(WindowChromeController.CharacterCloseButtonId);
        Assert.NotNull(close);
        Assert.True(close is UiButton or UiDatElement,
            $"character close button resolved to {close?.GetType().Name ?? "null"}");

        int closes = 0;
        CharacterStatController.Bind(layout, SampleData.SampleCharacter, onClose: () => closes++);
        close!.OnEvent(new UiEvent(0u, close, UiEventType.Click));

        Assert.Equal(1, closes);
    }

    [Fact]
    public void Footer_title_exposes_retail_append_text_palette()
    {
        string? datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        ImportedLayout? layout = LayoutImporter.Import(
            dats,
            CharacterLayout,
            _ => (1u, 30, 26),
            null);

        UiText title = Assert.IsType<UiText>(
            layout!.FindElement(CharacterStatController.FooterTitleId));
        Assert.Equal(
            [
                Vector4.One,
                new Vector4(0f, 1f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f),
                new Vector4(127f / 255f, 1f, 1f, 1f),
            ],
            title.FontColorPalette);
    }

    private static void CollectRows(UiElement node, List<UiClickablePanel> result)
    {
        if (node is UiClickablePanel row && row.Height is >= 20f and <= 22f && row.OnClick is not null)
            result.Add(row);
        foreach (var child in node.Children)
            CollectRows(child, result);
    }

    private static void CollectButtons(UiElement node, List<UiButton> result)
    {
        if (node is UiButton button
            && (button.ElementId == CharacterStatController.RaiseOneId
                || button.ElementId == CharacterStatController.RaiseTenId))
        {
            result.Add(button);
        }

        foreach (var child in node.Children)
            CollectButtons(child, result);
    }

    private static bool IsEffectivelyVisible(UiElement element)
    {
        for (UiElement? e = element; e is not null; e = e.Parent)
        {
            if (!e.Visible) return false;
        }
        return true;
    }

}
