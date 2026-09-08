using System;

namespace AcDream.Core.Physics;

public sealed class RetailObjectQuantumClock
{
    private double _pending;

    public double PendingSeconds => _pending;
    public bool IsActive { get; private set; } = true;

    public RetailObjectQuantumBatch Advance(double elapsedSeconds)
    {
        if (double.IsNaN(elapsedSeconds) || elapsedSeconds < 0.0)
        {
            _pending = 0.0;
            return new RetailObjectQuantumBatch(0, 0f, Discarded: true);
        }

        double elapsed = _pending + elapsedSeconds;
        if (elapsed <= FrameEpsilon)
        {
            _pending = 0.0;
            return default;
        }

        if (elapsed > PhysicsBody.HugeQuantum)
        {
            _pending = 0.0;
            return new RetailObjectQuantumBatch(0, 0f, Discarded: true);
        }

        int fullSteps = 0;
        while (elapsed > PhysicsBody.MaxQuantum)
        {
            fullSteps++;
            elapsed -= PhysicsBody.MaxQuantum;
        }

        float remainder = 0f;
        if (elapsed > PhysicsBody.MinQuantum)
        {
            remainder = (float)elapsed;
            elapsed = 0.0;
        }

        _pending = elapsed;
        return new RetailObjectQuantumBatch(fullSteps, remainder, Discarded: false);
    }

    public void Deactivate() => IsActive = false;

    public bool Activate()
    {
        if (IsActive)
            return false;
        IsActive = true;
        _pending = 0.0;
        return true;
    }

    public void Reset() => _pending = 0.0;

    public void ResetForEnterWorld(bool isStatic = false)
    {
        _pending = 0.0;
        if (!isStatic)
            IsActive = true;
    }

    private const double FrameEpsilon = 0.000199999995;
}

public enum RetailObjectClockDisposition
{
    Advance,
    Suspend,
}

public readonly record struct RetailObjectQuantumBatch(
    int FullSteps,
    float Remainder,
    bool Discarded)
{
    public int Count => FullSteps + (Remainder > 0f ? 1 : 0);

    public float GetQuantum(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return index < FullSteps ? PhysicsBody.MaxQuantum : Remainder;
    }
}
