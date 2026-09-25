using System.Buffers.Binary;
using System.Reflection;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The server, as far as an arm is concerned. A scenario says what the server
/// said and this hands it to that arm's world connection at the point the
/// connection's own decoder hands a message on, so from there the whole of
/// each client's real inbound path runs: its live-session event router, the
/// binding records that router was built with, the runtime owners underneath
/// and, for a game event, that event's own payload parser.
///
/// Both arms are given the same script at the same step, so anything the two
/// transcripts disagree about afterwards is a difference between the clients.
///
/// What this does NOT reach into, and why:
/// * the reliable transport and the packet codec, which sit above the decoder
///   and are the same code for both clients, so a difference cannot hide there;
/// * the windowed client's drawn-entity sink, which cannot be built without a
///   drawn world -- see <see cref="ParityInboundRoute"/>.
/// </summary>
internal sealed class ParityServer(WorldSession session, Func<uint> playerGuid)
{
    /// <summary>A movement that names the thing to walk to.</summary>
    private const byte MoveToObjectMovementType = 6;


    /// <summary>
    /// The flags a plain walk order carries: it may be run, and the walk is
    /// allowed to charge when the thing is far enough off.
    /// </summary>
    private const uint MoveToFlags = 0x2u | 0x10u;

    /// <summary>The stance a walk order is written in when it names none.</summary>
    private const ushort DefaultStance = 0x003D;

    private uint _gameEventSequence;

    /// <summary>Numbers the statements the server makes about the character.</summary>
    private byte _characterSequence;

    /// <summary>
    /// The last step of letting a character in: the connection is in the
    /// world from here on.
    /// </summary>
    /// <remarks>
    /// Without this the connection stays where a connection with no wire
    /// under it stays, and every client path that asks "am I in the world"
    /// before it sends -- which is every use, every pickup and every
    /// description -- answers no on BOTH clients. Two clients that both
    /// refuse agree line for line, so a scenario over them proves nothing.
    /// This is the same state change the real entry makes as its last act,
    /// so what watches for it is told in the ordinary way.
    /// </remarks>
    internal void LetTheCharacterIn() => Invoke(
        "Transition",
        Enum.Parse(typeof(WorldSession.State), nameof(WorldSession.State.InWorld)));

    /// <summary>An object arrives.</summary>
    internal void CreateObject(WorldSession.EntitySpawn spawn) =>
        Raise(nameof(WorldSession.EntitySpawned), spawn);

    /// <summary>An object is taken out of the world.</summary>
    internal void DeleteObject(uint guid, ushort instanceSequence) =>
        Raise(
            nameof(WorldSession.EntityDeleted),
            new DeleteObject.Parsed(guid, instanceSequence));

    /// <summary>An object's physics state changes -- hidden, frozen, and so on.</summary>
    internal void SetState(
        uint guid,
        PhysicsStateFlags state,
        ushort instanceSequence = 1,
        ushort stateSequence = 2) =>
        Raise(
            nameof(WorldSession.StateUpdated),
            new SetState.Parsed(
                guid, (uint)state, instanceSequence, stateSequence));

    /// <summary>An object is somewhere else now.</summary>
    internal void UpdatePosition(
        uint guid,
        float x,
        float y,
        float z,
        uint cell,
        ushort positionSequence,
        ushort instanceSequence = 1) =>
        Raise(
            nameof(WorldSession.PositionUpdated),
            new WorldSession.EntityPositionUpdate(
                guid,
                new CreateObject.ServerPosition(cell, x, y, z, 1f, 0f, 0f, 0f),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: instanceSequence,
                PositionSequence: positionSequence,
                TeleportSequence: 0,
                ForcePositionSequence: 0));

