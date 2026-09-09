using System;
using System.Numerics;

namespace AcDream.Core.Audio;

public readonly record struct RetailVoiceMix(bool Play, int Decibels, int Pan);

public static class RetailSoundMixer
{
    /// <summary>Distance below which gain is flat at the authored volume, metres.</summary>
    public const float VolMinDistance = 5.0f;

    public const float VolMinDistanceSq = 25.0f;

    public const int VolMinDecibels = -50;

    public const float PanScale = -15.0f;

    public const int PanDeadzoneMetres = 5;

    public const int VoiceCount = 16;

    private const float DegreesToRadians = 0.0174532924f;

    public static bool TryGetAttenuation(
        float distanceMetres,
        float volume,
        float masterVolume,
        out int decibels)
    {
        float g = distanceMetres < VolMinDistance
            ? volume
            : (VolMinDistanceSq * volume) / (distanceMetres * distanceMetres);

        if (g > 1.0f) g = 1.0f;
        g *= masterVolume;

        if (g <= 0.0f || float.IsNaN(g))
        {
            decibels = VolMinDecibels;
            return false;
        }

        decibels = (int)MathF.Ceiling(20.0f * MathF.Log10(g));
        if (decibels >= VolMinDecibels)
            return true;

        decibels = VolMinDecibels;
        return false;
    }

    public static float CompassHeadingDegrees(Vector3 from, Vector3 to) =>
        Physics.Motion.MoveToMath.PositionHeading(from, to);

    public static float NormalizeSignedDegrees(float degrees)
    {
        float delta = degrees % 360.0f;
        if (!(delta <= 180.0f)) delta -= 360.0f;
        return delta;
    }

    public static int GetPan(
        float bearingSourceToListener,
        float listenerHeadingDegrees,
        float distanceMetres,
        bool panningEnabled = true)
    {
        if (!panningEnabled)
            return 0;

        if (Math.Abs((int)distanceMetres) < PanDeadzoneMetres)
            return 0;

        float delta = NormalizeSignedDegrees(bearingSourceToListener - listenerHeadingDegrees);
        int pan = (int)(MathF.Sin(delta * DegreesToRadians) * PanScale);
        return Math.Clamp(pan, (int)PanScale, (int)-PanScale);
    }

    public static RetailVoiceMix Mix(
        Vector3 listenerPosition,
        float listenerHeadingDegrees,
        Vector3 sourcePosition,
        float volume,
        float masterVolume,
        bool panningEnabled = true)
    {
        float distance = Vector3.Distance(listenerPosition, sourcePosition);

        float bearing = CompassHeadingDegrees(sourcePosition, listenerPosition);
        int pan = GetPan(bearing, listenerHeadingDegrees, distance, panningEnabled);

        bool play = TryGetAttenuation(distance, volume, masterVolume, out int decibels);
        return new RetailVoiceMix(play, decibels, pan);
    }

    public static float LinearGain(int decibels) =>
        MathF.Pow(10.0f, decibels / 20.0f);

    public static float StereoPositionFromPan(int pan)
    {
        float difference = MathF.Pow(10.0f, pan / 20.0f);
        float position = (4.0f / MathF.PI) * MathF.Atan(difference) - 1.0f;
        return Math.Clamp(position, -1.0f, 1.0f);
    }

    public static float AudibleRadius(float volume, float masterVolume)
    {
        float scale = volume * masterVolume;
        if (scale <= 0f) return 0f;

        float minGain = MathF.Pow(10.0f, -51.0f / 20.0f);
        return MathF.Sqrt(VolMinDistanceSq * scale / minGain);
    }
}
