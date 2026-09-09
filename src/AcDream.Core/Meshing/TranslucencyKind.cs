using DatReaderWriter.Enums;

namespace AcDream.Core.Meshing;

public enum TranslucencyKind
{
    /// <summary>Standard opaque. Depth write + test, no blend.</summary>
    Opaque = 0,

    ClipMap = 1,

    /// <summary>
    /// Standard alpha blend: src*a + dst*(1-a).
    /// Depth-write off, depth-test on. Used for semi-transparent glass,
    /// water decals, and flame alpha surfaces.
    /// </summary>
    AlphaBlend = 2,

    /// <summary>
    /// Additive blend: src*a + dst. Depth-write off, depth-test on.
    /// Used for portal swirls, magical glows, and particle effects.
    /// </summary>
    Additive = 3,

    /// <summary>
    /// Inverted alpha blend: src*(1-a) + dst*a. Rare but present in
    /// the AC dat files.
    /// </summary>
    InvAlpha = 4,
}

public enum RetailSetSurfaceBlend : byte
{
    Opaque = 0,
    StraightAlpha = 1,
    AlphaAdditive = 2,
    Additive = 3,
    InverseAlpha = 4,
    InverseAdditive = 5,
    Clip = 6,
}

public enum RetailSetSurfaceAlphaTest : byte
{
    Disabled = 0,
    Paletted = 1,
    Dds = 2,
}

public readonly record struct RetailSetSurfaceMaterialState(
    RetailSetSurfaceBlend Blend,
    RetailSetSurfaceAlphaTest AlphaTest,
    bool FogEnabled)
{
    public static RetailSetSurfaceMaterialState Opaque { get; } = new(
        RetailSetSurfaceBlend.Opaque,
        RetailSetSurfaceAlphaTest.Disabled,
        FogEnabled: true);

    public bool AlphaTestEnabled => AlphaTest != RetailSetSurfaceAlphaTest.Disabled;

    public float AlphaTestReference => AlphaTest switch
    {
        RetailSetSurfaceAlphaTest.Disabled => 0f,
        RetailSetSurfaceAlphaTest.Paletted => 100f / 255f,
        RetailSetSurfaceAlphaTest.Dds => 200f / 255f,
        _ => throw new ArgumentOutOfRangeException(nameof(AlphaTest), AlphaTest, null),
    };

    /// <summary>One deterministic recipe byte; bits 6-7 remain reserved.</summary>
    public byte ToPackedByte()
    {
        if ((uint)Blend > (uint)RetailSetSurfaceBlend.Clip)
            throw new InvalidOperationException($"Unknown SetSurface blend {Blend}.");
        if ((uint)AlphaTest > (uint)RetailSetSurfaceAlphaTest.Dds)
            throw new InvalidOperationException($"Unknown SetSurface alpha test {AlphaTest}.");
        return (byte)((byte)Blend | ((byte)AlphaTest << 3) | (FogEnabled ? 0 : 0x20));
    }

    public static RetailSetSurfaceMaterialState FromPackedByte(byte value)
    {
        if ((value & 0xC0) != 0)
            throw new InvalidDataException($"Reserved SetSurface state bits are set: 0x{value:X2}.");
        var blend = (RetailSetSurfaceBlend)(value & 0x07);
        var alphaTest = (RetailSetSurfaceAlphaTest)((value >> 3) & 0x03);
        if ((uint)blend > (uint)RetailSetSurfaceBlend.Clip
            || (uint)alphaTest > (uint)RetailSetSurfaceAlphaTest.Dds)
        {
            throw new InvalidDataException($"Invalid SetSurface state byte: 0x{value:X2}.");
        }
        return new RetailSetSurfaceMaterialState(
            blend,
            alphaTest,
            FogEnabled: (value & 0x20) == 0);
    }

    public static RetailSetSurfaceMaterialState Resolve(
        SurfaceType type,
        bool texturePresent,
        bool textureHasPalette)
    {
        bool additive = (type & SurfaceType.Additive) != 0;
        bool alpha = (type & SurfaceType.Alpha) != 0;
        bool inverse = (type & SurfaceType.InvAlpha) != 0;
        bool clip = (type & SurfaceType.Base1ClipMap) != 0;
        bool translucent = (type & SurfaceType.Translucent) != 0;

        RetailSetSurfaceBlend blend = alpha
            ? additive
                ? RetailSetSurfaceBlend.AlphaAdditive
                : RetailSetSurfaceBlend.StraightAlpha
            : inverse
                ? additive
                    ? RetailSetSurfaceBlend.InverseAdditive
                    : RetailSetSurfaceBlend.InverseAlpha
                : additive
                    ? RetailSetSurfaceBlend.Additive
                    : RetailSetSurfaceBlend.Opaque;

        RetailSetSurfaceAlphaTest alphaTest = RetailSetSurfaceAlphaTest.Disabled;
        if (clip)
        {
            alphaTest = texturePresent && textureHasPalette
                ? RetailSetSurfaceAlphaTest.Paletted
                : RetailSetSurfaceAlphaTest.Dds;
            if (blend == RetailSetSurfaceBlend.Opaque)
                blend = RetailSetSurfaceBlend.Clip;
        }

        if (translucent
            && (clip
                || blend is RetailSetSurfaceBlend.Opaque
                    or RetailSetSurfaceBlend.StraightAlpha))
        {
            blend = RetailSetSurfaceBlend.StraightAlpha;
            alphaTest = RetailSetSurfaceAlphaTest.Disabled;
        }

        return new RetailSetSurfaceMaterialState(
            blend,
            alphaTest,
            FogEnabled: !additive);
    }
}

public static class TranslucencyKindExtensions
{

    public static TranslucencyKind FromSurfaceType(SurfaceType type)
    {
        bool isTranslucent = (type & SurfaceType.Translucent) != 0;
        bool isClipMap     = (type & SurfaceType.Base1ClipMap) != 0;
        bool wouldBeOpaque =
            (type & (SurfaceType.Additive
                   | SurfaceType.Alpha
                   | SurfaceType.InvAlpha)) == 0;
        if (isTranslucent && (isClipMap || wouldBeOpaque))
            return TranslucencyKind.AlphaBlend;

        // Step 2..6: existing priority order for non-overridden surfaces.
        if ((type & SurfaceType.Additive) != 0)
            return TranslucencyKind.Additive;

        if ((type & SurfaceType.InvAlpha) != 0)
            return TranslucencyKind.InvAlpha;

        if ((type & (SurfaceType.Alpha | SurfaceType.Translucent)) != 0)
            return TranslucencyKind.AlphaBlend;

        if ((type & SurfaceType.Base1ClipMap) != 0)
            return TranslucencyKind.ClipMap;

        return TranslucencyKind.Opaque;
    }

    public static float OpacityFromSurfaceTranslucency(SurfaceType type, float translucency)
    {
        if ((type & SurfaceType.Translucent) == 0)
            return 1f;

        return Math.Clamp(1f - translucency, 0f, 1f);
    }

    public static bool DisablesFixedFunctionFog(SurfaceType type)
        => (type & SurfaceType.Additive) != 0;
}
