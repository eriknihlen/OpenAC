using System;

namespace AcDream.UI.Abstractions.Input;

public sealed class MouseLookState
{
    private readonly Action<float> _applyHorizontalAdjustment;
    private int _horizontalExtent;
    private float _queuedDeltaX;
    private float _queuedDeltaY;
    private float _lastMouseInputTime;

    public const float IdleZeroDelaySeconds = 0.2f;

    public bool Active { get; private set; }

    public float CapturedCursorX { get; private set; }

    public float CapturedCursorY { get; private set; }

    public float SensitivityPerPixel { get; set; } = 0.0666666701f;

    public MouseLookState(Action<float> applyHorizontalAdjustment)
    {
        _applyHorizontalAdjustment = applyHorizontalAdjustment
            ?? throw new ArgumentNullException(nameof(applyHorizontalAdjustment));
    }

    public void Press(
        float cursorX,
        float cursorY,
        bool wantCaptureMouse,
        float nowSeconds = 0f)
    {
        if (wantCaptureMouse) return;
        if (Active) return;
        Active = true;
        CapturedCursorX = cursorX;
        CapturedCursorY = cursorY;
        _horizontalExtent = 0;
        _queuedDeltaX = 0f;
        _queuedDeltaY = 0f;
        _lastMouseInputTime = nowSeconds;
    }

    /// <summary>MMB release transition. Always deactivates if active.</summary>
    public void Release()
    {
        if (!Active) return;
        Active = false;
        _horizontalExtent = 0;
        _queuedDeltaX = 0f;
        _queuedDeltaY = 0f;
    }

    public void OnWantCaptureMouseChanged(bool wantCaptureMouse)
    {
        if (Active && wantCaptureMouse)
        {
            Active = false;
            _horizontalExtent = 0;
            _queuedDeltaX = 0f;
            _queuedDeltaY = 0f;
        }
    }

    public void QueueDelta(float dx, float dy = 0f)
    {
        if (Active && float.IsFinite(dx) && float.IsFinite(dy))
        {
            _queuedDeltaX += dx;
            _queuedDeltaY += dy;
        }
    }

    public bool TryTakeRawSample(
        float nowSeconds,
        out float dx,
        out float dy)
    {
        dx = 0f;
        dy = 0f;
        if (!Active)
        {
            _queuedDeltaX = 0f;
            _queuedDeltaY = 0f;
            return false;
        }

        dx = _queuedDeltaX;
        dy = _queuedDeltaY;
        _queuedDeltaX = 0f;
        _queuedDeltaY = 0f;

        if (dx != 0f || dy != 0f)
        {
            _lastMouseInputTime = nowSeconds;
            return true;
        }

        return nowSeconds > _lastMouseInputTime + IdleZeroDelaySeconds;
    }

    public void ApplyDelta(float dx, float extraSensitivity)
    {
        if (!Active) return;
        ProcessDelta(dx, extraSensitivity);
    }

    private void ProcessDelta(float dx, float extraSensitivity)
    {
        float adjustment = -dx * SensitivityPerPixel * extraSensitivity;
        if (adjustment == 0f)
        {
            _horizontalExtent = 0;
            return;
        }

        _horizontalExtent++;
        if (_horizontalExtent <= 5)
            return;

        _applyHorizontalAdjustment(adjustment);
    }
}
