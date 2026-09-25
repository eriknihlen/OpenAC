using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What a plugin reads about one spell from the client's own spell table,
/// on both clients. The effects played on the caster and the target, the
/// formula version, the spellbook display order and the component loss are
/// read by the same projection on either client, and this says so.
///
/// Every value is recorded AND asserted: two clients that both left these at
/// zero would agree line for line.
/// </summary>
public sealed class SpellTableParityTests
{
    private const uint Spell = 1640u;

    [Fact]
    public void ASpellsTableFieldsReachAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            arm.Runtime.CharacterOwner.InstallSpellMetadata(SpellTable.Create(
            [
                new SpellMetadata(
                    Spell,
                    "Heal Self VI",
                    "Life Magic",
                    67u,
                    0u,
                    string.Empty,
                    0f,
                    50,
                    false,
                    false,
                    string.Empty,
                    1417,
                    300,
                    0x0000000Cu,
                    6,
                    false,
                    false,
                    true,
                    0f,
                    0x39u,
                    0x1Fu,
                    0x10u,
                    3)
                {
                    FormulaVersion = 1u,
                    ComponentLoss = 0.4f,
                },
            ]));

            ISpellCatalog spells = arm.Host.Automation.Spells;
            transcript.Step("read the spell");
            Assert.True(spells.TryGet(Spell, out PluginSpellInfo spell));
            transcript.Record("casterEffect", spell.CasterEffect);
            transcript.Record("targetEffect", spell.TargetEffect);
            transcript.Record("formulaVersion", spell.FormulaVersion);
            transcript.Record("displayOrder", spell.DisplayOrder);
            transcript.Record("componentLoss", spell.ComponentLoss);
            Assert.Equal(0x39u, spell.CasterEffect);
            Assert.Equal(0x1Fu, spell.TargetEffect);
            Assert.Equal(1u, spell.FormulaVersion);
            Assert.Equal(1417, spell.DisplayOrder);
            Assert.Equal(0.4f, spell.ComponentLoss);
        });
}
