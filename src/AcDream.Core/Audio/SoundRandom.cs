using System;

namespace AcDream.Core.Audio;

public interface ISoundRandom
{
    float NextVariantRoll();

    float NextProbabilityRoll();
}

public sealed class SoundRandom : ISoundRandom
{
    internal const float MaxVariantRoll = 0.99999988f;

    internal const int RandMax = 32767;

    private readonly Random _rng;

    public SoundRandom(Random? rng = null) => _rng = rng ?? Random.Shared;

    public float NextVariantRoll() =>
        MathF.Min(MaxVariantRoll, (float)_rng.NextDouble());

    // Next's upper bound is exclusive, so RandMax + 1 makes RAND_MAX itself
    // reachable — which is what lets the scaled roll reach exactly 1.0.
    public float NextProbabilityRoll() =>
        _rng.Next(0, RandMax + 1) * (1f / RandMax);
}
