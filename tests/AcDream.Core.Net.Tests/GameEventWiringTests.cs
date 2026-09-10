using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Tests.Messages;
using AcDream.Core.Player;
using AcDream.Core.Spells;
using AcDream.Core.Social;
using Xunit;

namespace AcDream.Core.Net.Tests;

public sealed class GameEventWiringTests
{
    private static byte[] MakeString16L(string s)
    {
        byte[] data = Encoding.ASCII.GetBytes(s);
        int recordSize = 2 + data.Length;
        int padding = (4 - (recordSize & 3)) & 3;
        byte[] result = new byte[recordSize + padding];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)data.Length);
        Array.Copy(data, 0, result, 2, data.Length);
        return result;
    }

    private static void AppendU32(List<byte> payload, uint value)
    {
        byte[] u32 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, value);
        payload.AddRange(u32);
    }

    private static byte[] WrapEnvelope(GameEventType type, byte[] payload)
    {
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,             GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),   0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),   0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12),  (uint)type);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }

    private static (GameEventDispatcher, ClientObjectTable, CombatState, Spellbook, ChatLog) MakeAll(Func<uint>? playerGuid = null)
    {
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        var combat = new CombatState();
        var spellbook = new Spellbook();
        var chat = new ChatLog();
        GameEventWiring.WireAll(dispatcher, items, combat, spellbook, chat, playerGuid: playerGuid);
        return (dispatcher, items, combat, spellbook, chat);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireAll_IdentifyObjectResponse_ForwardsCompleteResultAndMergesOnlySuccess(
        bool success)
    {
        const uint guid = 0x50000001u;
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        items.UpsertProperties(guid, new PropertyBundle());
        AppraiseInfoParser.Parsed? received = null;
        GameEventWiring.WireAll(
            dispatcher,
            items,
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            onAppraisal: appraisal => received = appraisal);

        byte[] payload = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, guid);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4),
            (uint)AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), success ? 1u : 0u);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 25u);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20), 42);

        GameEventEnvelope? envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.IdentifyObjectResponse, payload));
        Assert.NotNull(envelope);
        dispatcher.Dispatch(envelope.Value);

        Assert.NotNull(received);
        Assert.Equal(success, received.Value.Success);
        Assert.Equal(42, received.Value.Properties.Ints[25u]);
        if (success)
            Assert.Equal(42, items.Get(guid)!.Properties.Ints[25u]);
        else
            Assert.False(items.Get(guid)!.Properties.Ints.ContainsKey(25u));
    }

    [Fact]
    public void WireAll_LiteralAceCreatureFlag_ForwardsAndUpdatesHealth()
    {
        const uint guid = 0x50000001u;
        var dispatcher = new GameEventDispatcher();
        var combat = new CombatState();
        AppraiseInfoParser.Parsed? received = null;
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            combat,
            new Spellbook(),
            new ChatLog(),
            onAppraisal: appraisal => received = appraisal);

        byte[] payload = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, guid);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4),
            0x0100u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 75u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 100u);

        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.IdentifyObjectResponse, payload))!.Value);

        Assert.NotNull(received);
        Assert.NotNull(received.Value.CreatureProfile);
        Assert.Equal(0.75f, combat.GetHealthPercent(guid), 3);
    }


    [Fact]
    public void WireAll_ChannelBroadcast_RoutesToChatLog()
    {
        var (d, _, _, _, chat) = MakeAll();

        byte[] payload = new byte[4 + MakeString16L("Alice").Length + MakeString16L("hi").Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 42);
        int p = 4;
        var senderBytes = MakeString16L("Alice");
        Array.Copy(senderBytes, 0, payload, p, senderBytes.Length); p += senderBytes.Length;
        var msgBytes = MakeString16L("hi");
        Array.Copy(msgBytes, 0, payload, p, msgBytes.Length);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.ChannelBroadcast, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(1, chat.Count);
        var entry = chat.Snapshot()[0];
        Assert.Equal(ChatKind.Channel, entry.Kind);
        Assert.Equal("Alice", entry.Sender);
        Assert.Equal(0x08u, entry.LogTextType);
    }

    [Fact]
    public void WireAll_UpdateHealth_RoutesToCombatState()
    {
        var (d, _, combat, _, _) = MakeAll();

        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,           0xCAFE);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), 0.42f);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.UpdateHealth, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(0.42f, combat.GetHealthPercent(0xCAFE), 4);
    }

    [Fact]
    public void WireAll_MagicUpdateSpell_RoutesToSpellbook()
    {
        var (d, _, _, book, _) = MakeAll();

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x3E1);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.MagicUpdateSpell, payload));
        d.Dispatch(env!.Value);

        Assert.True(book.Knows(0x3E1));
    }

    [Fact]
    public void WireAll_WieldObject_RoutesToClientObjectTable()
    {
        const uint player = 0x2000u;
        var (d, items, _, _, _) = MakeAll(() => player);
        items.AddOrUpdate(new ClientObject { ObjectId = 0x1000, WeenieClassId = 1 });

        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,            0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),  (uint)EquipMask.MeleeWeapon);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WieldObject, payload));
        d.Dispatch(env!.Value);

        var item = items.Get(0x1000);
        Assert.NotNull(item);
        Assert.Equal(EquipMask.MeleeWeapon, item!.CurrentlyEquippedLocation);
        Assert.Equal(0u, item.ContainerId);
        Assert.Equal(player, item.WielderId);
    }

    [Fact]
    public void WireAll_WieldObject_ConfirmsOptimisticWield()
    {
        const uint player = 0x2000u;
        var (d, items, _, _, _) = MakeAll(() => player);
        items.AddOrUpdate(new ClientObject { ObjectId = 0x1500, WeenieClassId = 1 });
        items.MoveItem(0x1500, 0x9999u, newSlot: 0);                 // start in a pack
        items.WieldItemOptimistic(0x1500, player, EquipMask.MeleeWeapon);   // optimistic wield → snapshot pending
        var wieldConfirmed = new List<uint>();
        items.WieldConfirmed += itemId => wieldConfirmed.Add(itemId);

        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,            0x1500);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),  (uint)EquipMask.MeleeWeapon);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WieldObject, payload));
        d.Dispatch(env!.Value);

        Assert.False(items.RollbackMove(0x1500));
        Assert.Equal(0u, items.Get(0x1500)!.ContainerId);
        Assert.Equal(player, items.Get(0x1500)!.WielderId);
        Assert.Equal(new[] { 0x1500u }, wieldConfirmed);
    }

    [Fact]
    public void WireAll_HouseUpdateRestrictions_UpdatesClientObjectTable()
    {
        var (d, items, _, _, _) = MakeAll();
        const uint houseGuid = 0x70200099u;
        items.AddOrUpdate(new ClientObject { ObjectId = houseGuid, WeenieClassId = 1 });

        var payload = new List<byte>();
        payload.Add(5);                          // Sequence
        AppendU32(payload, houseGuid);            // SenderId
        AppendU32(payload, 0x10000002u);           // Version
        AppendU32(payload, 0u);                    // Flags (private)
        AppendU32(payload, 0x50000500u);           // MonarchId
        AppendU32(payload, 1u);
        AppendU32(payload, 0x50000001u);           // guest guid
        AppendU32(payload, 1u);                    // guest permission (storage)

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.HouseUpdateRestrictions, payload.ToArray()));
        d.Dispatch(env!.Value);

        var house = items.Get(houseGuid);
        Assert.NotNull(house);
        Assert.NotNull(house!.Restrictions);
        Assert.False(house.Restrictions!.OpenToPublic);
        Assert.Equal(0x50000500u, house.Restrictions.AllegianceMonarchId);
        Assert.Single(house.Restrictions.Guests);
        Assert.Equal(1u, house.Restrictions.Guests[0x50000001u]);
    }

    [Fact]
    public void WireAll_HouseUpdateRestrictions_UnknownHouseObject_NoOps()
    {
        var (d, items, _, _, _) = MakeAll();
        const uint unknownHouseGuid = 0x70200100u;

        var payload = new List<byte>();
        payload.Add(1);
        AppendU32(payload, unknownHouseGuid);
        AppendU32(payload, 0x10000002u);
        AppendU32(payload, 1u); // open
        AppendU32(payload, 0u);
        AppendU32(payload, 0u); // zero guests

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.HouseUpdateRestrictions, payload.ToArray()));
        d.Dispatch(env!.Value);

        Assert.Null(items.Get(unknownHouseGuid));
    }

    [Fact]
    public void WireAll_ConfirmationRequest_UsesRetailTwoIntegerHeader()
    {
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        var combat = new CombatState();
        var spellbook = new Spellbook();
        var chat = new ChatLog();
        GameEvents.CharacterConfirmationRequest? received = null;
        GameEventWiring.WireAll(
            dispatcher,
            items,
            combat,
            spellbook,
            chat,
            onConfirmationRequest: request => received = request);

        byte[] text = MakeString16L("Proceed?");
        byte[] payload = new byte[8 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 7u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 42u);
        text.CopyTo(payload.AsSpan(8));

        var env = GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.CharacterConfirmationRequest,
            payload));
        dispatcher.Dispatch(env!.Value);

        Assert.Equal(new GameEvents.CharacterConfirmationRequest(7u, 42u, "Proceed?"), received);
    }

    [Fact]
    public void WireAll_ConfirmationDone_UsesRetailTypeAndContextTuple()
    {
        var dispatcher = new GameEventDispatcher();
        GameEvents.CharacterConfirmationDone? received = null;
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            onConfirmationDone: done => received = done);
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 6u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 314u);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.CharacterConfirmationDone,
            payload));
        dispatcher.Dispatch(env!.Value);

        Assert.Equal(new GameEvents.CharacterConfirmationDone(6u, 314u), received);
    }

    [Fact]
    public void WireAll_FriendsUpdate_ReplacesAuthoritativeSocialState()
    {
        var dispatcher = new GameEventDispatcher();
        var friends = new FriendsState();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            friends: friends);

        byte[] name = MakeString16L("Alice");
        byte[] payload = new byte[4 + 12 + name.Length + 4 + 4 + 4];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(p), 1u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(p), 0x50000001u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(p), 1u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(p), 0u); p += 4;
        name.CopyTo(payload.AsSpan(p)); p += name.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(p), 0u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(p), 0u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(p), (uint)FriendsUpdateType.Full);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.FriendsListUpdate, payload));
        dispatcher.Dispatch(env!.Value);

        FriendEntry friend = Assert.Single(friends.Snapshot());
        Assert.Equal("Alice", friend.Name);
        Assert.True(friend.Online);
    }

    [Fact]
    public void WireAll_PopupString_RoutesToChatLog()
    {
        var (d, _, _, _, chat) = MakeAll();

        byte[] payload = MakeString16L("A modal message");
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PopupString, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(1, chat.Count);
        Assert.Equal(ChatKind.Popup, chat.Snapshot()[0].Kind);
    }

    [Theory]
    [InlineData("", "2d 4h", "You have played for 2d 4h.")]
    [InlineData("Alice", "1y 2mo", "Alice has played for 1y 2mo.")]
    public void WireAll_QueryAgeResponse_UsesRetailWording(
        string name, string age, string expected)
    {
        var (d, _, _, _, chat) = MakeAll();
        byte[] nameBytes = MakeString16L(name);
        byte[] ageBytes = MakeString16L(age);
        byte[] payload = [.. nameBytes, .. ageBytes];

        var env = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.QueryAgeResponse, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(expected, chat.Snapshot().Single().Text);
    }

    [Fact]
    public void WireAll_PlayerDescription_PopulatesLocalPlayerStateVitals()
    {
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        var combat = new CombatState();
        var spellbook = new Spellbook();
        var chat = new ChatLog();
        var local = new LocalPlayerState();
        GameEventWiring.WireAll(dispatcher, items, combat, spellbook, chat, local);

        byte[] body = new byte[140];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0u); p += 4;          // propertyFlags
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0x52u); p += 4;       // weenieType
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0x01u); p += 4;       // vectorFlags = ATTRIBUTE
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  1u); p += 4;          // has_health
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0x1FFu); p += 4;      // attribute_flags = Full

        // Primary attrs in order 1..6.
        WritePrimaryAttr(body, ref p, ranks: 0, start: 50, xp: 0);   // 1 Strength
        WritePrimaryAttr(body, ref p, ranks: 50, start: 150, xp: 0);
        WritePrimaryAttr(body, ref p, ranks: 0, start: 50, xp: 0);   // 3 Quickness
        WritePrimaryAttr(body, ref p, ranks: 0, start: 50, xp: 0);
        WritePrimaryAttr(body, ref p, ranks: 0, start: 50, xp: 0);   // 5 Focus
        WritePrimaryAttr(body, ref p, ranks: 50, start: 50, xp: 0);
        // Vitals 7/8/9.
        WriteVitalBlock(body, ref p, ranks: 0, start: 0, xp: 0, current: 90);   // Health
        WriteVitalBlock(body, ref p, ranks: 0, start: 0, xp: 0, current: 140);  // Stamina
        WriteVitalBlock(body, ref p, ranks: 0, start: 0, xp: 0, current: 50);   // Mana

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PlayerDescription, body));
        dispatcher.Dispatch(env!.Value);

        var health = local.Get(LocalPlayerState.VitalKind.Health);
        var stam   = local.Get(LocalPlayerState.VitalKind.Stamina);
        var mana   = local.Get(LocalPlayerState.VitalKind.Mana);
        Assert.NotNull(health);
        Assert.NotNull(stam);
        Assert.NotNull(mana);
        Assert.Equal(90u,  health!.Value.Current);
        Assert.Equal(140u, stam!.Value.Current);
        Assert.Equal(50u,  mana!.Value.Current);
        Assert.Equal(200u, local.GetAttribute(LocalPlayerState.AttributeKind.Endurance)!.Value.Current);
        Assert.Equal(100u, local.GetAttribute(LocalPlayerState.AttributeKind.Self)!.Value.Current);
        Assert.Equal(0.9f, local.HealthPercent!.Value, precision: 3);
        Assert.Equal(0.7f, local.StaminaPercent!.Value, precision: 3);
        Assert.Equal(0.5f, local.ManaPercent!.Value, precision: 3);
    }

    [Fact]
    public void WireAll_PlayerDescription_PopulatesLocalPlayerStateSkills()
    {
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        var combat = new CombatState();
        var spellbook = new Spellbook();
        var chat = new ChatLog();
        var local = new LocalPlayerState();
        int callbackRun = -1;

        GameEventWiring.WireAll(dispatcher, items, combat, spellbook, chat, local,
            onSkillsUpdated: (run, _) => callbackRun = run,
            resolveSkillFormulaBonus: (skillId, attrs) =>
            {
                Assert.Equal(24u, skillId);
                Assert.Equal(50u, attrs[1u]);
                return 80u;
            });

        var sb = new MemoryStream();
        using var w = new BinaryWriter(sb);
        w.Write(0u);     // propertyFlags
        w.Write(0x52u);  // weenieType
        w.Write(0x03u);  // vectorFlags = Attribute | Skill
        w.Write(0u);     // has_health = false
        w.Write(0x01u);  // attribute_flags = Strength only
        w.Write(0u);     // Strength ranks
        w.Write(50u);    // Strength start
        w.Write(0u);     // Strength xp

        w.Write((ushort)1);
        w.Write((ushort)8); // buckets
        w.Write(24u);       // Run
        w.Write((ushort)12);
        w.Write((ushort)1);
        w.Write(2u);        // trained
        w.Write(3456u);     // xp
        w.Write(30u);       // init
        w.Write(0u);        // resistance
        w.Write(1.5);       // last used

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PlayerDescription, sb.ToArray()));
        dispatcher.Dispatch(env!.Value);

        var run = local.GetSkill(24u);
        Assert.NotNull(run);
        Assert.Equal(122u, run!.Value.CurrentLevel);
        Assert.Equal(2u, run.Value.Status);
        Assert.Equal(3456u, run.Value.Xp);
        Assert.Equal(122, callbackRun);
    }

    [Fact]
    public void WireAll_PlayerDescription_FeedsSpellbook()
    {
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        var combat = new CombatState();
        var spellbook = new Spellbook();
        var chat = new ChatLog();
        var local = new LocalPlayerState();
        GameEventWiring.WireAll(dispatcher, items, combat, spellbook, chat, local);

        // Body: SPELL vector flag + a spell table with 2 entries.
        var sb = new MemoryStream();
        using var w = new BinaryWriter(sb);
        w.Write(0u);     // propertyFlags
        w.Write(0x52u);  // weenieType
        w.Write(0x100u); // vectorFlags = SPELL only
        w.Write(0u);     // has_health = false
        w.Write((ushort)2);
        w.Write((ushort)64); // buckets
        w.Write(0x3E1u); w.Write(2.0f);
        w.Write(0x3E2u); w.Write(2.0f);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PlayerDescription, sb.ToArray()));
        dispatcher.Dispatch(env!.Value);

        Assert.True(spellbook.Knows(0x3E1u));
        Assert.True(spellbook.Knows(0x3E2u));
    }

    private static void WriteVitalBlock(byte[] body, ref int p, uint ranks, uint start, uint xp, uint current)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), ranks);   p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), start);   p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), xp);      p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), current); p += 4;
    }

    private static void WritePrimaryAttr(byte[] body, ref int p, uint ranks, uint start, uint xp)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), ranks); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), start); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), xp);    p += 4;
    }

    [Fact]
    public void WireAll_KillerNotification_AppendsCombatLine()
    {
        var (d, _, _, _, chat) = MakeAll();
        byte[] payload = MakeString16L("You killed the drudge!");

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.KillerNotification, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(1, chat.Count);
        var entry = chat.Snapshot()[0];
        Assert.Equal(ChatKind.Combat, entry.Kind);
        Assert.Equal(CombatLineKind.Info, entry.CombatKind);
        Assert.Equal("You killed the drudge!", entry.Text);
        Assert.Equal(0x00u, entry.LogTextType);
    }

    [Fact]
    public void WireAll_CombatCommenceAttack_FiresCombatStateEvent()
    {
        var (d, _, combat, _, _) = MakeAll();
        bool commenced = false;
        combat.AttackCommenced += () => commenced = true;

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.CombatCommenceAttack, Array.Empty<byte>()));
        d.Dispatch(env!.Value);

        Assert.True(commenced);
    }

    [Fact]
    public void WireAll_MagicPurgeEnchantments_CallsOnPurgeAll()
    {
        var (d, _, _, book, _) = MakeAll();
        book.OnEnchantmentAdded(new(1, 1, 100, 0, Bucket: 1));
        book.OnEnchantmentAdded(new(2, 2, 100, 0, Bucket: 2));
        Assert.Equal(2, book.ActiveCount);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.MagicPurgeEnchantments, Array.Empty<byte>()));
        d.Dispatch(env!.Value);

        Assert.Equal(0, book.ActiveCount);
    }

    [Fact]
    public void WireAll_MagicUpdateEnchantment_ConvertsRelativeTimesAndClassifiesBucket()
    {
        var dispatcher = new GameEventDispatcher();
        var book = new Spellbook();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            book,
            new ChatLog(),
            clientTime: () => 100d);

        byte[] payload = BuildEnchantment(
            spellId: 42, layer: 3, duration: 60, caster: 0xBEEF,
            startTime: 10, lastDegraded: 2, statModType: 0x00008000u);
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.MagicUpdateEnchantment, payload))!.Value);

        ActiveEnchantmentRecord record = Assert.Single(book.ActiveEnchantmentSnapshot);
        Assert.Equal(110d, record.StartTime);
        Assert.Equal(102d, record.LastTimeDegraded);
        Assert.Equal(2u, record.Bucket);
        Assert.Equal(60d, record.Duration);
    }

    [Fact]
    public void WireAll_MagicUpdateEnchantment_PropagatesFullStatModMidSession()
    {
        var dispatcher = new GameEventDispatcher();
        var book = new Spellbook(SpellTable.Create([TestSpell(42u)]));
        var player = new LocalPlayerState(book);
        player.OnSkillUpdate(
            skillId: 7u,
            ranks: 100u,
            status: 2u,
            xp: 0u,
            init: 0u,
            resistance: 0u,
            lastUsed: 0d,
            formulaBonus: 0u);
        int changed = 0;
        book.EnchantmentsChanged += () => changed++;
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            book,
            new ChatLog(),
            localPlayer: player,
            clientTime: () => 100d);

        byte[] payload = BuildEnchantment(
            spellId: 42,
            layer: 3,
            duration: 60,
            caster: 0xBEEF,
            startTime: 10,
            lastDegraded: 2,
            statModType: 0x00008010u,
            statModKey: 7u,
            statModValue: 25f);
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(
                GameEventType.MagicUpdateEnchantment,
                payload))!.Value);

        ActiveEnchantmentRecord record =
            Assert.Single(book.ActiveEnchantmentSnapshot);
        Assert.Equal(0x00008010u, record.StatModType);
        Assert.Equal(7u, record.StatModKey);
        Assert.Equal(25f, record.StatModValue);
        Assert.Equal(125, player.GetEffectiveSkill(7u));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void WireAll_WeenieError_RoutesToChatLog()
    {
        var (d, _, _, _, chat) = MakeAll();

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x04Au);  // Ack! You killed yourself! (Default)

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WeenieError, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(1, chat.Count);
        var e = chat.Snapshot()[0];
        Assert.Equal(ChatKind.System, e.Kind);
        Assert.Equal("Ack! You killed yourself!", e.Text);
        Assert.Equal(0x00u, e.LogTextType);
    }

    [Fact]
    public void WireAll_WeenieError_UnmappedCode_DoesNotReachChat()
    {
        var (d, _, _, _, chat) = MakeAll();

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x9C);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WeenieError, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(0, chat.Count);
    }

    [Theory]
    [InlineData(0x003Bu)] // ILeftTheWorld
    [InlineData(0x003Cu)] // ITeleported
    public void WireAll_RetailSilentClientControlStatus_DoesNotReachChat(uint code)
    {
        var (dispatcher, _, _, _, chat) = MakeAll();
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, code);

        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.WeenieError, payload))!.Value);

        Assert.Equal(0, chat.Count);
    }

    [Fact]
    public void WireAll_WeenieErrorWithString_RoutesToChatLogWithInterpolation()
    {
        var (d, _, _, _, chat) = MakeAll();

        byte[] interpBytes = MakeString16L("Caith");
        byte[] payload = new byte[4 + interpBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x0521u); // "%s has been added to the list of people you can hear."
        Array.Copy(interpBytes, 0, payload, 4, interpBytes.Length);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WeenieErrorWithString, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(1, chat.Count);
        var e = chat.Snapshot()[0];
        Assert.Equal(ChatKind.System, e.Kind);
        Assert.Equal("Caith has been added to the list of people you can hear.", e.Text);
    }

    [Fact]
    public void WireAll_WeenieErrorWithString_UnmappedCode_DoesNotReachChat()
    {
        var (d, _, _, _, chat) = MakeAll();

        byte[] interpBytes = MakeString16L("Mana Stone");
        byte[] payload = new byte[4 + interpBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x42u);
        Array.Copy(interpBytes, 0, payload, 4, interpBytes.Length);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WeenieErrorWithString, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(0, chat.Count);
    }


    private static (GameEventDispatcher Dispatcher, ChatLog Chat, List<(string Text, RetailLogTextType Type)> Reported)
        MakeAllWithInterfaceTextRouter()
    {
        var dispatcher = new GameEventDispatcher();
        var chat = new ChatLog();
        var reported = new List<(string, RetailLogTextType)>();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            chat,
            onInterfaceText: (text, type) => reported.Add((text, type)));
        return (dispatcher, chat, reported);
    }

    [Fact]
    public void WireAll_WithRouter_WeenieError_ClientLocalCode_RoutesThroughRouter_NotChat()
    {
        var (d, chat, reported) = MakeAllWithInterfaceTextRouter();

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x024u);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WeenieError, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(0, chat.Count);
        var (text, type) = Assert.Single(reported);
        Assert.Equal(ClientTextRefusals.CantJumpInAir, text);
        Assert.Equal(RetailLogTextType.ClientLocal, type);
    }

    [Fact]
    public void WireAll_WithRouter_WeenieError_ChatTypeCode_RoutesThroughRouter_AsChatType()
    {
        var (d, _, reported) = MakeAllWithInterfaceTextRouter();

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x402u);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WeenieError, payload));
        d.Dispatch(env!.Value);

        var (text, type) = Assert.Single(reported);
        Assert.Equal("Your spell fizzled.", text);
        Assert.Equal(RetailLogTextType.Magic, type);
    }

    [Fact]
    public void WireAll_WithRouter_WeenieErrorWithString_SubstitutesParam_AndRoutesByType()
    {
        var (d, chat, reported) = MakeAllWithInterfaceTextRouter();

        // 0x521 = "%s has been added to the list of people you can hear." -> Default (0x00).
        byte[] interpBytes = MakeString16L("Caith");
        byte[] payload = new byte[4 + interpBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x521u);
        Array.Copy(interpBytes, 0, payload, 4, interpBytes.Length);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WeenieErrorWithString, payload));
        d.Dispatch(env!.Value);

        var (text, type) = Assert.Single(reported);
        Assert.Equal("Caith has been added to the list of people you can hear.", text);
        Assert.Equal(RetailLogTextType.Default, type);
        Assert.Equal(0, chat.Count);
    }

    [Fact]
    public void WireAll_WithRouter_SilentClientControlStatus_StillDropped()
    {
        var (d, chat, reported) = MakeAllWithInterfaceTextRouter();
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x003Bu); // ILeftTheWorld
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.WeenieError, payload));
        d.Dispatch(env!.Value);

        Assert.Empty(reported);
        Assert.Equal(0, chat.Count);
    }

    [Fact]
    public void WireAll_WithRouter_CommunicationTransientString_AlwaysClientLocal()
    {
        var (d, chat, reported) = MakeAllWithInterfaceTextRouter();

        byte[] payload = MakeString16L("You are too encumbered to carry that!");
        var env = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.CommunicationTransientString, payload));
        d.Dispatch(env!.Value);

        var (text, type) = Assert.Single(reported);
        Assert.Equal("You are too encumbered to carry that!", text);
        Assert.Equal(RetailLogTextType.ClientLocal, type);
        Assert.Equal(0, chat.Count);
    }

    [Fact]
    public void WireAll_NoRouter_CommunicationTransientString_FallsBackToChatWithClientLocalType()
    {
        var (d, _, _, _, chat) = MakeAll();

        byte[] payload = MakeString16L("fallback text");
        var env = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.CommunicationTransientString, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(1, chat.Count);
        var e = chat.Snapshot()[0];
        Assert.Equal("fallback text", e.Text);
        Assert.Equal((uint)RetailLogTextType.ClientLocal, e.LogTextType);
    }

    [Fact]
    public void WireAll_WithRouter_UseDone_NonZeroError_RoutesResolvedTextAndType()
    {
        var (d, chat, reported) = MakeAllWithInterfaceTextRouter();

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x001Du);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.UseDone, payload));
        d.Dispatch(env!.Value);

        var (text, type) = Assert.Single(reported);
        Assert.Equal("You're too busy!", text);
        Assert.Equal(RetailLogTextType.ClientLocal, type);
        Assert.Equal(0, chat.Count);
    }

    [Fact]
    public void WireAll_WithRouter_UseDone_ZeroError_ReportsNothing()
    {
        var (d, _, reported) = MakeAllWithInterfaceTextRouter();

        byte[] payload = new byte[4]; // err == 0
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.UseDone, payload));
        d.Dispatch(env!.Value);

        Assert.Empty(reported);
    }

    [Fact]
    public void PlayerDescription_RegistersInventoryEntries_InClientObjectTable()
    {
        var dispatcher = new GameEventDispatcher();
        var items      = new ClientObjectTable();
        var combat     = new CombatState();
        var spellbook  = new Spellbook();
        var chat       = new ChatLog();
        GameEventWiring.WireAll(dispatcher, items, combat, spellbook, chat);

        Assert.Equal(0, items.ObjectCount);

        var sb = new MemoryStream();
        using var w = new BinaryWriter(sb);
        w.Write(0u);     // propertyFlags = 0
        w.Write(0x52u);  // weenieType
        w.Write(0x201u); // vectorFlags = ATTRIBUTE | ENCHANTMENT
        w.Write(1u);     // has_health
        w.Write(0u);     // attribute_flags = 0 (no attrs)
        w.Write(0u);     // enchantment_mask = 0

        w.Write(0u);     // option_flags = None (no GAMEPLAY_OPTIONS → strict inv path)
        w.Write(0u);     // options1
        w.Write(0u);
        w.Write(0u);     // spellbook_filters

        // Inventory: 2 entries
        w.Write(2u);
        w.Write(0x50000A01u); w.Write(0u);
        w.Write(0x50000A02u); w.Write(1u);

        // Equipped: 0 entries
        w.Write(0u);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PlayerDescription, sb.ToArray()));
        dispatcher.Dispatch(env!.Value);

        Assert.Equal(2, items.ObjectCount);
        Assert.NotNull(items.Get(0x50000A01u));
        Assert.NotNull(items.Get(0x50000A02u));
        Assert.Equal(0u, items.Get(0x50000A01u)!.ContainerTypeHint);
        Assert.Equal(1u, items.Get(0x50000A02u)!.ContainerTypeHint);
    }

    [Fact]
    public void PlayerDescription_IndexesLoginEquipmentForDefaultCombatMode()
    {
        const uint playerGuid = 0x50000001u;
        const uint packItemGuid = 0x50000A01u;
        const uint crossbowGuid = 0x50000B01u;
        const uint ammoGuid = 0x50000B02u;
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        GameEventWiring.WireAll(
            dispatcher,
            items,
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            playerGuid: () => playerGuid);

        var sb = new MemoryStream();
        using var w = new BinaryWriter(sb);
        w.Write(0u);     // propertyFlags
        w.Write(0x52u);  // weenieType
        w.Write(0x201u); // ATTRIBUTE | ENCHANTMENT
        w.Write(1u);     // has_health
        w.Write(0u);     // attribute_flags
        w.Write(0u);     // enchantment_mask
        w.Write(0u);     // option_flags
        w.Write(0u);     // options1
        w.Write(0u);
        w.Write(0u);     // spellbook_filters
        w.Write(1u);
        w.Write(packItemGuid); w.Write(0u);
        w.Write(2u);
        w.Write(crossbowGuid);
        w.Write((uint)EquipMask.MissileWeapon);
        w.Write(7u);     // layering priority
        w.Write(ammoGuid);
        w.Write((uint)EquipMask.MissileAmmo);
        w.Write(8u);

        var envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.PlayerDescription, sb.ToArray()));
        dispatcher.Dispatch(envelope!.Value);

        ClientObject crossbow = items.Get(crossbowGuid)!;
        crossbow.Type = ItemType.MissileWeapon;
        crossbow.CombatUse = 2;
        IReadOnlyList<ClientObject> orderedEquipment = items.GetEquippedBy(playerGuid);

        Assert.Equal(new[] { packItemGuid }, items.GetContents(playerGuid));
        Assert.Equal(
            new[] { crossbowGuid, ammoGuid },
            orderedEquipment.Select(item => item.ObjectId));
        Assert.Equal(0u, crossbow.ContainerId);
        Assert.Equal(playerGuid, crossbow.WielderId);
        Assert.Equal(EquipMask.MissileWeapon, crossbow.CurrentlyEquippedLocation);
        Assert.Equal(7u, crossbow.Priority);
        Assert.Equal(
            CombatMode.Missile,
            CombatInputPlanner.GetDefaultCombatMode(orderedEquipment));
    }

    [Fact]
    public void WireAll_QueryItemManaResponse_RoutesValidityToItemManaState()
    {
        var dispatcher = new GameEventDispatcher();
        var itemMana = new ItemManaState();
        (uint Guid, float Percent, bool Valid)? observed = null;
        itemMana.ItemManaChanged += (guid, percent, valid) => observed = (guid, percent, valid);
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            itemMana: itemMana);

        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x50000A01u);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), 0.375f);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 1u);
        var envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.QueryItemManaResponse, payload));

        dispatcher.Dispatch(envelope!.Value);

        Assert.Equal((0x50000A01u, 0.375f, true), observed);
        Assert.True(itemMana.HasMana(0x50000A01u));
    }

    [Fact]
    public void UseDone_releasesAppBusyOwnerThroughCallback()
    {
        var dispatcher = new GameEventDispatcher();
        uint? completedWith = null;
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            onUseDone: error => completedWith = error);
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x1Du);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.UseDone, payload));
        dispatcher.Dispatch(env!.Value);

        Assert.Equal(0x1Du, completedWith);
    }

    [Fact]
    public void PlayerDescription_ReplacesPlayerPackContents_InRetailManifestOrder()
    {
        const uint playerGuid = 0x50000001u;
        var dispatcher = new GameEventDispatcher();
        var items      = new ClientObjectTable();
        var combat     = new CombatState();
        var spellbook  = new Spellbook();
        var chat       = new ChatLog();
        GameEventWiring.WireAll(dispatcher, items, combat, spellbook, chat,
            playerGuid: () => playerGuid);

        var sb = new MemoryStream();
        using var w = new BinaryWriter(sb);
        w.Write(0u);     // propertyFlags = 0
        w.Write(0x52u);  // weenieType
        w.Write(0x201u); // vectorFlags = ATTRIBUTE | ENCHANTMENT
        w.Write(1u);     // has_health
        w.Write(0u);     // attribute_flags = 0 (no attrs)
        w.Write(0u);     // enchantment_mask = 0

        w.Write(0u);     // option_flags = None (no GAMEPLAY_OPTIONS -> strict inv path)
        w.Write(0u);     // options1
        w.Write(0u);
        w.Write(0u);     // spellbook_filters

        w.Write(2u);
        w.Write(0x50000A02u); w.Write(1u);
        w.Write(0x50000A01u); w.Write(0u);
        w.Write(0u);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PlayerDescription, sb.ToArray()));
        dispatcher.Dispatch(env!.Value);

        Assert.Equal(new[] { 0x50000A02u, 0x50000A01u }, items.GetContents(playerGuid));
        Assert.Equal(playerGuid, items.Get(0x50000A02u)!.ContainerId);
        Assert.Equal(1u, items.Get(0x50000A02u)!.ContainerTypeHint);
        Assert.Equal(0u, items.Get(0x50000A01u)!.ContainerTypeHint);
    }

    [Fact]
    public void PlayerDescription_SeedsMembership_NotWeenieClassIdMisuse()
    {
        var dispatcher = new GameEventDispatcher();
        var items      = new ClientObjectTable();
        var combat     = new CombatState();
        var spellbook  = new Spellbook();
        var chat       = new ChatLog();
        GameEventWiring.WireAll(dispatcher, items, combat, spellbook, chat);

        var sb = new MemoryStream();
        using var w = new BinaryWriter(sb);
        w.Write(0u);     // propertyFlags = 0
        w.Write(0x52u);  // weenieType
        w.Write(0x201u); // vectorFlags = ATTRIBUTE | ENCHANTMENT
        w.Write(1u);     // has_health
        w.Write(0u);     // attribute_flags = 0 (no attrs)
        w.Write(0u);     // enchantment_mask = 0

        w.Write(0u);     // option_flags = None (no GAMEPLAY_OPTIONS → strict inv path)
        w.Write(0u);     // options1
        w.Write(0u);
        w.Write(0u);     // spellbook_filters

        w.Write(1u);
        w.Write(0x700u); w.Write(1u);

        // Equipped: 1 entry with EquipLocation = MeleeWeapon (0x1).
        // Wire format: guid(4) + loc(4) + priority(4) = 12 bytes per entry.
        w.Write(1u);
        w.Write(0x701u); w.Write((uint)EquipMask.MeleeWeapon); w.Write(0u);  // guid=0x701, slot=MeleeWeapon, prio=0

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PlayerDescription, sb.ToArray()));
        dispatcher.Dispatch(env!.Value);

        // (a) inventory guid is registered
        Assert.NotNull(items.Get(0x700u));
        Assert.Equal(0u, items.Get(0x700u)!.WeenieClassId);
        Assert.Equal(1u, items.Get(0x700u)!.ContainerTypeHint);
        // (c) equipped guid has its equip slot set
        Assert.NotNull(items.Get(0x701u));
        Assert.Equal(EquipMask.MeleeWeapon, items.Get(0x701u)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void WireAll_PlayerDescription_invokesOnShortcuts()
    {
        IReadOnlyList<ShortcutEntry>? got = null;

        var dispatcher = new GameEventDispatcher();
        var items      = new ClientObjectTable();
        var combat     = new CombatState();
        var spellbook  = new Spellbook();
        var chat       = new ChatLog();
        GameEventWiring.WireAll(dispatcher, items, combat, spellbook, chat,
            onShortcuts: list => got = list);

        var sb = new MemoryStream();
        using var w = new BinaryWriter(sb);
        w.Write(0u);     // propertyFlags = 0
        w.Write(0x52u);  // weenieType
        w.Write(0x201u); // vectorFlags = ATTRIBUTE | ENCHANTMENT
        w.Write(1u);     // has_health
        w.Write(0u);     // attribute_flags = 0 (no attrs)
        w.Write(0u);     // enchantment_mask = 0

        // Trailer
        w.Write(0x00000001u); // option_flags = Shortcut
        w.Write(0u);          // options1
        // Shortcut block (option_flags & 0x1 set):
        w.Write(1u);
        w.Write(0u);          // idx = 0
        w.Write(0x5001u);     // guid = 0x5001
        w.Write((ushort)0);   // spellId = 0
        w.Write((ushort)0);   // layer = 0
        // SpellLists8 NOT set → legacy single-list fallback:
        w.Write(0u);
        w.Write(0u);          // spellbook_filters = 0
        w.Write(0u);
        w.Write(0u);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PlayerDescription, sb.ToArray()));
        dispatcher.Dispatch(env!.Value);

        Assert.NotNull(got);
        Assert.Single(got!);
        Assert.Equal(0x5001u, got![0].ObjectId);
    }

    [Fact]
    public void WireAll_PlayerDescription_UpsertsPlayerPropertiesIntoClientObject()
    {
        const uint playerGuid = 0x50000001u;
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        GameEventWiring.WireAll(dispatcher, items, new CombatState(), new Spellbook(),
            new ChatLog(), playerGuid: () => playerGuid);

        var sb = new MemoryStream();
        using var w = new BinaryWriter(sb);
        w.Write(0x00000001u); // propertyFlags = PropertyInt32
        w.Write(0x52u);       // weenieType
        w.Write((ushort)1);
        w.Write((ushort)8);   // buckets (ignored)
        w.Write(5u);          // key = EncumbranceVal
        w.Write(1500u);       // val
        // vector + has_health (no vector blocks)
        w.Write(0u);          // vectorFlags = None
        w.Write(0u);          // has_health
        // strict trailer
        w.Write(0u);          // option_flags = None
        w.Write(0u);          // options1
        w.Write(0u);
        w.Write(0u);          // spellbook_filters
        w.Write(0u);
        w.Write(0u);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.PlayerDescription, sb.ToArray()));
        dispatcher.Dispatch(env!.Value);

        var player = items.Get(playerGuid);
        Assert.NotNull(player);
        Assert.Equal(1500, player!.Properties.Ints[5]);
    }

    [Fact]
    public void WireAll_ViewContents_RecordsMembershipInClientObjectTable()
    {
        var (d, items, _, _, _) = MakeAll();

        byte[] payload = new byte[8 + 2 * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), 0x500000C9u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 0x50000A01u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 0x50000A02u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 1u);

        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.ViewContents, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(0u, items.Get(0x50000A01u)!.ContainerId);
        Assert.Equal(0u, items.Get(0x50000A02u)!.ContainerId);
        Assert.Equal(new[] { 0x50000A01u, 0x50000A02u }, items.GetContents(0x500000C9u));
        Assert.Equal(0u, items.Get(0x50000A01u)!.ContainerTypeHint);
        Assert.Equal(1u, items.Get(0x50000A02u)!.ContainerTypeHint);
    }

    [Fact]
    public void WireAll_ViewContents_IsFullReplace_DropsRemovedItems()
    {
        var (d, items, _, _, _) = MakeAll();

        DispatchViewContents(d, 0x500000C9u, new uint[] { 0x50000A01u, 0x50000A02u });
        Assert.Equal(2, items.GetContents(0x500000C9u).Count);

        // Second view: only one item — the other was removed server-side. Additive merge would keep both.
        DispatchViewContents(d, 0x500000C9u, new uint[] { 0x50000A02u });
        Assert.Equal(new[] { 0x50000A02u }, items.GetContents(0x500000C9u));
        Assert.Equal(0u, items.Get(0x50000A01u)!.ContainerId);
    }

    [Fact]
    public void WireAll_ExternalContainer_FiltersNestedViewsAndAppliesAuthoritativeClose()
    {
        var dispatcher = new GameEventDispatcher();
        var items = new ClientObjectTable();
        var external = new ExternalContainerState();
        GameEventWiring.WireAll(
            dispatcher,
            items,
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            externalContainers: external);
        external.RequestOpen(0x70000001u);

        DispatchViewContents(
            dispatcher,
            0x70000001u,
            new uint[] { 0x60000001u, 0x70000002u });
        DispatchViewContents(dispatcher, 0x70000002u, new uint[] { 0x60000002u });

        Assert.Equal(0x70000001u, external.CurrentContainerId);
        Assert.Equal(
            new uint[] { 0x60000001u, 0x70000002u },
            items.GetContents(0x70000001u));
        Assert.Equal(new uint[] { 0x60000002u }, items.GetContents(0x70000002u));

        byte[] closePayload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(closePayload, 0x70000001u);
        var close = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.CloseGroundContainer, closePayload));
        dispatcher.Dispatch(close!.Value);

        Assert.Equal(0u, external.CurrentContainerId);
        Assert.Empty(items.GetContents(0x70000001u));
        Assert.NotNull(items.Get(0x60000001u));
        Assert.Empty(items.GetContents(0x70000002u));
    }

    private static void DispatchViewContents(GameEventDispatcher d, uint containerGuid, uint[] itemGuids)
    {
        byte[] payload = new byte[8 + itemGuids.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), containerGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)itemGuids.Length);
        for (int i = 0; i < itemGuids.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8 + i * 8), itemGuids[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12 + i * 8), 0u);
        }
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.ViewContents, payload));
        d.Dispatch(env!.Value);
    }

    [Fact]
    public void WireAll_InventoryServerSaveFailed_RollsBackOptimisticMove()
    {
        var (d, items, _, _, _) = MakeAll();
        items.RecordMembership(0x50000B01u, containerId: 0x500000C0u);
        items.MoveItem(0x50000B01u, 0x500000C0u, 2);
        items.MoveItemOptimistic(0x50000B01u, 0x500000C1u, 0);
        Assert.Equal(0x500000C1u, items.Get(0x50000B01u)!.ContainerId);   // moved (optimistic)

        // Server bounces it: InventoryServerSaveFailed (itemGuid, weenieError).
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), 0x50000B01u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x1Au);   // some WeenieError
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.InventoryServerSaveFailed, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(0x500000C0u, items.Get(0x50000B01u)!.ContainerId);   // snapped back
        Assert.Equal(2, items.Get(0x50000B01u)!.ContainerSlot);
    }

    [Fact]
    public void WireAll_InventoryPutObjInContainer_ConfirmsOptimisticMove_soNoRollback()
    {
        var (d, items, _, _, _) = MakeAll();
        items.RecordMembership(0x50000B02u, containerId: 0x500000C0u);
        items.MoveItem(0x50000B02u, 0x500000C0u, 2);
        items.MoveItemOptimistic(0x50000B02u, 0x500000C1u, 0);            // optimistic move (pending snapshot)

        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), 0x50000B02u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x500000C1u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 0u);   // placement
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 1u);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.InventoryPutObjInContainer, payload));
        d.Dispatch(env!.Value);

        Assert.False(items.RollbackMove(0x50000B02u));
        Assert.Equal(0x500000C1u, items.Get(0x50000B02u)!.ContainerId);
        Assert.Equal(1u, items.Get(0x50000B02u)!.ContainerTypeHint);
    }

    [Fact]
    public void WireAll_InventoryPutObjInContainer_ClearsPriorWielder()
    {
        const uint player = 0x50000001u;
        const uint pack = 0x500000C1u;
        const uint bow = 0x50000B03u;
        var (d, items, _, _, _) = MakeAll(() => player);
        items.AddOrUpdate(new ClientObject
        {
            ObjectId = bow,
            ContainerId = player,
            WielderId = player,
            CurrentlyEquippedLocation = EquipMask.MissileWeapon,
        });

        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), bow);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), pack);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 1u);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.InventoryPutObjInContainer,
            payload));

        d.Dispatch(env!.Value);

        Assert.Equal(pack, items.Get(bow)!.ContainerId);
        Assert.Equal(0u, items.Get(bow)!.WielderId);
        Assert.Equal(EquipMask.None, items.Get(bow)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void WireAll_InventoryPutObjectIn3D_UnparentsFromContainer()
    {
        var (d, items, _, _, _) = MakeAll();
        items.RecordMembership(0x50000A01u, containerId: 0x500000C9u);

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x50000A01u);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.InventoryPutObjectIn3D, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(0u, items.Get(0x50000A01u)!.ContainerId);
    }

    [Fact]
    public void WireAll_InventoryPutObjectIn3D_ConfirmsOptimisticDrop()
    {
        var (d, items, _, _, _) = MakeAll();
        items.RecordMembership(0x50000A03u, containerId: 0x500000C9u);
        items.MoveItem(0x50000A03u, 0x500000C9u, 4);
        items.MoveItemOptimistic(0x50000A03u, newContainerId: 0u, newSlot: -1);

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x50000A03u);
        var env = GameEventEnvelope.TryParse(WrapEnvelope(GameEventType.InventoryPutObjectIn3D, payload));
        d.Dispatch(env!.Value);

        Assert.Equal(0u, items.Get(0x50000A03u)!.ContainerId);
        Assert.False(items.RollbackMove(0x50000A03u));
    }

    [Fact]
    public void WireAll_SalvageOperationsResult_EmitsTheSalvagingSuccessLine()
    {
        var lines = new List<(string Text, RetailLogTextType Type)>();
        var dispatcher = new GameEventDispatcher();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            onInterfaceText: (text, type) => lines.Add((text, type)));

        byte[] payload = new AceWireWriter()
            .Write(28u)
            .Write(0u)
            .Write(1u)
            .Write(63u).Write(unchecked((ulong)BitConverter.DoubleToInt64Bits(8d))).Write(1u)
            .Write(0)
            .ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.SalvageOperationsResult, payload))!.Value);

        Assert.Equal(
            ("You obtain 1 Silver (ws 8.00) using your knowledge of Weapon Tinkering.",
                RetailLogTextType.Salvaging),
            Assert.Single(lines));
    }

    [Fact]
    public void WireAll_SalvageOperationsResult_EmitsUnsuitableItemsInSalvagingLog()
    {
        var lines = new List<(string Text, RetailLogTextType Type)>();
        var items = new ClientObjectTable();
        const uint unsuitableItem = 0x50000020u;
        items.AddOrUpdate(new ClientObject { ObjectId = unsuitableItem, Name = "Broken Item" });
        var dispatcher = new GameEventDispatcher();
        GameEventWiring.WireAll(
            dispatcher,
            items,
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            onInterfaceText: (text, type) => lines.Add((text, type)));

        byte[] payload = new AceWireWriter()
            .Write(28u)
            .Write(1u).Write(unsuitableItem)
            .Write(0u)
            .Write(0)
            .ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.SalvageOperationsResult, payload))!.Value);

        Assert.Equal(
            (" The following were not suitable for salvaging: Broken Item",
                RetailLogTextType.Salvaging),
            Assert.Single(lines));
    }

    [Fact]
    public void WireAll_SalvageOperationsResult_EmitsFailureInSalvagingLog()
    {
        var lines = new List<(string Text, RetailLogTextType Type)>();
        var dispatcher = new GameEventDispatcher();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            onInterfaceText: (text, type) => lines.Add((text, type)));

        byte[] payload = new AceWireWriter()
            .Write(28u)
            .Write(0u)
            .Write(0u)
            .Write(0)
            .ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.SalvageOperationsResult, payload))!.Value);

        Assert.Equal(
            ("Salvaging Failed!", RetailLogTextType.Salvaging),
            Assert.Single(lines));
    }

    [Fact]
    public void WireAll_PlayerDescription_PublishesCharacterOptions()
    {
        var dispatcher = new GameEventDispatcher();
        (uint Options1, uint Options2, bool Truncated)? observed = null;
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            onCharacterOptions: (options1, options2, trailerTruncated) =>
                observed = (options1, options2, trailerTruncated));

        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);          // property flags
        writer.Write(0x52u);       // player weenie type
        writer.Write(0u);          // vector flags
        writer.Write(0u);          // has health
        writer.Write(0x40u);
        writer.Write(0x50C4A54Au); // options1
        writer.Write(0u);
        writer.Write(0u);          // spellbook filters
        writer.Write(0x948700u);   // options2
        writer.Write(0u);
        writer.Write(0u);

        var envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.PlayerDescription, stream.ToArray()));
        dispatcher.Dispatch(envelope!.Value);

        Assert.Equal((0x50C4A54Au, 0x948700u, false), observed);
    }


    [Fact]
    public void WireAll_FellowshipFullUpdate_ReachesTheCallback()
    {
        var dispatcher = new GameEventDispatcher();
        GameEvents.FellowshipFullUpdate? observed = null;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onFellowshipFullUpdate: update => observed = update);

        byte[] wire = new AceWireWriter()
            .Write((ushort)0).Write((ushort)16)
            .WriteString16L("TestFellowship")
            .Write((uint)0)  // leaderGuid
            .Write((uint)1)  // shareXp
            .Write((uint)0)  // evenXpSplit
            .Write((uint)0)  // openFellow
            .Write((uint)0)  // locked
            .Write((ushort)0).Write((ushort)32)
            .ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.FellowshipFullUpdate, wire))!.Value);

        Assert.NotNull(observed);
        Assert.Equal("TestFellowship", observed.Value.Name);
        Assert.True(observed.Value.ShareXp);
    }

    [Fact]
    public void WireAll_FellowshipUpdateFellow_ReachesTheCallback()
    {
        var dispatcher = new GameEventDispatcher();
        GameEvents.FellowshipUpdateFellow? observed = null;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onFellowshipUpdateFellow: update => observed = update);

        byte[] wire = new AceWireWriter()
            .Write(0x50000001u)
            .Write((uint)0).Write((uint)0).Write((uint)1)
            .Write((uint)100).Write((uint)100).Write((uint)100)
            .Write((uint)42).Write((uint)100).Write((uint)100)
            .Write((uint)0)
            .WriteString16L("Self")
            .Write((uint)3)
            .ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.FellowshipUpdateFellow, wire))!.Value);

        Assert.NotNull(observed);
        Assert.Equal(0x50000001u, observed.Value.MemberGuid);
        Assert.Equal(42u, observed.Value.Member.CurrentHealth);
        Assert.Equal(3u, observed.Value.UpdateType);
    }

    [Fact]
    public void WireAll_FellowshipQuit_ReachesTheCallbackWithQuitterGuid()
    {
        var dispatcher = new GameEventDispatcher();
        uint? observed = null;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onFellowshipQuit: guid => observed = guid);

        byte[] wire = new AceWireWriter().Write(0x50000042u).ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.FellowshipQuit, wire))!.Value);

        Assert.Equal(0x50000042u, observed);
    }

    [Fact]
    public void WireAll_FellowshipDismiss_ReachesTheCallbackWithDismissedGuid()
    {
        var dispatcher = new GameEventDispatcher();
        uint? observed = null;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onFellowshipDismiss: guid => observed = guid);

        byte[] wire = new AceWireWriter().Write(0x50000043u).ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.FellowshipDismiss, wire))!.Value);

        Assert.Equal(0x50000043u, observed);
    }

    [Fact]
    public void WireAll_FellowshipDisband_ReachesTheCallback()
    {
        var dispatcher = new GameEventDispatcher();
        bool fired = false;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onFellowshipDisband: () => fired = true);

        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.FellowshipDisband, []))!.Value);

        Assert.True(fired);
    }

    /// <summary>Minimal valid <c>AllegianceProfile</c> body at <c>oldVersion=0</c> — no gated fields, one monarch record only.</summary>
    private static byte[] BuildMinimalAllegianceProfile(
        uint leadingField,
        uint monarchGuid,
        string monarchName = "Monarch")
    {
        return new AceWireWriter()
            .Write(leadingField)
            .Write((uint)1)   // totalMembers
            .Write((uint)0)   // totalVassals
            .Write((ushort)1) // recordCount
            .Write((ushort)0) // oldVersion — no gates fire
            .Write(monarchGuid)
            .Write((uint)0).Write((uint)0)
            .Write(0x4u | 0x8u)                  // bitfield: HasAllegianceAge | HasPackedLevel
            .Write((byte)0).Write((byte)0)       // gender, heritageGroup
            .Write((ushort)1)                    // rank
            .Write((uint)10)                     // level (HasPackedLevel)
            .Write((ushort)0).Write((ushort)0)   // loyalty, leadership
            .Write((uint)0).Write((uint)0)       // timeOnline, allegianceAge (HasAllegianceAge)
            .WriteString16L(monarchName)
            .ToArray();
    }

    [Fact]
    public void WireAll_AllegianceUpdate_ReachesTheCallback()
    {
        var dispatcher = new GameEventDispatcher();
        ClientCommandResponses.AllegianceUpdate? observed = null;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onAllegianceUpdate: update => observed = update);

        byte[] wire = BuildMinimalAllegianceProfile(leadingField: 5u, monarchGuid: 0x50000001u);
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.AllegianceUpdate, wire))!.Value);

        Assert.NotNull(observed);
        Assert.Equal(5u, observed.Value.Rank);
        Assert.Equal(0x50000001u, observed.Value.Monarch!.Value.CharacterId);
        Assert.Equal("Monarch", observed.Value.Monarch.Value.Name);
    }

    [Fact]
    public void WireAll_AllegianceInfoResponse_IsTextOnly_ForSelfAndOtherGuidsAlike()
    {
        const uint self = 0x50000001u;
        const uint other = 0x50000002u;
        var dispatcher = new GameEventDispatcher();
        int chatLines = 0;
        var chat = new ChatLog();
        chat.EntryAppended += _ => chatLines++;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), chat,
            playerGuid: () => self);

        byte[] otherWire = BuildMinimalAllegianceProfile(leadingField: other, monarchGuid: other, monarchName: "Other");
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.AllegianceInfoResponse, otherWire))!.Value);
        Assert.True(chatLines > 0);

        chatLines = 0;
        byte[] selfWire = BuildMinimalAllegianceProfile(leadingField: self, monarchGuid: self, monarchName: "Self");
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.AllegianceInfoResponse, selfWire))!.Value);
        Assert.True(chatLines > 0);
    }

    [Fact]
    public void WireAll_AllegianceUpdateDoneAndAborted_ReachTheirCallbacks()
    {
        var dispatcher = new GameEventDispatcher();
        uint? done = null;
        uint? aborted = null;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onAllegianceUpdateDone: code => done = code,
            onAllegianceUpdateAborted: code => aborted = code);

        dispatcher.Dispatch(GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.AllegianceUpdateDone,
            new AceWireWriter().Write(0x0561u).ToArray()))!.Value);
        dispatcher.Dispatch(GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.AllegianceUpdateAborted,
            new AceWireWriter().Write(0x0562u).ToArray()))!.Value);

        Assert.Equal(0x0561u, done);
        Assert.Equal(0x0562u, aborted);
    }

    [Fact]
    public void WireAll_AllegianceLoginNotification_ReachesTheCallback()
    {
        var dispatcher = new GameEventDispatcher();
        GameEvents.AllegianceLoginNotification? observed = null;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onAllegianceLoginNotification: notice => observed = notice);

        byte[] wire = new AceWireWriter().Write(0x50000042u).Write((uint)1).ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.AllegianceLoginNotification, wire))!.Value);

        Assert.NotNull(observed);
        Assert.Equal(0x50000042u, observed.Value.CharacterGuid);
        Assert.True(observed.Value.IsLoggedIn);
    }

    [Fact]
    public void WireAll_HouseFamily_ReachesTheirCallbacks()
    {
        var dispatcher = new GameEventDispatcher();
        GameEvents.HouseData? data = null;
        uint? status = null;
        uint? rentTime = null;
        IReadOnlyList<GameEvents.HousePayment>? rentPayment = null;
        GameEventWiring.WireAll(
            dispatcher, new ClientObjectTable(), new CombatState(), new Spellbook(), new ChatLog(),
            onHouseData: d => data = d,
            onHouseStatus: code => status = code,
            onHouseUpdateRentTime: t => rentTime = t,
            onHouseUpdateRentPayment: p => rentPayment = p);

        byte[] houseDataWire = new AceWireWriter()
            .Write(0u).Write(0u).Write(0u).Write(0u)
            .Write(0).Write(0)
            .Write(0x00120001u)
            .Write(0f).Write(0f).Write(0f)
            .Write(1f).Write(0f).Write(0f).Write(0f)
            .ToArray();
        dispatcher.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.HouseData, houseDataWire))!.Value);
        dispatcher.Dispatch(GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.HouseStatus, new AceWireWriter().Write(0u).ToArray()))!.Value);
        dispatcher.Dispatch(GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.UpdateRentTime, new AceWireWriter().Write(1_700_000_000u).ToArray()))!.Value);
        dispatcher.Dispatch(GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.UpdateRentPayment, new AceWireWriter().Write(0).ToArray()))!.Value);

        Assert.NotNull(data);
        Assert.Equal(0x00120001u, data!.Value.Position.LandblockId);
        Assert.Equal(0u, status);
        Assert.Equal(1_700_000_000u, rentTime);
        Assert.NotNull(rentPayment);
        Assert.Empty(rentPayment);
    }

    [Fact]
    public void DispelledEnchantment_AnnouncesItsExpiryAtTheRetailTextType()
    {
        var lines = new List<(string Text, RetailLogTextType Type)>();
        var dispatcher = new GameEventDispatcher();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            SpellbookWithNames(),
            new ChatLog(),
            onInterfaceText: (text, type) => lines.Add((text, type)));

        dispatcher.Dispatch(GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.MagicDispelEnchantment,
            DispelPayload(spellId: 1234, layer: 1)))!.Value);

        Assert.Equal(
            ("Fire Protection Self has expired.", RetailLogTextType.Magic),
            Assert.Single(lines));
    }

    [Fact]
    public void DispelledVitaeReadsAsAPenalty()
    {
        var lines = new List<(string Text, RetailLogTextType Type)>();
        var dispatcher = new GameEventDispatcher();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            SpellbookWithNames(),
            new ChatLog(),
            onInterfaceText: (text, type) => lines.Add((text, type)));

        dispatcher.Dispatch(GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.MagicDispelEnchantment,
            DispelPayload(spellId: 0x29A, layer: 1)))!.Value);

        Assert.Equal("Vitae penalty has expired.", Assert.Single(lines).Text);
    }

    [Fact]
    public void ADispelledSpellMissingFromTheTablePrintsNothing()
    {
        var lines = new List<(string Text, RetailLogTextType Type)>();
        var dispatcher = new GameEventDispatcher();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            SpellbookWithNames(),
            new ChatLog(),
            onInterfaceText: (text, type) => lines.Add((text, type)));

        dispatcher.Dispatch(GameEventEnvelope.TryParse(WrapEnvelope(
            GameEventType.MagicDispelEnchantment,
            DispelPayload(spellId: 4321, layer: 1)))!.Value);

        Assert.Empty(lines);
    }

    private static Spellbook SpellbookWithNames()
        => new(SpellTable.LoadFromReader(new System.IO.StringReader(
            "Spell ID,Name,Flags [Hex]\n"
            + "1234,Fire Protection Self,0x4\n"
            + "666,Vitae,0x4\n")));

    private static byte[] DispelPayload(ushort spellId, ushort layer)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, spellId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), layer);
        return payload;
    }

    private static byte[] BuildEnchantment(
        ushort spellId,
        ushort layer,
        double duration,
        uint caster,
        double startTime,
        double lastDegraded,
        uint statModType,
        uint statModKey = 7u,
        float statModValue = 1.25f)
    {
        byte[] payload = new byte[60];
        int offset = 0;
        WriteU16(spellId); WriteU16(layer); WriteU16(3); WriteU16(0);
        WriteU32(8); WriteF64(startTime); WriteF64(duration); WriteU32(caster);
        WriteF32(0.1f); WriteF32(-1f); WriteF64(lastDegraded);
        WriteU32(statModType); WriteU32(statModKey); WriteF32(statModValue);
        return payload;

        void WriteU16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), value); offset += 2; }
        void WriteU32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), value); offset += 4; }
        void WriteF32(float value) { BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset), value); offset += 4; }
        void WriteF64(double value) { BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(offset), value); offset += 8; }
    }

    private static SpellMetadata TestSpell(uint spellId) => new(
        spellId, "Test", "War Magic", 0u, 0u, "", 0f, 0,
        false, false, "", 0, 0, 0u, 0, false, false, true,
        0f, 0u, 0u, 0u, 0);

}
