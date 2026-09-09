using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Audio;

public enum AmbientDirection
{
    InViewerBlock = 0,
    North = 1,
    South = 2,
    East = 3,
    West = 4,
    Northwest = 5,
    Southwest = 6,
    Northeast = 7,
    Southeast = 8,
}

public readonly record struct AmbientSoundDescriptor(
    SoundId Sound,
    float Volume,
    float BaseChance,
    float MinRate,
    float MaxRate)
{
    public bool IsContinuous => BaseChance == 0f;
}

public static class AmbientSoundConstants
{
    public const float MinDistance = 20.0f;

    public const float MinDistanceSq = 400.0f;

    public const float MaxDistance = 120.0f;

    public const float MaxDistanceSq = 14400.0f;

    public const float MinVolume = 0.03f;

    public const float HeadingSpread = 0.392699093f;

    public const float InViewerBlockDistanceSq = MinDistanceSq * 0.5f;

    public const float ShellHalfThickness = MinDistance * 0.5f;

    /// <summary>Near bound used for the omnidirectional spread, metres (<c>5.0f − 1.0f</c>).</summary>
    public const float InBlockNearDistance = 4.0f;

    public const float LandCellLength = 24.0f;

    public static float Heading(AmbientDirection direction) => direction switch
    {
        AmbientDirection.North => 0.0f,
        AmbientDirection.South => 3.14159274f,
        AmbientDirection.East => 1.57079637f,
        AmbientDirection.West => 4.71238899f,
        AmbientDirection.Northwest => 5.49778700f,
        AmbientDirection.Southwest => 3.92699075f,
        AmbientDirection.Northeast => 0.78539819f,
        AmbientDirection.Southeast => 2.35619450f,
        // IN_VIEWER_BLOCK and anything out of range fall to 0.0.
        _ => 0.0f,
    };

    public static float CalcWeight(Vector3 offset)
    {
        float distanceSq = offset.LengthSquared();
        if (distanceSq > MaxDistanceSq) return 0f;
        if (distanceSq < MinDistanceSq) return 1f;
        return MinDistanceSq / distanceSq;
    }

    public static AmbientDirection CalcDirection(Vector3 offset)
    {
        float x = offset.X;
        float y = offset.Y;

        if (((x * x) + (y * y)) < InViewerBlockDistanceSq)
            return AmbientDirection.InViewerBlock;
        float ax = MathF.Abs(x);
        float ay = MathF.Abs(y);

        const float diagonalRatio = 2.0f;
        const float epsilon = 0.0002f;

        bool diagonal =
            ax > epsilon && ay > epsilon
            && ay / ax <= diagonalRatio
            && ax / ay <= diagonalRatio;

        if (diagonal)
        {
            return y >= 0f
                ? (x >= 0f ? AmbientDirection.Northeast : AmbientDirection.Northwest)
                : (x >= 0f ? AmbientDirection.Southeast : AmbientDirection.Southwest);
        }

        if (ay >= ax)
            return y >= 0f ? AmbientDirection.North : AmbientDirection.South;
        return x >= 0f ? AmbientDirection.East : AmbientDirection.West;
    }
}

public sealed class AmbientSoundInstance
{
    private readonly List<AmbientDirectionShell> _directions = [];

    public AmbientSoundInstance(AmbientSoundDescriptor descriptor, uint soundTableDid)
    {
        Descriptor = descriptor;
        SoundTableDid = soundTableDid;
    }

    public AmbientSoundDescriptor Descriptor { get; }

    /// <summary>The SoundTable the descriptor's slot is looked up in.</summary>
    public uint SoundTableDid { get; }

    public float SoundCount { get; private set; }

    public float CurrentVolume { get; private set; }

    /// <summary>Per-fire probability — intermittent instances only.</summary>
    public float PlayChance { get; private set; }

    /// <summary>True while this instance holds a slot in the deadline queue.</summary>
    public bool OnQueue { get; set; }

    public IReadOnlyList<AmbientDirectionShell> Directions => _directions;

    public void ResetCount()
    {
        SoundCount = 0f;
        _directions.Clear();
        if (!Descriptor.IsContinuous)
            PlayChance = 0f;
    }

