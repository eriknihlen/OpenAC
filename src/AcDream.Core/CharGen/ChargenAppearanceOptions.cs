namespace AcDream.Core.CharGen;

public sealed record ChargenHairStyle(
    uint IconId,
    bool Bald,
    uint AlternateSetup,
    ChargenObjDesc ObjDesc);

public sealed record ChargenEyeStrip(
    uint IconId,
    uint BaldIconId,
    ChargenObjDesc ObjDesc,
    ChargenObjDesc BaldObjDesc);

public sealed record ChargenFaceStrip(uint IconId, ChargenObjDesc ObjDesc);

public sealed record ChargenGearOption(
    string Name,
    uint ClothingTableId,
    uint WeenieDefaultId);
