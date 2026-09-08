using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ShadowShapeBuilderShapeSourceTests
{
    [Fact]
    public void Setup_WithNoCylSpheres_NoSpheres_NoPhysicsBspParts_YieldsEmptyShapeList()
    {
        var setup = new Setup
        {
            CylSpheres      = new List<CylSphere>(),
            Spheres         = new List<Sphere>(),
            Parts           = { 0x01000ABCu },
            PlacementFrames = new Dictionary<Placement, AnimationFrame>(),
        };
        var shapes = ShadowShapeBuilder.FromSetup(setup, entScale: 1f, hasPhysicsBsp: _ => false);
        Assert.Empty(shapes);
    }

    [Fact]
    public void Setup_WithBspPart_NoCylSpheres_EmitsBspShape()
    {
        const uint BspGfxObjId = 0x0100AAAAu;
        var setup = new Setup
        {
            CylSpheres      = new List<CylSphere>(),
            Spheres         = new List<Sphere>(),
            Parts           = { BspGfxObjId },
            PlacementFrames = new Dictionary<Placement, AnimationFrame>(),
        };
        var shapes = ShadowShapeBuilder.FromSetup(setup, entScale: 1f,
            hasPhysicsBsp: id => id == BspGfxObjId);

        Assert.Contains(shapes, s => s.CollisionType == ShadowCollisionType.BSP);
    }

    [Fact]
    public void Setup_WithPartButNoBsp_NoCylSpheres_YieldsEmptyShapeList()
    {
        const uint NoBspGfxObjId = 0x0100BBBBu;
        var setup = new Setup
        {
            CylSpheres      = new List<CylSphere>(),
            Spheres         = new List<Sphere>(),
            Parts           = { NoBspGfxObjId },
            PlacementFrames = new Dictionary<Placement, AnimationFrame>(),
        };
        var shapes = ShadowShapeBuilder.FromSetup(setup, entScale: 1f,
            hasPhysicsBsp: _ => false);

        Assert.Empty(shapes);
    }

    [Fact]
    public void Setup_WithBodySpheres_NoCylSpheres_EmitsSphereShapes()
    {
        var setup = new Setup
        {
            CylSpheres = new List<CylSphere>(),
            Spheres = new List<Sphere>
            {
                new Sphere { Origin = new Vector3(0f, 0f, 0.475f), Radius = 0.48f },
                new Sphere { Origin = new Vector3(0f, 0f, 1.350f), Radius = 0.48f },
            },
            Parts           = new List<QualifiedDataId<GfxObj>>(),
            PlacementFrames = new Dictionary<Placement, AnimationFrame>(),
        };

        var shapes = ShadowShapeBuilder.FromSetup(setup, entScale: 1f, hasPhysicsBsp: _ => false);

        Assert.Equal(2, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(ShadowCollisionType.Sphere, s.CollisionType));
    }
}
