using System.Numerics;
using AcDream.Core.CharGen;

namespace AcDream.Runtime.Tests.CharGen;

internal static class RuntimeCharacterCreationStateFixture
{
    public const uint AluvianId = 1u;
    public const uint OlthoiId = (uint)ChargenHeritageGroup.Olthoi;
    public const uint ImpoverishedId = 90u;
    public const uint MaleGenderKey = 1u;

    public const uint FemaleGenderKey = 2u;

    public const uint CustomTemplateIndex = 0u;

    /// <summary>str=16, everything else at floor — sum 66 (fully spent).</summary>
    public const uint PresetTemplateIndex = 1u;

    public const uint SkillTrainSpecialize = 1u;

    /// <summary>The Preset template's Primary (specialized) skill.</summary>
    public const uint SkillPresetPrimary = 2u;

    /// <summary>FREE and pre-specialized by <c>ResetSkillLevels</c>'s baseline
    /// (NormalCost=0, PrimaryCost&lt;=0).</summary>
    public const uint SkillFreeSpecialized = 3u;

    public const uint SkillFreeTrained = 4u;

    /// <summary>Uncostable in BOTH tiers — must never be settable (the CC1
    /// R1 binding fact: keep the both-miss refund path unreachable).</summary>
    public const uint SkillUncostable = 5u;

    public const uint SkillCustomNormal = 24u;

