using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal sealed class ChargenPreviewAnimator
{
    public const float IdleFramerate = 30f;

    private readonly ChargenPreviewAnimatedBuild _build;
    private float _currFrame;
    private bool _zoomedIn;

    private readonly List<MeshRef> _meshRefsBufferA = [];
    private readonly List<MeshRef> _meshRefsBufferB = [];
    private bool _nextBufferIsA = true;

    public ChargenPreviewAnimator(ChargenPreviewAnimatedBuild build)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _currFrame = build.IdleLowFrame;
        if (build.IdleAnimation is not null)
            ApplyIdleFrame();
        // Else: Entity.MeshRefs already holds RestMeshRefs (set by
        // TryBuildAnimated) as the best available fallback.
    }

    public WorldEntity Entity => _build.Entity;

    public bool IsZoomedIn => _zoomedIn;

    public void SetZoomedIn(bool zoomedIn)
    {
        if (_zoomedIn == zoomedIn)
            return;
        _zoomedIn = zoomedIn;
        if (zoomedIn)
        {
            _build.Entity.MeshRefs = _build.RestMeshRefs;
        }
        else
        {
            _currFrame = _build.IdleLowFrame;
            if (_build.IdleAnimation is not null)
                ApplyIdleFrame();
        }
    }

    public void Tick(float elapsedSeconds)
    {
        if (_zoomedIn || _build.IdleAnimation is null || elapsedSeconds <= 0f)
            return;

        _currFrame = RetailAnimationCyclePlayback.Advance(
            _currFrame, _build.IdleLowFrame, _build.IdleHighFrame, IdleFramerate, elapsedSeconds);
        ApplyIdleFrame();
    }

    private void ApplyIdleFrame()
    {
        DatReaderWriter.DBObjs.Animation animation = _build.IdleAnimation!;
        IReadOnlyList<ChargenPreviewDrawablePart> parts = _build.DrawableParts;
        List<MeshRef> meshRefs = _nextBufferIsA ? _meshRefsBufferA : _meshRefsBufferB;
        _nextBufferIsA = !_nextBufferIsA;
        meshRefs.Clear();
        foreach (ChargenPreviewDrawablePart part in parts)
        {
            bool resolved = RetailAnimationCyclePlayback.TryInterpolatePart(
                animation, _currFrame, _build.IdleLowFrame, _build.IdleHighFrame,
                part.SetupPartIndex, out Vector3 origin, out Quaternion orientation);
            if (!resolved)
            {
                origin = Vector3.Zero;
                orientation = Quaternion.Identity;
            }
            Matrix4x4 transform = RetailHeldPose.ComposePartTransform(part.DefaultScale, origin, orientation);
            meshRefs.Add(new MeshRef(part.GfxObjId, transform) { SurfaceOverrides = part.SurfaceOverrides });
        }
        _build.Entity.MeshRefs = meshRefs;
    }
}
