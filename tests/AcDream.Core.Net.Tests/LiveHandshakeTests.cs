using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests;

[Trait("Lane", "Live")]
public class LiveHandshakeTests
{
    [Fact]
    public void Live_LoginRequest_ReceivesConnectRequestFromServer()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_LIVE") != "1")
            Assert.Fail("Lane=Live requires ACDREAM_LIVE=1 and a reachable configured server.");  // skipped — not a failure

        var host = Environment.GetEnvironmentVariable("ACDREAM_TEST_HOST") ?? "127.0.0.1";
        var portStr = Environment.GetEnvironmentVariable("ACDREAM_TEST_PORT") ?? "9000";
        var user = Environment.GetEnvironmentVariable("ACDREAM_TEST_USER");
        var pass = Environment.GetEnvironmentVariable("ACDREAM_TEST_PASS");

        Assert.NotNull(user);
        Assert.NotNull(pass);
        Assert.NotEmpty(user!);
        Assert.NotEmpty(pass!);

        var remote = new IPEndPoint(IPAddress.Parse(host), int.Parse(portStr));
        using var net = new NetClient(remote);

        uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] loginBody = LoginRequest.Build(user, pass, timestamp);
        var loginHeader = new PacketHeader { Flags = PacketHeaderFlags.LoginRequest };
        byte[] loginDatagram = PacketCodec.Encode(loginHeader, loginBody, outboundIsaac: null);

        Console.WriteLine($"[live] sending {loginDatagram.Length}-byte LoginRequest to {remote} " +
                          $"(user.len={user.Length}, pass.len={pass.Length})");
        net.Send(loginDatagram);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        Packet? connectRequest = null;
        int packetsReceived = 0;

        while (DateTime.UtcNow < deadline && connectRequest is null)
        {
            var bytes = net.Receive(deadline - DateTime.UtcNow, out var from);
            if (bytes is null) break;
            packetsReceived++;
            Console.WriteLine($"[live] received {bytes.Length}-byte datagram from {from}");

            var decoded = PacketCodec.TryDecode(bytes, inboundIsaac: null);
            Console.WriteLine($"[live]   decode result: {decoded.Error}, " +
                              $"flags: {(decoded.Packet?.Header.Flags.ToString() ?? "n/a")}");

            if (decoded.IsOk && decoded.Packet!.Header.HasFlag(PacketHeaderFlags.ConnectRequest))
            {
                connectRequest = decoded.Packet;
                break;
            }
        }

        Console.WriteLine($"[live] total packets received: {packetsReceived}");

        Assert.True(packetsReceived > 0,
            "Server did not respond at all within 5s — is ACE actually running on " +
            $"{remote} and does the account '{user}' exist?");

        Assert.NotNull(connectRequest);
        var opt = connectRequest!.Optional;
        Console.WriteLine($"[live] ConnectRequest decoded: " +
                          $"serverTime={opt.ConnectRequestServerTime:F3} " +
                          $"cookie=0x{opt.ConnectRequestCookie:X16} " +
                          $"clientId=0x{opt.ConnectRequestClientId:X8} " +
                          $"serverSeed=0x{opt.ConnectRequestServerSeed:X8} " +
                          $"clientSeed=0x{opt.ConnectRequestClientSeed:X8}");

        Assert.NotEqual(0UL, opt.ConnectRequestCookie);
        Assert.NotEqual(0u, opt.ConnectRequestClientId);
        Assert.NotEqual(0u, opt.ConnectRequestServerSeed);
        Assert.NotEqual(0u, opt.ConnectRequestClientSeed);
    }

    [Fact]
    public void Live_FullThreeWayHandshake_ReachesConnectedState()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_LIVE") != "1")
            Assert.Fail("Lane=Live requires ACDREAM_LIVE=1 and a reachable configured server.");

        var host = Environment.GetEnvironmentVariable("ACDREAM_TEST_HOST") ?? "127.0.0.1";
        var portStr = Environment.GetEnvironmentVariable("ACDREAM_TEST_PORT") ?? "9000";
        var user = Environment.GetEnvironmentVariable("ACDREAM_TEST_USER")!;
        var pass = Environment.GetEnvironmentVariable("ACDREAM_TEST_PASS")!;

        int loginPort = int.Parse(portStr);
        int connectPort = loginPort + 1;

        var loginEndpoint = new IPEndPoint(IPAddress.Parse(host), loginPort);
        var connectEndpoint = new IPEndPoint(IPAddress.Parse(host), connectPort);

        using var net = new NetClient(loginEndpoint);

        // Step 1: send LoginRequest to port 9000.
        uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] loginPayload = LoginRequest.Build(user, pass, timestamp);
        var loginHeader = new PacketHeader { Flags = PacketHeaderFlags.LoginRequest };
        byte[] loginDatagram = PacketCodec.Encode(loginHeader, loginPayload, outboundIsaac: null);

        Console.WriteLine($"[live] step 1: sending {loginDatagram.Length}-byte LoginRequest to {loginEndpoint}");
        net.Send(loginDatagram);

        Packet? connectRequest = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var bytes = net.Receive(deadline - DateTime.UtcNow, out var from);
            if (bytes is null) break;
            var decoded = PacketCodec.TryDecode(bytes, inboundIsaac: null);
            Console.WriteLine($"[live] step 2: got {bytes.Length}-byte datagram from {from}, " +
                              $"decode={decoded.Error}, flags={decoded.Packet?.Header.Flags}");
            if (decoded.IsOk && decoded.Packet!.Header.HasFlag(PacketHeaderFlags.ConnectRequest))
            {
                connectRequest = decoded.Packet;
                break;
            }
        }
        Assert.NotNull(connectRequest);

        var cr = connectRequest!.Optional;
        Console.WriteLine($"[live] step 2: ConnectRequest cookie=0x{cr.ConnectRequestCookie:X16} " +
                          $"clientId=0x{cr.ConnectRequestClientId:X8}");

        byte[] connectResponseBody = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(connectResponseBody, cr.ConnectRequestCookie);

        var crHeader = new PacketHeader
        {
            Sequence  = 1,
            Flags     = PacketHeaderFlags.ConnectResponse,
            Id        = 0,
            Time      = 0,
            Iteration = 0,
        };
        byte[] connectResponseDatagram = PacketCodec.Encode(crHeader, connectResponseBody, outboundIsaac: null);

        Console.WriteLine($"[live] step 3: sleeping 200ms (ACE handshake race delay) then sending " +
                          $"{connectResponseDatagram.Length}-byte ConnectResponse to {connectEndpoint}");
        Thread.Sleep(200);
        net.Send(connectEndpoint, connectResponseDatagram);

        // Seed the two ISAAC streams NOW. From this point on the server
        // will use EncryptedChecksum for everything it sends us, and expects
        // the same from us.
        var serverSeedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(serverSeedBytes, cr.ConnectRequestServerSeed);
        var clientSeedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(clientSeedBytes, cr.ConnectRequestClientSeed);
        var inboundIsaac  = new IsaacRandom(serverSeedBytes);  // decrypts server's outbound
        var outboundIsaac = new IsaacRandom(clientSeedBytes);  // encrypts our outbound
        Console.WriteLine($"[live] step 3: ISAAC seeds primed, " +
                          $"inbound.next=0x{new IsaacRandom(serverSeedBytes).Next():X8}, " +
                          $"outbound.next=0x{new IsaacRandom(clientSeedBytes).Next():X8}");

        // Step 4: receive post-handshake traffic. Run fragments through a
        // FragmentAssembler so multi-packet game messages reassemble, then
        // read the opcode (first 4 bytes of the assembled body) to prove
        // we're looking at real GameMessage opcodes.
        var assembler = new FragmentAssembler();
        int postHandshakePackets = 0;
        int successfullyDecoded = 0;
        int checksumFailures = 0;
        var seenOpcodes = new List<uint>();
        var postDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < postDeadline)
        {
            var bytes = net.Receive(postDeadline - DateTime.UtcNow, out var from);
            if (bytes is null) break;
            postHandshakePackets++;
            var decoded = PacketCodec.TryDecode(bytes, inboundIsaac);
            Console.WriteLine($"[live] step 4: got {bytes.Length}-byte datagram from {from}, " +
                              $"decode={decoded.Error}, flags={decoded.Packet?.Header.Flags}, " +
                              $"seq={decoded.Packet?.Header.Sequence}");
            if (decoded.IsOk)
            {
                successfullyDecoded++;
                foreach (var frag in decoded.Packet!.Fragments)
                {
                    var completeBody = assembler.Ingest(frag, out _);
                    if (completeBody is not null && completeBody.Length >= 4)
                    {
                        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(completeBody);
                        seenOpcodes.Add(opcode);
                        string name = opcode switch
                        {
                            0xF658 => "CharacterList",
                            0xF7E1 => "ServerName",
                            0xF7E5 => "DDDInterrogation",
                            _      => "unknown",
                        };
                        Console.WriteLine($"[live]   GameMessage assembled: opcode=0x{opcode:X8} ({name}), " +
                                          $"body={completeBody.Length} bytes");
                    }
                }
            }
            else if (decoded.Error == PacketCodec.DecodeError.ChecksumMismatch)
                checksumFailures++;
        }

        Console.WriteLine($"[live] step 4 summary: {postHandshakePackets} packets received, " +
                          $"{successfullyDecoded} decoded OK, {checksumFailures} checksum failures, " +
                          $"{seenOpcodes.Count} GameMessages assembled");

        Assert.True(postHandshakePackets > 0,
            "Server did not send any post-handshake packets — ConnectResponse rejected?");
    }

    [Fact]
    public void Live_CharacterEnterWorld_ReceivesCreateObjectFlood()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_LIVE") != "1")
            Assert.Fail("Lane=Live requires ACDREAM_LIVE=1 and a reachable configured server.");

        var host = Environment.GetEnvironmentVariable("ACDREAM_TEST_HOST") ?? "127.0.0.1";
        var portStr = Environment.GetEnvironmentVariable("ACDREAM_TEST_PORT") ?? "9000";
        var user = Environment.GetEnvironmentVariable("ACDREAM_TEST_USER")!;
        var pass = Environment.GetEnvironmentVariable("ACDREAM_TEST_PASS")!;

        int loginPort = int.Parse(portStr);
        var loginEndpoint = new IPEndPoint(IPAddress.Parse(host), loginPort);
        var connectEndpoint = new IPEndPoint(IPAddress.Parse(host), loginPort + 1);
        using var net = new NetClient(loginEndpoint);

        uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] loginPayload = LoginRequest.Build(user, pass, timestamp);
        var loginHeader = new PacketHeader { Flags = PacketHeaderFlags.LoginRequest };
        net.Send(PacketCodec.Encode(loginHeader, loginPayload, null));
        Console.WriteLine("[live] step 1: LoginRequest sent");

        Packet? cr = null;
        var d1 = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < d1)
        {
            var b = net.Receive(d1 - DateTime.UtcNow, out _);
            if (b is null) break;
            var dec = PacketCodec.TryDecode(b, null);
            if (dec.IsOk && dec.Packet!.Header.HasFlag(PacketHeaderFlags.ConnectRequest))
            { cr = dec.Packet; break; }
        }
        Assert.NotNull(cr);
        var opt = cr!.Optional;
        Console.WriteLine($"[live] step 2: ConnectRequest received (cookie=0x{opt.ConnectRequestCookie:X16} clientId=0x{opt.ConnectRequestClientId:X8})");

        byte[] crBody = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(crBody, opt.ConnectRequestCookie);
        var crHeader = new PacketHeader { Sequence = 1, Flags = PacketHeaderFlags.ConnectResponse, Id = 0 };
        Thread.Sleep(200);
        net.Send(connectEndpoint, PacketCodec.Encode(crHeader, crBody, null));
        Console.WriteLine("[live] step 3: ConnectResponse sent to 9001");

        byte[] serverSeedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(serverSeedBytes, opt.ConnectRequestServerSeed);
        byte[] clientSeedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(clientSeedBytes, opt.ConnectRequestClientSeed);
        var inboundIsaac  = new IsaacRandom(serverSeedBytes);
        var outboundIsaac = new IsaacRandom(clientSeedBytes);
        ushort sessionClientId = (ushort)opt.ConnectRequestClientId;

        uint clientPacketSequence = 2;

        uint fragmentSequence = 1;

        var assembler = new FragmentAssembler();
        CharacterList.Parsed? charList = null;
        var d4 = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < d4 && charList is null)
        {
            var b = net.Receive(d4 - DateTime.UtcNow, out _);
            if (b is null) break;
            var dec = PacketCodec.TryDecode(b, inboundIsaac);
            if (!dec.IsOk) continue;
            foreach (var frag in dec.Packet!.Fragments)
            {
                var body = assembler.Ingest(frag, out _);
                if (body is null || body.Length < 4) continue;
                uint op = BinaryPrimitives.ReadUInt32LittleEndian(body);
                if (op == CharacterList.Opcode)
                {
                    charList = CharacterList.Parse(body);
                    Console.WriteLine($"[live] step 4: CharacterList received " +
                                      $"account={charList.AccountName} count={charList.Characters.Count}");
                    foreach (var c in charList.Characters)
                        Console.WriteLine($"[live]   character: id=0x{c.Id:X8} name={c.Name}");
                }
            }
        }

        Assert.NotNull(charList);
        Assert.NotEmpty(charList!.Characters);
        Assert.True(
            CharacterList.TrySelectFirstAvailable(charList, out var selection),
            "CharacterList contains no active, non-greyed character");
        var chosen = selection.Character;
        Console.WriteLine($"[live] choosing character: 0x{chosen.Id:X8} {chosen.Name}");

        void SendGameMessage(byte[] gameMessageBody, string label)
        {
            var fragment = GameMessageFragment.BuildSingleFragment(
                fragmentSequence++, GameMessageGroup.UIQueue, gameMessageBody);
            byte[] packetBody = GameMessageFragment.Serialize(fragment);
            var header = new PacketHeader
            {
                Sequence = clientPacketSequence++,
                Flags    = PacketHeaderFlags.BlobFragments | PacketHeaderFlags.EncryptedChecksum,
                Id       = sessionClientId,
            };
            byte[] datagram = PacketCodec.Encode(header, packetBody, outboundIsaac);
            net.Send(datagram);
            Console.WriteLine($"[live] sent {label}: packet.seq={header.Sequence} " +
                              $"frag.seq={fragment.Header.Sequence} bytes={datagram.Length}");
        }

        SendGameMessage(CharacterEnterWorld.BuildEnterWorldRequestBody(),
                        "CharacterEnterWorldRequest");

        bool sawServerReady = false;
        var d6 = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < d6 && !sawServerReady)
        {
            var b = net.Receive(d6 - DateTime.UtcNow, out _);
            if (b is null) break;
            var dec = PacketCodec.TryDecode(b, inboundIsaac);
            if (!dec.IsOk)
            {
                Console.WriteLine($"[live] step 6: decode error {dec.Error}");
                continue;
            }
            foreach (var frag in dec.Packet!.Fragments)
            {
                var body = assembler.Ingest(frag, out _);
                if (body is null || body.Length < 4) continue;
                uint op = BinaryPrimitives.ReadUInt32LittleEndian(body);
                Console.WriteLine($"[live] step 6: got GameMessage opcode=0x{op:X8}");
                if (op == 0xF7DFu)
                {
                    sawServerReady = true;
                    Console.WriteLine("[live] step 6: CharacterEnterWorldServerReady received");
                    break;
                }
            }
        }

        Assert.True(sawServerReady, "Server did not send CharacterEnterWorldServerReady");

        SendGameMessage(
            CharacterEnterWorld.BuildEnterWorldBody(chosen.Id, charList.AccountName),
            $"CharacterEnterWorld(guid=0x{chosen.Id:X8})");

        int totalMessages = 0;
        int createObjectCount = 0;
        int createObjectParsed = 0;
        int createObjectWithPosition = 0;
        int createObjectWithSetup = 0;
        var seenOpcodes = new HashSet<uint>();
        var parsedCreateObjects = new List<CreateObject.Parsed>();
        var d8 = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < d8)
        {
            var b = net.Receive(d8 - DateTime.UtcNow, out _);
            if (b is null) break;
            var dec = PacketCodec.TryDecode(b, inboundIsaac);
            if (!dec.IsOk)
            {
                Console.WriteLine($"[live] step 8: decode error {dec.Error} on {b.Length}-byte packet");
                continue;
            }
            foreach (var frag in dec.Packet!.Fragments)
            {
                var body = assembler.Ingest(frag, out _);
                if (body is null || body.Length < 4) continue;
                uint op = BinaryPrimitives.ReadUInt32LittleEndian(body);
                seenOpcodes.Add(op);
                totalMessages++;
                if (op == CreateObject.Opcode)
                {
                    createObjectCount++;
                    var parsed = CreateObject.TryParse(body);
                    if (parsed is not null)
                    {
                        createObjectParsed++;
                        parsedCreateObjects.Add(parsed.Value);
                        if (parsed.Value.Position is not null) createObjectWithPosition++;
                        if (parsed.Value.SetupTableId is not null) createObjectWithSetup++;
                    }
                }
            }
        }

        Console.WriteLine($"[live] step 8 summary: {totalMessages} GameMessages assembled, " +
                          $"{createObjectCount} CreateObject, " +
                          $"{createObjectParsed} parsed, " +
                          $"{createObjectWithPosition} w/position, " +
                          $"{createObjectWithSetup} w/setup");
        Console.WriteLine("[live] unique opcodes seen: " +
                          string.Join(", ", seenOpcodes.Select(o => $"0x{o:X8}")));

        foreach (var co in parsedCreateObjects.Take(10))
        {
            string posStr = co.Position is { } p
                ? $"lb=0x{p.LandblockId:X8} xyz=({p.PositionX:F2},{p.PositionY:F2},{p.PositionZ:F2})"
                : "no position";
            string setupStr = co.SetupTableId is { } s ? $"setup=0x{s:X8}" : "no setup";
            Console.WriteLine($"[live]   CreateObject guid=0x{co.Guid:X8} {posStr} {setupStr}");
        }

        Assert.True(createObjectCount > 0,
            $"Expected at least one CreateObject post-login, got {createObjectCount}");
    }
}
