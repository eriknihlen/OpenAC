using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Content;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.World;

public sealed class SkyDescLoaderTests
{
    [Theory]
    [InlineData(20, 10, 0, 16)]
    [InlineData(20, 10, 83, 5)]
    [InlineData(20, 116, 83, 18)]
    [InlineData(20, 127, 359, 4)]
    public void DayGroupSelector_MatchesNamedRetailGoldenValues(
        int count,
        int absoluteYear,
        int dayOfYear,
        int expected)
    {
        Assert.Equal(
            expected,
            SkyDayGroupSelector.SelectIndex(
                count,
                absoluteYear,
                daysPerYear: 360,
                dayOfYear));
    }

    [Fact]
    public void DayGroupSelector_ForcedIndexIsExplicitInput()
    {
        Assert.Equal(
            7,
            SkyDayGroupSelector.SelectIndex(
                20,
                absoluteYear: 10,
                daysPerYear: 360,
                dayOfYear: 0,
                forcedIndex: 7));
    }

    /// <summary>
    /// Hand-build a Region with a minimal sky descriptor to feed the
    /// loader without needing real dat bytes. The LoadFromRegion
    /// separator exists precisely for this — keeps the parsing logic
    /// testable independent of DatCollection.
    /// </summary>
    private static Region MakeRegion(float dirBright, byte rBgrOrder)
    {
        var region = new Region();
        region.PartsMask = PartsMask.HasSkyInfo;

        var sky = new SkyDesc
        {
            TickSize = 1.0,
            LightTickSize = 2.0,
        };

        var dg = new DayGroup
        {
            ChanceOfOccur = 1.0f,
        };

        var time = new SkyTimeOfDay
        {
            Begin         = 0.5f,
            DirBright     = dirBright,
            DirHeading    = 180f,
            DirPitch      = 70f,
            DirColor      = new ColorARGB { Blue = 0, Green = 0, Red = rBgrOrder, Alpha = 255 },
            AmbBright     = 0.4f,
            AmbColor      = new ColorARGB { Blue = 100, Green = 100, Red = 100, Alpha = 255 },
            MinWorldFog   = 120f,
            MaxWorldFog   = 400f,
            WorldFogColor = new ColorARGB { Blue = 50, Green = 50, Red = 50, Alpha = 255 },
            WorldFog      = 1,  // Linear
        };

        dg.SkyTime.Add(time);
        sky.DayGroups.Add(dg);
        region.SkyInfo = sky;

        return region;
    }

    [Fact]
    public void LoadFromRegion_ConvertsFogFields()
    {
        var region = MakeRegion(dirBright: 1.5f, rBgrOrder: 200);
        var loaded = SkyDescLoader.LoadFromRegion(region);

        Assert.NotNull(loaded);
        Assert.Equal(1.0, loaded!.TickSize);
        Assert.Single(loaded.DayGroups);
        var grp = loaded.DayGroups[0];
        Assert.Single(grp.SkyTimes);

        var kf = grp.SkyTimes[0].Keyframe;
        Assert.Equal(120f, kf.FogStart);
        Assert.Equal(400f, kf.FogEnd);
        Assert.Equal(FogMode.Linear, kf.FogMode);
    }

    [Fact]
    public void LoadFromRegion_CapturesSkyObjectPesId()
    {
        var region = MakeRegion(dirBright: 1.0f, rBgrOrder: 255);
        var dg = region.SkyInfo!.DayGroups[0];
        dg.SkyObjects.Add(new SkyObject
        {
            BeginTime = 0f,
            EndTime = 1f,
            DefaultGfxObjectId = 0x01004C44u,
            DefaultPesObjectId = 0x3300042Cu,
            Properties = 0x05,
        });

        var loaded = SkyDescLoader.LoadFromRegion(region);

        Assert.NotNull(loaded);
        var obj = Assert.Single(loaded!.DayGroups[0].SkyObjects);
        Assert.Equal(0x01004C44u, obj.GfxObjId);
        Assert.Equal(0x3300042Cu, obj.PesObjectId);
        Assert.True(obj.IsPostScene);
        Assert.Equal(Vector3.Zero, obj.AuthoredSortCenter);
    }

