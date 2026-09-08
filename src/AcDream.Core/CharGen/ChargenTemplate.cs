namespace AcDream.Core.CharGen;

public sealed record ChargenTemplate(
    string Name,
    uint IconId,
    uint TitleStringId,
    ChargenAttributeValues Attributes,
    IReadOnlyList<uint> NormalSkills,
    IReadOnlyList<uint> PrimarySkills);
