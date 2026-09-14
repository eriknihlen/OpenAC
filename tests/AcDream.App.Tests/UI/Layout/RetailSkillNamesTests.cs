using System;
using System.Collections.Generic;
using System.IO;
using AcDream.App.UI.Layout;
using AcDream.Content.CharGen;
using AcDream.Core.CharGen;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using AcDream.Automation;

namespace AcDream.App.Tests.UI.Layout;

// OpenAC #88: character creation listed "Creature Appraisal" and "Person
// Appraisal" because it named skills from the client's built-in list instead
// of the authored skill data every skill window reads. Both lists are the
// game's own and they disagree; what matters is that each screen uses the one
// the game uses there.
public sealed class ChargenSkillNameTests
{
    [Fact]
    public void AuthoredName_WinsOverTheBuiltInList()
    {
        ChargenOptions options = Options(27u, "Assess Creature");

        Assert.Equal("Assess Creature", ChargenSkillNames.Resolve(options, 27u));

        // The built-in list still says what it always said.
        Assert.True(RetailSkillNames.TryGetName(27, out string? builtIn));
        Assert.Equal("Creature Appraisal", builtIn);
    }

    [Fact]
    public void AnEmptyAuthoredName_FallsBackRatherThanShowingNothing()
    {
        Assert.Equal("Person Appraisal", ChargenSkillNames.Resolve(Options(19u, "   "), 19u));
    }

    [Fact]
    public void WithoutAuthoredData_TheBuiltInListStandsIn()
    {
        // Unreachable against real client data, since the creation tables and
        // the skill table live in the same file. This covers fixtures and a
        // host running without client data.
        Assert.Equal("Person Appraisal", ChargenSkillNames.Resolve(ChargenOptions.Empty, 19u));
        Assert.Equal("Creature Appraisal", ChargenSkillNames.Resolve(ChargenOptions.Empty, 27u));

        // Shield is the one skill neither list names offline.
        Assert.Equal("Skill 48", ChargenSkillNames.Resolve(ChargenOptions.Empty, 48u));
        Assert.Equal("Skill 200", ChargenSkillNames.Resolve(ChargenOptions.Empty, 200u));
    }

    private static ChargenOptions Options(uint skillId, string name) =>
        ChargenOptions.Empty with
        {
            GlobalSkillDetailsBySkillId = new Dictionary<uint, ChargenSkillDetail>
            {
                [skillId] = new ChargenSkillDetail(
                    skillId, name, MinLevel: 1u, Description: string.Empty, Formula: default),
            },
        };
}

// Each appraisal line answers an unnamed skill its own way, and the three ways
// are different. These rows pin all three.
public sealed class AppraisalSkillNameTests
{
    private const int RetiredSkill = 26;

    private static RetailAppraisalNameResolver Names() =>
        new(
            new Dictionary<uint, string>(),
            new CreatureDisplayNameResolver(new Dictionary<uint, string>()),
            new Dictionary<uint, string> { [34u] = "War Magic" });

    [Fact]
    public void AUseRequirement_SaysUnknownSkill_ButStillPrintsItsLine()
    {
        var properties = new PropertyBundle();
        properties.Ints[366u] = RetiredSkill;
        properties.Ints[367u] = 150;
        properties.Ints[368u] = RetiredSkill;

        string report = Build(properties);

        Assert.Contains("Use requires Unknown Skill of at least 150.", report);
        Assert.Contains("Use requires specialized Unknown Skill.", report);
    }

    [Fact]
    public void AnActivationRequirement_IsLeftOutEntirely()
    {
        var properties = new PropertyBundle();
        properties.Ints[115u] = 300;
        properties.Ints[176u] = RetiredSkill;

        string report = Build(properties);

        Assert.DoesNotContain("300", report);
        Assert.DoesNotContain("Unknown Skill", report);
    }

    [Fact]
    public void AnActivationRequirement_NamedByTheAuthoredData_IsListed()
    {
        var properties = new PropertyBundle();
        properties.Ints[115u] = 300;
        properties.Ints[176u] = 34;

        Assert.Contains("War Magic: 300", Build(properties));
    }

    [Fact]
    public void AWieldRequirement_LeavesTheSkillNameBlank()
    {
        var properties = new PropertyBundle();
        properties.Ints[158u] = 2;            // a "base <skill>" requirement...
        properties.Ints[159u] = RetiredSkill; // ...naming a skill the authored data dropped
        properties.Ints[160u] = 250;

        string report = Build(properties);

        Assert.Contains("Wield requires base  250", report);
        Assert.DoesNotContain("Armor Repair", report);
    }

