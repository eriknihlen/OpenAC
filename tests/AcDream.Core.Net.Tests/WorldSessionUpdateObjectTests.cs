using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests;

/// <summary>
/// #35: rending or imbuing an item, and revealing Aetheria, change that item's
/// icon. The server says so by re-sending the whole description under its own
/// opcode. The session has to read it and refresh what it already holds, without
/// treating the item as newly arrived.
/// </summary>
public sealed class WorldSessionUpdateObjectTests
{
    private const uint ItemGuid = 0x50000042u;
    private const uint UnderlayDataId = 52u;
    private const uint OverlayDataId = 50u;
    private const uint RendUnderlay = 0x06001234u;

    private sealed class NullTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }
        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }
        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<NetReceiveResult>(cancellationToken);
        public void Dispose() { }
    }

    [Fact]
    public void UpdateObject_RefreshesTheDescriptionAndIsNotASpawn()
    {
        using var session = CreateSession();
        int spawns = 0;
        var refreshed = new List<WorldSession.EntitySpawn>();
        session.EntitySpawned += _ => spawns++;
        session.EntityDescriptionRefreshed += refreshed.Add;

        InvokeProcessDatagram(
            session,
            BuildPacket(BuildDescription(
                CreateObject.UpdateOpcode,
                name: "Sword",
                iconUnderlayId: RendUnderlay)));

        Assert.Equal(0, spawns);
        WorldSession.EntitySpawn spawn = Assert.Single(refreshed);
        Assert.Equal(ItemGuid, spawn.Guid);
        Assert.Equal(RendUnderlay, spawn.IconUnderlayId);
    }

    [Fact]
    public void CreateObject_StaysASpawnAndIsNotARefresh()
    {
        using var session = CreateSession();
        int spawns = 0;
        int refreshes = 0;
        session.EntitySpawned += _ => spawns++;
        session.EntityDescriptionRefreshed += _ => refreshes++;

        InvokeProcessDatagram(
            session,
            BuildPacket(BuildDescription(CreateObject.Opcode, name: "Sword")));

        Assert.Equal(1, spawns);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public void UpdateObject_RepublishesTheItemWithItsNewUnderlay()
    {
        using var session = CreateSession();
        var table = new ClientObjectTable();
        using IDisposable wiring = ObjectTableWiring.Wire(session, table);
        table.AddOrUpdate(new ClientObject { ObjectId = ItemGuid, Name = "Sword" });

        var republished = new List<uint>();
        int added = 0;
        table.ObjectUpdated += o => republished.Add(o.ObjectId);
        table.ObjectAdded += _ => added++;

        InvokeProcessDatagram(
            session,
            BuildPacket(BuildDescription(
                CreateObject.UpdateOpcode,
                name: "Sword",
                iconUnderlayId: RendUnderlay)));

        Assert.Equal(ItemGuid, Assert.Single(republished));
        Assert.Equal(0, added);
        Assert.Equal(RendUnderlay, table.Get(ItemGuid)!.IconUnderlayId);
    }

    [Fact]
    public void PublicDataIdUpdate_RepublishesTheItemWithItsNewOverlay()
    {
        using var session = CreateSession();
        var table = new ClientObjectTable();
        using IDisposable wiring = ObjectTableWiring.Wire(session, table);
        table.AddOrUpdate(new ClientObject { ObjectId = ItemGuid, Name = "Sword" });

        var republished = new List<uint>();
        table.ObjectUpdated += o => republished.Add(o.ObjectId);

        InvokeProcessDatagram(
            session,
            BuildPacket(BuildPublicDataId(ItemGuid, OverlayDataId, 0x06005678u)));

        Assert.Equal(ItemGuid, Assert.Single(republished));
        ClientObject item = table.Get(ItemGuid)!;
        Assert.Equal(0x06005678u, item.IconOverlayId);
        Assert.Equal(0x06005678u, item.Properties.DataIds[OverlayDataId]);
    }

    [Fact]
    public void PrivateDataIdUpdate_RepublishesThePlayer()
    {
        using var session = CreateSession();
        var table = new ClientObjectTable();
        using IDisposable wiring = ObjectTableWiring.Wire(
            session,
            table,
            playerGuid: () => 0x50000001u);
        table.AddOrUpdate(new ClientObject { ObjectId = 0x50000001u, Name = "Player" });

        var republished = new List<uint>();
        table.ObjectUpdated += o => republished.Add(o.ObjectId);

        InvokeProcessDatagram(
            session,
            BuildPacket(BuildPrivateDataId(UnderlayDataId, 0x0600ABCDu)));

        Assert.Equal(0x50000001u, Assert.Single(republished));
        Assert.Equal(0x0600ABCDu, table.Get(0x50000001u)!.IconUnderlayId);
    }

    [Fact]
    public void PublicInstanceIdUpdate_RecordsTheValueAndRepublishes()
    {
        using var session = CreateSession();
        var table = new ClientObjectTable();
        using IDisposable wiring = ObjectTableWiring.Wire(session, table);
        table.AddOrUpdate(new ClientObject { ObjectId = ItemGuid });

        var republished = new List<uint>();
        table.ObjectUpdated += o => republished.Add(o.ObjectId);

        InvokeProcessDatagram(
            session,
            BuildPacket(BuildPublicInstanceId(ItemGuid, 3u, 0x50000001u)));

        Assert.Equal(ItemGuid, Assert.Single(republished));
        Assert.Equal(0x50000001u, table.Get(ItemGuid)!.Properties.InstanceIds[3u]);
    }

    private static WorldSession CreateSession() =>
        new(new IPEndPoint(IPAddress.Loopback, 9000), new NullTransport());

    private static byte[] BuildPublicDataId(uint guid, uint property, uint value)
    {
        byte[] body = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(
            body, PublicUpdatePropertyDataId.Opcode);
        body[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), guid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), property);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), value);
        return body;
    }

    private static byte[] BuildPublicInstanceId(uint guid, uint property, uint value)
    {
        byte[] body = BuildPublicDataId(guid, property, value);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body, PublicUpdatePropertyInstanceId.Opcode);
        return body;
    }

    private static byte[] BuildPrivateDataId(uint property, uint value)
    {
        byte[] body = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(
            body, PrivateUpdatePropertyDataId.Opcode);
        body[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), property);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), value);
        return body;
    }

    /// <summary>
    /// One object description: an empty appearance block, an empty physics
    /// block with its nine sequence stamps, then the weenie block. The two
    /// description opcodes share this body exactly.
    /// </summary>
    private static byte[] BuildDescription(
        uint opcode,
        string name,
        uint iconUnderlayId = 0)
    {
        var bytes = new List<byte>();
        WriteU32(bytes, opcode);
        WriteU32(bytes, ItemGuid);

        bytes.Add(0x11);
        bytes.Add(0);
        bytes.Add(0);
        bytes.Add(0);

        WriteU32(bytes, 0u);              // physics flags
        WriteU32(bytes, 0u);              // physics state
        for (int i = 0; i < 9; i++)
            WriteU16(bytes, 0);
        Align4(bytes);

        // The second weenie flag word rides behind its own description bit.
        uint objectDescriptionFlags = iconUnderlayId != 0 ? 0x04000000u : 0u;
        uint weenieFlags2 = iconUnderlayId != 0 ? 0x00000001u : 0u;

        WriteU32(bytes, 0u);              // weenie flags
        WriteString16L(bytes, name);
        WritePackedDword(bytes, 0x1234u); // weenie class id
        WritePackedDword(bytes, 0u);      // icon
        WriteU32(bytes, 0u);              // item type
        WriteU32(bytes, objectDescriptionFlags);
        Align4(bytes);

        if (objectDescriptionFlags != 0)
            WriteU32(bytes, weenieFlags2);
        if ((weenieFlags2 & 0x00000001u) != 0)
            WritePackedDword(bytes, iconUnderlayId);
        Align4(bytes);

        return bytes.ToArray();
    }

    private static void WriteU32(List<byte> bytes, uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        bytes.AddRange(tmp.ToArray());
    }

    private static void WriteU16(List<byte> bytes, ushort value)
    {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
        bytes.AddRange(tmp.ToArray());
    }

    private static void WritePackedDword(List<byte> bytes, uint value)
    {
        if (value <= 0x7FFF)
        {
            WriteU16(bytes, (ushort)value);
            return;
        }

        WriteU16(bytes, (ushort)(((value >> 16) & 0x7FFF) | 0x8000));
        WriteU16(bytes, (ushort)(value & 0xFFFF));
    }

    private static void WriteString16L(List<byte> bytes, string value)
    {
        byte[] encoded = Encoding.GetEncoding(1252).GetBytes(value);
        WriteU16(bytes, checked((ushort)encoded.Length));
        bytes.AddRange(encoded);
        Align4(bytes);
    }

    private static void Align4(List<byte> bytes)
    {
        while ((bytes.Count & 3) != 0)
            bytes.Add(0);
    }

    private static byte[] BuildPacket(params byte[][] messages)
    {
        int length = messages.Sum(message =>
            MessageFragmentHeader.Size + message.Length);
        var fragments = new byte[length];
        int position = 0;
        uint sequence = 1u;
        foreach (byte[] message in messages)
        {
            position += GameMessageFragment.WriteSingleFragment(
                fragments.AsSpan(position),
                sequence++,
                GameMessageGroup.UIQueue,
                message);
        }
        return PacketCodec.Encode(
            new PacketHeader
            {
                Sequence = 1u,
                Flags = PacketHeaderFlags.BlobFragments,
            },
            fragments,
            outboundIsaac: null);
    }

    private static void InvokeProcessDatagram(
        WorldSession session,
        byte[] datagram)
    {
        MethodInfo method = typeof(WorldSession).GetMethod(
            "ProcessDatagram",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(
            session,
            [new ReadOnlyMemory<byte>(datagram), null, true]);
    }
}
