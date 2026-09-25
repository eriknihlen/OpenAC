using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A summoner looks for room in front of the character before it calls a pet.
/// Both clients answer from their own collision, the one physics world the
/// runtime owns, so open ground, a wall and a creature in that spot have to
/// read the same on both -- and each answer is asserted, because two clients
/// that both said "unknown" would agree line for line and prove nothing.
///
/// Mutation check (2026-09-25), run: answering the host default (unknown) from
/// the runtime turned the open-ground, wall and creature scenarios red; letting
/// the body slide aside turned the wall red; counting creatures turned the
/// creature scenario red.
/// </summary>
public sealed class RoomAheadParityTests
{
    /// <summary>Something solid three metres north of the character, where the body would stand.</summary>
    private const uint Wall = 0x80007001u;

    /// <summary>A creature standing in that same spot.</summary>
    private const uint Creature = 0x80007002u;

    [Fact]
    public void OpenGroundAheadIsRoomOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;
            double north = navigation.Snapshot.Position.NorthSouth;

            transcript.Step("look three metres ahead");
            PluginRoomAhead room = navigation.CheckRoomAhead(3f);
            Record(transcript, room);

            Assert.Equal(PluginRoomAheadStatus.Clear, room.Status);
            Assert.Equal(3d, (room.Position.NorthSouth - north) * 240d, 2);
        });

    [Fact]
    public void AWallAheadLeavesNoRoomOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;
            RegisterAhead(arm, Wall, EntityCollisionFlags.None);

            transcript.Step("look three metres ahead at a wall");
            PluginRoomAhead room = navigation.CheckRoomAhead(3f);
            Record(transcript, room);

            Assert.Equal(PluginRoomAheadStatus.Blocked, room.Status);
        });

    [Fact]
    public void ACreatureAheadStillLeavesRoomOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;
            RegisterAhead(arm, Creature, EntityCollisionFlags.IsCreature);

            transcript.Step("look three metres ahead at a creature");
            PluginRoomAhead room = navigation.CheckRoomAhead(3f);
            Record(transcript, room);

            Assert.Equal(PluginRoomAheadStatus.Clear, room.Status);
        });

    [Fact]
    public void ADistanceOutOfRangeIsUnknownOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;

            transcript.Step("look eleven metres ahead");
            PluginRoomAhead room = navigation.CheckRoomAhead(11f);
            Record(transcript, room);

            Assert.Equal(PluginRoomAheadStatus.Unknown, room.Status);
        });

    /// <summary>
    /// A tall cylinder where the body would stand: three metres north of the
    /// character, who faces north when it arrives.
    /// </summary>
    private static void RegisterAhead(ParityArm arm, uint id, EntityCollisionFlags flags) =>
        arm.Runtime.EntityObjects.Physics.Engine.ShadowObjects.Register(
            id,
            gfxObjId: 0u,
            worldPos: new Vector3(
                ParityWorld.PlayerX,
                ParityWorld.PlayerY + 3f,
                ParityPlayerBody.GroundHeight),
            rotation: Quaternion.Identity,
            radius: 1f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: ParityPlayerBody.Landblock | 0xFFFFu,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: 4f,
            flags: flags,
            seedCellId: ParityPlayerBody.Cell,
            isStatic: false);

    private static void Record(ParityTranscript transcript, PluginRoomAhead room)
    {
        transcript.Record("status", room.Status);
        transcript.Record("cell", room.Position.CellId);
        transcript.Record("east", Math.Round(room.Position.EastWest * 240d, 2));
        transcript.Record("north", Math.Round(room.Position.NorthSouth * 240d, 2));
        transcript.Record("height", Math.Round(room.Position.Elevation * 240d, 2));
    }
}
