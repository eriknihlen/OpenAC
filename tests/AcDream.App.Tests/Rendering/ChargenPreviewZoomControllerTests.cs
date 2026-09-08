using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.CharGen;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class ChargenPreviewZoomControllerTests
{
    private static ChargenPreviewAnimator MakeAnimator()
    {
        const uint gfxObjId = 0x0100_0001u;
        var restMeshRefs = new System.Collections.Generic.List<MeshRef>
        {
            new(gfxObjId, Matrix4x4.CreateTranslation(new Vector3(99f, 99f, 99f))),
        };
        var drawableParts = new System.Collections.Generic.List<ChargenPreviewDrawablePart>
        {
            new(SetupPartIndex: 0, GfxObjId: gfxObjId, DefaultScale: Vector3.One, SurfaceOverrides: null),
        };
        var anim = new Animation();
        var pf0 = new AnimationFrame(1);
        pf0.Frames.Add(new Frame { Origin = new Vector3(1f, 0f, 0f), Orientation = Quaternion.Identity });
        anim.PartFrames.Add(pf0);
        var entity = new WorldEntity
        {
            Id = ChargenPreviewEntityBuilder.PreviewRenderId,
            ServerGuid = ChargenPreviewEntityBuilder.PreviewServerGuid,
            SourceGfxObjOrSetupId = 0x0200_0001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = restMeshRefs,
        };
        var build = new ChargenPreviewAnimatedBuild
        {
            Entity = entity,
            DrawableParts = drawableParts,
            RestMeshRefs = restMeshRefs,
            IdleAnimation = anim,
            IdleLowFrame = 0,
            IdleHighFrame = 0,
        };
        return new ChargenPreviewAnimator(build);
    }

    [Fact]
    public void Constructor_NullAnimator_Throws()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        Assert.Throws<ArgumentNullException>(
            () => new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, animator: null!));
    }

    [Fact]
    public void IsZoomedIn_ReadsThroughToTheAnimator_NoIndependentState()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        var animator = MakeAnimator();
        var controller = new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, animator);

        Assert.False(controller.IsZoomedIn);

        animator.SetZoomedIn(true);
        Assert.True(controller.IsZoomedIn);
    }

    [Fact]
    public void ZoomIn_StartsATweenTowardTheDefaultEye_AndMarksZoomedIn()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        camera.Eye = ChargenPreviewCamera.ResolveZoomedOutEye((uint)ChargenHeritageGroup.Aluvian);
        var controller = new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, MakeAnimator());

        controller.ZoomIn();

        Assert.True(controller.IsZoomedIn);
        // Tween in progress — eye hasn't jumped yet (Tick hasn't run).
        Assert.Equal(ChargenPreviewCamera.ResolveZoomedOutEye((uint)ChargenHeritageGroup.Aluvian), camera.Eye);
    }

    [Fact]
    public void ZoomIn_WhileAlreadyZoomedIn_IsANoOp()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        camera.Eye = ChargenPreviewCamera.ResolveZoomedOutEye((uint)ChargenHeritageGroup.Aluvian);
        var controller = new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, MakeAnimator());
        controller.ZoomIn(); // real tween: zoomed-out eye -> default eye.
        controller.Tick(10.0);
        controller.Tick(10.0 + ChargenPreviewCamera.ZoomTweenDurationSeconds + 1.0);
        Vector3 eyeAfterCompletion = camera.Eye;

        controller.ZoomIn();
        controller.Tick(9999.0); // if ZoomIn wrongly armed a tween, this would move the eye.

        Assert.Equal(eyeAfterCompletion, camera.Eye);
    }

    [Fact]
    public void ZoomOut_WhileNotZoomedIn_IsANoOp()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        Vector3 startEye = camera.Eye;
        var controller = new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, MakeAnimator());

        controller.ZoomOut();

        Assert.False(controller.IsZoomedIn);
        Assert.Equal(startEye, camera.Eye);
    }

    [Fact]
    public void Tick_LinearlyInterpolatesTheEye_HalfwayAtHalfTheDuration()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        Vector3 startEye = camera.Eye;
        var controller = new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, MakeAnimator());
        controller.ZoomIn(); // reach the "zoomed in" state (zero-distance tween — Eye already there).
        Vector3 targetEye = ChargenPreviewCamera.ResolveZoomedOutEye((uint)ChargenHeritageGroup.Aluvian);
        controller.ZoomOut(); // NOW arms a real tween: default eye -> zoomed-out eye.

        controller.Tick(100.0); // seeds the tween's own start time (first tick of a fresh -0.1 sentinel).
        controller.Tick(100.0 + ChargenPreviewCamera.ZoomTweenDurationSeconds / 2.0);

        Vector3 expectedHalfway = Vector3.Lerp(startEye, targetEye, 0.5f);
        Assert.Equal(expectedHalfway.X, camera.Eye.X, 3);
        Assert.Equal(expectedHalfway.Y, camera.Eye.Y, 3);
        Assert.Equal(expectedHalfway.Z, camera.Eye.Z, 3);
    }

    [Fact]
    public void Tick_PastTheFullDuration_ClampsExactlyToTheTargetEye_AndStopsAnimating()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        var controller = new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, MakeAnimator());
        controller.ZoomIn(); // reach "zoomed in" (zero-distance).
        Vector3 targetEye = ChargenPreviewCamera.ResolveZoomedOutEye((uint)ChargenHeritageGroup.Aluvian);
        controller.ZoomOut(); // arms the real tween toward targetEye.

        controller.Tick(0.0);
        controller.Tick(100.0); // way past the 0.6s duration.

        Assert.Equal(targetEye, camera.Eye);

        Vector3 eyeAfterCompletion = camera.Eye;
        controller.Tick(200.0); // tween finished — further ticks must not move the eye.
        Assert.Equal(eyeAfterCompletion, camera.Eye);
    }

    [Fact]
    public void ZoomIn_ImmediatelyFreezesTheAnimatorToTheRestPose_BeforeTheTweenCompletes()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        var animator = MakeAnimator();
        var controller = new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, animator);

        controller.ZoomIn();

        Assert.True(animator.IsZoomedIn);
        Assert.Equal(new Vector3(99f, 99f, 99f), animator.Entity.MeshRefs[0].PartTransform.Translation);
    }

    [Fact]
    public void ZoomOut_ImmediatelyResumesTheAnimatorsIdleLoop()
    {
        var camera = new ChargenPreviewCamera((uint)ChargenHeritageGroup.Aluvian);
        var animator = MakeAnimator();
        var controller = new ChargenPreviewZoomController((uint)ChargenHeritageGroup.Aluvian, camera, animator);
        controller.ZoomIn();

        controller.ZoomOut();

        Assert.False(animator.IsZoomedIn);
        Assert.Equal(new Vector3(1f, 0f, 0f), animator.Entity.MeshRefs[0].PartTransform.Translation);
    }
}
