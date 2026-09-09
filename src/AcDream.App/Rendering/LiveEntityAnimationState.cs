using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering;

internal sealed class LiveEntityAnimationState : ILiveEntityAnimationRuntime
{
    public required WorldEntity Entity;
    WorldEntity ILiveEntityAnimationRuntime.Entity => Entity;
    uint ILiveEntityAnimationRuntime.CurrentMotion => Sequencer?.CurrentMotion ?? 0u;

    public required Setup Setup;
    public required Animation Animation;
    public required int LowFrame;
    public required int HighFrame;
    public required float Framerate;
    public required float Scale;
    public required IReadOnlyList<LiveAnimationPartTemplate> PartTemplate;
    public required IReadOnlyList<bool> PartAvailability;
    public float CurrFrame;
    public AnimationSequencer? Sequencer;

    public IReadOnlyList<PartTransform>? PreparedSequenceFrames;
    public bool SequenceAdvancedBeforeAnimationPass;
    public readonly Frame RootMotionScratch = new();
    public readonly MotionDeltaFrame RootMotionDeltaScratch = new();
    public readonly List<PartTransform> SequenceFramesScratch = new();
    public readonly List<PartTransform> ScheduleFramesScratch = new();

    public readonly List<MeshRef> MeshRefsScratch = new();
    public readonly List<Matrix4x4> EffectPartPosesScratch = new();
    public readonly List<Matrix4x4> VisualPartPosesScratch = new();
    public bool PresentationPosesInitialized;
    public ulong PresentationRevision { get; private set; } = 1UL;

    public double LastSequenceDiagnosticTime;
    public double LastPartDiagnosticTime;

    public void InvalidatePresentationPoses()
    {
        PresentationRevision++;
        if (PresentationRevision == 0UL)
            PresentationRevision++;
        PresentationPosesInitialized = false;
        VisualPartPosesScratch.Clear();
        EffectPartPosesScratch.Clear();
    }

    public IReadOnlyList<PartTransform> CaptureSequenceFrames(
        IReadOnlyList<PartTransform> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SequenceFramesScratch.Clear();
        for (int i = 0; i < source.Count; i++)
            SequenceFramesScratch.Add(source[i]);
        return SequenceFramesScratch;
    }

    public IReadOnlyList<PartTransform> CaptureScheduleFrames(
        IReadOnlyList<PartTransform> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ScheduleFramesScratch.Clear();
        for (int i = 0; i < source.Count; i++)
            ScheduleFramesScratch.Add(source[i]);
        return ScheduleFramesScratch;
    }
}

internal readonly record struct LiveAnimationPartTemplate(
    uint GfxObjId,
    IReadOnlyDictionary<uint, uint>? SurfaceOverrides,
    bool IsDrawable);