    public void AddTo(float weight, Vector3 offset, AmbientDirection direction)
    {
        SoundCount += weight;
        if (Descriptor.IsContinuous)
            return;

        float distance = MathF.Sqrt(offset.LengthSquared());
        float half = AmbientSoundConstants.ShellHalfThickness;

        if (direction != AmbientDirection.InViewerBlock)
        {
            AddDirection(direction, distance - half, distance + half);
            return;
        }

        AddDirection(AmbientDirection.North, AmbientSoundConstants.InBlockNearDistance, half);
        AddDirection(AmbientDirection.South, AmbientSoundConstants.InBlockNearDistance, half);
        AddDirection(AmbientDirection.East, AmbientSoundConstants.InBlockNearDistance, half);
        AddDirection(AmbientDirection.West, AmbientSoundConstants.InBlockNearDistance, half);
        AddDirection(AmbientDirection.Northwest, AmbientSoundConstants.InBlockNearDistance, half);
        AddDirection(AmbientDirection.Southwest, AmbientSoundConstants.InBlockNearDistance, half);
        AddDirection(AmbientDirection.Northeast, AmbientSoundConstants.InBlockNearDistance, half);
        AddDirection(AmbientDirection.Southeast, AmbientSoundConstants.InBlockNearDistance, half);
    }

    private void AddDirection(AmbientDirection direction, float min, float max)
    {
        for (int i = 0; i < _directions.Count; i++)
        {
            if (_directions[i].Direction != direction)
                continue;

            AmbientDirectionShell existing = _directions[i];
            _directions[i] = new AmbientDirectionShell(
                direction,
                MathF.Min(existing.MinDistance, min),
                MathF.Max(existing.MaxDistance, max));
            return;
        }

        if (_directions.Count >= 8)
            return;
        _directions.Add(new AmbientDirectionShell(direction, min, max));
    }

    public void UpdateSound(float totalSoundCount)
    {
        if (Descriptor.IsContinuous)
        {
            if (SoundCount == 0f)
            {
                CurrentVolume = 0f;
                return;
            }
            CurrentVolume = Descriptor.Volume / totalSoundCount * SoundCount;
            return;
        }

        if (SoundCount <= 0f)
            return;
        PlayChance = Descriptor.BaseChance / totalSoundCount * SoundCount;
    }

    public bool CanHear() =>
        Descriptor.IsContinuous
            ? CurrentVolume >= AmbientSoundConstants.MinVolume
            : PlayChance > 0f;

    public bool PlayNow(ISoundRandom rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return Descriptor.IsContinuous || rng.NextVariantRoll() <= PlayChance;
    }

    public float GetVolume() =>
        Descriptor.IsContinuous ? CurrentVolume : Descriptor.Volume;

    public float GetPlayInterval(ISoundRandom rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return Descriptor.IsContinuous
            ? Descriptor.MinRate
            : RollDice(Descriptor.MinRate, Descriptor.MaxRate, rng);
    }

    public bool TryGetSoundPosition(
        Vector3 listenerPosition,
        ISoundRandom rng,
        out Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(rng);
        position = listenerPosition;

        if (Descriptor.IsContinuous || _directions.Count == 0)
            return false;

        int index = (int)MathF.Floor(rng.NextVariantRoll() * _directions.Count);
        if (index >= _directions.Count)
            index = _directions.Count - 1;
        AmbientDirectionShell shell = _directions[index];

        float spread = AmbientSoundConstants.HeadingSpread;
        float angle = AmbientSoundConstants.Heading(shell.Direction)
            + (rng.NextVariantRoll() * spread)
            - (spread * 0.5f);

        float t = rng.NextVariantRoll();
        float distance = shell.MinDistance
            + ((shell.MaxDistance - shell.MinDistance) * t * t);

        position = new Vector3(
            listenerPosition.X + (MathF.Sin(angle) * distance),
            listenerPosition.Y + (MathF.Cos(angle) * distance),
            listenerPosition.Z);
        return true;
    }

    internal static float RollDice(float min, float max, ISoundRandom rng)
    {
        if (min == max) return min;
        float lo = min, hi = max;
        if (max < min) { lo = max; hi = min; }
        return lo + ((hi - lo) * rng.NextVariantRoll());
    }
}

public readonly record struct AmbientDirectionShell(
    AmbientDirection Direction,
    float MinDistance,
    float MaxDistance);
