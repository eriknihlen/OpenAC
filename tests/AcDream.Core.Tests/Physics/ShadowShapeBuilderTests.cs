using System;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ShadowShapeBuilderTests
{
    private static Setup CreateDoorSetup()
    {
        var setup = new Setup
        {
            Radius         = 0.141f,
            Height         = 0.200f,
            StepUpHeight   = 0.090f,
            StepDownHeight = 0.090f,
            Parts          = { 0x010044B5u, 0x010044B6u, 0x010044B6u },
            Spheres        =
            {
                new Sphere { Radius = 0.100f, Origin = new Vector3(0f, 0f, 0.018f) }
            },
            PlacementFrames =
            {
                [Placement.Default] = new AnimationFrame(3)
                {
                    Frames =
                    {
                        new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
                        new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
                        new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity }
                    }
                }
            }
        };
        return setup;
    }

    [Fact]
    public void FromSetup_DoorSetup_EmitsBspPartsOnly()
    {
        var setup = CreateDoorSetup();
        Func<uint, bool> hasBsp = id => id == 0x010044B5u || id == 0x010044B6u;

        var shapes = ShadowShapeBuilder.FromSetup(setup, entScale: 1.0f, hasBsp);

        Assert.Equal(3, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(ShadowCollisionType.BSP, s.CollisionType));
        Assert.DoesNotContain(shapes, s => s.CollisionType == ShadowCollisionType.Sphere);
        Assert.DoesNotContain(shapes, s => s.CollisionType == ShadowCollisionType.Cylinder);
    }

    [Fact]
    public void FromSetup_DoorSetup_SphereAtExpectedLocalOffset()
    {
        var setup = CreateDoorSetup();
        var shapes = ShadowShapeBuilder.FromSetup(setup, 1.0f, _ => false);

        var sphereShape = Assert.Single(shapes);
        Assert.Equal(ShadowCollisionType.Sphere, sphereShape.CollisionType);
        Assert.Equal(0f,     sphereShape.LocalPosition.X, 4);
        Assert.Equal(0f,     sphereShape.LocalPosition.Y, 4);
        Assert.Equal(0.018f, sphereShape.LocalPosition.Z, 4);
        Assert.Equal(0.100f, sphereShape.Radius, 4);
        Assert.Equal(0f, sphereShape.CylHeight, 4);
    }

    [Fact]
    public void FromSetup_DispatchGateReadsTheEffectivePartIdentities()
    {
        const uint basePart = 0x010044B5u;
        const uint replacementWithBsp = 0x0100AA01u;
        var setup = new Setup
        {
            Parts      = { basePart },
            CylSpheres = { new CylSphere { Radius = 0.4f, Height = 1.2f, Origin = Vector3.Zero } },
        };
        Func<uint, bool> hasBsp = id => id == replacementWithBsp;

        var swapped = ShadowShapeBuilder.FromSetup(
            setup, 1.0f, hasBsp, effectivePartGfxObjIds: [replacementWithBsp]);
        var unswapped = ShadowShapeBuilder.FromSetup(setup, 1.0f, hasBsp);

        ShadowShape swappedShape = Assert.Single(swapped);
        Assert.Equal(ShadowCollisionType.BSP, swappedShape.CollisionType);
        Assert.Equal(replacementWithBsp, swappedShape.GfxObjId);

        ShadowShape unswappedShape = Assert.Single(unswapped);
        Assert.Equal(ShadowCollisionType.Cylinder, unswappedShape.CollisionType);
        Assert.Equal(0.4f, unswappedShape.Radius, 4);
    }

    [Fact]
    public void FromSetup_PartWithoutBsp_SkipsBspShape()
    {
        var setup = CreateDoorSetup();
        Func<uint, bool> hasBsp = id => id == 0x010044B5u;

        var shapes = ShadowShapeBuilder.FromSetup(setup, 1.0f, hasBsp);

        int bspCount = 0;
        foreach (var s in shapes)
            if (s.CollisionType == ShadowCollisionType.BSP) bspCount++;
        Assert.Equal(1, bspCount);
    }

    [Fact]
    public void FromSetup_EffectivePartIdentitiesControlPhysicsBspSelection()
    {
        const uint replacementWithBsp = 0x0100AA01u;
        const uint replacementWithoutBsp = 0x0100AA02u;
        var setup = new Setup
        {
            Parts = { 0x010044B5u, 0x010044B6u },
        };

        var shapes = ShadowShapeBuilder.FromSetup(
            setup,
            entScale: 1f,
            hasPhysicsBsp: id => id == replacementWithBsp,
            effectivePartGfxObjIds: [replacementWithBsp, replacementWithoutBsp]);

        ShadowShape shape = Assert.Single(shapes);
        Assert.Equal(replacementWithBsp, shape.GfxObjId);
    }

    [Fact]
    public void FromSetup_CreatureWithCylSpheres_OnlyEmitsCylinders()
    {
        var setup = new Setup
        {
            Parts      = { 0x02000001u },
            CylSpheres =
            {
                new CylSphere { Radius = 0.40f, Height = 1.20f, Origin = new Vector3(0, 0, 0.6f) }
            },
            Spheres =
            {
                new Sphere { Radius = 0.50f, Origin = new Vector3(0, 0, 0.7f) }
            }
        };

        var shapes = ShadowShapeBuilder.FromSetup(setup, 1.0f, _ => false);

        Assert.Single(shapes);
        Assert.Equal(ShadowCollisionType.Cylinder, shapes[0].CollisionType);
        Assert.Equal(0.40f, shapes[0].Radius, 3);
        Assert.Equal(1.20f, shapes[0].CylHeight, 3);
    }

    [Fact]
    public void FromSetup_ScaleFactor_MultipliesAllRadiiAndOffsets()
    {
        var sphereShape = Assert.Single(
            ShadowShapeBuilder.FromSetup(CreateDoorSetup(), entScale: 2.0f, _ => false));
        Assert.Equal(ShadowCollisionType.Sphere, sphereShape.CollisionType);
        Assert.Equal(2.0f,   sphereShape.Scale,            3);
        Assert.Equal(0.200f, sphereShape.Radius,           3);   // 0.100 * 2
        Assert.Equal(0.036f, sphereShape.LocalPosition.Z,  3);   // 0.018 * 2

        var cylSetup = new Setup
        {
            CylSpheres =
            {
                new CylSphere { Radius = 0.40f, Height = 1.20f, Origin = new Vector3(0.1f, 0.2f, 0.6f) }
            }
        };
        var cylShape = Assert.Single(
            ShadowShapeBuilder.FromSetup(cylSetup, entScale: 2.0f, _ => false));
        Assert.Equal(ShadowCollisionType.Cylinder, cylShape.CollisionType);
        Assert.Equal(2.0f,   cylShape.Scale,            3);
        Assert.Equal(0.800f, cylShape.Radius,           3);   // 0.40 * 2
        Assert.Equal(2.400f, cylShape.CylHeight,        3);   // 1.20 * 2
        Assert.Equal(0.200f, cylShape.LocalPosition.X,  3);
        Assert.Equal(0.400f, cylShape.LocalPosition.Y,  3);
        Assert.Equal(1.200f, cylShape.LocalPosition.Z,  3);
    }

    [Fact]
    public void FromSetup_EmptySetup_ReturnsEmptyList()
    {
        var setup = new Setup();

        var shapes = ShadowShapeBuilder.FromSetup(setup, 1.0f, _ => true);

        Assert.Empty(shapes);
    }

    [Fact]
    public void FromSetup_NullSetup_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShadowShapeBuilder.FromSetup(null!, 1.0f, _ => true));
    }
}
