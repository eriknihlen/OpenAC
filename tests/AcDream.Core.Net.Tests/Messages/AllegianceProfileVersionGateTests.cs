using System.Collections.Generic;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class AllegianceProfileVersionGateTests
{
    private const uint MonarchGuid = 0x50000001u;

    private static readonly List<(uint characterId, uint parentGuid, bool loggedIn, string name)> OneMonarch =
        new() { (MonarchGuid, 0u, true, "Monarch") };

    private static byte[] BuildProfileWire(
        uint leadingField,
        ushort oldVersion,
        List<(uint characterId, uint parentGuid, bool loggedIn, string name)> records,
        int officerEntries = 0,
        int officerTitleEntries = 0,
        string motd = "TheMotd",
        string motdSetBy = "MotdSetter",
        uint chatRoomId = 0x77770000u,
        string allegianceName = "TestAllegiance",
        uint nameLastSetTime = 0x88880000u,
        bool isLocked = true,
        uint approvedVassal = 0x99990000u)
    {
        var w = new AceWireWriter()
            .Write(leadingField)
            .Write((uint)records.Count)  // totalMembers
            .Write((uint)0)              // totalVassals
            .Write((ushort)records.Count) // recordCount
            .Write(oldVersion);

        // §4.2 gates 1/2.
        if (oldVersion >= 6)
        {
            w.Write((ushort)officerEntries).Write((ushort)256);
            for (int i = 0; i < officerEntries; i++)
                w.Write(0x60000000u + (uint)i).Write((uint)2);
        }
        else if (oldVersion >= 1)
        {
            w.Write(0xDEADBEEFu); // old single spokesperson id
        }

        // §4.2 gate 3.
        if (oldVersion >= 9)
        {
            w.Write((uint)officerTitleEntries);
            for (int i = 0; i < officerTitleEntries; i++)
                w.WriteString16L($"Title{i}");
        }

        if (oldVersion >= 2)
            w.Write((uint)1).Write((uint)2).Write((uint)3).Write((uint)4);

        // §4.2 gate 5 (MotdAdded).
        if (oldVersion >= 3)
            w.WriteString16L(motd).WriteString16L(motdSetBy);

        if (oldVersion >= 4)
            w.Write(chatRoomId);

        // §4.2 gate 7 (Bindstones) — 32-byte Position.
        if (oldVersion >= 7)
        {
            w.Write((uint)0)
                .Write(1f).Write(2f).Write(3f)
                .Write(1f).Write(0f).Write(0f).Write(0f);
        }

        // §4.2 gate 8 (AllegianceName).
        if (oldVersion >= 8)
            w.WriteString16L(allegianceName).Write(nameLastSetTime);

        // §4.2 gate 9 (LockedState).
        if (oldVersion >= 10)
            w.Write(isLocked ? 1u : 0u);

        // §4.2 gate 10 (ApprovedVassal).
        if (oldVersion >= 11)
            w.Write(approvedVassal);

        for (int i = 0; i < records.Count; i++)
        {
            (uint characterId, uint parentGuid, bool loggedIn, string name) = records[i];
            if (i > 0)
                w.Write(parentGuid);

            uint bitfield = 0x4u | 0x8u; // HasAllegianceAge | HasPackedLevel
            if (loggedIn) bitfield |= 0x1u;

            w.Write(characterId)
                .Write((uint)0).Write((uint)0)
                .Write(bitfield)
                .Write((byte)0).Write((byte)0)  // gender, heritage
                .Write((ushort)1)               // rank
                .Write((uint)5)                 // level
                .Write((ushort)0).Write((ushort)0) // loyalty, leadership
                .Write((uint)0).Write((uint)0)     // timeOnline, allegianceAge
                .WriteString16L(name);
        }

        return w.ToArray();
    }

    private static ClientCommandResponses.AllegianceInfoResponse ParseAt(
        ushort oldVersion,
        List<(uint, uint, bool, string)> records,
        int officerEntries = 0,
        int officerTitleEntries = 0)
    {
        byte[] wire = BuildProfileWire(
            MonarchGuid, oldVersion, records,
            officerEntries: officerEntries, officerTitleEntries: officerTitleEntries);
        var parsed = ClientCommandResponses.ParseAllegianceInfoResponse(wire);
        Assert.NotNull(parsed);
        return parsed!.Value;
    }

    // ── Gate 1/2 boundary: v0 (neither) → v1 (old spokesperson 4-byte skip) ──

    [Fact]
    public void VersionGate_0to1_OldSpokespersonSkipTurnsOn()
    {
        var v0 = ParseAt(0, OneMonarch);
        var v1 = ParseAt(1, OneMonarch);

        Assert.Equal((ushort)0, v0.OldVersion);
        Assert.Equal((ushort)1, v1.OldVersion);
        Assert.Equal("Monarch", v0.Monarch!.Value.Name);
        Assert.Equal("Monarch", v1.Monarch!.Value.Name);
        Assert.Equal(MonarchGuid, v0.Monarch!.Value.CharacterId);
        Assert.Equal(MonarchGuid, v1.Monarch!.Value.CharacterId);
    }


    [Fact]
    public void VersionGate_1to2_BroadcastCountersTurnOn()
    {
        var v1 = ParseAt(1, OneMonarch);
        var v2 = ParseAt(2, OneMonarch);

        Assert.Equal("Monarch", v1.Monarch!.Value.Name);
        Assert.Equal("Monarch", v2.Monarch!.Value.Name);
    }

    // ── Gate 5: v2 → v3 (MotdAdded) ───────────────────────────────────────

    [Fact]
    public void VersionGate_2to3_MotdTurnsOn()
    {
        var v2 = ParseAt(2, OneMonarch);
        var v3 = ParseAt(3, OneMonarch);

        Assert.Equal("", v2.Motd);
        Assert.Equal("", v2.MotdSetBy);
        Assert.Equal("TheMotd", v3.Motd);
        Assert.Equal("MotdSetter", v3.MotdSetBy);
        Assert.Equal("Monarch", v3.Monarch!.Value.Name);
    }


    [Fact]
    public void VersionGate_3to4_ChatRoomIdTurnsOn()
    {
        var v3 = ParseAt(3, OneMonarch);
        var v4 = ParseAt(4, OneMonarch);

        Assert.Equal(0u, v3.ChatRoomId);
        Assert.Equal(0x77770000u, v4.ChatRoomId);
        Assert.Equal("Monarch", v4.Monarch!.Value.Name);
    }

    // ── v4 → v5: BannedCharactersAdded (version 5) gates NOTHING in UnPack ──
    // (lane C §4.2 note) — this is the negative proof: v5 must parse
    // structurally IDENTICALLY to v4 since no new field exists between them.

    [Fact]
    public void VersionGate_4to5_BannedCharactersAddedGatesNothing()
    {
        var v4 = ParseAt(4, OneMonarch);
        var v5 = ParseAt(5, OneMonarch);

        Assert.Equal(v4.ChatRoomId, v5.ChatRoomId);
        Assert.Equal(v4.Motd, v5.Motd);
        Assert.Equal("Monarch", v5.Monarch!.Value.Name);
    }


    [Fact]
    public void VersionGate_5to6_OfficersTableReplacesSpokespersonSkip()
    {
        var v5 = ParseAt(5, OneMonarch);
        var v6 = ParseAt(6, OneMonarch, officerEntries: 2);

        Assert.Equal((ushort)5, v5.OldVersion);
        Assert.Equal((ushort)6, v6.OldVersion);
        Assert.Equal("Monarch", v5.Monarch!.Value.Name);
        Assert.Equal("Monarch", v6.Monarch!.Value.Name);
    }

    // ── Gate: v6 → v7 (Bindstones — 32-byte Position) ────────────────────────

    [Fact]
    public void VersionGate_6to7_BindPointTurnsOn()
    {
        var v6 = ParseAt(6, OneMonarch);
        var v7 = ParseAt(7, OneMonarch);

        Assert.Equal("Monarch", v6.Monarch!.Value.Name);
        Assert.Equal("Monarch", v7.Monarch!.Value.Name);
    }

    // ── Gate 8: v7 → v8 (AllegianceName + NameLastSetTime) ───────────────────

    [Fact]
    public void VersionGate_7to8_AllegianceNameTurnsOn()
    {
        var v7 = ParseAt(7, OneMonarch);
        var v8 = ParseAt(8, OneMonarch);

        Assert.Equal("", v7.AllegianceName);
        Assert.Equal(0u, v7.NameLastSetTime);
        Assert.Equal("TestAllegiance", v8.AllegianceName);
        Assert.Equal(0x88880000u, v8.NameLastSetTime);
        Assert.Equal("Monarch", v8.Monarch!.Value.Name);
    }

    // ── Gate 3: v8 → v9 (OfficersTitlesAdded) ────────────────────────────────

    [Fact]
    public void VersionGate_8to9_OfficerTitlesTurnOn()
    {
        var v8 = ParseAt(8, OneMonarch);
        var v9 = ParseAt(9, OneMonarch, officerTitleEntries: 2);

        Assert.Equal("Monarch", v8.Monarch!.Value.Name);
        Assert.Equal("Monarch", v9.Monarch!.Value.Name);
    }

    // ── Gate 9: v9 → v10 (LockedState) ───────────────────────────────────────

    [Fact]
    public void VersionGate_9to10_IsLockedTurnsOn()
    {
        var v9 = ParseAt(9, OneMonarch);
        var v10 = ParseAt(10, OneMonarch);

        Assert.False(v9.IsLocked);
        Assert.True(v10.IsLocked);
        Assert.Equal("Monarch", v10.Monarch!.Value.Name);
    }

    // ── Gate 10: v10 → v11 (ApprovedVassal) ──────────────────────────────────

    [Fact]
    public void VersionGate_10to11_ApprovedVassalTurnsOn()
    {
        var v10 = ParseAt(10, OneMonarch);
        var v11 = ParseAt(11, OneMonarch);

        Assert.Equal(0u, v10.ApprovedVassal);
        Assert.Equal(0x99990000u, v11.ApprovedVassal);
        Assert.Equal("Monarch", v11.Monarch!.Value.Name);
    }

    // ── §4.4 tree-assembly rules ──────────────────────────────────────────

    [Fact]
    public void TreeAssembly_OrphanTreeParent_DiscardsWholeMessage()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0x50000005u, 0x5000FFFFu /* never-seen parent */, true, "Orphan"),
        };
        byte[] wire = BuildProfileWire(MonarchGuid, 11, records);

        Assert.Null(ClientCommandResponses.ParseAllegianceInfoResponse(wire));
    }

    [Fact]
    public void TreeAssembly_SelfParent_DiscardsWholeMessage()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0x50000006u, 0x50000006u /* itself */, true, "SelfParent"),
        };
        byte[] wire = BuildProfileWire(MonarchGuid, 11, records);

        Assert.Null(ClientCommandResponses.ParseAllegianceInfoResponse(wire));
    }

    [Fact]
    public void TreeAssembly_DuplicateId_DiscardsWholeMessage()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0x50000007u, MonarchGuid, true, "First"),
            (0x50000007u, MonarchGuid, true, "DuplicateAgain"),
        };
        byte[] wire = BuildProfileWire(MonarchGuid, 11, records);

        Assert.Null(ClientCommandResponses.ParseAllegianceInfoResponse(wire));
    }


    [Fact]
    public void TreeAssembly_ZeroIdMonarch_DiscardsWholeMessage()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (0u /* zero id */, 0u, true, "ZeroIdMonarch"),
        };
        byte[] wire = BuildProfileWire(MonarchGuid, 11, records);

        Assert.Null(ClientCommandResponses.ParseAllegianceInfoResponse(wire));
    }

    [Fact]
    public void TreeAssembly_ZeroIdChildRecord_DiscardsWholeMessage()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0u /* zero id */, MonarchGuid, true, "ZeroIdChild"),
        };
        byte[] wire = BuildProfileWire(MonarchGuid, 11, records);

        Assert.Null(ClientCommandResponses.ParseAllegianceInfoResponse(wire));
    }

    [Fact]
    public void TreeAssembly_ZeroTreeParent_OnNonMonarchRecord_DiscardsWholeMessage()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0x50000008u, 0u /* treeParent == 0, not the monarch */, true, "ZeroParent"),
        };
        byte[] wire = BuildProfileWire(MonarchGuid, 11, records);

        Assert.Null(ClientCommandResponses.ParseAllegianceInfoResponse(wire));
    }

    [Fact]
    public void TreeAssembly_ValidChain_ParentBeforeChild_Succeeds()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0x50000002u, MonarchGuid, true, "Patron"),
            (0x50000003u, 0x50000002u, true, "Self"),
        };
        byte[] wire = BuildProfileWire(MonarchGuid, 11, records);

        var parsed = ClientCommandResponses.ParseAllegianceInfoResponse(wire);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Records.Count);
    }

    [Fact]
    public void TreeAssembly_SiblingOrder_ReversesOnAssembly()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0x50000010u, MonarchGuid, true, "VassalA"),
            (0x50000011u, MonarchGuid, true, "VassalB"),
            (0x50000012u, MonarchGuid, true, "VassalC"),
        };
        byte[] wire = BuildProfileWire(MonarchGuid, 11, records);

        var parsed = ClientCommandResponses.ParseAllegianceInfoResponse(wire);
        Assert.NotNull(parsed);

        var vassalNames = new List<string>();
        foreach (var v in parsed!.Value.FindVassals(MonarchGuid))
            vassalNames.Add(v.Name);

        Assert.Equal(new[] { "VassalC", "VassalB", "VassalA" }, vassalNames);
    }

    // ── Surfaced per-record fields (lane C §7.2's "panel needs" list) ────────

    [Fact]
    public void ReadAllegianceData_SurfacesRankLevelLoyaltyLeadershipCpGenderHeritage()
    {
        var w = new AceWireWriter()
            .Write(MonarchGuid) // targetGuid
            .Write((uint)1).Write((uint)0) // totalMembers, totalVassals
            .Write((ushort)1).Write((ushort)11) // recordCount, oldVersion=11 (newest)
            .Write((ushort)0).Write((ushort)256) // officers: empty
            .Write((uint)0) // officerTitles: empty
            .Write((uint)0).Write((uint)0).Write((uint)0).Write((uint)0)
            .WriteString16L("").WriteString16L("") // motd, motdSetBy
            .Write((uint)0)
            .Write((uint)0).Write(0f).Write(0f).Write(0f).Write(1f).Write(0f).Write(0f).Write(0f) // bindPoint
            .WriteString16L("Alle") // allegianceName
            .Write((uint)0) // nameLastSetTime
            .Write((uint)0) // isLocked
            .Write((uint)0) // approvedVassal
            // monarch AllegianceData
            .Write(MonarchGuid)
            .Write((uint)12345)
            .Write((uint)67890)
            .Write((uint)(0x4u | 0x8u | 0x1u)) // HasAllegianceAge | HasPackedLevel | LoggedIn
            .Write((byte)2)  // gender
            .Write((byte)3)  // heritage group
            .Write((ushort)7) // rank
            .Write((uint)42)  // level (HasPackedLevel set)
            .Write((ushort)200) // loyalty
            .Write((ushort)150) // leadership
            .Write((uint)0).Write((uint)0) // timeOnline, allegianceAge
            .WriteString16L("Monarch");

        var parsed = ClientCommandResponses.ParseAllegianceInfoResponse(w.ToArray());

        Assert.NotNull(parsed);
        var monarch = parsed!.Value.Monarch!.Value;
        Assert.Equal((ushort)7, monarch.Rank);
        Assert.Equal(42u, monarch.Level);
        Assert.Equal((ushort)200, monarch.Loyalty);
        Assert.Equal((ushort)150, monarch.Leadership);
        Assert.Equal(12345u, monarch.CpCached);
        Assert.Equal(67890u, monarch.CpTithed);
        Assert.Equal((byte)2, monarch.Gender);
        Assert.Equal((byte)3, monarch.HeritageGroup);
        Assert.False(monarch.MayPassupExperience);
    }

    [Fact]
    public void ReadAllegianceData_MayPassupExperience_SetWhenHasPackedLevelAbsent()
    {
        var w = new AceWireWriter()
            .Write(MonarchGuid)
            .Write((uint)2).Write((uint)0)
            .Write((ushort)2).Write((ushort)11)
            .Write((ushort)0).Write((ushort)256)
            .Write((uint)0)
            .Write((uint)0).Write((uint)0).Write((uint)0).Write((uint)0)
            .WriteString16L("").WriteString16L("")
            .Write((uint)0)
            .Write((uint)0).Write(0f).Write(0f).Write(0f).Write(1f).Write(0f).Write(0f).Write(0f)
            .WriteString16L("Alle")
            .Write((uint)0)
            .Write((uint)0)
            .Write((uint)0)
            // monarch — ordinary HasPackedLevel-set record, not under test.
            .Write(MonarchGuid)
            .Write((uint)0).Write((uint)0)
            .Write((uint)(0x4u | 0x8u))
            .Write((byte)0).Write((byte)0)
            .Write((ushort)0)
            .Write((uint)0) // level (HasPackedLevel set)
            .Write((ushort)0).Write((ushort)0)
            .Write((uint)0).Write((uint)0)
            .WriteString16L("Monarch")
            // vassal — the record under test: HasPackedLevel absent.
            .Write(MonarchGuid) // treeParent
            .Write(0x50000005u)
            .Write((uint)0).Write((uint)0)
            .Write((uint)0x4u) // HasAllegianceAge only — NO HasPackedLevel, NO MayPassupExperience bit
            .Write((byte)0).Write((byte)0)
            .Write((ushort)0)
            // no level field (HasPackedLevel unset)
            .Write((ushort)0).Write((ushort)0)
            .Write((uint)0).Write((uint)0)
            .WriteString16L("Vassal");

        var parsed = ClientCommandResponses.ParseAllegianceInfoResponse(w.ToArray());

        Assert.NotNull(parsed);
        Assert.False(parsed!.Value.Monarch!.Value.MayPassupExperience); // always forced false
        Assert.Single(parsed.Value.Records);
        Assert.True(parsed.Value.Records[0].MayPassupExperience);
        Assert.Equal(0u, parsed.Value.Records[0].Level); // never read — HasPackedLevel unset
    }

    [Fact]
    public void ReadAllegianceData_MonarchMayPassupExperience_ForcedFalseRegardlessOfWireBit()
    {
        var w = new AceWireWriter()
            .Write(MonarchGuid) // targetGuid
            .Write((uint)1).Write((uint)0) // totalMembers, totalVassals
            .Write((ushort)1).Write((ushort)11) // recordCount, oldVersion=11 (newest)
            .Write((ushort)0).Write((ushort)256) // officers: empty
            .Write((uint)0) // officerTitles: empty
            .Write((uint)0).Write((uint)0).Write((uint)0).Write((uint)0)
            .WriteString16L("").WriteString16L("") // motd, motdSetBy
            .Write((uint)0)
            .Write((uint)0).Write(0f).Write(0f).Write(0f).Write(1f).Write(0f).Write(0f).Write(0f) // bindPoint
            .WriteString16L("Alle") // allegianceName
            .Write((uint)0) // nameLastSetTime
            .Write((uint)0) // isLocked
            .Write((uint)0) // approvedVassal
            // monarch AllegianceData — HasAllegianceAge | HasPackedLevel |
            // MayPassupExperience (0x4|0x8|0x10 = 0x1C), i.e. the wire bit
            // asks for MayPassupExperience = true.
            .Write(MonarchGuid)
            .Write((uint)0).Write((uint)0)
            .Write((uint)0x1Cu)
            .Write((byte)0).Write((byte)0)
            .Write((ushort)1)
            .Write((uint)5) // level (HasPackedLevel set)
            .Write((ushort)0).Write((ushort)0)
            .Write((uint)0).Write((uint)0)
            .WriteString16L("Monarch");

        var parsed = ClientCommandResponses.ParseAllegianceInfoResponse(w.ToArray());

        Assert.NotNull(parsed);
        Assert.False(parsed!.Value.Monarch!.Value.MayPassupExperience);
    }

    // ── 0x0020 AllegianceUpdate shares the same profile reader ─────────────

    [Fact]
    public void ParseAllegianceUpdate_RankLeading_SharesProfileReaderWithInfoResponse()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0x50000002u, MonarchGuid, true, "Vassal"),
        };
        const uint rank = 3u;
        byte[] wire = BuildProfileWire(rank, 11, records);

        var update = ClientCommandResponses.ParseAllegianceUpdate(wire);

        Assert.NotNull(update);
        Assert.Equal(rank, update!.Value.Rank);
        Assert.Equal("Monarch", update.Value.Monarch!.Value.Name);
        Assert.Single(update.Value.Records);
        Assert.Equal("Vassal", update.Value.Records[0].Name);
        Assert.Equal("TestAllegiance", update.Value.AllegianceName);
        Assert.True(update.Value.IsLocked);
        Assert.Equal(0x99990000u, update.Value.ApprovedVassal);
    }

    [Fact]
    public void ParseAllegianceUpdate_TreeAssemblyRulesApply_OrphanDiscardsWholeMessage()
    {
        var records = new List<(uint, uint, bool, string)>
        {
            (MonarchGuid, 0u, true, "Monarch"),
            (0x50000005u, 0x5000FFFFu, true, "Orphan"),
        };
        byte[] wire = BuildProfileWire(3u /* rank */, 11, records);

        Assert.Null(ClientCommandResponses.ParseAllegianceUpdate(wire));
    }
}
