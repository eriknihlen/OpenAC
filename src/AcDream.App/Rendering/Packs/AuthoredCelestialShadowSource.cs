using System.Numerics;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Packs;

internal enum AuthoredCelestialShadowSourceKind : uint
{
    None = 0,
    Sun = 1,
    DominantMoon = 2,
    SecondaryMoon = 3,
}

internal readonly record struct AuthoredCelestialShadowSource(
    AuthoredCelestialShadowSourceKind Kind,
    int ObjectIndex,
    uint GfxObjId,
    Vector3 SurfaceToLightDirection,
    float ElevationSin,
    float AuthoredEnergy)
{
    internal static AuthoredCelestialShadowSource None(float authoredEnergy = 0f) =>
        new(
            AuthoredCelestialShadowSourceKind.None,
            -1,
            0u,
            Vector3.UnitZ,
            0f,
            Math.Clamp(authoredEnergy, 0f, 1f));

    internal bool IsAvailable =>
        Kind is not AuthoredCelestialShadowSourceKind.None;
}

internal static class AuthoredCelestialShadowSourceResolver
{
    internal const uint SunGfxObjId = 0x01001348u;
    internal const uint DominantMoonGfxObjId = 0x01001F6Au;
    internal const uint SecondaryMoonGfxObjId = 0x01001F67u;

    internal static AuthoredCelestialShadowSource Resolve(
        DayGroupData? dayGroup,
        float dayFraction,
        in SkyKeyframe sky)
    {
        float energy = Math.Clamp(
            MathF.Max(sky.SunColor.X, MathF.Max(sky.SunColor.Y, sky.SunColor.Z)),
            0f,
            1f);
        if (dayGroup is null || !float.IsFinite(dayFraction))
            return AuthoredCelestialShadowSource.None(energy);

        if (TryResolve(
                dayGroup,
                dayFraction,
                SunGfxObjId,
                AuthoredCelestialShadowSourceKind.Sun,
                energy,
                out var source)
            || TryResolve(
                dayGroup,
                dayFraction,
                DominantMoonGfxObjId,
                AuthoredCelestialShadowSourceKind.DominantMoon,
                energy,
                out source)
            || TryResolve(
                dayGroup,
                dayFraction,
                SecondaryMoonGfxObjId,
                AuthoredCelestialShadowSourceKind.SecondaryMoon,
                energy,
                out source))
        {
            return source;
        }

        return AuthoredCelestialShadowSource.None(energy);
    }

    private static bool TryResolve(
        DayGroupData dayGroup,
        float dayFraction,
        uint roleGfxObjId,
        AuthoredCelestialShadowSourceKind kind,
        float energy,
        out AuthoredCelestialShadowSource source)
    {
        for (int index = 0; index < dayGroup.SkyObjects.Count; index++)
        {
            SkyObjectData skyObject = dayGroup.SkyObjects[index];
            if (skyObject.GfxObjId != roleGfxObjId
                || !skyObject.IsVisible(dayFraction))
            {
                continue;
            }

            SkyObjectReplaceData? replace = ActiveReplace(
                dayGroup,
                dayFraction,
                checked((uint)index));
            if (replace is not null && replace.Transparent >= 1f - 1e-5f)
                continue;

            uint effectiveGfxObjId = replace is { GfxObjId: not 0u }
                ? replace.GfxObjId
                : skyObject.GfxObjId;
            Vector3 anchor = replace is { GfxObjId: not 0u }
                ? replace.AuthoredSortCenter
                : skyObject.AuthoredSortCenter;
            if (!IsFiniteDirection(anchor))
                continue;

            float headingRadians = (replace?.Rotate ?? 0f) * (MathF.PI / 180f);
            float rotationRadians = skyObject.CurrentAngle(dayFraction)
                * (MathF.PI / 180f);
            Matrix4x4 model = Matrix4x4.CreateRotationZ(-headingRadians)
                * Matrix4x4.CreateRotationY(-rotationRadians);
            Vector3 transformed = Vector3.TransformNormal(anchor, model);
            float length = transformed.Length();
            if (!float.IsFinite(length) || length <= 1e-5f)
                continue;

            Vector3 direction = transformed / length;
            if (!IsFiniteDirection(direction) || direction.Z <= 0f)
                continue;

            source = new AuthoredCelestialShadowSource(
                kind,
                index,
                effectiveGfxObjId,
                direction,
                direction.Z,
                energy);
            return true;
        }

        source = default;
        return false;
    }

    private static SkyObjectReplaceData? ActiveReplace(
        DayGroupData dayGroup,
        float dayFraction,
        uint objectIndex)
    {
        if (dayGroup.SkyTimes.Count == 0)
            return null;

        DatSkyKeyframeData active = dayGroup.SkyTimes[^1];
        for (int i = 0; i < dayGroup.SkyTimes.Count; i++)
        {
            if (dayGroup.SkyTimes[i].Keyframe.Begin <= dayFraction)
                active = dayGroup.SkyTimes[i];
            else
                break;
        }

        SkyObjectReplaceData? result = null;
        foreach (SkyObjectReplaceData replace in active.Replaces)
        {
            if (replace.ObjectIndex == objectIndex)
                result = replace;
        }
        return result;
    }

    private static bool IsFiniteDirection(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && value.LengthSquared() > 1e-10f;
}
