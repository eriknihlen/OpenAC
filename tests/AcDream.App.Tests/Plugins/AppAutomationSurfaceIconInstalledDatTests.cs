using AcDream.App.Plugins;
using AcDream.App.Tests.Rendering;
using AcDream.Content;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.Plugins;

[Trait("Lane", "InstalledDat")]
public sealed class AppAutomationSurfaceIconInstalledDatTests
{
    private const uint KnownSpellId = 1u;

    [Fact]
    public void KnownSpell_IconId_MatchesTheInstalledSpellTablesIconFieldExactly()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        MagicCatalog catalog = MagicCatalog.Load(adapter);

        Assert.True(
            catalog.SpellTable.TryGet(KnownSpellId, out SpellMetadata expected),
            $"expected spell {KnownSpellId} (Strength Other I) to exist in the installed SpellTable");
        // A real production install's SpellTable always has non-zero art for
        // an ordinary named spell; a zero here would mean the projector
        // silently dropped SpellBase.Icon rather than proving the pipeline.
        Assert.NotEqual(0u, expected.IconId);

        using var runtime = GameRuntimeTestFactory.Create();
        runtime.CharacterOwner.InstallSpellMetadata(catalog.SpellTable);
        runtime.CharacterOwner.Spellbook.OnSpellLearned(KnownSpellId);
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        Assert.True(surface.Spells.TryGet(KnownSpellId, out PluginSpellInfo info));
        Assert.Equal(expected.IconId, info.IconId);
    }
}
