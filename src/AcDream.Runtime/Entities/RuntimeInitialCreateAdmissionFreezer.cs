using System.Collections.Immutable;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Entities;

internal static class RuntimeInitialCreateAdmissionFreezer
{
    internal static WorldSession.EntitySpawn Freeze(
        in WorldSession.EntitySpawn spawn) => spawn with
    {
        AnimPartChanges = spawn.AnimPartChanges.ToImmutableArray(),
        TextureChanges = spawn.TextureChanges.ToImmutableArray(),
        SubPalettes = spawn.SubPalettes.ToImmutableArray(),
        MotionState = Freeze(spawn.MotionState),
        Physics = Freeze(spawn.Physics),
    };

    internal static ObjDescEvent.Parsed Freeze(
        in ObjDescEvent.Parsed update) => update with
    {
        ModelData = Freeze(update.ModelData),
    };

    internal static WorldSession.EntityMotionUpdate Freeze(
        in WorldSession.EntityMotionUpdate update) => update with
    {
        MotionState = Freeze(update.MotionState),
    };

    internal static RuntimeInitialCreateTailAction Freeze(
        in RuntimeInitialCreateTailAction action) => action with
    {
        Description = Freeze(action.Description),
        ObjDesc = action.ObjDesc is { } objDesc
            ? Freeze(objDesc)
            : null,
        Movement = action.Movement is { } movement
            ? Freeze(movement)
            : null,
        WeenieDescription = action.WeenieDescription is { } spawn
            ? Freeze(spawn)
            : null,
    };

    private static CreateObject.ModelData Freeze(
        in CreateObject.ModelData model) => model with
    {
        SubPalettes = model.SubPalettes.ToImmutableArray(),
        TextureChanges = model.TextureChanges.ToImmutableArray(),
        AnimPartChanges = model.AnimPartChanges.ToImmutableArray(),
    };

    private static CreateObject.ServerMotionState Freeze(
        in CreateObject.ServerMotionState motion) => motion with
    {
        Commands = motion.Commands?.ToImmutableArray(),
    };

    private static CreateObject.ServerMotionState? Freeze(
        CreateObject.ServerMotionState? motion) => motion is { } value
            ? Freeze(value)
            : null;

    private static PhysicsSpawnData? Freeze(PhysicsSpawnData? physics)
    {
        if (physics is not { } value)
            return null;

        PhysicsMovementData? movement = value.Movement is { } source
            ? source with
            {
                RawData = source.RawData.ToArray(),
                MotionState = Freeze(source.MotionState),
            }
            : null;
        ReadOnlyMemory<PhysicsAttachment>? children = value.Children is { } list
            ? list.ToArray()
            : null;
        return value with
        {
            Movement = movement,
            Children = children,
        };
    }
}
