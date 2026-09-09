using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.Core.Lighting;
using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.Lighting;

public sealed class SceneLightingUboTests
{
    [Fact]
    public void UboLight_StructSize_Is64Bytes()
    {
        // std140 mandates 4× vec4 = 64 bytes. If this drifts the shader
        // will read garbage.
        Assert.Equal(64, Marshal.SizeOf<UboLight>());
    }

    [Fact]
    public void SceneLightingUbo_StructSize_MatchesConstant()
    {
        Assert.Equal(SceneLightingUbo.SizeInBytes, Marshal.SizeOf<SceneLightingUbo>());
    }

    [Fact]
    public void Build_PacksActiveLightsIntoSlotsInOrder()
    {
        var lights = new LightManager();
        lights.Register(new LightSource
        {
            Kind = LightKind.Point,
            WorldPosition = new Vector3(1, 2, 3),
            RankingOrigin = new Vector3(1, 2, 3),
            ColorLinear = new Vector3(1f, 0.5f, 0.25f),
            Intensity = 0.8f,
            Range = 6f,
        });
        lights.Tick(Vector3.Zero);

        var atmo = new AtmosphereSnapshot(
            Kind: WeatherKind.Clear,
            Intensity: 1f,
            FogColor: new Vector3(0.7f, 0.8f, 0.9f),
            FogStart: 100f,
            FogEnd: 400f,
            FogMode: FogMode.Linear,
            LightningFlash: 0f,
            Override: EnvironOverride.None);

        var ubo = SceneLightingUbo.Build(lights, in atmo, new Vector3(10, 20, 30), 0.5f);

        // Light 0 is the slot we populated.
        Assert.Equal(1f, ubo.Light0.PosAndKind.X);
        Assert.Equal(2f, ubo.Light0.PosAndKind.Y);
        Assert.Equal(3f, ubo.Light0.PosAndKind.Z);
        Assert.Equal((float)(int)LightKind.Point, ubo.Light0.PosAndKind.W);
        Assert.Equal(6f, ubo.Light0.DirAndRange.W);
        Assert.Equal(0.8f, ubo.Light0.ColorAndIntensity.W);

        // Unused slots should be zero-packed.
        Assert.Equal(0f, ubo.Light1.DirAndRange.W);

        Assert.Equal(1f, ubo.CellAmbient.W);

        // Fog params passed through.
        Assert.Equal(100f, ubo.FogParams.X);
        Assert.Equal(400f, ubo.FogParams.Y);
        Assert.Equal(0f,   ubo.FogParams.Z);  // no flash
        Assert.Equal((float)(int)FogMode.Linear, ubo.FogParams.W);

        Assert.Equal(10f,  ubo.CameraAndTime.X);
        Assert.Equal(0.5f, ubo.CameraAndTime.W);
    }

    [Fact]
    public void Build_ClampsAtEightLights()
    {
        var lights = new LightManager();
        for (int i = 0; i < 20; i++)
        {
            lights.Register(new LightSource
            {
                Kind = LightKind.Point,
                WorldPosition = new Vector3(i, 0, 0),
                RankingOrigin = new Vector3(i, 0, 0),
                Range = 200f,  // all in range
            });
        }
        lights.Tick(Vector3.Zero);

        var atmo = new AtmosphereSnapshot(
            WeatherKind.Clear, 1f, Vector3.Zero, 0, 0, FogMode.Off, 0f, EnvironOverride.None);
        var ubo = SceneLightingUbo.Build(lights, in atmo, Vector3.Zero, 0f);

        Assert.Equal(8f, ubo.CellAmbient.W);
        Assert.NotEqual(0f, ubo.Light7.DirAndRange.W);
    }

    [Fact]
    public void Build_WithSun_SlotZeroIsDirectional()
    {
        var lights = new LightManager();
        lights.Sun = new LightSource
        {
            Kind = LightKind.Directional,
            WorldForward = new Vector3(0, 0, -1),
            ColorLinear  = new Vector3(1f, 0.9f, 0.8f),
            Intensity    = 1.2f,
        };
        lights.Tick(Vector3.Zero);

        var atmo = new AtmosphereSnapshot(
            WeatherKind.Clear, 1f, Vector3.Zero, 0, 0, FogMode.Off, 0f, EnvironOverride.None);
        var ubo = SceneLightingUbo.Build(lights, in atmo, Vector3.Zero, 0f);

        Assert.Equal((float)(int)LightKind.Directional, ubo.Light0.PosAndKind.W);
        Assert.Equal(1.2f, ubo.Light0.ColorAndIntensity.W);
    }
}
