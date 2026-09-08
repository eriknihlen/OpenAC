using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class ChargenPreviewAnimatorTests
{
    private static Animation MakeTwoFrameAnim(Vector3 frame0Origin, Vector3 frame1Origin)
    {
        var anim = new Animation();
        var pf0 = new AnimationFrame(1);
        pf0.Frames.Add(new Frame { Origin = frame0Origin, Orientation = Quaternion.Identity });
        var pf1 = new AnimationFrame(1);
        pf1.Frames.Add(new Frame { Origin = frame1Origin, Orientation = Quaternion.Identity });
        anim.PartFrames.Add(pf0);
        anim.PartFrames.Add(pf1);
        return anim;
    }

    private static ChargenPreviewAnimatedBuild MakeBuild(Animation? idleAnimation, int idleLow = 0, int idleHigh = 1)
    {
        const uint gfxObjId = 0x0100_0001u;
        var restMeshRefs = new List<MeshRef>
        {
            new(gfxObjId, Matrix4x4.CreateTranslation(new Vector3(99f, 99f, 99f))), // distinct from any idle frame, so tests can tell them apart.
        };
        var drawableParts = new List<ChargenPreviewDrawablePart>
        {
            new(SetupPartIndex: 0, GfxObjId: gfxObjId, DefaultScale: Vector3.One, SurfaceOverrides: null),
        };
        var entity = new WorldEntity
        {
            Id = ChargenPreviewEntityBuilder.PreviewRenderId,
            ServerGuid = ChargenPreviewEntityBuilder.PreviewServerGuid,
            SourceGfxObjOrSetupId = 0x0200_0001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = restMeshRefs,
        };
        return new ChargenPreviewAnimatedBuild
        {
            Entity = entity,
            DrawableParts = drawableParts,
            RestMeshRefs = restMeshRefs,
            IdleAnimation = idleAnimation,
            IdleLowFrame = idleLow,
            IdleHighFrame = idleHigh,
        };
    }

    [Fact]
    public void Constructor_WithIdleAnimation_SeedsFrameZeroPose_NotTheRestPose()
    {
        var origin0 = new Vector3(1f, 0f, 0f);
        var origin1 = new Vector3(5f, 0f, 0f);
        var build = MakeBuild(MakeTwoFrameAnim(origin0, origin1));

        var animator = new ChargenPreviewAnimator(build);

        Assert.False(animator.IsZoomedIn);
        Assert.Equal(origin0, animator.Entity.MeshRefs[0].PartTransform.Translation);
    }

    [Fact]
    public void Constructor_WithNoIdleAnimation_KeepsTheRestPoseFallback()
    {
        var build = MakeBuild(idleAnimation: null);

        var animator = new ChargenPreviewAnimator(build);

        Assert.Equal(new Vector3(99f, 99f, 99f), animator.Entity.MeshRefs[0].PartTransform.Translation);
    }

    [Fact]
    public void Tick_AdvancesTheIdleFrame_InterpolatingBetweenFrames()
    {
        var origin0 = new Vector3(0f, 0f, 0f);
        var origin1 = new Vector3(10f, 0f, 0f);
        var build = MakeBuild(MakeTwoFrameAnim(origin0, origin1));
        var animator = new ChargenPreviewAnimator(build);

        animator.Tick(1f / 60f);

        Assert.Equal(5f, animator.Entity.MeshRefs[0].PartTransform.Translation.X, 3);
    }

    [Fact]
    public void SetZoomedIn_True_SwapsToTheFrozenRestPoseImmediately()
    {
        var build = MakeBuild(MakeTwoFrameAnim(new Vector3(1f, 0f, 0f), new Vector3(5f, 0f, 0f)));
        var animator = new ChargenPreviewAnimator(build);

        animator.SetZoomedIn(true);

        Assert.True(animator.IsZoomedIn);
        Assert.Equal(new Vector3(99f, 99f, 99f), animator.Entity.MeshRefs[0].PartTransform.Translation);
    }

    [Fact]
    public void Tick_WhileZoomedIn_DoesNotAdvanceTheFrozenPose()
    {
        var build = MakeBuild(MakeTwoFrameAnim(new Vector3(1f, 0f, 0f), new Vector3(5f, 0f, 0f)));
        var animator = new ChargenPreviewAnimator(build);
        animator.SetZoomedIn(true);

        animator.Tick(10f); // large elapsed time — must still be a no-op while zoomed in.

        Assert.Equal(new Vector3(99f, 99f, 99f), animator.Entity.MeshRefs[0].PartTransform.Translation);
    }

    [Fact]
    public void SetZoomedIn_False_RestartsTheIdleLoopAtFrameZero()
    {
        var origin0 = new Vector3(1f, 0f, 0f);
        var origin1 = new Vector3(5f, 0f, 0f);
        var build = MakeBuild(MakeTwoFrameAnim(origin0, origin1));
        var animator = new ChargenPreviewAnimator(build);

        animator.Tick(1f / 30f); // advance to frame 1.
        animator.SetZoomedIn(true);
        animator.SetZoomedIn(false);

        Assert.Equal(origin0, animator.Entity.MeshRefs[0].PartTransform.Translation);
    }

    [Fact]
    public void SetZoomedIn_SameStateTwice_IsANoOp()
    {
        var build = MakeBuild(MakeTwoFrameAnim(new Vector3(1f, 0f, 0f), new Vector3(5f, 0f, 0f)));
        var animator = new ChargenPreviewAnimator(build);

        animator.Tick(1f / 60f); // partway through frame 0->1.
        Vector3 beforeX = animator.Entity.MeshRefs[0].PartTransform.Translation;
        animator.SetZoomedIn(false); // already not zoomed in — must not restart the loop.

        Assert.Equal(beforeX, animator.Entity.MeshRefs[0].PartTransform.Translation);
    }
}
