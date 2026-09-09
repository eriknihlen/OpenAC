using System;
using System.Numerics;
using AcDream.Core.Audio;
using Xunit;

namespace AcDream.Core.Tests.Audio;

public sealed class RetailSoundMixerTests
{
    // ── GetAttenuation ─────────────────────────────────────────────────────

    [Theory]
    // Inside the 5 m knee gain is flat at the authored volume.
    [InlineData(0f, 0)]
    [InlineData(2f, 0)]
    [InlineData(4.99f, 0)]
    [InlineData(5f, 0)]
    [InlineData(10f, -12)]
    [InlineData(20f, -24)]
    [InlineData(30f, -31)]
    [InlineData(50f, -40)]
    [InlineData(90f, -50)]
    [InlineData(94f, -50)]      // last audible metre
    public void Attenuation_MatchesRetailCurve(float distance, int expectedDecibels)
    {
        Assert.True(RetailSoundMixer.TryGetAttenuation(distance, 1f, 1f, out int db));
        Assert.Equal(expectedDecibels, db);
    }

    [Theory]
    [InlineData(95f)]
    [InlineData(120f)]
    [InlineData(1000f)]
    public void Attenuation_BeyondCutoff_DoesNotPlay(float distance)
    {
        Assert.False(RetailSoundMixer.TryGetAttenuation(distance, 1f, 1f, out int db));
        Assert.Equal(RetailSoundMixer.VolMinDecibels, db);
    }

    [Fact]
    public void Attenuation_IsInverseSquare_NotInverseFirstPower()
    {
        RetailSoundMixer.TryGetAttenuation(10f, 1f, 1f, out int near);
        RetailSoundMixer.TryGetAttenuation(20f, 1f, 1f, out int far);
        Assert.Equal(12, near - far);
    }

    [Fact]
    public void Attenuation_ClampsAboveUnity()
    {
        Assert.True(RetailSoundMixer.TryGetAttenuation(1f, 10f, 1f, out int db));
        Assert.Equal(0, db);
    }

    [Fact]
    public void Attenuation_ClampsBeforeTheMasterMultiply_NotAfter()
    {
        Assert.True(RetailSoundMixer.TryGetAttenuation(1f, 10f, 0.5f, out int db));
        Assert.Equal(-6, db);
    }

    [Fact]
    public void Attenuation_HighVolume_ExtendsAudibleRadius()
    {
        Assert.False(RetailSoundMixer.TryGetAttenuation(200f, 1f, 1f, out _));
        Assert.True(RetailSoundMixer.TryGetAttenuation(200f, 10f, 1f, out int loud));
        Assert.Equal(-44, loud);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Attenuation_NonPositiveMaster_DoesNotPlay(float master)
    {
        Assert.False(RetailSoundMixer.TryGetAttenuation(1f, 1f, master, out int db));
        Assert.Equal(RetailSoundMixer.VolMinDecibels, db);
    }

    [Fact]
    public void Attenuation_MasterIsAppliedExactlyOnce()
    {
        RetailSoundMixer.TryGetAttenuation(10f, 1f, 1f, out int full);
        RetailSoundMixer.TryGetAttenuation(10f, 1f, 0.5f, out int half);
        Assert.Equal(-6, half - full);
    }

    [Theory]
    [InlineData(1f, 94.2f)]
    [InlineData(0.5f, 66.6f)]
    [InlineData(0.1f, 29.8f)]
    public void AudibleRadius_MatchesDecodedRadii(float scale, float expectedMetres)
    {
        Assert.Equal(expectedMetres, RetailSoundMixer.AudibleRadius(scale, 1f), 1);
    }

    [Fact]
    public void AudibleRadius_AgreesWithTheLivePredicate()
    {
        // The radius helper and the play decision must not drift apart.
        for (float volume = 0.1f; volume <= 3f; volume += 0.1f)
        {
            float radius = RetailSoundMixer.AudibleRadius(volume, 1f);
            Assert.True(RetailSoundMixer.TryGetAttenuation(radius - 0.5f, volume, 1f, out _));
            Assert.False(RetailSoundMixer.TryGetAttenuation(radius + 0.5f, volume, 1f, out _));
        }
    }

    [Fact]
    public void Decibels_AreWholeNumbers_QuantisedByCeil()
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        for (float d = 5f; d < 94f; d += 0.05f)
        {
            RetailSoundMixer.TryGetAttenuation(d, 1f, 1f, out int db);
            seen.Add(db);
        }
        // 0 dB down to -50 dB inclusive is at most 51 distinct steps.
        Assert.InRange(seen.Count, 40, 51);
    }

