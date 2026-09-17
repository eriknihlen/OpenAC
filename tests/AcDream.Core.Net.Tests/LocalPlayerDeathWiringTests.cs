using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using Xunit;

namespace AcDream.Core.Net.Tests;

public sealed class LocalPlayerDeathWiringTests
{
    [Fact]
    public void TheVictimMessageReportsTheDeathAndStillWritesTheCombatLine()
    {
        var dispatcher = new GameEventDispatcher();
        var chat = new ChatLog();
        var deaths = new List<string>();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            chat,
            onLocalPlayerDeath: deaths.Add);

        Dispatch(dispatcher, "You were killed by an Olthoi Worker!");

        Assert.Equal(["You were killed by an Olthoi Worker!"], deaths);
        ChatEntry entry = Assert.Single(chat.Snapshot());
        Assert.Equal("You were killed by an Olthoi Worker!", entry.Text);
    }

    [Fact]
    public void NothingIsReportedWhenNoHandlerIsWired()
    {
        var dispatcher = new GameEventDispatcher();
        var chat = new ChatLog();
        GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            chat);

        Dispatch(dispatcher, "You have died!");

        Assert.Equal(1, chat.Count);
    }

    private static void Dispatch(GameEventDispatcher dispatcher, string message)
    {
        byte[] payload = MakeString16L(message);
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(12),
            (uint)GameEventType.VictimNotification);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        dispatcher.Dispatch(GameEventEnvelope.TryParse(body)!.Value);
    }

    private static byte[] MakeString16L(string text)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        int recordSize = 2 + data.Length;
        int padding = (4 - (recordSize & 3)) & 3;
        byte[] result = new byte[recordSize + padding];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)data.Length);
        Array.Copy(data, 0, result, 2, data.Length);
        return result;
    }
}
