using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Input;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class PaperdollFramePresenterTests
{
    /// <summary>One frame in production order: the pre-world resource phase
    /// (build/redress + prewarm) then the late presentation phase.</summary>
    private static void Frame(PaperdollFramePresenter presenter)
    {
        presenter.PrepareResources();
        presenter.Render();
    }

    [Fact]
    public void HiddenView_BuildsAndPrewarmsWithoutRendering()
    {
        var renderer = new RecordingRenderer();
        var view = new RecordingView { Visible = false };
        var factory = new RecordingFactory { Doll = CreateDoll() };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);

        Assert.False(presenter.IsDirty);
        Assert.Equal(1, factory.BuildCount);
        Assert.Equal(1, renderer.PrepareCount);
        Assert.Equal(0, renderer.RenderCount);
        Assert.Empty(view.TextureHandles);
    }

    [Fact]
    public void FirstVisibleFrame_BuildsThenRendersAndPublishesTexture()
    {
        var doll = CreateDoll();
        var renderer = new RecordingRenderer { TextureHandle = 91u };
        var view = new RecordingView { Width = 240, Height = 320 };
        var factory = new RecordingFactory { Doll = doll };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);

        Assert.False(presenter.IsDirty);
        Assert.Equal(1, factory.BuildCount);
        Assert.Same(doll, renderer.Dolls.Single());
        Assert.Equal([(240, 320)], renderer.RenderSizes);
        Assert.Equal([91u], view.TextureHandles);
    }

    [Fact]
    public void CleanFrame_ReusesDollUntilMarkedDirty()
    {
        var renderer = new RecordingRenderer();
        var view = new RecordingView();
        var factory = new RecordingFactory
        {
            Doll = CreateDoll(),
        };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        Frame(presenter);
        presenter.MarkDirty();
        Frame(presenter);

        Assert.Equal(2, factory.BuildCount);
        Assert.Equal(2, renderer.Dolls.Count);
        Assert.Equal(3, renderer.RenderCount);
        Assert.False(presenter.IsDirty);
    }

    [Fact]
    public void PortalRefresh_RedressesEvenAnEquivalentAppearance()
    {
        WorldEntity first = CreateDoll();
        WorldEntity repeated = CreateDoll();
        var renderer = new RecordingRenderer();
        var view = new RecordingView();
        var factory = new RecordingFactory { Doll = first };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        factory.Doll = repeated;
        presenter.MarkDirty();
        Frame(presenter);

        Assert.Equal(2, factory.BuildCount);
        Assert.Equal([first, repeated], renderer.Dolls);
        Assert.Equal(2, renderer.RenderCount);
        Assert.False(presenter.IsDirty);
    }

    [Fact]
    public void ChangedScale_ReplacesPrivateDoll()
    {
        WorldEntity first = CreateDoll();
        WorldEntity changed = CreateDoll(scale: 1.25f);
        var renderer = new RecordingRenderer();
        var view = new RecordingView();
        var factory = new RecordingFactory { Doll = first };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        factory.Doll = changed;
        presenter.MarkDirty();
        Frame(presenter);

        Assert.Equal([first, changed], renderer.Dolls);
    }

    [Fact]
    public void TransientZeroRender_NeverErasesThePublishedTexture()
    {
        var renderer = new RecordingRenderer { TextureHandle = 91u };
        var view = new RecordingView();
        var factory = new RecordingFactory { Doll = CreateDoll() };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        renderer.TextureHandle = 0u;
        Frame(presenter);
        renderer.TextureHandle = 91u;
        Frame(presenter);

        Assert.Equal([91u, 91u], view.TextureHandles);
        Assert.Equal(0, view.ClearCount);
    }

    private static WorldEntity CreateDoll(float scale = 1f) => new()
    {
        Id = 42u,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
        Scale = scale,
    };

    [Fact]
    public void TemporaryMissingPlayer_KeepsSuccessfulDollAndRetriesRedress()
    {
        var firstDoll = CreateDoll();
        var renderer = new RecordingRenderer { TextureHandle = 81u };
        var view = new RecordingView();
        var factory = new RecordingFactory { Doll = firstDoll };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        factory.CanBuild = false;
        presenter.MarkDirty();
        Frame(presenter);
        Frame(presenter);

        Assert.True(presenter.IsDirty);
        Assert.Equal(3, factory.BuildCount);
        Assert.Equal([firstDoll], renderer.Dolls);
        Assert.Equal(3, renderer.RenderCount);
        Assert.Equal([81u, 81u, 81u], view.TextureHandles);
    }

    [Fact]
    public void ResetSession_ClearsPrivateDollOnceAndArmsNextCharacterBuild()
    {
        var firstDoll = CreateDoll();
        var secondDoll = CreateDoll();
        var renderer = new RecordingRenderer();
        var view = new RecordingView();
        var factory = new RecordingFactory { Doll = firstDoll };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        presenter.ResetSession();
        factory.Doll = secondDoll;
        Frame(presenter);

        Assert.False(presenter.IsDirty);
        Assert.Equal(2, factory.BuildCount);
        Assert.Equal([firstDoll, null, secondDoll], renderer.Dolls);
        Assert.Equal(1, view.ClearCount);
    }

    /// <summary>
    /// The doll is posed and framed by heritage, and the heritage arrives with
    /// the character description, which can land after the first build. A doll
    /// built before it must be redressed once it is known, or an Olthoi keeps
    /// the humanoid pose and the humanoid camera it was first built with.
    /// </summary>
    [Fact]
    public void LateHeritage_RedressesTheDollAndReframesTheView()
    {
        var renderer = new RecordingRenderer();
        var view = new RecordingView();
        var factory = new RecordingFactory { Doll = CreateDoll() };
        var heritage = new RecordingHeritageSource();
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        Assert.Equal([0u], factory.Heritages);

        heritage.HeritageGroup = 12u;
        Frame(presenter);
        Frame(presenter);

        Assert.Equal([0u, 12u], factory.Heritages);
        Assert.Equal([12u], renderer.Heritages);
        Assert.False(presenter.IsDirty);
    }

    [Fact]
    public void SteadyHeritage_DoesNotRebuildTheDollEveryFrame()
    {
        var renderer = new RecordingRenderer();
        var view = new RecordingView();
        var factory = new RecordingFactory { Doll = CreateDoll() };
        var heritage = new RecordingHeritageSource { HeritageGroup = 13u };
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        Frame(presenter);
        Frame(presenter);

        Assert.Equal(1, factory.BuildCount);
        Assert.Equal([13u], factory.Heritages);
        Assert.Equal([13u], renderer.Heritages);
    }

    [Fact]
    public void ResetSession_ReturnsTheViewToTheUnknownHeritageFraming()
    {
        var renderer = new RecordingRenderer();
        var view = new RecordingView();
        var factory = new RecordingFactory { Doll = CreateDoll() };
        var heritage = new RecordingHeritageSource { HeritageGroup = 12u };
        var presenter = new PaperdollFramePresenter(renderer, view, factory, heritage);

        Frame(presenter);
        presenter.ResetSession();
        heritage.HeritageGroup = 1u;
        Frame(presenter);

        Assert.Equal([12u, 0u, 1u], renderer.Heritages);
    }

    [Fact]
    public void PresenterAndProductionHelpersHaveNoDirectWindowOrDelegateBackReference()
    {
        Type[] owners =
        [
            typeof(PaperdollFramePresenter),
            typeof(RetailPaperdollFrameView),
            typeof(PaperdollInventoryVisibility),
            typeof(LivePaperdollEntityLookup),
            typeof(RetailPaperdollDollFactory),
            typeof(RetailPaperdollPoseApplicator),
        ];

        foreach (Type owner in owners)
        {
            FieldInfo[] fields = owner.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
            Assert.DoesNotContain(
                fields,
                field => field.FieldType == typeof(AcDream.App.UI.RetailUiRuntime));
            Assert.DoesNotContain(
                fields,
                field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        }
    }

    [Fact]
    public void RetailFactory_MissingOrEmptyCurrentPlayerRetriesWithoutApplyingPose()
    {
        var entities = new RecordingEntityLookup();
        var identity = new LocalPlayerIdentityState { ServerGuid = 7u };
        var pose = new RecordingPoseApplicator();
        var factory = new RetailPaperdollDollFactory(entities, identity, pose);

        Assert.False(factory.TryBuild(0u, out WorldEntity? missing));
        Assert.Null(missing);

        entities.Entities[7u] = CreateDoll();
        Assert.False(factory.TryBuild(0u, out WorldEntity? empty));
        Assert.Null(empty);
        Assert.Empty(pose.Applications);
    }

    [Fact]
    public void RetailFactory_ResolvesCurrentIdentityAndClonesLiveAppearance()
    {
        var firstMesh = new MeshRef(0x01000001u, Matrix4x4.Identity)
        {
            SurfaceOverrides = new Dictionary<uint, uint> { [2] = 0x08000001u },
        };
        var secondMesh = new MeshRef(
            0x01000002u,
            Matrix4x4.CreateTranslation(1f, 2f, 3f));
        var first = CreatePlayer(0x02000001u, firstMesh);
        var second = CreatePlayer(0x02000002u, secondMesh);
        var entities = new RecordingEntityLookup
        {
            Entities =
            {
                [10u] = first,
                [20u] = second,
            },
        };
        var identity = new LocalPlayerIdentityState { ServerGuid = 10u };
        var pose = new RecordingPoseApplicator();
        var factory = new RetailPaperdollDollFactory(entities, identity, pose);

        Assert.True(factory.TryBuild(1u, out WorldEntity? firstDoll));
        identity.ServerGuid = 20u;
        Assert.True(factory.TryBuild(12u, out WorldEntity? secondDoll));

        Assert.NotNull(firstDoll);
        Assert.NotNull(secondDoll);
        Assert.Equal(0x02000001u, firstDoll.SourceGfxObjOrSetupId);
        Assert.Equal(0x02000002u, secondDoll.SourceGfxObjOrSetupId);
        Assert.NotSame(first.MeshRefs, firstDoll.MeshRefs);
        Assert.Equal(firstMesh.GfxObjId, firstDoll.MeshRefs[0].GfxObjId);
        Assert.Same(firstMesh.SurfaceOverrides, firstDoll.MeshRefs[0].SurfaceOverrides);
        Assert.Equal(
            [(firstDoll, 0x02000001u, 1u), (secondDoll, 0x02000002u, 12u)],
            pose.Applications);
        Assert.Equal([10u, 20u], entities.RequestedGuids);
    }

    /// <summary>
    /// The doll is the character's own body, so it is drawn at the size that
    /// body wears — and the two places that size has to reach are the entity
    /// the view draws and the pose that rewrites every part placement. Issue
    /// #98: neither got it, so a large body and a small one both came out the
    /// size of a default one.
    /// </summary>
    [Theory]
    [InlineData(0.6f)]
    [InlineData(1.3f)]
    public void RetailFactory_CarriesTheBodysOwnSizeToTheDollAndItsPose(float objectScale)
    {
        var mesh = new MeshRef(0x01000001u, Matrix4x4.Identity);
        var entities = new RecordingEntityLookup
        {
            Entities = { [10u] = CreatePlayer(0x02000001u, mesh) },
            Scales = { [10u] = objectScale },
        };
        var identity = new LocalPlayerIdentityState { ServerGuid = 10u };
        var pose = new RecordingPoseApplicator();
        var factory = new RetailPaperdollDollFactory(entities, identity, pose);

        Assert.True(factory.TryBuild(12u, out WorldEntity? doll));

        Assert.NotNull(doll);
        Assert.Equal(objectScale, doll!.Scale);
        Assert.Equal([objectScale], pose.Scales);
    }

    /// <summary>A body of ordinary size is left exactly as it was.</summary>
    [Fact]
    public void RetailFactory_ADefaultSizedBodyIsUnchanged()
    {
        var mesh = new MeshRef(0x01000001u, Matrix4x4.Identity);
        var entities = new RecordingEntityLookup
        {
            Entities = { [10u] = CreatePlayer(0x02000001u, mesh) },
        };
        var identity = new LocalPlayerIdentityState { ServerGuid = 10u };
        var pose = new RecordingPoseApplicator();
        var factory = new RetailPaperdollDollFactory(entities, identity, pose);

        Assert.True(factory.TryBuild(1u, out WorldEntity? doll));

        Assert.Equal(1f, doll!.Scale);
        Assert.Equal([1f], pose.Scales);
    }

    /// <summary>
    /// A part on a body that wears a size of its own has to grow or shrink with
    /// that body and stay where it belongs on it, so the same factor applies
    /// twice: to the part's own size, and to how far from the body's centre the
    /// part sits. Scaling only the part would leave the limbs detached.
    /// </summary>
    [Theory]
    [InlineData(0.6f)]
    [InlineData(1.3f)]
    public void ScaledPartPlacement_ScalesBothThePartAndItsOffsetFromTheBody(
        float objectScale)
    {
        Vector3 partSize = new(1.1f, 0.9f, 1.4f);
        Vector3 origin = new(0.25f, -0.5f, 1.75f);
        Quaternion orientation = Quaternion.CreateFromYawPitchRoll(0.3f, -0.4f, 0.2f);

        Matrix4x4 plain = RetailHeldPose.ComposePartTransform(
            partSize, origin, orientation);
        Matrix4x4 scaled = RetailHeldPose.ComposePartTransform(
            partSize, origin, orientation, objectScale);

        AssertClose(plain.Translation * objectScale, scaled.Translation);
        for (int row = 0; row < 3; row++)
            AssertClose(BasisRow(plain, row) * objectScale, BasisRow(scaled, row));
    }

    /// <summary>A size of one leaves the placement untouched, bit for bit.</summary>
    [Fact]
    public void UnscaledPartPlacement_IsIdenticalToThePlacementWithNoSizeAtAll()
    {
        Vector3 partSize = new(1.1f, 0.9f, 1.4f);
        Vector3 origin = new(0.25f, -0.5f, 1.75f);
        Quaternion orientation = Quaternion.CreateFromYawPitchRoll(0.3f, -0.4f, 0.2f);

        Assert.Equal(
            RetailHeldPose.ComposePartTransform(partSize, origin, orientation),
            RetailHeldPose.ComposePartTransform(partSize, origin, orientation, 1f));
    }

    internal static Vector3 BasisRow(Matrix4x4 transform, int row) => row switch
    {
        0 => new Vector3(transform.M11, transform.M12, transform.M13),
        1 => new Vector3(transform.M21, transform.M22, transform.M23),
        _ => new Vector3(transform.M31, transform.M32, transform.M33),
    };

    internal static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(
            (expected - actual).Length() <= 1e-5f,
            $"expected {expected} but got {actual}.");
    }

    private static WorldEntity CreatePlayer(uint setupId, MeshRef mesh) => new()
    {
        Id = setupId,
        SourceGfxObjOrSetupId = setupId,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = new List<MeshRef> { mesh },
    };

    private sealed class RecordingRenderer : IPaperdollDollRenderer
    {
        public uint TextureHandle { get; set; }
        public List<WorldEntity?> Dolls { get; } = [];
        public List<(int Width, int Height)> RenderSizes { get; } = [];
        public int RenderCount => RenderSizes.Count;
        public int PrepareCount { get; private set; }

        public List<uint> Heritages { get; } = [];

        public void SetHeritage(uint heritageId) => Heritages.Add(heritageId);

        public void SetDoll(WorldEntity? doll) => Dolls.Add(doll);

        public void Prepare() => PrepareCount++;

        public uint Render(int width, int height)
        {
            RenderSizes.Add((width, height));
            return TextureHandle;
        }
    }

    private sealed class RecordingView : IPaperdollFrameView
    {
        public bool Visible { get; init; } = true;
        public int Width { get; init; } = 100;
        public int Height { get; init; } = 120;
        public List<uint> TextureHandles { get; } = [];

        public bool TryGetVisibleSize(out int width, out int height)
        {
            width = Width;
            height = Height;
            return Visible;
        }

        public void SetTextureHandle(uint textureHandle) =>
            TextureHandles.Add(textureHandle);

        public int ClearCount { get; private set; }

        public void ClearTextureHandle() => ClearCount++;
    }

    private sealed class RecordingFactory : IPaperdollDollFactory
    {
        public bool CanBuild { get; set; } = true;
        public WorldEntity? Doll { get; set; }
        public int BuildCount { get; private set; }

        public List<uint> Heritages { get; } = [];

        public bool TryBuild(uint heritageId, out WorldEntity? doll)
        {
            BuildCount++;
            Heritages.Add(heritageId);
            doll = Doll;
            return CanBuild;
        }
    }

    private sealed class RecordingEntityLookup : IPaperdollEntityLookup
    {
        public Dictionary<uint, WorldEntity> Entities { get; } = [];
        public List<uint> RequestedGuids { get; } = [];

        public Dictionary<uint, float> Scales { get; } = [];

        public bool TryGet(uint serverGuid, out WorldEntity player, out float objectScale)
        {
            RequestedGuids.Add(serverGuid);
            objectScale = Scales.TryGetValue(serverGuid, out float found) ? found : 1f;
            return Entities.TryGetValue(serverGuid, out player!);
        }
    }

    private sealed class RecordingPoseApplicator : IPaperdollPoseApplicator
    {
        public List<(WorldEntity Doll, uint SetupId, uint HeritageId)> Applications { get; } = [];

        public List<float> Scales { get; } = [];

        public void Apply(WorldEntity doll, uint setupId, uint heritageId, float objectScale)
        {
            Applications.Add((doll, setupId, heritageId));
            Scales.Add(objectScale);
        }
    }

    private sealed class RecordingHeritageSource : IPaperdollHeritageSource
    {
        public uint HeritageGroup { get; set; }
    }
}
