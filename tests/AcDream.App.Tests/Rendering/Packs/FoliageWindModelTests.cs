using System.Numerics;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class FoliageWindModelTests
{
    private static readonly Vector3 WorldPos = new(12f, -7f, 5f);
    private static readonly Vector3 InstanceOrigin = new(10f, -8f, 1f);
    private static readonly Vector4 Amplitude = new(0.25f, 0.15f, 0.05f, 8f);
    private static readonly Vector4 CalmWind = new(3.5f, 0f, 0f, 3.9f); // mean=gust=0

    [Fact]
    public void ZeroFlagsIsIdentityRegardlessOfWindStrength()
    {
        var windyClockWind = new Vector4(3.5f, 1f, 1f, 3.9f);

        Vector3 result = FoliageWindModel.Displace(
            WorldPos,
            InstanceOrigin,
            batchFlags: 0u,
            windyClockWind,
            Amplitude);

        Assert.Equal(WorldPos, result);
    }

    [Theory]
    [InlineData(FoliageWindClassification.CutoutFoliageFlag)]
    [InlineData(FoliageWindClassification.TrunkFlag)]
    public void CalmWindIsIdentityForAnyFoliageFlags(uint flags)
    {
        Vector3 result = FoliageWindModel.Displace(
            WorldPos,
            InstanceOrigin,
            flags,
            CalmWind,
            Amplitude);

        AssertApproximatelyEqual(WorldPos, result);
    }

    [Theory]
    [InlineData(FoliageWindClassification.CutoutFoliageFlag)]
    [InlineData(FoliageWindClassification.TrunkFlag)]
    public void TheBaseVertexNeverMoves(uint flags)
    {
        Vector3 baseVertex = InstanceOrigin with { X = InstanceOrigin.X + 3f };
        var windyClockWind = new Vector4(11f, 1f, 1f, 2.1f);

        Vector3 result = FoliageWindModel.Displace(
            baseVertex,
            InstanceOrigin,
            flags,
            windyClockWind,
            Amplitude);

        AssertApproximatelyEqual(baseVertex, result);
    }

    [Fact]
    public void CanopyTopDisplacementMagnitudeIsBoundedByTheDeclaredAmplitudes()
    {
        // gust = 0 pins s = mean exactly (no gust-envelope overshoot above
        // 1 to reason about), and mean = 1 is the maximum authored strength,
        // so |lean| <= amp.lean, |branch| <= amp.branch, |flutter| <=
        // amp.flutter follow directly from each term's own sin/cos factors
        // being bounded by 1. The triangle inequality then bounds the
        // summed 2-D displacement by amp.lean + 1.35*amp.branch (the extra
        // 0.35 is the perpendicular branch-sway term) + amp.flutter.
        var maxMean = new Vector4(0f, 1f, 0f, 0f);
        float bound = Amplitude.X + (1.35f * Amplitude.Y) + Amplitude.Z + 1e-4f;

        for (float t = 0f; t < 40f; t += 3.7f)
        {
            for (float xy = -5f; xy <= 5f; xy += 4.3f)
            {
                Vector3 canopyTop = InstanceOrigin with
                {
                    X = InstanceOrigin.X + xy,
                    Y = InstanceOrigin.Y - xy,
                    Z = InstanceOrigin.Z + Amplitude.W, // h = 1
                };
                var clockWind = maxMean with { X = t };

                Vector3 result = FoliageWindModel.Displace(
                    canopyTop,
                    InstanceOrigin,
                    FoliageWindClassification.CutoutFoliageFlag,
                    clockWind,
                    Amplitude);

                Vector2 displacementXY = new(
                    result.X - canopyTop.X,
                    result.Y - canopyTop.Y);
                Assert.True(
                    displacementXY.Length() <= bound,
                    $"t={t} xy={xy}: |d|={displacementXY.Length()} exceeds bound {bound}");
            }
        }
    }

    [Fact]
    public void HeightNeverIncreases()
    {
        var rng = new Random(1337);
        for (int i = 0; i < 200; i++)
        {
            var worldPos = new Vector3(
                (float)((rng.NextDouble() * 40) - 20),
                (float)((rng.NextDouble() * 40) - 20),
                (float)(rng.NextDouble() * Amplitude.W));
            var clockWind = new Vector4(
                (float)(rng.NextDouble() * 1000),
                (float)rng.NextDouble(),
                (float)rng.NextDouble(),
                (float)(rng.NextDouble() * MathF.Tau));
            uint flags = (rng.Next(2) == 0)
                ? FoliageWindClassification.CutoutFoliageFlag
                : FoliageWindClassification.TrunkFlag;

            Vector3 result = FoliageWindModel.Displace(
                worldPos,
                InstanceOrigin,
                flags,
                clockWind,
                Amplitude);

            Assert.True(
                result.Z <= worldPos.Z + 1e-5f,
                $"iteration {i}: z increased from {worldPos.Z} to {result.Z}");
        }
    }

    [Fact]
    public void TrunkDisplacementIsIndependentOfWorldXyHash()
    {
        float z = InstanceOrigin.Z + (0.5f * Amplitude.W);
        var clockWind = new Vector4(7.25f, 0.8f, 0.6f, 1.1f);

        Vector3 displacementAt(float x, float y)
        {
            var worldPos = new Vector3(x, y, z);
            Vector3 result = FoliageWindModel.Displace(
                worldPos,
                InstanceOrigin,
                FoliageWindClassification.TrunkFlag,
                clockWind,
                Amplitude);
            return result - worldPos;
        }

        Vector3 reference = displacementAt(InstanceOrigin.X, InstanceOrigin.Y);
        Vector3 farAway = displacementAt(InstanceOrigin.X + 500f, InstanceOrigin.Y - 300f);
        Vector3 elsewhere = displacementAt(InstanceOrigin.X - 17.3f, InstanceOrigin.Y + 91f);

        AssertApproximatelyEqual(reference, farAway);
        AssertApproximatelyEqual(reference, elsewhere);
    }

    [Fact]
    public void CutoutDisplacementVariesWithWorldXyHashButTrunkDoesNot()
    {
        float z = InstanceOrigin.Z + (0.5f * Amplitude.W);
        var clockWind = new Vector4(7.25f, 0.8f, 0.6f, 1.1f);

        Vector3 displacementAt(float x, float y)
        {
            var worldPos = new Vector3(x, y, z);
            Vector3 result = FoliageWindModel.Displace(
                worldPos,
                InstanceOrigin,
                FoliageWindClassification.CutoutFoliageFlag,
                clockWind,
                Amplitude);
            return result - worldPos;
        }

        Vector3 reference = displacementAt(InstanceOrigin.X, InstanceOrigin.Y);
        Vector3 farAway = displacementAt(InstanceOrigin.X + 500f, InstanceOrigin.Y - 300f);

        Assert.NotEqual(reference, farAway);
    }

    [Fact]
    public void FlutterHashIsRelativeToInstanceOriginSoTranslatingTheWholeTreeDoesNotChangeIt()
    {
        var localOffset = new Vector3(3.7f, -2.1f, 4f); // fixed offset from trunk base to this leaf
        var nearOrigin = InstanceOrigin;
        var translation = new Vector3(0.291f, -0.137f, 0f) * 50_000f;
        var farOrigin = nearOrigin + translation;
        var clockWind = new Vector4(19.5f, 0.8f, 0.6f, 2.3f);

        Vector3 nearVertex = nearOrigin + localOffset;
        Vector3 farVertex = farOrigin + localOffset;

        Vector3 nearResult = FoliageWindModel.Displace(
            nearVertex,
            nearOrigin,
            FoliageWindClassification.CutoutFoliageFlag,
            clockWind,
            Amplitude);
        Vector3 farResult = FoliageWindModel.Displace(
            farVertex,
            farOrigin,
            FoliageWindClassification.CutoutFoliageFlag,
            clockWind,
            Amplitude);

        Vector3 nearDisplacement = nearResult - nearVertex;
        Vector3 farDisplacement = farResult - farVertex;

        AssertApproximatelyEqual(nearDisplacement, farDisplacement, tolerance: 1e-3f);
    }

    [Fact]
    public void MidHeightCutoutDisplacementHasAPositiveFloorUnderStormWind()
    {
        const float floorMetres = 0.01f;
        var stormClockWind = new Vector4(0f, 1.00f, 0.75f, 0f); // mean/gust match Storm
        Vector3 midHeightVertex = InstanceOrigin with
        {
            X = InstanceOrigin.X + 2.4f,
            Y = InstanceOrigin.Y - 1.1f,
            Z = InstanceOrigin.Z + (0.5f * Amplitude.W), // h = 0.5
        };

        for (float t = 0f; t < 30f; t += 2.9f)
        {
            Vector3 result = FoliageWindModel.Displace(
                midHeightVertex,
                InstanceOrigin,
                FoliageWindClassification.CutoutFoliageFlag,
                stormClockWind with { X = t },
                Amplitude);

            float magnitude = (result - midHeightVertex).Length();
            Assert.True(
                magnitude > floorMetres,
                $"t={t}: displacement magnitude {magnitude} did not clear the {floorMetres} m floor");
        }
    }

    private static void AssertApproximatelyEqual(
        Vector3 expected,
        Vector3 actual,
        float tolerance = 1e-4f)
    {
        Assert.True(
            (expected - actual).Length() <= tolerance,
            $"expected {expected}, got {actual} (delta length {(expected - actual).Length()})");
    }
}
