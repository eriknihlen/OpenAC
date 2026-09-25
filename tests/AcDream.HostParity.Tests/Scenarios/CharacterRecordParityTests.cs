using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What a plugin reads about its own character beyond the stats: the vitae
/// penalty and the titles. Both clients read these from the one runtime, and
/// each step is asserted per arm so two clients agreeing on nothing is still
/// caught.
///
/// Mutation checks (2026-09-25): reporting 1 regardless of the vitae effect
/// turned <see cref="EitherClientReportsTheVitaePenalty"/> red on both arms;
/// reading the earned titles from an empty list, and sorting them by number
/// instead of keeping the server's order, each turned
/// <see cref="EitherClientReportsTheTitlesTheServerSent"/> red.
/// </summary>
public sealed class CharacterRecordParityTests
{
    [Fact]
    public void EitherClientReportsTheVitaePenalty() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ICharacterInfo character = arm.Host.Automation.Character;

            transcript.Step("none");
            transcript.Record("multiplier", character.VitaeMultiplier);
            transcript.Record("percent", character.VitaePenaltyPercent);
            Assert.Equal(1f, character.VitaeMultiplier);
            Assert.Equal(0, character.VitaePenaltyPercent);

            transcript.Step("died");
            arm.Runtime.CharacterOwner.Spellbook.OnEnchantmentAdded(
                new ActiveEnchantmentRecord(
                    SpellId: 666u,
                    LayerId: 1u,
                    Duration: -1d,
                    CasterGuid: 0u,
                    StatModType: 0u,
                    StatModKey: 0u,
                    StatModValue: 0.95f,
                    Bucket: 4u));
            transcript.Record("multiplier", character.VitaeMultiplier);
            transcript.Record("percent", character.VitaePenaltyPercent);
            Assert.Equal(0.95f, character.VitaeMultiplier);
            Assert.Equal(5, character.VitaePenaltyPercent);
        });

    /// <summary>
    /// The titles arrive as the server says them -- a whole list at login,
    /// one more whenever one is earned -- through each client's own parser
    /// and route, and a plugin reads them back in the server's order.
    /// </summary>
    [Fact]
    public void EitherClientReportsTheTitlesTheServerSent() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ICharacterInfo character = arm.Host.Automation.Character;

            transcript.Step("listed");
            arm.Server.GameEvent(GameEventType.CharacterTitle, Words(1u, 5u, 2u, 7u, 5u));
            transcript.Record("current", character.CurrentTitleId);
            transcript.Record("titles", string.Join(",", character.Titles.Select(static title => title.TitleId)));
            Assert.Equal(5u, character.CurrentTitleId);
            // The server's order, not the client's: 7 was listed first.
            Assert.Equal([7u, 5u], character.Titles.Select(static title => title.TitleId));
            Assert.All(character.Titles, static title => Assert.NotNull(title.Name));

            transcript.Step("earned");
            arm.Server.GameEvent(GameEventType.UpdateTitle, Words(9u, 1u));
            transcript.Record("current", character.CurrentTitleId);
            transcript.Record("titles", string.Join(",", character.Titles.Select(static title => title.TitleId)));
            Assert.Equal(9u, character.CurrentTitleId);
            Assert.Equal([7u, 5u, 9u], character.Titles.Select(static title => title.TitleId));
        });

    private static byte[] Words(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (int index = 0; index < words.Length; index++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * 4), words[index]);
        return bytes;
    }
}
