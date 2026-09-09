using System;
using System.Collections.Generic;
using System.IO;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class MapNoteLiveDatTests
{
    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    private const uint TemplateLayoutId = 0x21000026u;
    private const uint TemplateElementId = 0x100001F0u;
    private const uint HighlightChildId = 0x100001F1u;
    private const uint MapNoteSkinRootId = 0x10000398u;
    private const uint TooltipCatalogLayoutId = 0x21000041u;
    private const uint TooltipTextChildId = 0x10000396u;
    private const uint MapNoteFontDid = 0x40000015u;
    private const uint GenericSkinFontDid = 0x40000002u;
    private const uint GreenFrameSurfaceId = 0x06004CC9u;

    [InstalledDatFact]
    public void HotspotTemplate_AuthorsItsOwnPopupLocator_ZeroDelay_AndRollover()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        ElementInfo? template = LayoutImporter.ImportInfos(dats, TemplateLayoutId, TemplateElementId);
        Assert.NotNull(template);

        Assert.Equal(MapNoteSkinRootId, template!.TooltipRootElementId);
        Assert.Equal(TooltipCatalogLayoutId, template.TooltipLayoutDid);
        Assert.True(template.TooltipEnabled);                 // P0x4B
        Assert.True(template.TooltipDelaySeconds.HasValue);   // P0x50 authored
        Assert.Equal(0f, template.TooltipDelaySeconds!.Value); // = 0.0

        // P0x13 RolloverEnabled — drives UiButtonStateMachine's
        // NormalRollover request on pointer-over.
        Assert.True(template.TryGetEffectiveBool(0x13u, out bool rollover) && rollover);

        Assert.True(template.States.TryGetValue(1u, out UiStateInfo? normal));
        Assert.True(template.States.TryGetValue(2u, out UiStateInfo? normalRollover));
        Assert.Equal("Normal", normal!.Name);
        Assert.Equal("Normal_rollover", normalRollover!.Name);
        Assert.True(normal.PassToChildren);
        Assert.True(normalRollover.PassToChildren);
    }

    [InstalledDatFact]
    public void HotspotTemplate_HighlightChild_FlipsPerStateInvisible_WithGreenFrameMedia()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        ElementInfo? template = LayoutImporter.ImportInfos(dats, TemplateLayoutId, TemplateElementId);
        Assert.NotNull(template);

        ElementInfo highlight = Assert.Single(template!.Children, c => c.Id == HighlightChildId);

        // Per-state P0x3B: hidden in Normal, shown in Normal_rollover.
        Assert.True(highlight.States.TryGetValue(1u, out UiStateInfo? normal));
        Assert.True(highlight.States.TryGetValue(2u, out UiStateInfo? rollover));
        Assert.True(normal!.Properties.TryGetValue(0x3Bu, out var normalInvisible));
        Assert.True(rollover!.Properties.TryGetValue(0x3Bu, out var rolloverInvisible));
        Assert.Equal(UiPropertyKind.Bool, normalInvisible.Kind);
        Assert.Equal(UiPropertyKind.Bool, rolloverInvisible.Kind);
        Assert.True(normalInvisible.BoolValue);
        Assert.False(rolloverInvisible.BoolValue);

        Assert.Equal(4, highlight.Children.Count);
        foreach (ElementInfo edge in highlight.Children)
        {
            Assert.True(edge.StateMedia.TryGetValue("", out var media));
            Assert.Equal(GreenFrameSurfaceId, media.File);
        }
    }

    [InstalledDatFact]
    public void MapNoteSkin_TextChild_FontsTheSpecialFont_UnlikeTheGenericSkins()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var fonts = new Dictionary<uint, uint>();
        foreach (uint skinId in new[] { 0x10000487u, 0x10000395u, 0x10000397u, MapNoteSkinRootId })
        {
            ElementInfo? skin = LayoutImporter.ImportInfos(dats, TooltipCatalogLayoutId, skinId);
            Assert.NotNull(skin);
            ElementInfo text = Assert.Single(skin!.Children, c => c.Id == TooltipTextChildId);
            fonts[skinId] = text.FontDid;
        }

        Assert.Equal(MapNoteFontDid, fonts[MapNoteSkinRootId]);
        Assert.Equal(GenericSkinFontDid, fonts[0x10000487u]);
        Assert.Equal(GenericSkinFontDid, fonts[0x10000395u]);
        Assert.Equal(GenericSkinFontDid, fonts[0x10000397u]);
    }
}
