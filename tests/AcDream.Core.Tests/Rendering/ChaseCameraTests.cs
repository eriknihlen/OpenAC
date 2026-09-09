using System;
using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.Core.Tests.Rendering;

public class ChaseCameraTests
{
    [Fact]
    public void Position_BehindPlayer_WhenYawIsZero()
    {
        var camera = new ChaseCamera { Aspect = 16f / 9f };
        camera.Update(playerPosition: Vector3.Zero, playerYaw: 0f);

        Assert.True(camera.Position.X < -1f, $"Camera X={camera.Position.X} should be behind player (negative X)");
        Assert.True(camera.Position.Z > 0f, $"Camera Z={camera.Position.Z} should be above player");
    }

    [Fact]
    public void Position_BehindPlayer_WhenYawIsHalfPi()
    {
        var camera = new ChaseCamera { Aspect = 16f / 9f };
        camera.Update(playerPosition: Vector3.Zero, playerYaw: MathF.PI / 2f);

        Assert.True(camera.Position.Y < -1f, $"Camera Y={camera.Position.Y} should be behind player (negative Y)");
    }

    [Fact]
    public void Position_FollowsPlayerPosition()
    {
        var camera = new ChaseCamera { Aspect = 16f / 9f };
        var playerPos = new Vector3(100f, 200f, 50f);
        camera.Update(playerPosition: playerPos, playerYaw: 0f);

        Assert.InRange(camera.Position.X, 85f, 100f);
        Assert.InRange(camera.Position.Y, 195f, 205f);  // roughly same Y
    }

    [Fact]
    public void PitchAdjustment_ChangesHeight()
    {
        var camera = new ChaseCamera { Aspect = 16f / 9f };
        camera.Update(playerPosition: Vector3.Zero, playerYaw: 0f);
        float z1 = camera.Position.Z;

        camera.AdjustPitch(0.2f);
        camera.Update(playerPosition: Vector3.Zero, playerYaw: 0f);
        float z2 = camera.Position.Z;

        Assert.True(z2 > z1, "Increasing pitch should raise the camera");
    }
}
