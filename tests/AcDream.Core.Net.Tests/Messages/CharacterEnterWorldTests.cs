using System.Buffers.Binary;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Messages;

public class CharacterEnterWorldTests
{
    [Fact]
    public void BuildEnterWorldRequestBody_IsJustTheOpcode()
    {
        var body = CharacterEnterWorld.BuildEnterWorldRequestBody();

        Assert.Equal(4, body.Length);
        Assert.Equal(CharacterEnterWorld.EnterWorldRequestOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
    }

    [Fact]
    public void BuildEnterWorldBody_Layout_OpcodeThenGuidThenAccountName()
    {
        var body = CharacterEnterWorld.BuildEnterWorldBody(
            characterGuid: 0x50000001u,
            accountName: "testaccount");

        int pos = 0;
        Assert.Equal(CharacterEnterWorld.EnterWorldOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4;
        Assert.Equal(0x50000001u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4;

        // String16L("testaccount") = u16(11), 11 ASCII bytes, pad to 4-byte
        // boundary from start of u16: 2 + 11 = 13, padded to 16 → 3 pad bytes.
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos));
        Assert.Equal(11, len); pos += 2;
        string name = System.Text.Encoding.ASCII.GetString(body.AsSpan(pos, 11));
        Assert.Equal("testaccount", name); pos += 11;
        // Verify padding bytes are zero and record size is aligned to 4.
        Assert.Equal(0, body[pos++]);
        Assert.Equal(0, body[pos++]);
        Assert.Equal(0, body[pos++]);
        Assert.Equal(4 + 4 + 16, body.Length);  // opcode + guid + padded string
    }

    [Fact]
    public void SessionSelection_UsesCanonicalCharacterListAccountInF657()
    {
        CharacterList.Parsed characters = MakeCharacters(
            [new CharacterList.Character(0x50000001u, "Ready", 0)],
            accountName: "CanonicalCase");

        WorldSession.EnterWorldSelection selection =
            WorldSession.SelectCharacterForEnterWorld(characters, 0);

        ReadOnlySpan<byte> body = selection.EnterWorldBody;
        Assert.Equal(CharacterEnterWorld.EnterWorldOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(0x50000001u,
            BinaryPrimitives.ReadUInt32LittleEndian(body[4..]));
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        Assert.Equal("CanonicalCase",
            System.Text.Encoding.ASCII.GetString(body.Slice(10, length)));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0x50000001u, 1u)]
    [InlineData(0x50000001u, 30u)]
    public void SessionSelection_RejectsUnavailableActiveIdentity(
        uint guid,
        uint secondsGreyedOut)
    {
        CharacterList.Parsed characters = MakeCharacters(
            [new CharacterList.Character(guid, "Unavailable", secondsGreyedOut)],
            accountName: "Account");

        Assert.Throws<InvalidOperationException>(() =>
            WorldSession.SelectCharacterForEnterWorld(characters, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void SessionSelection_RejectsIndexOutsideActiveCollection(int index)
    {
        CharacterList.Parsed characters = MakeCharacters(
            [new CharacterList.Character(0x50000001u, "Ready", 0)],
            accountName: "Account");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorldSession.SelectCharacterForEnterWorld(characters, index));
    }

    private static CharacterList.Parsed MakeCharacters(
        IReadOnlyList<CharacterList.Character> active,
        string accountName) =>
        new(
            Status: 0,
            Characters: active,
            DeletedCharacters: [],
            SlotCount: 11,
            AccountName: accountName,
            UseTurbineChat: true,
            HasThroneOfDestiny: true);
}

public class GameMessageFragmentTests
{
    [Fact]
    public void BuildSingleFragment_FragmentHeaderMatchesProtocol()
    {
        byte[] msg = { 0x58, 0xF6, 0x00, 0x00, 0x01, 0x00, 0x00, 0x50 };  // fake opcode + 4 body bytes
        var frag = GameMessageFragment.BuildSingleFragment(
            fragmentSequence: 42,
            queue: GameMessageGroup.UIQueue,
            gameMessageBytes: msg);

        Assert.Equal(42u, frag.Header.Sequence);
        Assert.Equal(GameMessageFragment.OutboundFragmentId, frag.Header.Id);
        Assert.Equal(1, frag.Header.Count);
        Assert.Equal(0, frag.Header.Index);
        Assert.Equal(24, frag.Header.TotalSize);  // 16 header + 8 payload
        Assert.Equal((ushort)GameMessageGroup.UIQueue, frag.Header.Queue);
        Assert.Equal(msg, frag.Payload);
    }

    [Fact]
    public void BuildSingleFragment_OversizeBody_Throws()
    {
        var big = new byte[MessageFragmentHeader.MaxFragmentDataSize + 1];
        Assert.Throws<ArgumentException>(
            () => GameMessageFragment.BuildSingleFragment(0, GameMessageGroup.UIQueue, big));
    }

    [Fact]
    public void Serialize_Then_MessageFragment_TryParse_RoundTrips()
    {
        byte[] msg = { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var original = GameMessageFragment.BuildSingleFragment(
            fragmentSequence: 7, queue: GameMessageGroup.UIQueue, gameMessageBytes: msg);

        byte[] serialized = GameMessageFragment.Serialize(original);
        var (reparsed, consumed) = MessageFragment.TryParse(serialized);

        Assert.NotNull(reparsed);
        Assert.Equal(serialized.Length, consumed);
        Assert.Equal(original.Header.Sequence, reparsed!.Value.Header.Sequence);
        Assert.Equal(original.Header.Count,    reparsed.Value.Header.Count);
        Assert.Equal(original.Payload, reparsed.Value.Payload);
    }

    [Fact]
    public void WriteSingleFragment_MatchesOwnedBuildAndSerialize()
    {
        byte[] message = [1, 2, 3, 4, 5, 6];
        MessageFragment owned =
            GameMessageFragment.BuildSingleFragment(
                fragmentSequence: 99,
                queue: GameMessageGroup.ControlQueue,
                gameMessageBytes: message);
        byte[] expected = GameMessageFragment.Serialize(owned);
        Span<byte> destination = stackalloc byte[
            MessageFragmentHeader.MaxFragmentSize];

        int written = GameMessageFragment.WriteSingleFragment(
            destination,
            fragmentSequence: 99,
            queue: GameMessageGroup.ControlQueue,
            gameMessageBytes: message);

        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, destination.Slice(0, written).ToArray());
    }

    [Fact]
    public void WriteSingleFragment_ShortDestination_Throws()
    {
        byte[] message = [1, 2, 3, 4];
        Assert.Throws<ArgumentException>(
            () => GameMessageFragment.WriteSingleFragment(
                new byte[
                    MessageFragmentHeader.Size
                    + message.Length - 1],
                fragmentSequence: 1,
                queue: GameMessageGroup.UIQueue,
                gameMessageBytes: message));
    }
}
