using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.Content;
using AcDream.Content.CharGen;
using AcDream.Core.CharGen;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;
using DatCharGen = DatReaderWriter.DBObjs.CharGen;
using DatObjDesc = DatReaderWriter.Types.ObjDesc;

namespace AcDream.Content.Tests.CharGen;

public sealed class ChargenTableReaderTests
{
    private static PStringBase<byte> Str(string value)
    {
        var s = new PStringBase<byte>();
        s.Value = value;
        return s;
    }

    private static QualifiedDataId<T> Qdi<T>(uint id) where T : DBObj, new()
    {
        var qdi = new QualifiedDataId<T>();
        qdi.DataId = id;
        return qdi;
    }

    private static DatObjDesc MakeObjDesc(uint paletteId, byte partIndex, uint oldTex, uint newTex)
    {
        var od = new DatObjDesc();
        od.PaletteId = new PackedQualifiedDataId<Palette>();
        od.PaletteId.DataId = paletteId;

        var sub = new SubPalette();
        sub.SubId = new PackedQualifiedDataId<Palette>();
        sub.SubId.DataId = 0x04000099u;
        sub.Offset = 8;
        sub.NumColors = 24;
        od.SubPalettes.Add(sub);

        var tex = new TextureMapChange();
        tex.PartIndex = partIndex;
        tex.OldTexture = new PackedQualifiedDataId<SurfaceTexture>();
        tex.OldTexture.DataId = oldTex;
        tex.NewTexture = new PackedQualifiedDataId<SurfaceTexture>();
        tex.NewTexture.DataId = newTex;
        od.TextureChanges.Add(tex);

        var part = new AnimationPartChange();
        part.PartIndex = 2;
        part.PartId = new PackedQualifiedDataId<GfxObj>();
        part.PartId.DataId = 0x0100ABCDu;
        od.AnimPartChanges.Add(part);

        return od;
    }

    private static DatCharGen BuildFixture()
    {
        var table = new DatCharGen();

        var area = new StartingArea();
        area.Name = Str("Holtburg");
        var position = new Position();
        position.CellId = 0xA9B40000u;
        position.Frame = new Frame
        {
            Origin = new Vector3(1f, 2f, 3f),
            Orientation = new Quaternion(0f, 0f, 0f, 1f),
        };
        area.Locations.Add(position);
        table.StartingAreas.Add(area);

        var heritage = new HeritageGroupCG();
        heritage.Name = Str("Aluvian");
        heritage.IconId = Qdi<RenderSurface>(0x06000001u);
        heritage.SetupId = Qdi<Setup>(0x02000010u);
        heritage.EnvironmentSetupId = Qdi<Setup>(0x02000020u);
        heritage.AttributeCredits = 180u;
        heritage.SkillCredits = 50u;
        heritage.PrimaryStartAreas.Add(0);
        heritage.SecondaryStartAreas.Add(0);

        var skill = new SkillCG();
        skill.Id = DatReaderWriter.Enums.SkillId.Axe;
        skill.NormalCost = 4;
        skill.PrimaryCost = 12;
        heritage.Skills.Add(skill);

        var template = new TemplateCG();
        template.Name = Str("Soldier");
        template.IconId = Qdi<RenderSurface>(0x06000002u);
        template.Title = 42u;
        template.Strength = 40;
        template.Endurance = 40;
        template.Coordination = 40;
        template.Quickness = 20;
        template.Focus = 20;
        template.Self = 20;
        template.NormalSkills.Add(DatReaderWriter.Enums.SkillId.Axe);
        template.PrimarySkills.Add(DatReaderWriter.Enums.SkillId.MeleeDefense);
        heritage.Templates.Add(template);

        var sex = new SexCG();
        sex.Name = Str("Male");
        sex.Scale = 1000000u;
        sex.SetupId = Qdi<Setup>(0x02000030u);
        sex.SoundTable = Qdi<SoundTable>(0x22000001u);
        sex.IconId = Qdi<RenderSurface>(0x06000003u);
        sex.BasePalette = Qdi<Palette>(0x04000001u);
        sex.SkinPalSet = Qdi<PalSet>(0x04001001u);
        sex.PhysicsTable = Qdi<PhysicsScriptTable>(0x0D000001u);
        sex.MotionTable = Qdi<MotionTable>(0x09000001u);
        sex.CombatTable = Qdi<CombatTable>(0x0F000001u);
        sex.BaseObjDesc = MakeObjDesc(0x04000002u, 0, 0x05000001u, 0x05000002u);
        sex.HairColors.Add(0x0Au);
        sex.EyeColors.Add(0x0Bu);
        sex.ClothingColors.Add(0x0Cu);

        var hairStyle = new HairStyleCG();
        hairStyle.IconId = Qdi<RenderSurface>(0x06000004u);
        hairStyle.Bald = false;
        hairStyle.AlternateSetup = 0x02000099u;
        hairStyle.ObjDesc = MakeObjDesc(0x04000003u, 1, 0x05000003u, 0x05000004u);
        sex.HairStyles.Add(hairStyle);

        var eyeStrip = new EyeStripCG();
        eyeStrip.IconId = Qdi<RenderSurface>(0x06000005u);
        eyeStrip.BaldIconId = 0x06000006u;
        eyeStrip.ObjDesc = MakeObjDesc(0x04000004u, 2, 0x05000005u, 0x05000006u);
        eyeStrip.BaldObjDesc = MakeObjDesc(0x04000005u, 3, 0x05000007u, 0x05000008u);
        sex.EyeStrips.Add(eyeStrip);

        var noseStrip = new FaceStripCG();
        noseStrip.IconId = Qdi<RenderSurface>(0x06000007u);
        noseStrip.ObjDesc = MakeObjDesc(0x04000006u, 4, 0x05000009u, 0x0500000Au);
        sex.NoseStrips.Add(noseStrip);

        var mouthStrip = new FaceStripCG();
        mouthStrip.IconId = Qdi<RenderSurface>(0x06000008u);
        mouthStrip.ObjDesc = MakeObjDesc(0x04000007u, 5, 0x0500000Bu, 0x0500000Cu);
        sex.MouthStrips.Add(mouthStrip);

        var headgear = new GearCG();
        headgear.Name = Str("Leather Cap");
        headgear.ClothingTable = Qdi<ClothingTable>(0x31000001u);
        headgear.WeenieDefault = 300001u;
        sex.Headgears.Add(headgear);

        var shirt = new GearCG();
        shirt.Name = Str("Tunic");
        shirt.ClothingTable = Qdi<ClothingTable>(0x31000002u);
        shirt.WeenieDefault = 300002u;
        sex.Shirts.Add(shirt);

        var pants = new GearCG();
        pants.Name = Str("Breeches");
        pants.ClothingTable = Qdi<ClothingTable>(0x31000003u);
        pants.WeenieDefault = 300003u;
        sex.Pants.Add(pants);

        var footwear = new GearCG();
        footwear.Name = Str("Boots");
        footwear.ClothingTable = Qdi<ClothingTable>(0x31000004u);
        footwear.WeenieDefault = 300004u;
        sex.Footwear.Add(footwear);

        heritage.Genders.Add(0, sex);

        table.HeritageGroups.Add(1u, heritage);

        return table;
    }