    [Fact]
    public void LinearGain_RoundTripsTheDecibelScale()
    {
        Assert.Equal(1f, RetailSoundMixer.LinearGain(0), 5);
        Assert.Equal(0.5f, RetailSoundMixer.LinearGain(-6), 2);
        Assert.Equal(0.25f, RetailSoundMixer.LinearGain(-12), 2);
        Assert.Equal(0.00316f, RetailSoundMixer.LinearGain(-50), 5);
    }

    // ── Heading + pan ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, 1f, 0f)]      // north
    [InlineData(1f, 0f, 90f)]     // east
    [InlineData(0f, -1f, 180f)]   // south
    [InlineData(-1f, 0f, 270f)]   // west
    public void CompassHeading_UsesRetailConvention(float dx, float dy, float expected)
    {
        float heading = RetailSoundMixer.CompassHeadingDegrees(
            Vector3.Zero, new Vector3(dx, dy, 0f));
        Assert.Equal(expected, heading, 2);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(180f, 180f)]        // inclusive upper bound
    [InlineData(181f, -179f)]
    [InlineData(270f, -90f)]
    [InlineData(359f, -1f)]
    [InlineData(-90f, -90f)]
    public void NormalizeSigned_MapsIntoRetailsWindow(float input, float expected)
    {
        Assert.Equal(expected, RetailSoundMixer.NormalizeSignedDegrees(input), 3);
    }