    /// <summary>
    /// An order to walk to a named thing and stop a given distance short of
    /// it. This is the answer the server gives to a use issued from out of
    /// reach, so a client that lets it pass waits out the whole of the
    /// server's patience for a "done" that did nothing.
    /// </summary>
    /// <param name="guid">Whose movement this is.</param>
    /// <param name="targetGuid">The thing to walk to.</param>
    /// <param name="distanceToObject">How close to get, in metres.</param>
    /// <param name="originX">Where the order says the thing was.</param>
    /// <param name="originY">Where the order says the thing was.</param>
    /// <param name="runRate">How fast, as a multiple of the walk rate.</param>
    /// <param name="movementSequence">The order's place in the movement series.</param>
    /// <param name="bitfield">The order's own flags, as they arrive on the wire.</param>
    internal void MoveToObject(
        uint guid,
        uint targetGuid,
        float distanceToObject,
        float originX,
        float originY,
        float runRate = 1f,
        ushort movementSequence = 2,
        uint bitfield = MoveToFlags) =>
        Motion(
            guid,
            MoveToObjectMovementType,
            new CreateObject.MoveToPathData(
                TargetGuid: targetGuid,
                OriginCellId: ParityPlayerBody.Cell,
                OriginX: originX,
                OriginY: originY,
                OriginZ: ParityPlayerBody.GroundHeight,
                DistanceToObject: distanceToObject,
                MinDistance: 0f,
                FailDistance: 50f,
                WalkRunThreshold: 1f,
                DesiredHeading: 0f,
                Bitfield: bitfield),
            runRate,
            movementSequence);


    /// <summary>
    /// The movement the two orders above are written in, for a scenario that
    /// needs to say something the two shorthands do not cover.
    /// </summary>
    internal void Motion(
        uint guid,
        byte movementType,
        CreateObject.MoveToPathData path,
        float runRate,
        ushort movementSequence) =>
        Raise(
            nameof(WorldSession.MotionUpdated),
            new WorldSession.EntityMotionUpdate(
                guid,
                new CreateObject.ServerMotionState(
                    Stance: DefaultStance,
                    ForwardCommand: null,
                    MovementType: movementType,
                    MoveToParameters: path.Bitfield,
                    MoveToSpeed: 1f,
                    MoveToRunRate: runRate,
                    MoveToPath: path),
                InstanceSequence: 1,
                MovementSequence: movementSequence,
                ServerControlSequence: movementSequence,
                IsAutonomous: false));

