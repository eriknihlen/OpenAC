namespace AcDream.Core.CharGen;

public sealed record ChargenGenderOptions(
    int GenderKey,
    string Name,
    uint Scale,
    uint SetupId,
    uint SoundTableId,
    uint IconId,
    uint BasePaletteId,
    uint SkinPalSetId,
    uint PhysicsTableId,
    uint MotionTableId,
    uint CombatTableId,
    ChargenObjDesc BaseObjDesc,
    IReadOnlyList<uint> HairColors,
    IReadOnlyList<ChargenHairStyle> HairStyles,
    IReadOnlyList<uint> EyeColors,
    IReadOnlyList<ChargenEyeStrip> EyeStrips,
    IReadOnlyList<ChargenFaceStrip> NoseStrips,
    IReadOnlyList<ChargenFaceStrip> MouthStrips,
    IReadOnlyList<ChargenGearOption> Headgears,
    IReadOnlyList<ChargenGearOption> Shirts,
    IReadOnlyList<ChargenGearOption> Pants,
    IReadOnlyList<ChargenGearOption> Footwear,
    IReadOnlyList<uint> ClothingColors)
{
    public bool HasAnyAppearanceOptions =>
        HairStyles.Count > 0
        || EyeStrips.Count > 0
        || NoseStrips.Count > 0
        || MouthStrips.Count > 0
        || Headgears.Count > 0
        || Shirts.Count > 0
        || Pants.Count > 0
        || Footwear.Count > 0;
}
