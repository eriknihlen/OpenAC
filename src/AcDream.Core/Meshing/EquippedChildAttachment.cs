using System.Numerics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Meshing;

public readonly record struct EquippedChildPose(
    Matrix4x4 RootLocal,
    Matrix4x4[] PartLocal,
    MeshRef[] AttachedParts);

public static class EquippedChildAttachment
{
    public static bool TryCompose(
        Setup parentSetup,
        IReadOnlyList<MeshRef> currentParentPose,
        Setup childSetup,
        ParentLocation parentLocation,
        Placement placement,
        IReadOnlyList<MeshRef> childPartTemplate,
        float childScale,
        out IReadOnlyList<MeshRef> attachedParts)
    {
        bool composed = TryComposePose(
            parentSetup,
            currentParentPose,
            childSetup,
            parentLocation,
            placement,
            childPartTemplate,
            childScale,
            out EquippedChildPose pose);
        attachedParts = composed ? pose.AttachedParts : Array.Empty<MeshRef>();
        return composed;
    }

    public static bool TryComposePose(
        Setup parentSetup,
        IReadOnlyList<MeshRef> currentParentPose,
        Setup childSetup,
        ParentLocation parentLocation,
        Placement placement,
        IReadOnlyList<MeshRef> childPartTemplate,
        float childScale,
        out EquippedChildPose pose)
    {
        ArgumentNullException.ThrowIfNull(currentParentPose);
        var parentParts = new Matrix4x4[currentParentPose.Count];
        for (int i = 0; i < parentParts.Length; i++)
            parentParts[i] = currentParentPose[i].PartTransform;
        return TryComposePose(
            parentSetup,
            parentParts,
            childSetup,
            parentLocation,
            placement,
            childPartTemplate,
            childScale,
            out pose);
    }

    public static bool TryComposePose(
        Setup parentSetup,
        IReadOnlyList<Matrix4x4> currentParentPose,
        Setup childSetup,
        ParentLocation parentLocation,
        Placement placement,
        IReadOnlyList<MeshRef> childPartTemplate,
        float childScale,
        out EquippedChildPose pose)
    {
        return TryComposePoseInto(
            parentSetup,
            currentParentPose,
            parentPartAvailability: null,
            childSetup,
            parentLocation,
            placement,
            childPartTemplate,
            childScale,
            partPoseBuffer: null,
            attachedPartBuffer: null,
            out pose);
    }

    public static bool TryComposePoseInto(
        Setup parentSetup,
        IReadOnlyList<Matrix4x4> currentParentPose,
        IReadOnlyList<bool>? parentPartAvailability,
        Setup childSetup,
        ParentLocation parentLocation,
        Placement placement,
        IReadOnlyList<MeshRef> childPartTemplate,
        float childScale,
        Matrix4x4[]? partPoseBuffer,
        MeshRef[]? attachedPartBuffer,
        out EquippedChildPose pose)
    {
        ArgumentNullException.ThrowIfNull(parentSetup);
        ArgumentNullException.ThrowIfNull(currentParentPose);
        ArgumentNullException.ThrowIfNull(childSetup);
        ArgumentNullException.ThrowIfNull(childPartTemplate);

        if (!parentSetup.HoldingLocations.TryGetValue(parentLocation, out LocationType? holding))
        {
            pose = new EquippedChildPose(
                Matrix4x4.Identity,
                Array.Empty<Matrix4x4>(),
                Array.Empty<MeshRef>());
            return false;
        }

        Matrix4x4 parentPart;
        if (holding.PartId >= 0 && holding.PartId < currentParentPose.Count)
        {
            if (parentPartAvailability is not null
                && (holding.PartId >= parentPartAvailability.Count
                    || !parentPartAvailability[(int)holding.PartId]))
            {
                pose = default;
                return false;
            }
            parentPart = currentParentPose[(int)holding.PartId];
        }
        else
        {
            parentPart = Matrix4x4.Identity;
        }
        Matrix4x4 holdingFrame = ToMatrix(holding.Frame);
        Matrix4x4 childRoot = holdingFrame * parentPart;

        AnimationFrame? placementFrame = null;
        if (!childSetup.PlacementFrames.TryGetValue(placement, out placementFrame))
            childSetup.PlacementFrames.TryGetValue(Placement.Default, out placementFrame);

        int partCount = Math.Min(childSetup.Parts.Count, childPartTemplate.Count);
        MeshRef[] result = attachedPartBuffer is { Length: var attachedLength }
            && attachedLength == partCount
                ? attachedPartBuffer
                : new MeshRef[partCount];
        Matrix4x4[] childPartPoses = partPoseBuffer is { Length: var poseLength }
            && poseLength == partCount
                ? partPoseBuffer
                : new Matrix4x4[partCount];
        for (int i = 0; i < partCount; i++)
        {
            Frame partFrame = placementFrame is not null && i < placementFrame.Frames.Count
                ? placementFrame.Frames[i]
                : new Frame { Orientation = Quaternion.Identity };
            Vector3 defaultScale = i < childSetup.DefaultScale.Count
                ? childSetup.DefaultScale[i]
                : Vector3.One;
            Matrix4x4 visualChildPart = Matrix4x4.CreateScale(defaultScale)
                * ToMatrix(partFrame);
            if (childScale != 1.0f)
                visualChildPart *= Matrix4x4.CreateScale(childScale);

            Matrix4x4 rigidChildPart = Matrix4x4.CreateFromQuaternion(partFrame.Orientation)
                * Matrix4x4.CreateTranslation(partFrame.Origin * childScale);

            childPartPoses[i] = rigidChildPart;

            MeshRef template = childPartTemplate[i];
            result[i] = new MeshRef(template.GfxObjId, visualChildPart * childRoot)
            {
                SurfaceOverrides = template.SurfaceOverrides,
            };
        }

        pose = new EquippedChildPose(childRoot, childPartPoses, result);
        return true;
    }

    private static Matrix4x4 ToMatrix(Frame frame) =>
        Matrix4x4.CreateFromQuaternion(frame.Orientation)
        * Matrix4x4.CreateTranslation(frame.Origin);
}
