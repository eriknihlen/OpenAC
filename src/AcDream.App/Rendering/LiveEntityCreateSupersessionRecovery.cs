using AcDream.App.World;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering;

internal static class LiveEntityCreateSupersessionRecovery
{
    public static bool TryApply(
        LiveEntityRuntime runtime,
        LiveEntityRecord expectedRecord,
        ulong expectedCreateIntegrationVersion,
        Func<LiveEntityAppearanceUpdateState?> captureAppearance,
        Func<LiveEntityAppearanceUpdateState, bool> publishAppearance,
        Action<LiveEntityAppearanceUpdateState> publishCurrentSnapshot,
        Action<LiveEntityAppearanceUpdateState> synchronizeAnimation)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(expectedRecord);
        ArgumentNullException.ThrowIfNull(captureAppearance);
        ArgumentNullException.ThrowIfNull(publishAppearance);
        ArgumentNullException.ThrowIfNull(publishCurrentSnapshot);
        ArgumentNullException.ThrowIfNull(synchronizeAnimation);

        if (!runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || captureAppearance() is not { } visualUpdate
            || !runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || !publishAppearance(visualUpdate)
            || !runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion))
        {
            return false;
        }

        publishCurrentSnapshot(visualUpdate);
        if (!runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion))
        {
            return false;
        }

        synchronizeAnimation(visualUpdate);
        return runtime.IsCurrentCreateIntegration(
            expectedRecord,
            expectedCreateIntegrationVersion);
    }
}

internal static class LiveEntityCreateAnimationSynchronization
{
    public static bool TrySynchronizeInterruptedInitialOwner(
        LiveEntityRecord record,
        LiveEntityAnimationState animation,
        Animation? canonicalAnimation,
        int canonicalLowFrame,
        int canonicalHighFrame,
        float canonicalFramerate,
        MotionTable? motionTable,
        CreateObject.ServerMotionState? wireState)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(animation);
        if (record.InitialHydrationCompleted)
            return false;

        if (canonicalAnimation is not null)
        {
            animation.Animation = canonicalAnimation;
            animation.LowFrame = canonicalLowFrame;
            animation.HighFrame = canonicalHighFrame;
            animation.Framerate = canonicalFramerate;
            animation.CurrFrame = canonicalLowFrame;
        }

        if (animation.Sequencer is { } sequencer && motionTable is not null)
        {
            SpawnMotionInitializer.Reinitialize(
                sequencer,
                motionTable,
                wireState);
        }
        return true;
    }
}
