using AcDream.UI.Abstractions.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public sealed class MmbMouseLookTests
{
    private sealed class AdjustmentSink
    {
        public float Total;
        public int   ApplyCount;
        public void Apply(float d) { Total += d; ApplyCount++; }
    }

    [Fact]
    public void Press_ActivatesAndCapturesCursorPosition()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);

        ml.Press(cursorX: 320f, cursorY: 240f, wantCaptureMouse: false);

        Assert.True(ml.Active);
        Assert.Equal(320f, ml.CapturedCursorX);
        Assert.Equal(240f, ml.CapturedCursorY);
    }

    [Fact]
    public void Release_DeactivatesWhenActive()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);
        ml.Press(0f, 0f, wantCaptureMouse: false);

        ml.Release();

        Assert.False(ml.Active);
    }

    [Fact]
    public void Press_WhileImGuiCapturesMouse_DoesNotActivate()
    {
        // Defense in depth — the dispatcher already filters on
        // WantCaptureMouse, but if a binding ever fires through the
        // state machine itself must not turn on.
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);

        ml.Press(0f, 0f, wantCaptureMouse: true);

        Assert.False(ml.Active);
    }

    [Fact]
    public void OnWantCaptureMouseChanged_TrueWhileActive_Deactivates()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);
        ml.Press(0f, 0f, wantCaptureMouse: false);
        Assert.True(ml.Active);

        ml.OnWantCaptureMouseChanged(wantCaptureMouse: true);

        Assert.False(ml.Active);
    }

    [Fact]
    public void OnWantCaptureMouseChanged_FalseWhileInactive_NoOp()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);

        ml.OnWantCaptureMouseChanged(wantCaptureMouse: false);

        Assert.False(ml.Active);
    }

    [Fact]
    public void ApplyDelta_WhileActive_DrivesMovementAdjustmentSink()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply) { SensitivityPerPixel = 0.01f };
        ml.Press(0f, 0f, wantCaptureMouse: false);

        for (int i = 0; i < 6; i++)
            ml.ApplyDelta(dx: 10f, extraSensitivity: 1.0f);

        // Sign: dragging right (positive dx) requests TurnRight, represented
        // by a negative adjustment. Magnitude: 10 * 0.01 * 1.0 = 0.1.
        Assert.Equal(1, sink.ApplyCount);
        Assert.Equal(-0.1f, sink.Total, 5);
    }

    [Fact]
    public void ApplyDelta_WhileInactive_DoesNothing()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);
        // Never pressed.

        ml.ApplyDelta(dx: 100f, extraSensitivity: 1.0f);

        Assert.Equal(0, sink.ApplyCount);
        Assert.Equal(0f, sink.Total);
    }

    [Fact]
    public void ApplyDelta_AfterRelease_DoesNothing()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);
        ml.Press(0f, 0f, wantCaptureMouse: false);
        ml.Release();

        ml.ApplyDelta(dx: 50f, extraSensitivity: 1.0f);

        Assert.Equal(0, sink.ApplyCount);
    }

    [Fact]
    public void Press_WhileAlreadyActive_IsIdempotent()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);
        ml.Press(100f, 200f, wantCaptureMouse: false);

        ml.Press(999f, 888f, wantCaptureMouse: false);

        Assert.Equal(100f, ml.CapturedCursorX);
        Assert.Equal(200f, ml.CapturedCursorY);
    }

    [Fact]
    public void ApplyDelta_DriverDrivesCharacterAndCameraInOneSink()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply) { SensitivityPerPixel = 0.005f };
        ml.Press(0f, 0f, wantCaptureMouse: false);

        for (int i = 0; i < 5; i++)
            ml.ApplyDelta(dx: 4f, extraSensitivity: 0.5f);
        ml.ApplyDelta(dx:  4f, extraSensitivity: 0.5f);  // -0.01
        ml.ApplyDelta(dx: -2f, extraSensitivity: 0.5f);  // +0.005

        Assert.Equal(2, sink.ApplyCount);
        Assert.Equal(-0.005f, sink.Total, 5);
    }

    [Fact]
    public void ApplyDelta_ZeroSampleResetsRetailHorizontalExtent()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply) { SensitivityPerPixel = 0.01f };
        ml.Press(0f, 0f, wantCaptureMouse: false);

        for (int i = 0; i < 5; i++)
            ml.ApplyDelta(dx: 2f, extraSensitivity: 1f);
        ml.ApplyDelta(dx: 0f, extraSensitivity: 1f);
        for (int i = 0; i < 5; i++)
            ml.ApplyDelta(dx: 2f, extraSensitivity: 1f);

        Assert.Equal(0, sink.ApplyCount);
    }

    [Fact]
    public void TryTakeRawSample_IdleZeroUsesStrictRetailDelayAndResetsExtent()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply) { SensitivityPerPixel = 0.01f };
        ml.Press(0f, 0f, wantCaptureMouse: false, nowSeconds: 1f);

        for (int i = 0; i < 5; i++)
        {
            ml.QueueDelta(2f);
            float now = 1.01f + (i * 0.01f);
            Assert.True(ml.TryTakeRawSample(now, out float dx, out float dy));
            Assert.Equal(0f, dy);
            ml.ApplyDelta(dx, extraSensitivity: 1f);
        }

        Assert.False(ml.TryTakeRawSample(1.25f, out _, out _));
        Assert.True(ml.TryTakeRawSample(1.2501f, out float idleDx, out float idleDy));
        Assert.Equal(0f, idleDx);
        Assert.Equal(0f, idleDy);
        ml.ApplyDelta(idleDx, extraSensitivity: 1f);

        ml.QueueDelta(2f);
        Assert.True(ml.TryTakeRawSample(1.26f, out float resumedDx, out _));
        ml.ApplyDelta(resumedDx, extraSensitivity: 1f);

        Assert.Equal(0, sink.ApplyCount);
    }

    [Fact]
    public void TryTakeRawSample_AccumulatesNativeEventsIntoOneFrameSample()
    {
        var sink = new AdjustmentSink();
        var ml = new MouseLookState(sink.Apply);
        ml.Press(0f, 0f, wantCaptureMouse: false, nowSeconds: 2f);

        ml.QueueDelta(2f, 1f);
        ml.QueueDelta(3f, -0.25f);

        Assert.True(ml.TryTakeRawSample(2.01f, out float dx, out float dy));
        Assert.Equal(5f, dx);
        Assert.Equal(0.75f, dy);
        Assert.False(ml.TryTakeRawSample(2.02f, out _, out _));
    }
}