    /// <summary>The server's answer that a use has finished, and how.</summary>
    /// <param name="weenieError">Zero when it worked.</param>
    internal void UseDone(uint weenieError = 0u)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, weenieError);
        GameEvent(GameEventType.UseDone, payload);
    }

    /// <summary>
    /// What a container holds, as the server lists it once the container has
    /// been opened. The objects themselves arrive separately.
    /// </summary>
    internal void ViewContents(uint containerGuid, params uint[] itemGuids)
    {
        ArgumentNullException.ThrowIfNull(itemGuids);
        var payload = new byte[8 + (itemGuids.Length * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, containerGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4), (uint)itemGuids.Length);
        for (int index = 0; index < itemGuids.Length; index++)
        {
            Span<byte> entry = payload.AsSpan(8 + (index * 8));
            BinaryPrimitives.WriteUInt32LittleEndian(entry, itemGuids[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 1u);
        }
        GameEvent(GameEventType.ViewContents, payload);
    }

    /// <summary>
    /// The description of an object the character asked to be told about,
    /// carrying whichever whole-number properties the scenario names.
    /// </summary>
    /// <param name="guid">The object described.</param>
    /// <param name="properties">Property id to value.</param>
    /// <param name="success">Whether the server had anything to say.</param>
    internal void AppraisalResponse(
        uint guid,
        IReadOnlyList<(uint Property, int Value)> properties,
        bool success = true)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var payload = new byte[16 + (properties.Count * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, guid);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4),
            (uint)AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(8), success ? 1u : 0u);
        // The whole-number table: how many, how many buckets it was written
        // into, then the pairs.
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(12), (ushort)properties.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(14), (ushort)properties.Count);
        for (int index = 0; index < properties.Count; index++)
        {
            Span<byte> entry = payload.AsSpan(16 + (index * 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry, properties[index].Property);
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry[4..], unchecked((uint)properties[index].Value));
        }
        GameEvent(GameEventType.IdentifyObjectResponse, payload);
    }

    /// <summary>How much of a creature's health is left, as the server tells it.</summary>
    internal void UpdateHealth(uint targetGuid, float fraction)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, targetGuid);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), fraction);
        GameEvent(GameEventType.UpdateHealth, payload);
    }

    /// <summary>
    /// The server closes a container the character had open, which is what
    /// really ends a looting session: the client asks, the server says so.
    /// </summary>
    /// <param name="containerGuid">The container that is now closed.</param>
    internal void ClosedTheContainer(uint containerGuid)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, containerGuid);
        GameEvent(GameEventType.CloseGroundContainer, payload);
    }

    /// <summary>
    /// The server states one of the character's own skills, which is how a
    /// skill is raised mid-session.
    /// </summary>
    /// <param name="skillId">Which skill; 24 is run and 22 is jump.</param>
    /// <param name="ranks">How much of it the character has trained.</param>
    /// <param name="xp">The experience banked into it towards those ranks.</param>
    internal void SkillUpdate(uint skillId, uint ranks, uint xp = 0u) =>
        Raise(
            nameof(WorldSession.SkillUpdated),
            new PrivateUpdateSkill.Parsed(
                Sequence: ++_characterSequence,
                SkillId: skillId,
                Ranks: ranks,
                AdjustPP: 0,
                // Trained, which is what a raised skill is.
                AdvancementClass: 2u,
                Xp: xp,
                Init: 0u,
                Resistance: 0u,
                LastUsed: 0d));

    /// <summary>
    /// The server states one of the character's primary attributes, which is
    /// how one is raised mid-session.
    /// </summary>
    /// <param name="attributeId">Which attribute; 1 is strength, 2 endurance.</param>
    /// <param name="ranks">How many times it has been raised.</param>
    /// <param name="start">What it was before any of those raises.</param>
    /// <param name="xp">The experience banked into it towards those ranks.</param>
    internal void AttributeUpdate(
        uint attributeId,
        uint ranks,
        uint start = 100u,
        uint xp = 0u) =>
        Raise(
            nameof(WorldSession.AttributeUpdated),
            new PrivateUpdateAttribute.Parsed(
                Sequence: ++_characterSequence,
                AttributeId: attributeId,
                Ranks: ranks,
                Start: start,
                Xp: xp));

    /// <summary>
    /// The server states one of the character's vitals in full: how much of
    /// it there is and how much is left.
    /// </summary>
    /// <param name="vitalId">Which vital; 4 is stamina.</param>
    /// <param name="current">How much of it is left.</param>
    /// <param name="ranks">How many times the pool has been raised.</param>
    /// <param name="xp">The experience banked into it towards those ranks.</param>
    internal void VitalUpdate(
        uint vitalId,
        uint current,
        uint ranks = 0u,
        uint xp = 0u) =>
        Raise(
            nameof(WorldSession.VitalUpdated),
            new PrivateUpdateVital.ParsedFull(
                Sequence: ++_characterSequence,
                VitalId: vitalId,
                Ranks: ranks,
                Start: 100u,
                Xp: xp,
                Current: current));

    /// <summary>
    /// The character was killed, with the line the server sends about it.
    /// </summary>
    /// <param name="deathMessage">What the server says happened.</param>
    internal void KilledTheCharacter(string deathMessage)
    {
        ArgumentNullException.ThrowIfNull(deathMessage);
        byte[] text = System.Text.Encoding.Latin1.GetBytes(deathMessage);
        // Length, the line itself, then padding up to the next four bytes:
        // the shape every length-prefixed line on the wire has.
        int size = 2 + text.Length;
        var payload = new byte[size + ((4 - (size & 3)) & 3)];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)text.Length);
        text.CopyTo(payload, 2);
        GameEvent(GameEventType.VictimNotification, payload);
    }

    /// <summary>A line said at range (a shout), as the server relays it.</summary>
    internal void RangedSpeech(string text, string sender, uint senderGuid) =>
        Raise(
            nameof(WorldSession.SpeechHeard),
            new HearSpeech.Parsed(
                text, sender, senderGuid, ChatType: 0x02u, IsRanged: true, Range: 60f));

    /// <summary>A line of text from the server itself.</summary>
    internal void SystemMessage(string text, uint chatType) =>
        Raise(
            nameof(WorldSession.ServerMessageReceived),
            new ServerMessage.Parsed(text, chatType));

    /// <summary>
    /// The allegiance, as the server states it when the client asks. This is
    /// written onto the wire and handed to the connection, so the client's
    /// own parser and its own inbound route carry it the rest of the way --
    /// which is what a scenario about a break wants to stand on, rather than
    /// reaching past both and writing the profile into the owner by hand.
    /// </summary>
    /// <param name="update">What the server says the allegiance is.</param>
    internal void AllegianceUpdate(ClientCommandResponses.AllegianceUpdate update)
    {
        var payload = new List<byte>();
        WriteU32(payload, update.Rank);
        WriteU32(payload, update.TotalMembers);
        WriteU32(payload, update.TotalVassals);
        WriteU16(payload, update.RecordCount);
        // The profile version this is written in. Eight is the first that
        // carries the allegiance's name, which is what the profile is read
        // for; the gates below are the ones that version opens.
        const ushort ProfileVersion = 8;
        WriteU16(payload, ProfileVersion);
        // Officers: none, and the count is followed by its own padding.
        WriteU16(payload, 0);
        WriteU16(payload, 0);
        // The four broadcast counters.
        for (int index = 0; index < 4; index++)
            WriteU32(payload, 0u);
        WriteString(payload, string.Empty);   // message of the day
        WriteString(payload, string.Empty);   // and who set it
        WriteU32(payload, 0u);                // the allegiance's chat room
        // The eight-word block version seven added.
        for (int index = 0; index < 8; index++)
            WriteU32(payload, 0u);
        WriteString(payload, update.AllegianceName);
        WriteU32(payload, 0u);                // when the name was last set

        if (update.Monarch is { } monarch)
        {
            WriteAllegianceMember(payload, monarch);
            foreach (ClientCommandResponses.AllegianceMemberRecord record in
                update.Records)
            {
                WriteU32(payload, record.ParentGuid);
                WriteAllegianceMember(payload, record);
            }
        }

        GameEvent(GameEventType.AllegianceUpdate, [.. payload]);
    }

    /// <summary>
    /// A line on one of the numbered chat channels, as the server relays it.
    /// An empty speaker is how the server hands a line back to the one who
    /// sent it.
    /// </summary>
    internal void ChannelBroadcast(uint channelId, string sender, string text)
    {
        var payload = new List<byte>();
        WriteU32(payload, channelId);
        WriteString(payload, sender);
        WriteString(payload, text);
        GameEvent(GameEventType.ChannelBroadcast, [.. payload]);
    }

    /// <summary>A tell, as the server delivers one to its listener.</summary>
    internal void Tell(
        string text,
        string sender,
        uint senderGuid,
        uint targetGuid,
        uint chatType)
    {
        var payload = new List<byte>();
        WriteString(payload, text);
        WriteString(payload, sender);
        WriteU32(payload, senderGuid);
        WriteU32(payload, targetGuid);
        WriteU32(payload, chatType);
        GameEvent(GameEventType.Tell, [.. payload]);
    }

    /// <summary>One member of a fellowship, as the server's roster states it.</summary>
    internal readonly record struct Fellow(uint Guid, string Name, uint Level);

    /// <summary>
    /// The whole fellowship, as the server states it when the character
    /// joins or the roster changes: written onto the wire, so the client's
    /// own parser and inbound route carry it the rest of the way. Every
    /// member is at full vitals of 100 and takes a share of the loot; nobody
    /// has departed.
    /// </summary>
    internal void FellowshipFullUpdate(
        string name,
        uint leaderGuid,
        bool shareExperience,
        bool evenSplit,
        params Fellow[] members)
    {
        var payload = new List<byte>();
        WriteU16(payload, (ushort)members.Length);
        WriteU16(payload, 0);                 // the table's bucket count
        foreach (Fellow fellow in members)
        {
            WriteU32(payload, fellow.Guid);   // the roster's key
            WriteU32(payload, 0u);            // experience owed to the leader
            WriteU32(payload, 0u);            // and luminance
            WriteU32(payload, fellow.Level);
            for (int vital = 0; vital < 6; vital++)
                WriteU32(payload, 100u);      // maximum then current vitals
            WriteU32(payload, 1u);            // takes a share of the loot
            WriteString(payload, fellow.Name);
        }
        WriteString(payload, name);
        WriteU32(payload, leaderGuid);
        WriteU32(payload, shareExperience ? 1u : 0u);
        WriteU32(payload, evenSplit ? 1u : 0u);
        WriteU32(payload, 0u);                // not open
        WriteU32(payload, 0u);                // not locked
        WriteU16(payload, 0);                 // nobody departed
        WriteU16(payload, 0);
        GameEvent(GameEventType.FellowshipFullUpdate, [.. payload]);
    }

    /// <summary>One member of the allegiance, as the profile carries it.</summary>
    private static void WriteAllegianceMember(
        List<byte> payload,
        ClientCommandResponses.AllegianceMemberRecord record)
    {
        // Logged in, and carrying its level as its own word -- which is also
        // what says the member's right to pass experience up is stated here
        // rather than assumed.
        const uint LoggedIn = 0x1u;
        const uint HasPackedLevel = 0x8u;
        const uint MayPassupExperience = 0x10u;
        uint bitfield = HasPackedLevel
            | (record.IsLoggedIn ? LoggedIn : 0u)
            | (record.MayPassupExperience ? MayPassupExperience : 0u);

        WriteU32(payload, record.CharacterId);
        WriteU32(payload, record.CpCached);
        WriteU32(payload, record.CpTithed);
        WriteU32(payload, bitfield);
        payload.Add(record.Gender);
        payload.Add(record.HeritageGroup);
        WriteU16(payload, record.Rank);
        WriteU32(payload, record.Level);
        WriteU16(payload, record.Loyalty);
        WriteU16(payload, record.Leadership);
        WriteU32(payload, 0u);                // how long it has been online
        WriteU32(payload, 0u);                // and the high half of it
        WriteString(payload, record.Name);
    }

    private static void WriteU16(List<byte> payload, ushort value)
    {
        Span<byte> word = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(word, value);
        payload.AddRange(word);
    }

    private static void WriteU32(List<byte> payload, uint value)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, value);
        payload.AddRange(word);
    }

    /// <summary>
    /// A line as the wire carries one: how long it is, the characters, then
    /// padding up to the next four bytes.
    /// </summary>
    private static void WriteString(List<byte> payload, string text)
    {
        byte[] characters = System.Text.Encoding.Latin1.GetBytes(text);
        WriteU16(payload, (ushort)characters.Length);
        payload.AddRange(characters);
        int written = 2 + characters.Length;
        for (int pad = (4 - (written & 3)) & 3; pad > 0; pad--)
            payload.Add(0);
    }

    /// <summary>Hands a game event to the connection's own event dispatcher.</summary>
    internal void GameEvent(GameEventType type, byte[] payload) =>
        session.GameEvents.Dispatch(new GameEventEnvelope(
            playerGuid(),
            ++_gameEventSequence,
            type,
            payload));

    /// <summary>
    /// Hands one decoded message to the connection's own subscribers. The
    /// decoder raises these events and nothing else does, so a client's route
    /// cannot tell this apart from a packet off the wire.
    /// </summary>
    /// <summary>Runs one of the connection's own state changes.</summary>
    private void Invoke(string methodName, params object?[] arguments)
    {
        MethodInfo method = typeof(WorldSession).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"The world connection has no {methodName}.");
        _ = method.Invoke(session, arguments);
    }

    private void Raise<T>(string eventName, T message)
    {
        FieldInfo field = typeof(WorldSession).GetField(
            eventName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"The world connection has no {eventName} to raise.");
        ((Action<T>?)field.GetValue(session))?.Invoke(message);
    }
}
