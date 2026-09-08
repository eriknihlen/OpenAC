using System.Collections.Frozen;
using AcDream.Core.CharGen;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using CoreChargenObjDesc = AcDream.Core.CharGen.ChargenObjDesc;
using DatCharGen = DatReaderWriter.DBObjs.CharGen;
using DatObjDesc = DatReaderWriter.Types.ObjDesc;
using DatSkillTable = DatReaderWriter.DBObjs.SkillTable;

namespace AcDream.Content.CharGen;

public static class ChargenTableReader
{
    public const uint ChargenTableDid = 0x0E000002u;

    public const uint SkillTableDid = 0x0E000004u;

    public static ChargenOptions Load(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);

        DatCharGen? table = dats.Get<DatCharGen>(ChargenTableDid);
        if (table is null)
            return ChargenOptions.Empty;

        DatSkillTable? skillTable = dats.Get<DatSkillTable>(SkillTableDid);
        return Project(table, skillTable);
    }

    public static ChargenOptions Project(DatCharGen table, DatSkillTable? skillTable = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        var starterAreas = new ChargenStarterArea[table.StartingAreas.Count];
        for (int i = 0; i < table.StartingAreas.Count; i++)
            starterAreas[i] = ProjectStarterArea(i, table.StartingAreas[i]);

        var heritagesById = new Dictionary<uint, ChargenHeritageOptions>(table.HeritageGroups.Count);
        foreach (KeyValuePair<uint, HeritageGroupCG> pair in table.HeritageGroups)
            heritagesById[pair.Key] = ProjectHeritage(pair.Key, pair.Value);

        var globalSkillCosts = new Dictionary<uint, ChargenSkillCost>(skillTable?.Skills.Count ?? 0);
        var globalSkillDetails = new Dictionary<uint, ChargenSkillDetail>(skillTable?.Skills.Count ?? 0);
        if (skillTable is not null)
        {
            foreach (KeyValuePair<DatReaderWriter.Enums.SkillId, SkillBase> pair in skillTable.Skills)
            {
                uint skillId = (uint)pair.Key;
                SkillBase skill = pair.Value;
                globalSkillCosts[skillId] = new ChargenSkillCost(
                    skillId,
                    skill.TrainedCost,
                    skill.SpecializedCost);
                globalSkillDetails[skillId] = new ChargenSkillDetail(
                    skillId,
                    skill.MinLevel,
                    skill.Description.Value,
                    new ChargenSkillFormula(
                        skill.Formula.AdditiveBonus,
                        skill.Formula.Attribute1Multiplier,
                        skill.Formula.Attribute2Multiplier,
                        skill.Formula.Divisor,
                        (uint)skill.Formula.Attribute1,
                        (uint)skill.Formula.Attribute2));
            }
        }

        return new ChargenOptions(
            Array.AsReadOnly(starterAreas),
            heritagesById.ToFrozenDictionary(),
            globalSkillCosts.ToFrozenDictionary(),
            globalSkillDetails.ToFrozenDictionary());
    }

    private static ChargenStarterArea ProjectStarterArea(int index, StartingArea area)
    {
        var locations = new ChargenPosition[area.Locations.Count];
        for (int i = 0; i < area.Locations.Count; i++)
        {
            Position position = area.Locations[i];
            locations[i] = new ChargenPosition(
                position.CellId,
                position.Frame.Origin,
                position.Frame.Orientation);
        }
        return new ChargenStarterArea(index, area.Name.Value, Array.AsReadOnly(locations));
    }

    private static ChargenHeritageOptions ProjectHeritage(uint heritageId, HeritageGroupCG cg)
    {
        var skillCosts = new Dictionary<uint, ChargenSkillCost>(cg.Skills.Count);
        foreach (SkillCG skill in cg.Skills)
        {
            uint skillId = (uint)skill.Id;
            skillCosts[skillId] = new ChargenSkillCost(skillId, skill.NormalCost, skill.PrimaryCost);
        }

        var templates = new ChargenTemplate[cg.Templates.Count];
        for (int i = 0; i < cg.Templates.Count; i++)
            templates[i] = ProjectTemplate(cg.Templates[i]);

        var gendersByKey = new Dictionary<int, ChargenGenderOptions>(cg.Genders.Count);
        foreach (KeyValuePair<int, SexCG> pair in cg.Genders)
            gendersByKey[pair.Key] = ProjectGender(pair.Key, pair.Value);

        return new ChargenHeritageOptions(
            heritageId,
            cg.Name.Value,
            cg.IconId.DataId,
            cg.SetupId.DataId,
            cg.EnvironmentSetupId.DataId,
            cg.AttributeCredits,
            cg.SkillCredits,
            Array.AsReadOnly(cg.PrimaryStartAreas.ToArray()),
            Array.AsReadOnly(cg.SecondaryStartAreas.ToArray()),
            skillCosts.ToFrozenDictionary(),
            Array.AsReadOnly(templates),
            gendersByKey.ToFrozenDictionary());
    }

    private static ChargenTemplate ProjectTemplate(TemplateCG template)
    {
        var normalSkills = new uint[template.NormalSkills.Count];
        for (int i = 0; i < template.NormalSkills.Count; i++)
            normalSkills[i] = (uint)template.NormalSkills[i];

        var primarySkills = new uint[template.PrimarySkills.Count];
        for (int i = 0; i < template.PrimarySkills.Count; i++)
            primarySkills[i] = (uint)template.PrimarySkills[i];

        return new ChargenTemplate(
            template.Name.Value,
            template.IconId.DataId,
            template.Title,
            new ChargenAttributeValues(
                template.Strength,
                template.Endurance,
                template.Coordination,
                template.Quickness,
                template.Focus,
                template.Self),
            Array.AsReadOnly(normalSkills),
            Array.AsReadOnly(primarySkills));
    }

    private static ChargenGenderOptions ProjectGender(int genderKey, SexCG sex)
    {
        var hairStyles = new ChargenHairStyle[sex.HairStyles.Count];
        for (int i = 0; i < sex.HairStyles.Count; i++)
        {
            HairStyleCG hair = sex.HairStyles[i];
            hairStyles[i] = new ChargenHairStyle(
                hair.IconId.DataId,
                hair.Bald,
                hair.AlternateSetup,
                ProjectObjDesc(hair.ObjDesc));
        }

        var eyeStrips = new ChargenEyeStrip[sex.EyeStrips.Count];
        for (int i = 0; i < sex.EyeStrips.Count; i++)
        {
            EyeStripCG eye = sex.EyeStrips[i];
            eyeStrips[i] = new ChargenEyeStrip(
                eye.IconId.DataId,
                eye.BaldIconId,
                ProjectObjDesc(eye.ObjDesc),
                ProjectObjDesc(eye.BaldObjDesc));
        }

        var noseStrips = new ChargenFaceStrip[sex.NoseStrips.Count];
        for (int i = 0; i < sex.NoseStrips.Count; i++)
        {
            FaceStripCG strip = sex.NoseStrips[i];
            noseStrips[i] = new ChargenFaceStrip(strip.IconId.DataId, ProjectObjDesc(strip.ObjDesc));
        }

        var mouthStrips = new ChargenFaceStrip[sex.MouthStrips.Count];
        for (int i = 0; i < sex.MouthStrips.Count; i++)
        {
            FaceStripCG strip = sex.MouthStrips[i];
            mouthStrips[i] = new ChargenFaceStrip(strip.IconId.DataId, ProjectObjDesc(strip.ObjDesc));
        }

        return new ChargenGenderOptions(
            genderKey,
            sex.Name.Value,
            sex.Scale,
            sex.SetupId.DataId,
            sex.SoundTable.DataId,
            sex.IconId.DataId,
            sex.BasePalette.DataId,
            sex.SkinPalSet.DataId,
            sex.PhysicsTable.DataId,
            sex.MotionTable.DataId,
            sex.CombatTable.DataId,
            ProjectObjDesc(sex.BaseObjDesc),
            Array.AsReadOnly(sex.HairColors.ToArray()),
            Array.AsReadOnly(hairStyles),
            Array.AsReadOnly(sex.EyeColors.ToArray()),
            Array.AsReadOnly(eyeStrips),
            Array.AsReadOnly(noseStrips),
            Array.AsReadOnly(mouthStrips),
            ProjectGearList(sex.Headgears),
            ProjectGearList(sex.Shirts),
            ProjectGearList(sex.Pants),
            ProjectGearList(sex.Footwear),
            Array.AsReadOnly(sex.ClothingColors.ToArray()));
    }

    private static IReadOnlyList<ChargenGearOption> ProjectGearList(List<GearCG> gearList)
    {
        var result = new ChargenGearOption[gearList.Count];
        for (int i = 0; i < gearList.Count; i++)
        {
            GearCG gear = gearList[i];
            result[i] = new ChargenGearOption(gear.Name.Value, gear.ClothingTable.DataId, gear.WeenieDefault);
        }
        return Array.AsReadOnly(result);
    }

    private static CoreChargenObjDesc ProjectObjDesc(DatObjDesc objDesc)
    {
        var subPalettes = new ChargenSubPalette[objDesc.SubPalettes.Count];
        for (int i = 0; i < objDesc.SubPalettes.Count; i++)
        {
            SubPalette sub = objDesc.SubPalettes[i];
            subPalettes[i] = new ChargenSubPalette(sub.SubId.DataId, sub.Offset, sub.NumColors);
        }

        var textureChanges = new ChargenTextureChange[objDesc.TextureChanges.Count];
        for (int i = 0; i < objDesc.TextureChanges.Count; i++)
        {
            TextureMapChange change = objDesc.TextureChanges[i];
            textureChanges[i] = new ChargenTextureChange(
                change.PartIndex,
                change.OldTexture.DataId,
                change.NewTexture.DataId);
        }

        var animPartChanges = new ChargenAnimPartChange[objDesc.AnimPartChanges.Count];
        for (int i = 0; i < objDesc.AnimPartChanges.Count; i++)
        {
            AnimationPartChange change = objDesc.AnimPartChanges[i];
            animPartChanges[i] = new ChargenAnimPartChange(change.PartIndex, change.PartId.DataId);
        }

        return new CoreChargenObjDesc(
            objDesc.PaletteId.DataId,
            Array.AsReadOnly(subPalettes),
            Array.AsReadOnly(textureChanges),
            Array.AsReadOnly(animPartChanges));
    }
}