    [Fact]
    public void Pan_SourceDueEastOfNorthFacingListener_IsFullRight()
    {
        var mix = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(10f, 0f, 0f), 1f, 1f);
        Assert.Equal(15, mix.Pan);
    }

    [Fact]
    public void Pan_SourceDueWestOfNorthFacingListener_IsFullLeft()
    {
        var mix = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(-10f, 0f, 0f), 1f, 1f);
        Assert.Equal(-15, mix.Pan);
    }

    [Fact]
    public void Pan_HasNoFrontBackDistinction()
    {
        var ahead = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(0f, 10f, 0f), 1f, 1f);
        var behind = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(0f, -10f, 0f), 1f, 1f);
        Assert.Equal(0, ahead.Pan);
        Assert.Equal(0, behind.Pan);
    }

    [Fact]
    public void Pan_RotatesWithListenerHeading()
    {
        var mix = RetailSoundMixer.Mix(
            Vector3.Zero, 90f, new Vector3(10f, 0f, 0f), 1f, 1f);
        Assert.Equal(0, mix.Pan);
    }

    [Theory]
    [InlineData(1f, 0)]        // inside the deadzone
    [InlineData(4.9f, 0)]      // (int)4.9 == 4 < 5
    [InlineData(5f, 15)]       // (int)5 == 5, deadzone ends
    public void Pan_DeadzoneIsAnIntegerMetreTest(float distance, int expectedPan)
    {
        var mix = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(distance, 0f, 0f), 1f, 1f);
        Assert.Equal(expectedPan, mix.Pan);
    }

    [Fact]
    public void Pan_ElevationNeverContributes()
    {
        // Z reaches the mix only through distance: two sources on the same
        // horizontal bearing pan identically however far apart they are
        // vertically, while their gains differ.
        var level = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(10f, 0f, 0f), 1f, 1f);
        var high = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(10f, 0f, 40f), 1f, 1f);

        Assert.Equal(level.Pan, high.Pan);
        Assert.NotEqual(level.Decibels, high.Decibels);
    }

    [Fact]
    public void Pan_PurelyVerticalOffset_InheritsRetailsAtan2Degeneracy()
    {
        var mix = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(0f, 0f, 10f), 1f, 1f);
        Assert.Equal(-15, mix.Pan);
        Assert.Equal(-12, mix.Decibels);
    }

    [Fact]
    public void Pan_DisabledByPreference_IsAlwaysCentre()
    {
        var mix = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(10f, 0f, 0f), 1f, 1f, panningEnabled: false);
        Assert.Equal(0, mix.Pan);
    }

    [Fact]
    public void Pan_StaysWithinFifteenDecibels()
    {
        for (int deg = 0; deg < 360; deg++)
        {
            float rad = deg * MathF.PI / 180f;
            var source = new Vector3(MathF.Sin(rad) * 20f, MathF.Cos(rad) * 20f, 0f);
            var mix = RetailSoundMixer.Mix(Vector3.Zero, 0f, source, 1f, 1f);
            Assert.InRange(mix.Pan, -15, 15);
        }
    }

    [Fact]
    public void Mix_BeyondCutoff_ReportsDoNotPlay()
    {
        var mix = RetailSoundMixer.Mix(
            Vector3.Zero, 0f, new Vector3(0f, 200f, 0f), 1f, 1f);
        Assert.False(mix.Play);
    }

    [Theory]
    [InlineData(64.158f, 13)]
    [InlineData(-64.158f, -13)]
    public void Pan_TruncatesTowardZero_NotFloor(float bearingDegrees, int expectedPan)
    {
        // Place the source at the given bearing FROM the listener, 20 m out.
        float rad = bearingDegrees * MathF.PI / 180f;
        var source = new Vector3(MathF.Sin(rad) * 20f, MathF.Cos(rad) * 20f, 0f);
        var mix = RetailSoundMixer.Mix(Vector3.Zero, 0f, source, 1f, 1f);
        Assert.Equal(expectedPan, mix.Pan);
    }

    [Fact]
    public void NormalizeSigned_LeavesLargeNegativesAlone_AsRetailDoes()
    {
        Assert.Equal(-270f, RetailSoundMixer.NormalizeSignedDegrees(-270f), 3);
        Assert.Equal(
            MathF.Sin(90f * MathF.PI / 180f),
            MathF.Sin(RetailSoundMixer.NormalizeSignedDegrees(-270f) * MathF.PI / 180f),
            3);
    }


    [Fact]
    public void StereoPosition_CentreIsCentre()
    {
        Assert.Equal(0f, RetailSoundMixer.StereoPositionFromPan(0), 4);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(-15)]
    public void StereoPosition_FullPan_StaysInsideTheSpeakerAngle(int pan)
    {
        float position = RetailSoundMixer.StereoPositionFromPan(pan);
        Assert.Equal(0.776f, MathF.Abs(position), 3);
        Assert.True(MathF.Abs(position) < 1f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(15)]
    [InlineData(-6)]
    [InlineData(-15)]
    public void StereoPosition_ReproducesTheRequestedDecibelDifference(int pan)
    {
        float p = RetailSoundMixer.StereoPositionFromPan(pan);
        float angle = (p + 1f) * MathF.PI / 4f;
        float left = MathF.Cos(angle);
        float right = MathF.Sin(angle);
        float differenceDb = 20f * MathF.Log10(right / left);
        Assert.Equal(pan, differenceDb, 2);
    }

    [Fact]
    public void StereoPosition_IsMonotonicAcrossThePanRange()
    {
        float previous = RetailSoundMixer.StereoPositionFromPan(-15);
        for (int pan = -14; pan <= 15; pan++)
        {
            float current = RetailSoundMixer.StereoPositionFromPan(pan);
            Assert.True(current > previous, $"pan {pan} did not increase position");
            previous = current;
        }
    }
}
