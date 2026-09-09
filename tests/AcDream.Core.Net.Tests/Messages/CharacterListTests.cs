using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Messages;

public class CharacterListTests
{
    [Fact]
    public void Parse_SingleCharacter_ExtractsGuidAndName()
    {
        var w = new PacketWriter(128);
        w.WriteUInt32(CharacterList.Opcode);
        w.WriteUInt32(0);
        w.WriteUInt32(1);
        w.WriteUInt32(0x50000001u);
        w.WriteString16L("+Acdream");
        w.WriteUInt32(0);  // deleteDelta
        w.WriteUInt32(0);
        w.WriteUInt32(11); // slotCount
        w.WriteString16L("testaccount");
        w.WriteUInt32(1);  // useTurbineChat
        w.WriteUInt32(1);  // hasToD

        var parsed = CharacterList.Parse(w.ToArray());

        Assert.Equal(0u, parsed.Status);
        Assert.Single(parsed.Characters);
        Assert.Empty(parsed.DeletedCharacters);
        Assert.Equal(0x50000001u, parsed.Characters[0].Id);
        Assert.Equal("+Acdream", parsed.Characters[0].Name);
        Assert.Equal(0u, parsed.Characters[0].SecondsGreyedOut);
        Assert.Equal(11, parsed.SlotCount);
        Assert.Equal("testaccount", parsed.AccountName);
        Assert.True(parsed.UseTurbineChat);
        Assert.True(parsed.HasThroneOfDestiny);
    }

    [Fact]
    public void Parse_MultipleCharacters_PreservesOrder()
    {
        var w = new PacketWriter(128);
        w.WriteUInt32(CharacterList.Opcode);
        w.WriteUInt32(0);
        w.WriteUInt32(3);
        w.WriteUInt32(0x50000001u);
        w.WriteString16L("Alice");
        w.WriteUInt32(0);
        w.WriteUInt32(0x50000002u);
        w.WriteString16L("Bob");
        w.WriteUInt32(0);
        w.WriteUInt32(0x50000003u);
        w.WriteString16L("Carol");
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(11);
        w.WriteString16L("acct");
        w.WriteUInt32(0);
        w.WriteUInt32(1);

        var parsed = CharacterList.Parse(w.ToArray());

        Assert.Equal(3, parsed.Characters.Count);
        Assert.Equal("Alice", parsed.Characters[0].Name);
        Assert.Equal("Bob",   parsed.Characters[1].Name);
        Assert.Equal("Carol", parsed.Characters[2].Name);
    }

    [Fact]
    public void Parse_NonzeroStatusAndDeletedCollection_PreservesRetailLayout()
    {
        var w = new PacketWriter(160);
        w.WriteUInt32(CharacterList.Opcode);
        w.WriteUInt32(7); // status
        w.WriteUInt32(1);
        WriteIdentity(w, 0x50000001u, "Active", 0);
        w.WriteUInt32(2);
        WriteIdentity(w, 0x50000002u, "Deleted One", 90);
        WriteIdentity(w, 0x50000003u, "Deleted Two", 180);
        WriteTail(w, 13, "CanonicalAccount", useTurbineChat: true, hasTod: false);

        CharacterList.Parsed parsed = CharacterList.Parse(w.ToArray());

        Assert.Equal(7u, parsed.Status);
        Assert.Single(parsed.Characters);
        Assert.Equal("Active", parsed.Characters[0].Name);
        Assert.Collection(
            parsed.DeletedCharacters,
            character =>
            {
                Assert.Equal(0x50000002u, character.Id);
                Assert.Equal("Deleted One", character.Name);
                Assert.Equal(90u, character.SecondsGreyedOut);
            },
            character =>
            {
                Assert.Equal(0x50000003u, character.Id);
                Assert.Equal("Deleted Two", character.Name);
                Assert.Equal(180u, character.SecondsGreyedOut);
            });
        Assert.Equal(13, parsed.SlotCount);
        Assert.Equal("CanonicalAccount", parsed.AccountName);
        Assert.True(parsed.UseTurbineChat);
        Assert.False(parsed.HasThroneOfDestiny);
    }

    [Fact]
    public void TrySelectFirstAvailable_SkipsZeroGuidAndGreyedActiveCharacters()
    {
        CharacterList.Parsed parsed = BuildParsed(
            [
                new CharacterList.Character(0, "Empty", 0),
                new CharacterList.Character(0x50000001u, "Deleting", 30),
                new CharacterList.Character(0x50000002u, "Ready", 0),
            ],
            [new CharacterList.Character(0x50000003u, "Deleted", 0)]);

        bool found = CharacterList.TrySelectFirstAvailable(parsed, out var selection);

        Assert.True(found);
        Assert.Equal(2, selection.ActiveIndex);
        Assert.Equal(0x50000002u, selection.Character.Id);
    }