    public static ChargenOptions Build()
    {
        var starterAreas = new List<ChargenStarterArea>
        {
            new(0, "Holtburg", [new ChargenPosition(1u, Vector3.Zero, Quaternion.Identity)]),
            new(1, "Yaraq", [new ChargenPosition(2u, Vector3.Zero, Quaternion.Identity)]),
        };

        var skillCosts = new Dictionary<uint, ChargenSkillCost>
        {
            [SkillTrainSpecialize] = new(SkillTrainSpecialize, NormalCost: 4, PrimaryCost: 12),
            [SkillPresetPrimary] = new(SkillPresetPrimary, NormalCost: 3, PrimaryCost: 9),
            [SkillFreeSpecialized] = new(SkillFreeSpecialized, NormalCost: 0, PrimaryCost: 0),
            [SkillFreeTrained] = new(SkillFreeTrained, NormalCost: 0, PrimaryCost: 5),
            [SkillCustomNormal] = new(SkillCustomNormal, NormalCost: 2, PrimaryCost: 6),
        };

        var gender = new ChargenGenderOptions(
            GenderKey: (int)MaleGenderKey,
            Name: "Male",
            Scale: 1u,
            SetupId: 0x2000054u,
            SoundTableId: 0u,
            IconId: 0u,
            BasePaletteId: 0u,
            SkinPalSetId: 0u,
            PhysicsTableId: 0u,
            MotionTableId: 0u,
            CombatTableId: 0u,
            BaseObjDesc: ChargenObjDesc.Empty,
            HairColors: [100u, 101u],
            HairStyles:
            [
                new ChargenHairStyle(1u, Bald: false, AlternateSetup: 0u, ObjDesc: ChargenObjDesc.Empty),
                new ChargenHairStyle(2u, Bald: true, AlternateSetup: 0u, ObjDesc: ChargenObjDesc.Empty),
            ],
            EyeColors: [200u, 201u],
            EyeStrips:
            [
                new ChargenEyeStrip(1u, 2u, ChargenObjDesc.Empty, ChargenObjDesc.Empty),
            ],
            NoseStrips: [new ChargenFaceStrip(1u, ChargenObjDesc.Empty)],
            MouthStrips: [new ChargenFaceStrip(1u, ChargenObjDesc.Empty)],
            Headgears: [new ChargenGearOption("Cap", 1u, 300u)],
            Shirts: [new ChargenGearOption("Shirt", 2u, 301u)],
            Pants: [new ChargenGearOption("Pants", 3u, 302u)],
            Footwear: [new ChargenGearOption("Boots", 4u, 303u)],
            ClothingColors: [400u, 401u, 402u]);

        // Same shape as `gender`, just the other GenderKey — every list is
        // deliberately populated so a full RandomizeCharacter roll never
        // finds an empty option list to skip.
        var femaleGender = gender with { GenderKey = (int)FemaleGenderKey, Name = "Female" };
        var bothGenders = new Dictionary<int, ChargenGenderOptions>
        {
            [(int)MaleGenderKey] = gender,
            [(int)FemaleGenderKey] = femaleGender,
        };

        var aluvianTemplates = new List<ChargenTemplate>
        {
            new(
                "Custom",
                IconId: 0u,
                TitleStringId: 0u,
                Attributes: new ChargenAttributeValues(10, 10, 10, 10, 10, 10),
                NormalSkills: [SkillCustomNormal],
                PrimarySkills: []),
            new(
                "Preset",
                IconId: 0u,
                TitleStringId: 0u,
                Attributes: new ChargenAttributeValues(16, 10, 10, 10, 10, 10),
                NormalSkills: [SkillTrainSpecialize],
                PrimarySkills: [SkillPresetPrimary]),
        };

        var aluvian = new ChargenHeritageOptions(
            AluvianId,
            "Aluvian",
            IconId: 0u,
            SetupId: 0x2000054u,
            EnvironmentSetupId: 0u,
            AttributeCredits: 66u,
            SkillCredits: 50u,
            PrimaryStartAreaIndices: [0, 1],
            SecondaryStartAreaIndices: [],
            SkillCostsBySkillId: skillCosts,
            Templates: aluvianTemplates,
            GendersByKey: bothGenders);

        var olthoiTemplates = new List<ChargenTemplate>
        {
            new(
                "Custom",
                IconId: 0u,
                TitleStringId: 0u,
                Attributes: new ChargenAttributeValues(10, 10, 10, 10, 10, 10),
                NormalSkills: [],
                PrimarySkills: []),
            new(
                "NeverChosen",
                IconId: 0u,
                TitleStringId: 0u,
                Attributes: new ChargenAttributeValues(20, 20, 20, 20, 20, 20),
                NormalSkills: [],
                PrimarySkills: []),
        };

        var olthoi = new ChargenHeritageOptions(
            OlthoiId,
            "Olthoi",
            IconId: 0u,
            SetupId: 0x2000054u,
            EnvironmentSetupId: 0u,
            AttributeCredits: 60u,
            SkillCredits: 0u,
            PrimaryStartAreaIndices: [0],
            SecondaryStartAreaIndices: [],
            SkillCostsBySkillId: new Dictionary<uint, ChargenSkillCost>(),
            Templates: olthoiTemplates,
            GendersByKey: new Dictionary<int, ChargenGenderOptions> { [(int)MaleGenderKey] = gender });

        ChargenHeritageOptions MakeHumanHeritage(uint id, string name) => new(
            id,
            name,
            IconId: 0u,
            SetupId: 0x2000054u,
            EnvironmentSetupId: 0u,
            AttributeCredits: 66u,
            SkillCredits: 50u,
            PrimaryStartAreaIndices: [0, 1],
            SecondaryStartAreaIndices: [],
            SkillCostsBySkillId: skillCosts,
            Templates: aluvianTemplates,
            GendersByKey: bothGenders);

        var impoverished = new ChargenHeritageOptions(
            ImpoverishedId,
            "Impoverished",
            IconId: 0u,
            SetupId: 0x2000054u,
            EnvironmentSetupId: 0u,
            AttributeCredits: 60u,
            SkillCredits: 5u,
            PrimaryStartAreaIndices: [0],
            SecondaryStartAreaIndices: [],
            SkillCostsBySkillId: new Dictionary<uint, ChargenSkillCost>
            {
                [SkillTrainSpecialize] = new(SkillTrainSpecialize, NormalCost: 4, PrimaryCost: 12),
            },
            Templates:
            [
                new ChargenTemplate(
                    "Custom",
                    IconId: 0u,
                    TitleStringId: 0u,
                    Attributes: new ChargenAttributeValues(10, 10, 10, 10, 10, 10),
                    NormalSkills: [],
                    PrimarySkills: []),
            ],
            GendersByKey: new Dictionary<int, ChargenGenderOptions> { [(int)MaleGenderKey] = gender });

        return new ChargenOptions(
            starterAreas,
            new Dictionary<uint, ChargenHeritageOptions>
            {
                [AluvianId] = aluvian,
                [(uint)ChargenHeritageGroup.Gharundim] = MakeHumanHeritage(
                    (uint)ChargenHeritageGroup.Gharundim, "Gharu'ndim"),
                [(uint)ChargenHeritageGroup.Sho] = MakeHumanHeritage(
                    (uint)ChargenHeritageGroup.Sho, "Sho"),
                [(uint)ChargenHeritageGroup.Viamontian] = MakeHumanHeritage(
                    (uint)ChargenHeritageGroup.Viamontian, "Viamontian"),
                [OlthoiId] = olthoi,
                [ImpoverishedId] = impoverished,
            },
            new Dictionary<uint, ChargenSkillCost>());
    }
}
