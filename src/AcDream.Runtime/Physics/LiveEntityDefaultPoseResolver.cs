using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Physics;

internal sealed class LiveEntityDefaultPoseResolver
{
    private readonly Func<uint, MotionTable?> _loadMotionTable;
    private readonly IAnimationLoader _animationLoader;
    private readonly bool _dumpMotion;

    public LiveEntityDefaultPoseResolver(
        Func<uint, MotionTable?> loadMotionTable,
        IAnimationLoader animationLoader,
        bool dumpMotion)
    {
        _loadMotionTable = loadMotionTable
            ?? throw new ArgumentNullException(nameof(loadMotionTable));
        _animationLoader = animationLoader
            ?? throw new ArgumentNullException(nameof(animationLoader));
        _dumpMotion = dumpMotion;
    }

    public IReadOnlyList<Frame>? Resolve(uint motionTableId, int partCount)
    {
        if (motionTableId == 0u || partCount == 0)
            return null;

        MotionTable? motionTable = _loadMotionTable(motionTableId);
        if (motionTable is null)
            return null;

        IReadOnlyList<Frame>? pose = MotionTablePose.DefaultStatePartFrames(
            motionTable,
            _animationLoader.LoadAnimation);

        if (_dumpMotion)
        {
            string description = pose is null
                ? "null->placement-fallback"
                : FormattableString.Invariant(
                    $"part0=({pose[0].Origin.X:F2},{pose[0].Origin.Y:F2},{pose[0].Origin.Z:F2})");
            Console.WriteLine(FormattableString.Invariant(
                $"[shape-pose] mt=0x{motionTableId:X8} parts={partCount} {description}"));
        }

        return pose;
    }
}
