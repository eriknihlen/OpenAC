using System.Buffers.Binary;
using System.Linq;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;
using AcDream.Core.Spells;
using AcDream.Core.Ui;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ClientCommandResponsesTests
{

    [Fact]
    public void ParseChannelIndex_RoundTrips()
    {
        byte[] wire = new AceWireWriter()
            .Write((uint)2)
            .WriteString16L("Admin")
            .WriteString16L("Help")
            .ToArray();

        var channels = ClientCommandResponses.ParseChannelIndex(wire);

        Assert.NotNull(channels);
        Assert.Equal(new[] { "Admin", "Help" }, channels);
    }

    [Fact]
    public void ParseChannelIndex_EmptyList_ParsesToEmpty()
    {
        byte[] wire = new AceWireWriter().Write((uint)0).ToArray();

        var channels = ClientCommandResponses.ParseChannelIndex(wire);

        Assert.NotNull(channels);
        Assert.Empty(channels);
    }

    [Fact]
    public void FormatChannelIndexLines_MatchesRetailHeaderAndOrder()
    {
        var lines = ClientCommandResponses.FormatChannelIndexLines(new[] { "Admin", "Help" }).ToArray();

        Assert.Equal(
            new[]
            {
                "The following channels are available to you:",
                "Admin",
                "Help",
            },
            lines);
    }

    [Fact]
    public void ParseChannelList_RoundTrips()
    {
        byte[] wire = new AceWireWriter()
            .Write((uint)3)
            .WriteString16L("Caith")
            .WriteString16L("Vandal")
            .WriteString16L("Elysia")
            .ToArray();

        var names = ClientCommandResponses.ParseChannelList(wire);

        Assert.NotNull(names);
        Assert.Equal(new[] { "Caith", "Vandal", "Elysia" }, names);
    }

    [Fact]
    public void FormatChannelListLines_MatchesRetailHeaderAndOrder()
    {
        var lines = ClientCommandResponses.FormatChannelListLines(new[] { "Caith" }).ToArray();

        Assert.Equal(
            new[]
            {
                "The following characters are currently listening on the channel:",
                "Caith",
            },
            lines);
    }

    // ── AvailableHouses ──────────────────────────────────────────────────────

    private const uint TestVillaLandblockId = 0x0A0A0001u;

    [Fact]
    public void ParseAvailableHouses_RoundTrips()
    {
        byte[] wire = new AceWireWriter()
            .Write((uint)2) // HouseType.Villa
            .Write((uint)1)
            .Write(TestVillaLandblockId)
            .Write(5) // totalAvailable
            .ToArray();

        var response = ClientCommandResponses.ParseAvailableHouses(wire);

        Assert.NotNull(response);
        Assert.Equal(2u, response.Value.HouseType);
        Assert.Equal(new[] { TestVillaLandblockId }, response.Value.Locations);
        Assert.Equal(5, response.Value.TotalAvailable);
    }

    [Fact]
    public void FormatAvailableHousesLines_VillasIncludesSummaryAndCoordinate()
    {
        var response = new ClientCommandResponses.AvailableHousesResponse(
            HouseType: 2u,
            Locations: new[] { TestVillaLandblockId },
            TotalAvailable: 5);

        var lines = ClientCommandResponses.FormatAvailableHousesLines(response).ToArray();

        Assert.True(RadarCoordinates.TryFromCell(TestVillaLandblockId, out var coordinates));
        Assert.Equal(
            new[]
            {
                "There are 5 villas available.",
                $"     {coordinates.YText}, {coordinates.XText}",
            },
            lines);
    }

    [Fact]
    public void FormatAvailableHousesLines_ApartmentsSkipsCoordinateList()
    {
        var response = new ClientCommandResponses.AvailableHousesResponse(
            HouseType: 4u,
            Locations: new[] { TestVillaLandblockId },
            TotalAvailable: 3);

        var lines = ClientCommandResponses.FormatAvailableHousesLines(response).ToArray();

        Assert.Equal(new[] { "There are 3 apartments available." }, lines);
    }

    [Fact]
    public void FormatAvailableHousesLines_OverFourHundred_AddsTruncationNotice()
    {
        var response = new ClientCommandResponses.AvailableHousesResponse(
            HouseType: 1u,
            Locations: System.Array.Empty<uint>(),
            TotalAvailable: 401);

        var lines = ClientCommandResponses.FormatAvailableHousesLines(response).ToArray();

        Assert.Equal(
            new[]
            {
                "There are 401 cottages available.",
                "There were too many houses to display all the locations. Only the first 400 locations are displayed here.",
            },
            lines);
    }

    // ── AllegianceInfoResponse ───────────────────────────────────────────────

    private static byte[] BuildAllegianceWire(
        uint targetGuid,
        System.Collections.Generic.List<(uint characterId, uint parentGuid, bool loggedIn, string name)> records)
    {
        var w = new AceWireWriter()
            .Write(targetGuid)
            .Write((uint)(records.Count))
            .Write((uint)0)
            .Write((ushort)records.Count) // recordCount
            .Write((ushort)0x000B) // oldVersion
            // officers PackableHashTable header: 0 entries, 256 buckets.
            .Write((ushort)0)
            .Write((ushort)256)
            .Write((uint)0)
            .Write((uint)0) // monarchBroadcastTime
            .Write((uint)0) // monarchBroadcastsToday
            .Write((uint)0) // spokesBroadcastTime
            .Write((uint)0) // spokesBroadcastsToday
            .WriteString16L("") // motd
            .WriteString16L("") // motdSetBy
            .Write((uint)0)
            .Write((uint)0)
            .Write(0f).Write(0f).Write(0f)
            .Write(0f).Write(0f).Write(0f).Write(0f)
            .WriteString16L("Test Allegiance") // allegianceName
            .Write((uint)0) // nameLastSetTime
            .Write((uint)0) // isLocked
            .Write(0); // approvedVassal

        for (int i = 0; i < records.Count; i++)
        {
            var (characterId, parentGuid, loggedIn, name) = records[i];
            if (i > 0)
                w.Write(parentGuid); // the wire's own "treeParent" tag precedes non-monarch records.

            uint bitfield = 0x4u | 0x8u;
            if (loggedIn) bitfield |= 0x1u; // LoggedIn

            w.Write(characterId)
                .Write((uint)0)
                .Write((uint)0)
                .Write(bitfield)
                .Write((byte)0) // gender
                .Write((byte)0) // heritage group
                .Write((ushort)1) // rank
                .Write((uint)5) // level (HasPackedLevel set)
                .Write((ushort)0) // loyalty
                .Write((ushort)0) // leadership
                .Write((uint)0) // timeOnline (HasAllegianceAge set)
                .Write((uint)0) // allegianceAge
                .WriteString16L(name);
        }

        return w.ToArray();
    }

    [Fact]
    public void ParseAllegianceInfoResponse_MonarchOnly_RoundTrips()
    {
        const uint monarchGuid = 0x50000010u;
        byte[] wire = BuildAllegianceWire(
            monarchGuid,
            new() { (monarchGuid, 0u, true, "Grandmaster") });

        var response = ClientCommandResponses.ParseAllegianceInfoResponse(wire);

        Assert.NotNull(response);
        Assert.Equal(monarchGuid, response.Value.TargetGuid);
        Assert.Equal((ushort)1, response.Value.RecordCount);
        Assert.Equal("Test Allegiance", response.Value.AllegianceName);
        Assert.NotNull(response.Value.Monarch);
        Assert.Equal("Grandmaster", response.Value.Monarch!.Value.Name);
        Assert.True(response.Value.Monarch!.Value.IsLoggedIn);
        Assert.Empty(response.Value.Records);
    }

    [Fact]
    public void FormatAllegianceInfoLines_MonarchOnly_PrintsHeaderAndSelfNoPatronNoVassals()
    {
        const uint monarchGuid = 0x50000010u;
        var response = new ClientCommandResponses.AllegianceInfoResponse(
            TargetGuid: monarchGuid,
            TotalMembers: 1,
            TotalVassals: 0,
            RecordCount: 1,
            AllegianceName: "Test Allegiance",
            Monarch: new ClientCommandResponses.AllegianceMemberRecord(monarchGuid, 0u, true, "Grandmaster"),
            Records: System.Array.Empty<ClientCommandResponses.AllegianceMemberRecord>());

        var lines = ClientCommandResponses.FormatAllegianceInfoLines(response).ToArray();

        Assert.Equal(
            new[]
            {
                "Note: An asterisk (*) indicates that the character is currently online.",
                "Allegiance information for Grandmaster *:",
            },
            lines);
    }

    [Fact]
    public void ParseAndFormatAllegianceInfoResponse_PatronAndVassals_RendersFullTree()
    {
        const uint monarchGuid = 0x50000001u;
        const uint patronGuid = 0x50000002u;
        const uint selfGuid = 0x50000003u;
        const uint vassalGuid = 0x50000004u;

        // Records order matches AllegianceHierarchy.Write: patron (parent=monarch),
        // self (parent=patron), then vassals (parent=self).
        byte[] wire = BuildAllegianceWire(
            selfGuid,
            new()
            {
                (monarchGuid, 0u, false, "Monarch"),
                (patronGuid, monarchGuid, true, "Patron"),
                (selfGuid, patronGuid, false, "Self"),
                (vassalGuid, selfGuid, true, "Vassal"),
            });

        var response = ClientCommandResponses.ParseAllegianceInfoResponse(wire);
        Assert.NotNull(response);
        Assert.Equal(3, response.Value.Records.Count);

        var lines = ClientCommandResponses.FormatAllegianceInfoLines(response.Value).ToArray();

        Assert.Equal(
            new[]
            {
                "Note: An asterisk (*) indicates that the character is currently online.",
                "Allegiance information for Self:",
                "   Patron: Patron *",
                "   Vassals: ",
                "      Vassal *",
            },
            lines);
    }

    [Fact]
    public void ParseAndFormatAllegianceInfoResponse_ThreeVassals_PrintsInReverseWireOrder()
    {
        const uint monarchGuid = 0x50000001u;
        const uint vassalAGuid = 0x50000010u;
        const uint vassalBGuid = 0x50000011u;
        const uint vassalCGuid = 0x50000012u;

        byte[] wire = BuildAllegianceWire(
            monarchGuid,
            new()
            {
                (monarchGuid, 0u, true, "Monarch"),
                (vassalAGuid, monarchGuid, true, "VassalA"),
                (vassalBGuid, monarchGuid, true, "VassalB"),
                (vassalCGuid, monarchGuid, true, "VassalC"),
            });

        var response = ClientCommandResponses.ParseAllegianceInfoResponse(wire);
        Assert.NotNull(response);

        var lines = ClientCommandResponses.FormatAllegianceInfoLines(response.Value).ToArray();

        Assert.Equal(
            new[]
            {
                "Note: An asterisk (*) indicates that the character is currently online.",
                "Allegiance information for Monarch *:",
                "   Vassals: ",
                "      VassalC *",
                "      VassalB *",
                "      VassalA *",
            },
            lines);
    }

    [Fact]
    public void WireAll_AllegianceInfoResponse_MalformedTree_PrintsNothing()
    {
        var dispatcher = new GameEventDispatcher();
        var chat = new ChatLog();
        GameEventWiring.WireAll(dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), chat);

        const uint monarchGuid = 0x50000001u;
        byte[] payload = BuildAllegianceWire(
            monarchGuid,
            new()
            {
                (monarchGuid, 0u, true, "Monarch"),
                (0x50000005u, 0x5000FFFFu /* never-seen parent — orphan */, true, "Orphan"),
            });

        GameEventEnvelope? envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.AllegianceInfoResponse, payload));
        Assert.NotNull(envelope);
        dispatcher.Dispatch(envelope.Value);

        Assert.Empty(chat.Snapshot());
    }

    [Fact]
    public void FormatAllegianceInfoLines_NoAllegiance_PrintsNothing()
    {
        const uint targetGuid = 0x50000099u;
        var response = new ClientCommandResponses.AllegianceInfoResponse(
            TargetGuid: targetGuid,
            TotalMembers: 0,
            TotalVassals: 0,
            RecordCount: 0,
            AllegianceName: "",
            Monarch: null,
            Records: System.Array.Empty<ClientCommandResponses.AllegianceMemberRecord>());

        Assert.Empty(ClientCommandResponses.FormatAllegianceInfoLines(response));
    }

    [Fact]
    public void ParseAllegianceInfoResponse_EmptyWire_ParsesToNoRecords()
    {
        const uint targetGuid = 0x50000099u;
        byte[] wire = BuildAllegianceWire(targetGuid, new());

        var response = ClientCommandResponses.ParseAllegianceInfoResponse(wire);

        Assert.NotNull(response);
        Assert.Equal((ushort)0, response.Value.RecordCount);
        Assert.Null(response.Value.Monarch);
        Assert.Empty(response.Value.Records);
        Assert.Empty(ClientCommandResponses.FormatAllegianceInfoLines(response.Value));
    }


    private static byte[] WrapEnvelope(GameEventType type, byte[] payload)
    {
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,             GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),   0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),   0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12),  (uint)type);
        payload.CopyTo(body, GameEventEnvelope.HeaderSize);
        return body;
    }

    [Fact]
    public void WireAll_ChannelIndex_ReachesChatTranscriptAsDefaultLogTextType()
    {
        var dispatcher = new GameEventDispatcher();
        var chat = new ChatLog();
        GameEventWiring.WireAll(dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), chat);

        byte[] payload = new AceWireWriter()
            .Write((uint)1)
            .WriteString16L("Sentinel")
            .ToArray();

        GameEventEnvelope? envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.ChannelIndex, payload));
        Assert.NotNull(envelope);
        dispatcher.Dispatch(envelope.Value);

        ChatEntry[] entries = chat.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, e => Assert.Equal(ChatKind.System, e.Kind));
        Assert.All(entries, e => Assert.Equal((uint)RetailLogTextType.Default, e.ChannelId));
        Assert.Equal("The following channels are available to you:", entries[0].Text);
        Assert.Equal("Sentinel", entries[1].Text);
    }

    [Fact]
    public void WireAll_AvailableHouses_ReachesChatTranscript()
    {
        var dispatcher = new GameEventDispatcher();
        var chat = new ChatLog();
        GameEventWiring.WireAll(dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), chat);

        byte[] payload = new AceWireWriter()
            .Write((uint)2)
            .Write((uint)1)
            .Write(TestVillaLandblockId)
            .Write(2)
            .ToArray();

        GameEventEnvelope? envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.AvailableHouses, payload));
        Assert.NotNull(envelope);
        dispatcher.Dispatch(envelope.Value);

        ChatEntry[] entries = chat.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal("There are 2 villas available.", entries[0].Text);
        Assert.All(entries, e => Assert.Equal((uint)RetailLogTextType.Default, e.ChannelId));
    }

    [Fact]
    public void WireAll_AllegianceInfoResponse_ReachesChatTranscript()
    {
        var dispatcher = new GameEventDispatcher();
        var chat = new ChatLog();
        GameEventWiring.WireAll(dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), chat);

        const uint monarchGuid = 0x50000010u;
        byte[] payload = BuildAllegianceWire(
            monarchGuid,
            new() { (monarchGuid, 0u, false, "Grandmaster") });

        GameEventEnvelope? envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.AllegianceInfoResponse, payload));
        Assert.NotNull(envelope);
        dispatcher.Dispatch(envelope.Value);

        ChatEntry[] entries = chat.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.Equal("Note: An asterisk (*) indicates that the character is currently online.", entries[0].Text);
        Assert.Equal("Allegiance information for Grandmaster:", entries[1].Text);
        Assert.All(entries, e => Assert.Equal((uint)RetailLogTextType.Default, e.ChannelId));
    }
}
