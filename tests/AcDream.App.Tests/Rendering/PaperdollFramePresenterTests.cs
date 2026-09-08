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
        var presenter = new PaperdollFramePresenter(renderer, view, factory);

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
        var presenter = new PaperdollFramePresenter(renderer, view, factory);

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
        var presenter = new PaperdollFramePresenter(renderer, view, factory);

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
        var presenter = new PaperdollFramePresenter(renderer, view, factory);

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
        var presenter = new PaperdollFramePresenter(renderer, view, factory);

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
        var presenter = new PaperdollFramePresenter(renderer, view, factory);

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
        var presenter = new PaperdollFramePresenter(renderer, view, factory);

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
        var presenter = new PaperdollFramePresenter(renderer, view, factory);

        Frame(presenter);
        presenter.ResetSession();
        factory.Doll = secondDoll;
        Frame(presenter);

        Assert.False(presenter.IsDirty);
        Assert.Equal(2, factory.BuildCount);
        Assert.Equal([firstDoll, null, secondDoll], renderer.Dolls);
        Assert.Equal(1, view.ClearCount);
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

        Assert.False(factory.TryBuild(out WorldEntity? missing));
        Assert.Null(missing);

        entities.Entities[7u] = CreateDoll();
        Assert.False(factory.TryBuild(out WorldEntity? empty));
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

        Assert.True(factory.TryBuild(out WorldEntity? firstDoll));
        identity.ServerGuid = 20u;
        Assert.True(factory.TryBuild(out WorldEntity? secondDoll));

        Assert.NotNull(firstDoll);
        Assert.NotNull(secondDoll);
        Assert.Equal(0x02000001u, firstDoll.SourceGfxObjOrSetupId);
        Assert.Equal(0x02000002u, secondDoll.SourceGfxObjOrSetupId);
        Assert.NotSame(first.MeshRefs, firstDoll.MeshRefs);
        Assert.Equal(firstMesh.GfxObjId, firstDoll.MeshRefs[0].GfxObjId);
        Assert.Same(firstMesh.SurfaceOverrides, firstDoll.MeshRefs[0].SurfaceOverrides);
        Assert.Equal(
            [(firstDoll, 0x02000001u), (secondDoll, 0x02000002u)],
            pose.Applications);
        Assert.Equal([10u, 20u], entities.RequestedGuids);
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

        public bool TryBuild(out WorldEntity? doll)
        {
            BuildCount++;
            doll = Doll;
            return CanBuild;
        }
    }

    private sealed class RecordingEntityLookup : IPaperdollEntityLookup
    {
        public Dictionary<uint, WorldEntity> Entities { get; } = [];
        public List<uint> RequestedGuids { get; } = [];

        public bool TryGet(uint serverGuid, out WorldEntity player)
        {
            RequestedGuids.Add(serverGuid);
            return Entities.TryGetValue(serverGuid, out player!);
        }
    }

    private sealed class RecordingPoseApplicator : IPaperdollPoseApplicator
    {
        public List<(WorldEntity Doll, uint SetupId)> Applications { get; } = [];

        public void Apply(WorldEntity doll, uint setupId) =>
            Applications.Add((doll, setupId));
    }
}
