using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace AcDream.Core.CharGen;

public sealed record ChargenOptions(
    IReadOnlyList<ChargenStarterArea> StarterAreas,
    IReadOnlyDictionary<uint, ChargenHeritageOptions> HeritagesById,
    IReadOnlyDictionary<uint, ChargenSkillCost> GlobalSkillCostsBySkillId,
    IReadOnlyDictionary<uint, ChargenSkillDetail>? GlobalSkillDetailsBySkillId = null)
{
    public static ChargenOptions Empty { get; } = new(
        Array.Empty<ChargenStarterArea>(),
        FrozenDictionary<uint, ChargenHeritageOptions>.Empty,
        FrozenDictionary<uint, ChargenSkillCost>.Empty);

    public bool TryGetHeritage(uint heritageId, [MaybeNullWhen(false)] out ChargenHeritageOptions heritage) =>
        HeritagesById.TryGetValue(heritageId, out heritage);

    public bool TryGetStarterArea(int index, [MaybeNullWhen(false)] out ChargenStarterArea area)
    {
        if (index >= 0 && index < StarterAreas.Count)
        {
            area = StarterAreas[index];
            return true;
        }
        area = default;
        return false;
    }

    public bool TryGetSkillDetail(uint skillId, [MaybeNullWhen(false)] out ChargenSkillDetail detail)
    {
        if (GlobalSkillDetailsBySkillId is { } details && details.TryGetValue(skillId, out detail))
            return true;
        detail = default;
        return false;
    }
}
