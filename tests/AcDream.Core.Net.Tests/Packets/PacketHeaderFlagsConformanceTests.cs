using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Net.Packets;
using Xunit;

namespace AcDream.Core.Net.Tests.Packets;

public class PacketHeaderFlagsConformanceTests
{
    private static readonly (string Name, uint Value)[] AceFlags =
    {
        ("None",               0x00000000u),
        ("Retransmission",     0x00000001u),
        ("EncryptedChecksum",  0x00000002u),  // can't be paired with 0x1
        ("BlobFragments",      0x00000004u),
        ("ServerSwitch",       0x00000100u),
        ("LogonServerAddr",    0x00000200u),
        ("EmptyHeader1",       0x00000400u),
        ("Referral",           0x00000800u),
        ("RequestRetransmit",  0x00001000u),  // Nak
        ("RejectRetransmit",   0x00002000u),  // Empty Ack
        ("AckSequence",        0x00004000u),  // Pak
        ("Disconnect",         0x00008000u),  // Empty Header 2
        ("LoginRequest",       0x00010000u),
        ("WorldLoginRequest",  0x00020000u),
        ("ConnectRequest",     0x00040000u),
        ("ConnectResponse",    0x00080000u),
        ("NetError",           0x00100000u),
        ("NetErrorDisconnect", 0x00200000u),
        ("CICMDCommand",       0x00400000u),
        ("TimeSync",           0x01000000u),
        ("EchoRequest",        0x02000000u),
        ("EchoResponse",       0x04000000u),
        ("Flow",               0x08000000u),
    };

    public static TheoryData<string, uint> Flags()
    {
        var data = new TheoryData<string, uint>();
        foreach ((string name, uint value) in AceFlags)
            data.Add(name, value);
        return data;
    }

    [Theory]
    [MemberData(nameof(Flags))]
    public void Flag_MatchesAceValue(string name, uint aceValue)
    {
        Assert.True(
            Enum.IsDefined(typeof(PacketHeaderFlags), aceValue) || aceValue == 0,
            $"ACE declares {name} = 0x{aceValue:X8}; acdream has no such value.");

        object parsed = Enum.Parse(typeof(PacketHeaderFlags), name);
        Assert.Equal(aceValue, (uint)(PacketHeaderFlags)parsed);
    }

    [Fact]
    public void EnumHasNoMembersAceDoesNotDeclare()
    {
        HashSet<string> ace = AceFlags.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        string[] ours = Enum.GetNames(typeof(PacketHeaderFlags));

        string[] extra = ours.Where(n => !ace.Contains(n)).ToArray();

        Assert.True(
            extra.Length == 0,
            $"acdream declares flags ACE does not: {string.Join(", ", extra)}");
        Assert.Equal(AceFlags.Length, ours.Length);
    }
}