    private static string Build(PropertyBundle properties)
    {
        var obj = new ClientObject { ObjectId = 0x50000020u, Name = "Test Item" };
        return ItemAppraisalTextFormatter.Build(
            obj,
            ItemAppraisalTextFormatterTests.ParsedFor(properties),
            _ => null,
            Names());
    }
}

[Trait("Lane", "InstalledDat")]
public sealed class SkillNamesInstalledDatTests
{
    [InstalledDatFact]
    public void CharacterCreation_NamesSkillsExactlyAsTheAuthoredDataDoes()
    {
        using var dats = new BoundedTestDatCollection(DatDirectory());

        SkillTable authored = Assert.IsType<SkillTable>(
            dats.Get<SkillTable>(ChargenTableReader.SkillTableDid));
        ChargenOptions options = ChargenTableReader.Load(dats);

        // The projection really carries the authored name; resolving through
        // the built-in list instead would give the wrong two below.
        Assert.True(options.TryGetSkillDetail(27u, out ChargenSkillDetail creature));
        Assert.Equal("Assess Creature", creature.Name);
        Assert.True(options.TryGetSkillDetail(19u, out ChargenSkillDetail person));
        Assert.Equal("Assess Person", person.Name);

        // The skills the report named wrongly, plus the one #83 fixed.
        Assert.Equal("Assess Creature", ChargenSkillNames.Resolve(options, 27u));
        Assert.Equal("Assess Person", ChargenSkillNames.Resolve(options, 19u));
        Assert.Equal("Shield", ChargenSkillNames.Resolve(options, 48u));

        // And every other skill the authored data carries, character-by-character.
        foreach ((DatReaderWriter.Enums.SkillId id, SkillBase skill) in authored.Skills)
        {
            Assert.Equal(skill.Name.Value, ChargenSkillNames.Resolve(options, (uint)id));
        }
    }

    [InstalledDatFact]
    public void Appraisals_ReadTheSameAuthoredNamesCharacterCreationDoes()
    {
        using var dats = new BoundedTestDatCollection(DatDirectory());

        SkillTable authored = Assert.IsType<SkillTable>(
            dats.Get<SkillTable>(ChargenTableReader.SkillTableDid));
        ChargenOptions options = ChargenTableReader.Load(dats);
        RetailAppraisalNameResolver names = RetailAppraisalNameResolver.Load(
            dats,
            CreatureDisplayNameResolver.Load(dats));

        // The resolver really loaded the authored table, rather than finding
        // nothing and leaving every requirement line unnamed.
        Assert.Equal(authored.Skills.Count, names.AuthoredSkillNameCount);
        Assert.Equal(0, RetailAppraisalNameResolver.Empty.AuthoredSkillNameCount);

        foreach ((DatReaderWriter.Enums.SkillId id, SkillBase skill) in authored.Skills)
        {
            Assert.True(names.TryResolveSkill((int)id, out string? name));
            Assert.Equal(skill.Name.Value, name);
            Assert.Equal(ChargenSkillNames.Resolve(options, (uint)id), name);
        }

        // A retired skill is absent from the authored data, so a requirement
        // line has no name to print.
        Assert.False(authored.Skills.ContainsKey((DatReaderWriter.Enums.SkillId)26));
        Assert.False(names.TryResolveSkill(26, out _));
    }

    [InstalledDatFact]
    public void TheTwoNameSources_DisagreeExactlyWhereTheGameLetsThem()
    {
        using var dats = new BoundedTestDatCollection(DatDirectory());

        SkillTable authored = Assert.IsType<SkillTable>(
            dats.Get<SkillTable>(ChargenTableReader.SkillTableDid));

        var disagreements = new List<string>();
        foreach ((DatReaderWriter.Enums.SkillId id, SkillBase skill) in authored.Skills)
        {
            if (RetailSkillNames.TryGetName((int)id, out string? builtIn)
                && !string.Equals(builtIn, skill.Name.Value, StringComparison.Ordinal))
            {
                disagreements.Add($"{(uint)id}: {builtIn} != {skill.Name.Value}");
            }
        }
        disagreements.Sort(StringComparer.Ordinal);

        // Two renames, and nothing else. If this list ever grows, a screen is
        // reading the wrong source somewhere.
        Assert.Equal(
            new[]
            {
                "19: Person Appraisal != Assess Person",
                "27: Creature Appraisal != Assess Creature",
            },
            disagreements.ToArray());
    }

    private static string DatDirectory()
    {
        string? configured = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;
        return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
    }
}