    [Fact]
    public void TrySelectFirstAvailable_RejectsAllGreyedAndNeverReadsDeletedCollection()
    {
        CharacterList.Parsed parsed = BuildParsed(
            [new CharacterList.Character(0x50000001u, "Deleting", 1)],
            [new CharacterList.Character(0x50000002u, "Deleted Ready", 0)]);

        Assert.False(CharacterList.TrySelectFirstAvailable(parsed, out _));
    }

    [Fact]
    public void Parse_EmptyCollectionsAndNegativeAllowedSentinel_ArePreserved()
    {
        var w = new PacketWriter(64);
        w.WriteUInt32(CharacterList.Opcode);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(uint.MaxValue);
        w.WriteString16L("Account");
        w.WriteUInt32(2);
        w.WriteUInt32(uint.MaxValue);

        CharacterList.Parsed parsed = CharacterList.Parse(w.ToArray());

        Assert.Empty(parsed.Characters);
        Assert.Empty(parsed.DeletedCharacters);
        Assert.Equal(-1, parsed.SlotCount);
        Assert.True(parsed.UseTurbineChat);
        Assert.True(parsed.HasThroneOfDestiny);
        Assert.False(CharacterList.TrySelectFirstAvailable(parsed, out _));
    }

    [Fact]
    public void Parse_CountThatCannotFitRemainingPayload_IsRejected()
    {
        var active = new PacketWriter(16);
        active.WriteUInt32(CharacterList.Opcode);
        active.WriteUInt32(0);
        active.WriteUInt32(256);
        Assert.Throws<FormatException>(() => CharacterList.Parse(active.ToArray()));

        var deleted = new PacketWriter(20);
        deleted.WriteUInt32(CharacterList.Opcode);
        deleted.WriteUInt32(0);
        deleted.WriteUInt32(0);
        deleted.WriteUInt32(256);
        Assert.Throws<FormatException>(() => CharacterList.Parse(deleted.ToArray()));
    }

    [Fact]
    public void Parse_255MinimumSizeIdentities_UsesPayloadBoundNotArbitraryCap()
    {
        var w = new PacketWriter(4096);
        w.WriteUInt32(CharacterList.Opcode);
        w.WriteUInt32(0);
        w.WriteUInt32(255);
        for (uint i = 1; i <= 255; i++)
            WriteIdentity(w, i, string.Empty, 0);
        w.WriteUInt32(0);
        WriteTail(w, 255, "Account", useTurbineChat: true, hasTod: true);

        CharacterList.Parsed parsed = CharacterList.Parse(w.ToArray());

        Assert.Equal(255, parsed.Characters.Count);
        Assert.Equal(255, parsed.SlotCount);
    }

    [Fact]
    public void Parse_EveryTruncatedPrefixOfRetailPayload_Throws()
    {
        var w = new PacketWriter(160);
        w.WriteUInt32(CharacterList.Opcode);
        w.WriteUInt32(3);
        w.WriteUInt32(1);
        WriteIdentity(w, 0x50000001u, "A", 0);
        w.WriteUInt32(1);
        WriteIdentity(w, 0x50000002u, "Deleted", 22);
        WriteTail(w, 11, "Account", useTurbineChat: true, hasTod: true);
        byte[] valid = w.ToArray();

        for (int length = 0; length < valid.Length; length++)
        {
            byte[] prefix = valid.AsSpan(0, length).ToArray();
            Assert.Throws<FormatException>(() => CharacterList.Parse(prefix));
        }

        _ = CharacterList.Parse(valid);
    }

    [Fact]
    public void Parse_WrongOpcode_Throws()
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEADBEEFu);
        Assert.Throws<FormatException>(() => CharacterList.Parse(bytes));
    }

    [Fact]
    public void Parse_Truncated_Throws()
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, CharacterList.Opcode);
        Assert.Throws<FormatException>(() => CharacterList.Parse(bytes));
    }

    private static void WriteIdentity(
        PacketWriter writer,
        uint id,
        string name,
        uint secondsGreyedOut)
    {
        writer.WriteUInt32(id);
        writer.WriteString16L(name);
        writer.WriteUInt32(secondsGreyedOut);
    }

    private static void WriteTail(
        PacketWriter writer,
        int slotCount,
        string accountName,
        bool useTurbineChat,
        bool hasTod)
    {
        writer.WriteUInt32(unchecked((uint)slotCount));
        writer.WriteString16L(accountName);
        writer.WriteUInt32(useTurbineChat ? 1u : 0u);
        writer.WriteUInt32(hasTod ? 1u : 0u);
    }

    private static CharacterList.Parsed BuildParsed(
        IReadOnlyList<CharacterList.Character> active,
        IReadOnlyList<CharacterList.Character> deleted) =>
        new(
            Status: 0,
            Characters: active,
            DeletedCharacters: deleted,
            SlotCount: 11,
            AccountName: "Account",
            UseTurbineChat: true,
            HasThroneOfDestiny: true);
}
