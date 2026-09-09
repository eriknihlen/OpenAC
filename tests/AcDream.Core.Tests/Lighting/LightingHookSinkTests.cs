using System.Numerics;
using AcDream.Core.Lighting;
using AcDream.Core.Vfx;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Lighting;

public sealed class LightingHookSinkTests
{
    [Fact]
    public void SetLightHook_FlipsOwnedLights()
    {
        var mgr = new LightManager();
        var sink = new LightingHookSink(mgr, new MutablePoseSource());

        var light1 = new LightSource { Kind = LightKind.Point, OwnerId = 42, IsLit = true, RankingOrigin = Vector3.Zero };
        var light2 = new LightSource { Kind = LightKind.Point, OwnerId = 42, IsLit = true, RankingOrigin = Vector3.Zero };
        var other  = new LightSource { Kind = LightKind.Point, OwnerId = 99, IsLit = true, RankingOrigin = Vector3.Zero };
        sink.RegisterOwnedLight(light1);
        sink.RegisterOwnedLight(light2);
        sink.RegisterOwnedLight(other);

        var hook = new SetLightHook { LightsOn = false };
        sink.OnHook(entityId: 42, entityWorldPosition: Vector3.Zero, hook: hook);

        Assert.False(light1.IsLit);
        Assert.False(light2.IsLit);
        Assert.True(other.IsLit);  // owner 99 untouched
    }

    [Fact]
    public void UnregisterOwner_RemovesAllOwnedLights()
    {
        var mgr = new LightManager();
        var sink = new LightingHookSink(mgr, new MutablePoseSource());

        sink.RegisterOwnedLight(new LightSource { OwnerId = 7, RankingOrigin = Vector3.Zero });
        sink.RegisterOwnedLight(new LightSource { OwnerId = 7, RankingOrigin = Vector3.Zero });
        Assert.Equal(2, mgr.RegisteredCount);

        sink.UnregisterOwner(7);
        Assert.Equal(0, mgr.RegisteredCount);
    }

    [Fact]
    public void UnrelatedHook_Ignored()
    {
        var mgr = new LightManager();
        var sink = new LightingHookSink(mgr, new MutablePoseSource());
        var light = new LightSource { OwnerId = 1, IsLit = true, RankingOrigin = Vector3.Zero };
        sink.RegisterOwnedLight(light);

        var noise = new SoundHook();
        sink.OnHook(entityId: 1, entityWorldPosition: Vector3.Zero, hook: noise);

        Assert.True(light.IsLit);
    }

    [Fact]
    public void RefreshAttachedLights_ComposesLocalFrameWithCurrentRootAndCell()
    {
        var mgr = new LightManager();
        var poses = new MutablePoseSource();
        poses.Publish(42u,
            Matrix4x4.CreateRotationZ(MathF.PI / 2f)
                * Matrix4x4.CreateTranslation(10, 20, 30),
            cellId: 0x01010002u);
        var sink = new LightingHookSink(mgr, poses);
        var light = new LightSource
        {
            OwnerId = 42u,
            RankingOrigin = Vector3.Zero,
            LocalPose = Matrix4x4.CreateTranslation(1, 0, 2),
            TracksOwnerPose = true,
        };
        sink.RegisterOwnedLight(light);

        sink.RefreshAttachedLights();

        Assert.InRange(light.WorldPosition.X, 9.99f, 10.01f);
        Assert.InRange(light.WorldPosition.Y, 20.99f, 21.01f);
        Assert.InRange(light.WorldPosition.Z, 31.99f, 32.01f);
        Assert.Equal(new Vector3(10f, 20f, 30f), light.RankingOrigin);
        Assert.Equal(0x01010002u, light.CellId);
    }

    [Fact]
    public void RefreshAttachedLights_DoesNotRecomposeDatStaticLight()
    {
        var mgr = new LightManager();
        var poses = new MutablePoseSource();
        poses.Publish(42u, Matrix4x4.CreateTranslation(100, 0, 0));
        var sink = new LightingHookSink(mgr, poses);
        var light = new LightSource
        {
            OwnerId = 42u,
            WorldPosition = new Vector3(7, 8, 9),
            RankingOrigin = new Vector3(7, 8, 9),
            LocalPose = Matrix4x4.CreateTranslation(1, 0, 0),
            TracksOwnerPose = false,
        };
        sink.RegisterOwnedLight(light);

        sink.RefreshAttachedLights();

        Assert.Equal(new Vector3(7, 8, 9), light.WorldPosition);
        Assert.Equal(0, poses.RootPoseQueryCount);
    }

