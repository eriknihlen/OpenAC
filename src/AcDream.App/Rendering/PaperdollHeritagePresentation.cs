using System.Numerics;
using AcDream.Core.CharGen;

namespace AcDream.App.Rendering;

/// <summary>
/// How the inventory doll is posed and framed for each heritage.
///
/// The doll is a held frame of an authored animation applied part-by-part to
/// the character's setup, so the animation has to be the one authored for that
/// body: the humanoid pose has 34 parts, while the two Olthoi bodies have 25
/// and 31. Feeding a humanoid pose to an Olthoi setup gives every Olthoi part
/// the origin and orientation of whatever humanoid limb happens to share its
/// index, which scatters the figure. The camera distance is authored per
/// heritage for the same reason: the bodies are different sizes, and one fixed
/// eye cannot frame all of them.
/// </summary>
internal static class PaperdollHeritagePresentation
{
    /// <summary>Pose used by every heritage that has no body-specific one.</summary>
    public const uint DefaultPoseEnum = 0x10000005u;

    private const uint OlthoiPoseEnum = 0x10000011u;

    private const uint OlthoiAcidPoseEnum = 0x10000013u;

    /// <summary>Eye the doll view starts at, before any heritage is known.</summary>
    public static readonly Vector3 DefaultEye = new(0.12f, -2.4f, 0.88f);

    public static uint ResolvePoseEnum(uint heritageId) => heritageId switch
    {
        (uint)ChargenHeritageGroup.Olthoi => OlthoiPoseEnum,
        (uint)ChargenHeritageGroup.OlthoiAcid => OlthoiAcidPoseEnum,
        _ => DefaultPoseEnum,
    };

    public static Vector3 ResolveEye(uint heritageId) => heritageId switch
    {
        (uint)ChargenHeritageGroup.Gearknight
            or (uint)ChargenHeritageGroup.Tumerok => new Vector3(0.12f, -3f, 0.88f),
        (uint)ChargenHeritageGroup.Lugian => new Vector3(0.12f, -3.4f, 1f),
        (uint)ChargenHeritageGroup.Empyrean
            or (uint)ChargenHeritageGroup.Olthoi
            or (uint)ChargenHeritageGroup.OlthoiAcid =>
                new Vector3(0.12f, -3.4f, 0.88f),
        _ => DefaultEye,
    };
}
