using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Tests.UI.Layout;
using AcDream.Content;
using AcDream.Content.CharGen;
using AcDream.Content.Vfx;
using AcDream.Core.CharGen;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Rendering;

[Trait("Lane", "InstalledDat")]
public sealed class ChargenPreviewControllerTests
{
    private readonly ITestOutputHelper _out;
    public ChargenPreviewControllerTests(ITestOutputHelper output) => _out = output;

    private const uint AluvianId = 1u;
    private const uint GearknightId = 6u;

    [InstalledDatFact]
    public void Rebuild_SameSelectionTwice_IsANoOpSecondTime()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var controller = new ChargenPreviewController(
                renderer, new ChargenPreviewCamera(), view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            ChargenAppearanceSelection selection = DefaultSelection(options, AluvianId, 1);
            Assert.True(controller.Rebuild(options, AluvianId, 1, selection));
            Assert.True(controller.Rebuild(options, AluvianId, 1, selection));

            Assert.Equal(1, renderer.SetPreviewCallCount);
        }
    }

    [InstalledDatFact]
    public void Rebuild_HeritageChange_ResetsCameraToTheNewHeritagesDefaultEye()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var camera = new ChargenPreviewCamera();
            var controller = new ChargenPreviewController(
                renderer, camera, view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            Assert.True(controller.Rebuild(
                options, AluvianId, 1, DefaultSelection(options, AluvianId, 1)));
            camera.Eye = new Vector3(0f, -99f, 99f);

            if (!options.TryGetHeritage(GearknightId, out ChargenHeritageOptions? gearknight)
                || gearknight!.GendersByKey.Count == 0)
            {
                _out.WriteLine("SKIP: installed dat has no Gearknight gender to switch to.");
                return;
            }
            int gearknightGender = gearknight.GendersByKey.Keys.First();
            Assert.True(controller.Rebuild(
                options, GearknightId, gearknightGender,
                DefaultSelection(options, GearknightId, gearknightGender)));

            Assert.Equal(ChargenPreviewCamera.ResolveDefaultEye(GearknightId), controller.CameraEye);
        }
    }

    [InstalledDatFact]
    public void Rebuild_AppearanceOnlyChange_LeavesTheCameraUntouched()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var camera = new ChargenPreviewCamera();
            var controller = new ChargenPreviewController(
                renderer, camera, view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            ChargenAppearanceSelection first = DefaultSelection(options, AluvianId, 1);
            Assert.True(controller.Rebuild(options, AluvianId, 1, first));
            var pokedEye = new Vector3(0f, -99f, 99f);
            camera.Eye = pokedEye;

            ChargenAppearanceSelection second = first with { SkinShade = 0.9 };
            Assert.True(controller.Rebuild(options, AluvianId, 1, second));

            Assert.Equal(pokedEye, controller.CameraEye);
        }
    }

    [InstalledDatFact]
    public void Rebuild_PreservesZoomState_AcrossAnAppearanceOnlyChange()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var controller = new ChargenPreviewController(
                renderer, new ChargenPreviewCamera(), view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            ChargenAppearanceSelection first = DefaultSelection(options, AluvianId, 1);
            Assert.True(controller.Rebuild(options, AluvianId, 1, first));
            controller.ZoomIn();
            Assert.True(controller.IsZoomedIn);

            ChargenAppearanceSelection second = first with { SkinShade = 0.9 };
            Assert.True(controller.Rebuild(options, AluvianId, 1, second));

            Assert.True(controller.IsZoomedIn);
        }
    }

    [InstalledDatFact]
    public void Rebuild_ThenRender_SeedsTheEntityHeadingToTheRetailDefault180Degrees()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var controller = new ChargenPreviewController(
                renderer, new ChargenPreviewCamera(), view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            Assert.True(controller.Rebuild(
                options, AluvianId, 1, DefaultSelection(options, AluvianId, 1)));
            Assert.NotNull(renderer.LastEntity);

            controller.Render();

            Quaternion expected = MoveToMath.SetHeading(
                Quaternion.Identity, ChargenPreviewRotationController.RetailDefaultHeadingDegrees);
            Quaternion actual = renderer.LastEntity!.Rotation;
            Assert.Equal(expected.X, actual.X, 4);
            Assert.Equal(expected.Y, actual.Y, 4);
            Assert.Equal(expected.Z, actual.Z, 4);
            Assert.Equal(expected.W, actual.W, 4);
        }
    }

    [InstalledDatFact]
    public void Rebuild_HeritageWithEnvironmentSetupId_SetsANonNullBackdrop()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            Assert.True(options.TryGetHeritage(AluvianId, out ChargenHeritageOptions? aluvian));
            if (aluvian!.EnvironmentSetupId == 0u)
            {
                _out.WriteLine("SKIP: installed dat's Aluvian heritage authors no EnvironmentSetupId.");
                return;
            }

            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var controller = new ChargenPreviewController(
                renderer, new ChargenPreviewCamera(), view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            Assert.True(controller.Rebuild(
                options, AluvianId, 1, DefaultSelection(options, AluvianId, 1)));

            Assert.Equal(1, renderer.SetBackdropCallCount);
            Assert.NotNull(renderer.LastBackdropEntity);
            Assert.Equal(aluvian.EnvironmentSetupId, renderer.LastBackdropEntity!.SourceGfxObjOrSetupId);
        }
    }

    [InstalledDatFact]
    public void Rebuild_HeritageWithNoEnvironmentSetupId_LeavesTheBackdropAbsent()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            Assert.True(options.TryGetHeritage(AluvianId, out ChargenHeritageOptions? aluvian));

            var heritages = new Dictionary<uint, ChargenHeritageOptions>(options.HeritagesById)
            {
                [AluvianId] = aluvian! with { EnvironmentSetupId = 0u },
            };
            ChargenOptions zeroed = options with { HeritagesById = heritages };

            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var controller = new ChargenPreviewController(
                renderer, new ChargenPreviewCamera(), view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            Assert.True(controller.Rebuild(
                zeroed, AluvianId, 1, DefaultSelection(zeroed, AluvianId, 1)));

            Assert.Equal(1, renderer.SetBackdropCallCount);
            Assert.Null(renderer.LastBackdropEntity);
        }
    }

    [InstalledDatFact]
    public void Rebuild_HeritageChange_SwapsTheBackdropEntity()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            Assert.True(options.TryGetHeritage(AluvianId, out ChargenHeritageOptions? aluvian));
            if (aluvian!.EnvironmentSetupId == 0u)
            {
                _out.WriteLine("SKIP: installed dat's Aluvian heritage authors no EnvironmentSetupId.");
                return;
            }
            if (!options.TryGetHeritage(GearknightId, out ChargenHeritageOptions? gearknight)
                || gearknight!.GendersByKey.Count == 0
                || gearknight.EnvironmentSetupId == 0u)
            {
                _out.WriteLine("SKIP: installed dat has no usable Gearknight environment/gender to switch to.");
                return;
            }

            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var controller = new ChargenPreviewController(
                renderer, new ChargenPreviewCamera(), view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            Assert.True(controller.Rebuild(
                options, AluvianId, 1, DefaultSelection(options, AluvianId, 1)));
            WorldEntity? firstBackdrop = renderer.LastBackdropEntity;
            Assert.NotNull(firstBackdrop);

            int gearknightGender = gearknight.GendersByKey.Keys.First();
            Assert.True(controller.Rebuild(
                options, GearknightId, gearknightGender,
                DefaultSelection(options, GearknightId, gearknightGender)));

            Assert.Equal(2, renderer.SetBackdropCallCount);
            Assert.NotSame(firstBackdrop, renderer.LastBackdropEntity);
            Assert.NotNull(renderer.LastBackdropEntity);
            Assert.Equal(
                gearknight.EnvironmentSetupId, renderer.LastBackdropEntity!.SourceGfxObjOrSetupId);
        }
    }

    [InstalledDatFact]
    public void Rebuild_AppearanceOnlyChange_DoesNotRebuildTheBackdrop()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView();
            var controller = new ChargenPreviewController(
                renderer, new ChargenPreviewCamera(), view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());

            ChargenAppearanceSelection first = DefaultSelection(options, AluvianId, 1);
            Assert.True(controller.Rebuild(options, AluvianId, 1, first));
            Assert.Equal(1, renderer.SetBackdropCallCount);

            ChargenAppearanceSelection second = first with { SkinShade = 0.9 };
            Assert.True(controller.Rebuild(options, AluvianId, 1, second));

            Assert.Equal(1, renderer.SetBackdropCallCount);
        }
    }

    [InstalledDatFact]
    public void Render_WhilePageInvisible_SkipsRenderAndTexturePublication()
    {
        if (!TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter))
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using (dats)
        using (adapter)
        {
            (ChargenOptions options, ChargenAppearanceCatalog catalog) = LoadFixture(adapter!);
            var renderer = new FakeChargenRenderer();
            var view = new FakeChargenView { Visible = false };
            var controller = new ChargenPreviewController(
                renderer, new ChargenPreviewCamera(), view,
                adapter!, new RetailAnimationLoader(adapter!), catalog, catalog, new object());
            Assert.True(controller.Rebuild(
                options, AluvianId, 1, DefaultSelection(options, AluvianId, 1)));

            controller.Render();

            Assert.Equal(0, renderer.RenderCallCount);
            Assert.Null(view.LastTextureHandle);
        }
    }

    private static ChargenAppearanceSelection DefaultSelection(
        ChargenOptions options, uint heritageId, int genderKey)
    {
        Assert.True(options.TryGetHeritage(heritageId, out ChargenHeritageOptions? heritage));
        Assert.True(heritage!.GendersByKey.TryGetValue(genderKey, out ChargenGenderOptions? gender));
        return ChargenAppearanceSelection.Default with
        {
            HairStyle = gender!.HairStyles.Count > 0 ? 0u : ChargenAppearanceSelection.Unset,
            SkinShade = 0.5,
        };
    }

    private static (ChargenOptions, ChargenAppearanceCatalog) LoadFixture(IDatReaderWriter dats) =>
        (ChargenTableReader.Load(dats), new ChargenAppearanceCatalog(dats));

    private bool TryOpen(out DatCollection? dats, out DatCollectionAdapter? adapter)
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: dats unavailable");
            dats = null;
            adapter = null;
            return false;
        }
        dats = new DatCollection(datDir, DatAccessType.Read);
        adapter = new DatCollectionAdapter(dats);
        return true;
    }

    private sealed class FakeChargenRenderer : IChargenPreviewRenderer
    {
        public WorldEntity? LastEntity { get; private set; }
        public int SetPreviewCallCount { get; private set; }
        public int RenderCallCount { get; private set; }

        public WorldEntity? LastBackdropEntity { get; private set; }
        public int SetBackdropCallCount { get; private set; }

        public void SetPreview(WorldEntity? entity)
        {
            LastEntity = entity;
            SetPreviewCallCount++;
        }

        public void SetBackdrop(WorldEntity? entity)
        {
            LastBackdropEntity = entity;
            SetBackdropCallCount++;
        }

        public uint Render(int width, int height)
        {
            RenderCallCount++;
            return 42u;
        }
    }

    private sealed class FakeChargenView : IChargenPreviewFrameView
    {
        public bool Visible { get; set; } = true;
        public int Width { get; set; } = 128;
        public int Height { get; set; } = 128;
        public uint? LastTextureHandle { get; private set; }

        public bool TryGetVisibleSize(out int width, out int height)
        {
            width = Width;
            height = Height;
            return Visible;
        }

        public void SetTextureHandle(uint textureHandle) => LastTextureHandle = textureHandle;
    }
}
