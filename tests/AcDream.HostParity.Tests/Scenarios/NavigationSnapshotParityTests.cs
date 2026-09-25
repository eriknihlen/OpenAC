using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Where the character stands, as a plugin reads it off the navigation
/// snapshot, on both clients.
///
/// A client without a window can be in the world with no body for its
/// character at all: it is given no installed data files to build one from,
/// yet the server has said where the character is and every other object
/// answers from what the server said. The snapshot answered nothing in that
/// state -- not available, cell zero -- while the world-object list gave the
/// same character's position correctly, so a plugin reading the snapshot
/// thought the character was nowhere. Both shapes are staged here, with a
/// body and without one, and each arm is asserted outright, since two clients
/// that both answered "nowhere" would write identical transcripts.
///
/// Mutation checks (2026-09-22), run: restoring the early "no body, no
/// snapshot" return turned the bodiless scenario red at the availability
/// assert; making the character's own object answer "not found" without a
/// body turned it red at the own-object assert. The bodied scenario stayed
/// green under both.
/// </summary>
public sealed class NavigationSnapshotParityTests
{
    // Where ParityWorld stands the character, as map coordinates: 240 m to a
    // unit, measured from the middle of the map. The cell is landblock
    // (0xA9, 0xB4), and the character stands 96 m east and 97 m north inside
    // it. How high it is depends on who answers; see below.
    private const double ExpectedEastWest = ((0xA9 - 127) * 192d + 96d - 84d) / 240d;
    private const double ExpectedNorthSouth = ((0xB4 - 127) * 192d + 97d - 84d) / 240d;
    // The server puts the character on the ground, 50 m up; the harness body
    // is one sphere of 0.48 m centred on the character, so it comes to rest
    // with its centre that far above the ground. The server's create names the
    // landblock's first cell; a body finds the cell the spot really lies in,
    // 24 m cells eight to a row, which is the fifth row's fifth (0x25).
    private static readonly Spot Server = new(ParityPlayerBody.Cell, 50d);
    private static readonly Spot Body = new(ParityPlayerBody.Landblock | 0x0025u, 50.48d);

    private readonly record struct Spot(uint Cell, double ElevationMeters);

    // A hundredth of a metre, in map units.
    private const double Tolerance = 0.01d / 240d;

    [Fact]
    public void ACharacterWithABodyIsWhereItStandsOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            Assert.True(arm.HasLiveBody, $"{arm.Name} built no body.");

            transcript.Step("a plugin asks where the character is");
            AssertWhereTheCharacterStands(arm, transcript, Body);
        });

    [Fact]
    public void ACharacterWithNoBodyYetIsWhereTheServerSaidOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ArriveWithoutABody(arm.Runtime);
            Assert.Null(arm.Runtime.MovementOwner.Controller);

            transcript.Step("a plugin asks where the bodiless character is");
            AssertWhereTheCharacterStands(arm, transcript, Server);
        });

    /// <summary>
    /// The character arrives the way it does on a client with nothing to
    /// build a body from: the server's create is taken and kept, and no
    /// first-entry drive ever turns it into a body.
    /// </summary>
    private static void ArriveWithoutABody(GameRuntime runtime)
    {
        runtime.PlayerIdentity.ServerGuid = ParityWorld.Player;
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                ParityWorld.Spawn(
                    ParityWorld.Player,
                    ParityWorld.PlayerX,
                    ParityWorld.PlayerY,
                    ParityPlayerBody.Cell,
                    state: 0),
                isLocalPlayer: true)
            .Canonical!;
        _ = runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false);
        runtime.InventoryOwner.Objects.AddOrUpdate(
            ParityWorld.PlayerObject(ParityWorld.Player));
    }

    private static void AssertWhereTheCharacterStands(
        ParityArm arm,
        ParityTranscript transcript,
        Spot live)
    {
        INavigationAutomation navigation = arm.Host.Automation.Navigation;
        PluginNavigationSnapshot snapshot = navigation.Snapshot;
        transcript.Record("available", snapshot.IsAvailable);
        transcript.Record("local", $"0x{snapshot.LocalObjectId:X8}");
        transcript.Record("cell", $"0x{snapshot.Position.CellId:X8}");
        transcript.Record("ew", snapshot.Position.EastWest.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        transcript.Record("ns", snapshot.Position.NorthSouth.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        transcript.Record("confirmedCell", $"0x{snapshot.ConfirmedPosition.CellId:X8}");

        Assert.True(snapshot.IsAvailable, $"{arm.Name}: the snapshot says the character is not in the world.");
        Assert.Equal(ParityWorld.Player, snapshot.LocalObjectId);
        AssertStaged(arm, "position", snapshot.Position, live);
        AssertStaged(arm, "confirmed position", snapshot.ConfirmedPosition, Server);

        // Asking for the character by its own id answers the same place.
        bool found = navigation.TryGetObject(
            ParityWorld.Player,
            out PluginNavigationObject self);
        transcript.Record("self.found", found);
        Assert.True(found, $"{arm.Name}: the character's own object was not found.");
        AssertStaged(arm, "own object", self.Position, live);
    }

    private static void AssertStaged(
        ParityArm arm,
        string what,
        in PluginNavigationPosition position,
        Spot spot)
    {
        Assert.True(
            position.CellId == spot.Cell,
            $"{arm.Name}: {what} cell 0x{position.CellId:X8}, expected 0x{spot.Cell:X8}.");
        Assert.InRange(position.EastWest, ExpectedEastWest - Tolerance, ExpectedEastWest + Tolerance);
        Assert.InRange(position.NorthSouth, ExpectedNorthSouth - Tolerance, ExpectedNorthSouth + Tolerance);
        double elevation = spot.ElevationMeters / 240d;
        Assert.InRange(position.Elevation, elevation - Tolerance, elevation + Tolerance);
    }
}
