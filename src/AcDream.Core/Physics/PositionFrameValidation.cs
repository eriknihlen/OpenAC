using System.Numerics;

namespace AcDream.Core.Physics;

public static class PositionFrameValidation
{
    public static bool IsValid(uint cellId, Vector3 origin, Quaternion rotation) =>
        LandDefs.InboundValidCellId(cellId)
        && IsFrameValid(origin, rotation);

    /// <summary>
    /// Frame validity alone, without the cell: no NaN in the origin or the
    /// rotation, and a rotation whose squared length is within 0.001 of one.
    /// </summary>
    public static bool IsFrameValid(Vector3 origin, Quaternion rotation)
    {
        if (float.IsNaN(origin.X)
            || float.IsNaN(origin.Y)
            || float.IsNaN(origin.Z)
            || float.IsNaN(rotation.W)
            || float.IsNaN(rotation.X)
            || float.IsNaN(rotation.Y)
            || float.IsNaN(rotation.Z))
        {
            return false;
        }

        float normSquared = rotation.LengthSquared();
        return !float.IsNaN(normSquared)
            && MathF.Abs(normSquared - 1f) <= 0.001f;
    }

    /// <summary>
    /// The rotation an object description's position carries into use.
    /// A frame that fails <see cref="IsFrameValid"/> does not stop the object
    /// being created and placed. Its parts take the sent rotation scaled to
    /// unit length, or keep the rotation they had (the identity, for a newly
    /// created object) when that scaling does not give a valid frame; this
    /// client uses that one rotation for the whole object. Server world data
    /// does carry such rotations (a spawn whose stored angles are rounded, or
    /// all left empty). A valid frame's rotation is returned unchanged.
    /// </summary>
    public static Quaternion DescriptionRotationInUse(
        Vector3 origin,
        Quaternion sent)
    {
        if (IsFrameValid(origin, sent))
            return sent;

        float scale = 1f / MathF.Sqrt(sent.LengthSquared());
        var unit = new Quaternion(
            sent.X * scale,
            sent.Y * scale,
            sent.Z * scale,
            sent.W * scale);
        return IsFrameValid(origin, unit) ? unit : Quaternion.Identity;
    }
}