    [Fact]
    public void UnregisterPresentation_PreservesSetLightStateForReregistration()
    {
        var mgr = new LightManager();
        var sink = new LightingHookSink(mgr, new MutablePoseSource());
        sink.InitializeOwnerLighting(7u, enabled: true);
        sink.SetOwnerLighting(7u, enabled: false);
        sink.UnregisterOwner(7u, forgetState: false);
        var replacement = new LightSource { OwnerId = 7u, RankingOrigin = Vector3.Zero };

        sink.RegisterOwnedLight(replacement);

        Assert.False(replacement.IsLit);
    }
}

public sealed class LightInfoLoaderTests
{
    [Fact]
    public void Load_EmptyLights_ReturnsEmpty()
    {
        var setup = new DatReaderWriter.DBObjs.Setup();
        var result = LightInfoLoader.Load(setup, 1u, Vector3.Zero, Quaternion.Identity);
        Assert.Empty(result);
    }

    [Fact]
    public void Load_PointLight_ProducesCorrectSource()
    {
        var setup = new DatReaderWriter.DBObjs.Setup();
        setup.Lights[0] = new LightInfo
        {
            ViewSpaceLocation = new Frame
            {
                Origin = new Vector3(1, 2, 3),
                Orientation = Quaternion.Identity,
            },
            Color = new ColorARGB { Red = 255, Green = 200, Blue = 50, Alpha = 255 },
            Intensity = 0.8f,
            Falloff = 8f,
            ConeAngle = 0f,  // point
        };

        var result = LightInfoLoader.Load(setup, ownerId: 77,
            entityPosition: new Vector3(100, 200, 300),
            entityRotation: Quaternion.Identity);

        Assert.Single(result);
        var light = result[0];
        Assert.Equal(LightKind.Point, light.Kind);
        Assert.Equal(77u, light.OwnerId);
        Assert.Equal(10.4f, light.Range, 3);
        Assert.Equal(0.8f, light.Intensity);
        Assert.Equal(new Vector3(101, 202, 303), light.WorldPosition);
        Assert.Equal(new Vector3(100, 200, 300), light.RankingOrigin);
        Assert.Equal(new Vector3(1, 2, 3), light.LocalPose.Translation);
        Assert.InRange(light.ColorLinear.X, 0.99f, 1.01f);
    }

    [Fact]
    public void Load_DynamicLight_UsesRetailHardwareRangeFactor()
    {
        var setup = new DatReaderWriter.DBObjs.Setup();
        setup.Lights[0] = new LightInfo
        {
            ViewSpaceLocation = new Frame { Orientation = Quaternion.Identity },
            Color = new ColorARGB { Red = 255, Green = 255, Blue = 255, Alpha = 255 },
            Intensity = 1f,
            Falloff = 8f,
        };

        var light = Assert.Single(LightInfoLoader.Load(
            setup,
            ownerId: 77u,
            entityPosition: Vector3.Zero,
            entityRotation: Quaternion.Identity,
            isDynamic: true));

        Assert.True(light.IsDynamic);
        Assert.Equal(12f, light.Range, 3);
    }

    [Fact]
    public void Load_NonZeroConeAngle_ProducesSpot()
    {
        var setup = new DatReaderWriter.DBObjs.Setup();
        setup.Lights[0] = new LightInfo
        {
            ViewSpaceLocation = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
            Color = new ColorARGB { Red = 255, Green = 255, Blue = 255, Alpha = 255 },
            Intensity = 1f,
            Falloff = 5f,
            ConeAngle = 0.5f,
        };

        var result = LightInfoLoader.Load(setup, ownerId: 1, entityPosition: Vector3.Zero, entityRotation: Quaternion.Identity);
        Assert.Equal(LightKind.Spot, result[0].Kind);
        Assert.Equal(0.5f, result[0].ConeAngle);
    }
}

file sealed class MutablePoseSource : IEntityEffectPoseSource, IEntityEffectCellSource
{
    private readonly Dictionary<uint, (Matrix4x4 Root, uint Cell)> _poses = new();

    public void Publish(uint id, Matrix4x4 root, uint cellId = 0) =>
        _poses[id] = (root, cellId);

    public int RootPoseQueryCount { get; private set; }

    public bool TryGetRootPose(uint localEntityId, out Matrix4x4 rootWorld)
    {
        RootPoseQueryCount++;
        if (_poses.TryGetValue(localEntityId, out var pose))
        {
            rootWorld = pose.Root;
            return true;
        }
        rootWorld = default;
        return false;
    }

    public bool TryGetPartPose(uint localEntityId, int partIndex, out Matrix4x4 partLocal)
    {
        partLocal = default;
        return false;
    }

    public bool TryGetCellId(uint localEntityId, out uint cellId)
    {
        if (_poses.TryGetValue(localEntityId, out var pose))
        {
            cellId = pose.Cell;
            return true;
        }
        cellId = 0;
        return false;
    }
}
