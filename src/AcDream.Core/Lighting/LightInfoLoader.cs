using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Lighting;

public static class LightInfoLoader
{
    public static IReadOnlyList<LightSource> Load(
        Setup setup,
        uint ownerId,
        Vector3 entityPosition,
        Quaternion entityRotation,
        bool isDynamic = false,
        uint cellId = 0,
        bool tracksOwnerPose = false)
    {
        var results = new List<LightSource>();
        if (setup?.Lights is null || setup.Lights.Count == 0) return results;

        foreach (var kvp in setup.Lights)
        {
            var info = kvp.Value;
            if (info is null) continue;

            // Local Frame offset into world space.
            Vector3 localOffset = Vector3.Zero;
            Quaternion localRot = Quaternion.Identity;
            if (info.ViewSpaceLocation is not null)
            {
                localOffset = new Vector3(
                    info.ViewSpaceLocation.Origin.X,
                    info.ViewSpaceLocation.Origin.Y,
                    info.ViewSpaceLocation.Origin.Z);
                localRot = new Quaternion(
                    info.ViewSpaceLocation.Orientation.X,
                    info.ViewSpaceLocation.Orientation.Y,
                    info.ViewSpaceLocation.Orientation.Z,
                    info.ViewSpaceLocation.Orientation.W);
            }

            Matrix4x4 localPose = Matrix4x4.CreateFromQuaternion(localRot)
                * Matrix4x4.CreateTranslation(localOffset);
            Matrix4x4 rootWorld = Matrix4x4.CreateFromQuaternion(entityRotation)
                * Matrix4x4.CreateTranslation(entityPosition);
            Matrix4x4 lightWorld = localPose * rootWorld;
            Vector3 worldPos = lightWorld.Translation;
            Vector3 forward = Vector3.TransformNormal(Vector3.UnitY, lightWorld);
            if (forward.LengthSquared() > 1e-8f)
                forward = Vector3.Normalize(forward);

            var light = new LightSource
            {
                Kind = info.ConeAngle > 0f ? LightKind.Spot : LightKind.Point,
                WorldPosition = worldPos,
                RankingOrigin = entityPosition,
                WorldForward  = forward,
                ColorLinear   = new Vector3(
                    (info.Color?.Red   ?? 255) / 255f,
                    (info.Color?.Green ?? 255) / 255f,
                    (info.Color?.Blue  ?? 255) / 255f),
                Intensity = info.Intensity,
                Range     = info.Falloff * (isDynamic ? 1.5f : 1.3f),
                ConeAngle = info.ConeAngle,
                OwnerId   = ownerId,
                CellId    = cellId,
                IsLit     = true,
                IsDynamic = isDynamic,
                TracksOwnerPose = tracksOwnerPose,
                LocalPose = localPose,
            };
            results.Add(light);
        }

        return results;
    }
}
