namespace AcDream.Core.CharGen;

public sealed record ChargenHeritageOptions(
    uint HeritageId,
    string Name,
    uint IconId,
    uint SetupId,
    uint EnvironmentSetupId,
    uint AttributeCredits,
    uint SkillCredits,
    IReadOnlyList<int> PrimaryStartAreaIndices,
    IReadOnlyList<int> SecondaryStartAreaIndices,
    IReadOnlyDictionary<uint, ChargenSkillCost> SkillCostsBySkillId,
    IReadOnlyList<ChargenTemplate> Templates,
    IReadOnlyDictionary<int, ChargenGenderOptions> GendersByKey)
{
    public bool IsOlthoi =>
        HeritageId == (uint)ChargenHeritageGroup.Olthoi
        || HeritageId == (uint)ChargenHeritageGroup.OlthoiAcid;
}