    [Fact]
    public void Project_MapsStarterAreasWithPositionsAndCellId()
    {
        ChargenOptions options = ChargenTableReader.Project(BuildFixture());

        ChargenStarterArea area = Assert.Single(options.StarterAreas);
        Assert.Equal(0, area.Index);
        Assert.Equal("Holtburg", area.Name);
        ChargenPosition position = Assert.Single(area.Locations);
        Assert.Equal(0xA9B40000u, position.CellId);
        Assert.Equal(new Vector3(1f, 2f, 3f), position.Origin);
        Assert.Equal(new Quaternion(0f, 0f, 0f, 1f), position.Orientation);
    }

    [Fact]
    public void Project_MapsHeritageScalarFieldsAndStartAreaIndices()
    {
        ChargenOptions options = ChargenTableReader.Project(BuildFixture());

        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? heritage));
        Assert.Equal("Aluvian", heritage!.Name);
        Assert.Equal(0x06000001u, heritage.IconId);
        Assert.Equal(0x02000010u, heritage.SetupId);
        Assert.Equal(0x02000020u, heritage.EnvironmentSetupId);
        Assert.Equal(180u, heritage.AttributeCredits);
        Assert.Equal(50u, heritage.SkillCredits);
        Assert.Equal([0], heritage.PrimaryStartAreaIndices);
        Assert.Equal([0], heritage.SecondaryStartAreaIndices);
        Assert.False(heritage.IsOlthoi);
    }

    [Fact]
    public void Project_MapsSkillCostsKeyedByRawSkillId()
    {
        ChargenOptions options = ChargenTableReader.Project(BuildFixture());
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? heritage));

        uint axeId = (uint)DatReaderWriter.Enums.SkillId.Axe;
        Assert.True(heritage!.SkillCostsBySkillId.TryGetValue(axeId, out ChargenSkillCost cost));
        Assert.Equal(axeId, cost.SkillId);
        Assert.Equal(4, cost.NormalCost);
        Assert.Equal(12, cost.PrimaryCost);
    }

    [Fact]
    public void Project_MapsTemplateAttributesAndSkillLists()
    {
        ChargenOptions options = ChargenTableReader.Project(BuildFixture());
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? heritage));

        ChargenTemplate template = Assert.Single(heritage!.Templates);
        Assert.Equal("Soldier", template.Name);
        Assert.Equal(42u, template.TitleStringId);
        Assert.Equal(new ChargenAttributeValues(40, 40, 40, 20, 20, 20), template.Attributes);
        Assert.Equal(180, template.Attributes.Total);
        Assert.Equal([(uint)DatReaderWriter.Enums.SkillId.Axe], template.NormalSkills);
        Assert.Equal([(uint)DatReaderWriter.Enums.SkillId.MeleeDefense], template.PrimarySkills);
    }

    [Fact]
    public void Project_MapsGenderScalarsAndOptionLists()
    {
        ChargenOptions options = ChargenTableReader.Project(BuildFixture());
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? heritage));

        Assert.True(heritage!.GendersByKey.TryGetValue(0, out ChargenGenderOptions? gender));
        Assert.Equal("Male", gender!.Name);
        Assert.Equal(1000000u, gender.Scale);
        Assert.Equal(0x02000030u, gender.SetupId);
        Assert.Equal(0x04000001u, gender.BasePaletteId);
        Assert.Equal(0x04001001u, gender.SkinPalSetId);
        Assert.Equal([0x0Au], gender.HairColors);
        Assert.Equal([0x0Bu], gender.EyeColors);
        Assert.Equal([0x0Cu], gender.ClothingColors);
        Assert.True(gender.HasAnyAppearanceOptions);

        ChargenHairStyle hair = Assert.Single(gender.HairStyles);
        Assert.Equal(0x06000004u, hair.IconId);
        Assert.False(hair.Bald);
        Assert.Equal(0x02000099u, hair.AlternateSetup);

        ChargenEyeStrip eye = Assert.Single(gender.EyeStrips);
        Assert.Equal(0x06000005u, eye.IconId);
        Assert.Equal(0x06000006u, eye.BaldIconId);

        ChargenFaceStrip nose = Assert.Single(gender.NoseStrips);
        Assert.Equal(0x06000007u, nose.IconId);
        ChargenFaceStrip mouth = Assert.Single(gender.MouthStrips);
        Assert.Equal(0x06000008u, mouth.IconId);

        ChargenGearOption headgear = Assert.Single(gender.Headgears);
        Assert.Equal("Leather Cap", headgear.Name);
        Assert.Equal(0x31000001u, headgear.ClothingTableId);
        Assert.Equal(300001u, headgear.WeenieDefaultId);

        Assert.Single(gender.Shirts);
        Assert.Single(gender.Pants);
        Assert.Single(gender.Footwear);
    }

    [Fact]
    public void Project_MapsObjDescPaletteSubPaletteTextureAndAnimPartChanges()
    {
        ChargenOptions options = ChargenTableReader.Project(BuildFixture());
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? heritage));
        heritage!.GendersByKey.TryGetValue(0, out ChargenGenderOptions? gender);

        ChargenObjDesc baseDesc = gender!.BaseObjDesc;
        Assert.Equal(0x04000002u, baseDesc.PaletteId);
        ChargenSubPalette sub = Assert.Single(baseDesc.SubPalettes);
        Assert.Equal(0x04000099u, sub.SubPaletteId);
        Assert.Equal((byte)8, sub.Offset);
        Assert.Equal((byte)24, sub.NumColors);

        ChargenTextureChange tex = Assert.Single(baseDesc.TextureChanges);
        Assert.Equal((byte)0, tex.PartIndex);
        Assert.Equal(0x05000001u, tex.OldTextureId);
        Assert.Equal(0x05000002u, tex.NewTextureId);

        ChargenAnimPartChange part = Assert.Single(baseDesc.AnimPartChanges);
        Assert.Equal((byte)2, part.PartIndex);
        Assert.Equal(0x0100ABCDu, part.PartId);

        ChargenEyeStrip eye = Assert.Single(gender.EyeStrips);
        Assert.Equal(0x04000004u, eye.ObjDesc.PaletteId);
        Assert.Equal(0x04000005u, eye.BaldObjDesc.PaletteId);
        Assert.NotEqual(eye.ObjDesc.PaletteId, eye.BaldObjDesc.PaletteId);
    }

    [Fact]
    public void Load_ReturnsEmptyOptions_WhenTableIsMissingFromDatSource()
    {
        var empty = new EmptyDatReaderWriter();

        ChargenOptions options = ChargenTableReader.Load(empty);

        Assert.Empty(options.StarterAreas);
        Assert.Empty(options.HeritagesById);
    }

    private sealed class EmptyDatReaderWriter : IDatReaderWriter
    {
        private readonly StubDatabase _db = new();

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => _db;
        public IDatDatabase Cell => _db;
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => _db;
        public IDatDatabase Language => _db;
        public IDatDatabase Local => _db;
        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj => Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj => default;

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public void Dispose() { }

        private sealed class StubDatabase : IDatDatabase
        {
            public DatDatabase Db => null!;
            public int Iteration => 0;
            public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj => Array.Empty<uint>();
            public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
            {
                value = default;
                return false;
            }
            public bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value)
            {
                value = default;
                return false;
            }
            public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }
            public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj => false;
            public void Dispose() { }
        }
    }
}
