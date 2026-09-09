using System;
using System.Collections.Generic;

namespace AcDream.App.UI.Layout;

public static class UiMediaClock
{
    public static double Seconds { get; private set; }

    public static void Advance(double deltaSeconds)
    {
        if (double.IsFinite(deltaSeconds) && deltaSeconds > 0d)
            Seconds += deltaSeconds;
    }

    /// <summary>Test seam: rewind to a known point.</summary>
    internal static void ResetForTest() => Seconds = 0d;
}

public static class UiMediaSequence
{
    private const int MaximumSteps = 512;

    /// <summary>
    /// Whether <paramref name="steps"/> does anything over time. A state whose
    /// media is a single image is NOT an animation and must keep the ordinary
    /// still-frame path.
    /// </summary>
    public static bool IsAnimated(IReadOnlyList<UiMediaStep>? steps)
    {
        if (steps is null || steps.Count < 2)
            return false;

        int images = 0;
        foreach (UiMediaStep step in steps)
        {
            switch (step.Kind)
            {
                case UiMediaStepKind.Pause:
                case UiMediaStepKind.Jump:
                case UiMediaStepKind.State:
                    return true;
                case UiMediaStepKind.Image when ++images > 1:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The frame showing at <paramref name="elapsedSeconds"/>, and the state
    /// the sequence hands off to if it has reached its end.
    /// </summary>
    /// <returns>
    /// <c>File</c> is 0 when no image has been reached yet.
    /// <c>TransitionState</c> is null until a terminal State step is due.
    /// </returns>
    public static (uint File, uint? TransitionState) Sample(
        IReadOnlyList<UiMediaStep>? steps,
        float elapsedSeconds)
    {
        if (steps is null || steps.Count == 0)
            return (0u, null);

        uint file = 0u;
        float at = 0f;
        int cursor = 0;

        for (int guard = 0; guard < MaximumSteps; guard++)
        {
            if (cursor < 0 || cursor >= steps.Count)
                return (file, null);

            UiMediaStep step = steps[cursor];
            switch (step.Kind)
            {
                case UiMediaStepKind.Image:
                    file = step.File;
                    cursor++;
                    break;

                case UiMediaStepKind.Pause:
                    at += Math.Max(0f, step.MinDuration);
                    if (elapsedSeconds < at)
                        return (file, null);      // still holding this frame
                    cursor++;
                    break;

                case UiMediaStepKind.Jump:
                    if (step.Probability >= 1f)
                        cursor = (int)step.JumpIndex;
                    else
                        cursor++;
                    break;

                case UiMediaStepKind.State:
                    return (file, step.Probability >= 1f ? step.JumpIndex : null);

                default:
                    cursor++;
                    break;
            }
        }

        return (file, null);
    }
}