    [Fact]
    public void LoadFromRegion_WithDatSourceCarriesDefaultAndReplacementSortCenters()
    {
        const uint defaultId = 0x01001348u;
        const uint replacementId = 0x01001F6Au;
        Vector3 defaultCenter = new(1050f, 0f, 0f);
        Vector3 replacementCenter = new(2066.82f, 552.99f, 0f);
        Region region = MakeRegion(dirBright: 1f, rBgrOrder: 255);
        DayGroup group = region.SkyInfo!.DayGroups[0];
        group.SkyObjects.Add(new SkyObject
        {
            DefaultGfxObjectId = defaultId,
        });
        group.SkyTime[0].SkyObjReplace.Add(new SkyObjectReplace
        {
            ObjectIndex = 0,
            GfxObjId = replacementId,
        });
        var dats = new FakeDatObjectSource();
        dats.Add(defaultId, new GfxObj { SortCenter = defaultCenter });
        dats.Add(replacementId, new GfxObj { SortCenter = replacementCenter });

        LoadedSkyDesc loaded = Assert.IsType<LoadedSkyDesc>(
            SkyDescLoader.LoadFromRegion(region, dats));

        Assert.Equal(defaultCenter,
            Assert.Single(loaded.DayGroups[0].SkyObjects).AuthoredSortCenter);
        Assert.Equal(replacementCenter,
            Assert.Single(loaded.DayGroups[0].SkyTimes[0].Replaces).AuthoredSortCenter);
    }

    [Fact]
    public void LoadFromRegion_FailedOptionalSortCenterLookupCannotFailSkyLoading()
    {
        Region region = MakeRegion(dirBright: 1f, rBgrOrder: 255);
        region.SkyInfo!.DayGroups[0].SkyObjects.Add(new SkyObject
        {
            DefaultGfxObjectId = 0x01001348u,
        });

        LoadedSkyDesc loaded = Assert.IsType<LoadedSkyDesc>(
            SkyDescLoader.LoadFromRegion(region, new ThrowingDatObjectSource()));

        Assert.Equal(
            Vector3.Zero,
            Assert.Single(loaded.DayGroups[0].SkyObjects).AuthoredSortCenter);
    }

    [Fact]
    public void LoadFromRegion_SunColor_UsesRetailSunVectorMagnitude()
    {
        var region = MakeRegion(dirBright: 1.5f, rBgrOrder: 200);
        var loaded = SkyDescLoader.LoadFromRegion(region);
        Assert.NotNull(loaded);

        var kf = loaded!.DayGroups[0].SkyTimes[0].Keyframe;
        Assert.InRange(kf.SunColor.X, 1.17f, 1.18f);
    }

    [Fact]
    public void LoadFromRegion_NoSkyInfo_ReturnsNull()
    {
        var region = new Region { PartsMask = 0 };
        Assert.Null(SkyDescLoader.LoadFromRegion(region));
    }

    [Fact]
    public void BuildDefaultProvider_FromDatKeyframes_SupportsInterpolation()
    {
        var region = MakeRegion(dirBright: 1.0f, rBgrOrder: 255);
        var loaded = SkyDescLoader.LoadFromRegion(region)!;
        var provider = loaded.BuildDefaultProvider();

        // Exactly one keyframe: interpolation at any t returns it.
        var s = provider.Interpolate(0.1f);
        Assert.InRange(s.SunColor.X, 0.99f, 1.01f);
    }

    [Fact]
    public void SkyObjectData_IsVisible_HandlesWrap()
    {
        var obj = new SkyObjectData
        {
            BeginTime = 0.9f,  // wraps across midnight
            EndTime   = 0.1f,
        };

        Assert.True(obj.IsVisible(0.95f));   // near end of day
        Assert.True(obj.IsVisible(0.05f));   // just after midnight
        Assert.False(obj.IsVisible(0.5f));   // mid-day (not visible)
    }

    [Fact]
    public void SkyObjectData_CurrentAngle_LerpsAcrossWindow()
    {
        var obj = new SkyObjectData
        {
            BeginTime  = 0.25f,
            EndTime    = 0.75f,
            BeginAngle = 0f,
            EndAngle   = 180f,
        };

        // Middle of the window → 90°.
        Assert.Equal(90f, obj.CurrentAngle(0.5f), precision: 2);
        // At begin → begin angle.
        Assert.Equal(0f,  obj.CurrentAngle(0.25f), precision: 2);
    }

    private sealed class FakeDatObjectSource : IDatObjectSource
    {
        private readonly Dictionary<uint, IDBObj> _objects = [];

        internal void Add(uint id, IDBObj value) => _objects[id] = value;

        public T Get<T>(uint fileId) where T : IDBObj =>
            _objects.TryGetValue(fileId, out IDBObj? value) && value is T typed
                ? typed
                : default!;

        public bool TryGet<T>(uint fileId, out T value) where T : IDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }
    }

    private sealed class ThrowingDatObjectSource : IDatObjectSource
    {
        public T Get<T>(uint fileId) where T : IDBObj =>
            throw new InvalidOperationException("synthetic DAT lookup failure");

        public bool TryGet<T>(uint fileId, out T value) where T : IDBObj =>
            throw new InvalidOperationException("synthetic DAT lookup failure");
    }
}
