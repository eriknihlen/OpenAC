using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.CharGen;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterCreationAppearancePageSwatchColorTests
{
    private const uint HeritageId = 1u;
    private const int GenderKey = 1;

    private const uint HairPalSetIdA = 0x0F00_0001u;
    private const uint HairPalSetIdB = 0x0F00_0002u;
    private const uint EyePaletteIdA = 0x0400_0010u;
    private const uint EyePaletteIdB = 0x0400_0011u;
    private const uint HeadgearClothingTableId = 0x1900_0001u;
    private const uint HeadgearDyePalSetId = 0x0F00_0010u;
    private const uint SkinPalSetId = 0x0F00_0099u;

    private static readonly ChargenSwatchRgb HairColorA = new(10, 20, 30);
    private static readonly ChargenSwatchRgb HairColorB = new(40, 50, 60);
    private static readonly ChargenSwatchRgb EyeColorA = new(70, 80, 90);
    private static readonly ChargenSwatchRgb EyeColorB = new(100, 110, 120);
    private static readonly ChargenSwatchRgb HeadgearColorA = new(130, 140, 150);
    private static readonly ChargenSwatchRgb SkinColor = new(160, 170, 180);

    private sealed class FakePalSetSource : IChargenPalSetSource
    {
        private readonly Dictionary<uint, ChargenPalSet> _sets = new();
        public void Add(uint id, params uint[] paletteIds) => _sets[id] = new ChargenPalSet(paletteIds);
        public ChargenPalSet? TryGetPalSet(uint palSetId) => _sets.TryGetValue(palSetId, out var s) ? s : null;
    }

    private sealed class FakeClothingTableSource : IChargenClothingTableSource
    {
        private readonly Dictionary<uint, ChargenClothingTable> _tables = new();
        public void Add(uint id, ChargenClothingTable table) => _tables[id] = table;
        public ChargenClothingTable? TryGetClothingTable(uint clothingTableId) =>
            _tables.TryGetValue(clothingTableId, out var t) ? t : null;
    }

    private sealed class FakeColorSource : IChargenPaletteColorSource
    {
        private readonly Dictionary<uint, ChargenSwatchRgb> _colors = new();
        public void Add(uint paletteId, ChargenSwatchRgb color) => _colors[paletteId] = color;
        public bool TryGetColor(uint paletteId, int index, out ChargenSwatchRgb color) =>
            _colors.TryGetValue(paletteId, out color);
    }

    private static ChargenGenderOptions MakeGender() => new(
        GenderKey: GenderKey,
        Name: "Male",
        Scale: 100u,
        SetupId: 0x0200_0001u,
        SoundTableId: 0u,
        IconId: 0u,
        BasePaletteId: 0u,
        SkinPalSetId: SkinPalSetId,
        PhysicsTableId: 0u,
        MotionTableId: 0u,
        CombatTableId: 0u,
        BaseObjDesc: ChargenObjDesc.Empty,
        HairColors: [HairPalSetIdA, HairPalSetIdB],
        HairStyles: [new ChargenHairStyle(0u, false, 0u, ChargenObjDesc.Empty)],
        EyeColors: [EyePaletteIdA, EyePaletteIdB],
        EyeStrips: [new ChargenEyeStrip(0u, 0u, ChargenObjDesc.Empty, ChargenObjDesc.Empty)],
        NoseStrips: [new ChargenFaceStrip(0u, ChargenObjDesc.Empty)],
        MouthStrips: [new ChargenFaceStrip(0u, ChargenObjDesc.Empty)],
        Headgears: [new ChargenGearOption("Cap", HeadgearClothingTableId, 0u)],
        Shirts: [],
        Pants: [],
        Footwear: [],
        ClothingColors: [9u]);

    private static ChargenOptions MakeOptions(uint heritageId, ChargenGenderOptions gender)
    {
        var heritage = new ChargenHeritageOptions(
            heritageId, "Test", 0u, 0x0200_0001u, 0u,
            180u, 100u, [0], [],
            new Dictionary<uint, ChargenSkillCost>(), [],
            new Dictionary<int, ChargenGenderOptions> { [GenderKey] = gender });
        return new ChargenOptions(
            [],
            new Dictionary<uint, ChargenHeritageOptions> { [heritageId] = heritage },
            new Dictionary<uint, ChargenSkillCost>());
    }

    private static (FakePalSetSource pal, FakeClothingTableSource clothing, FakeColorSource colors) MakeSources()
    {
        var pal = new FakePalSetSource();
        pal.Add(HairPalSetIdA, 0x0400_0001u);
        pal.Add(HairPalSetIdB, 0x0400_0002u);
        pal.Add(HeadgearDyePalSetId, 0x0400_0003u);
        pal.Add(SkinPalSetId, 0x0400_0004u);

        var colors = new FakeColorSource();
        colors.Add(0x0400_0001u, HairColorA);
        colors.Add(0x0400_0002u, HairColorB);
        colors.Add(EyePaletteIdA, EyeColorA);
        colors.Add(EyePaletteIdB, EyeColorB);
        colors.Add(0x0400_0003u, HeadgearColorA);
        colors.Add(0x0400_0004u, SkinColor);

        var choice = new ChargenClothingSubPaletteChoice(HeadgearDyePalSetId, [new ChargenClothingSubPaletteRange(0, 8)]);
        var templates = new Dictionary<uint, ChargenClothingPaletteTemplate>
        {
            [9u] = new ChargenClothingPaletteTemplate([choice]),
        };
        var clothing = new FakeClothingTableSource();
        clothing.Add(HeadgearClothingTableId, new ChargenClothingTable(
            new Dictionary<uint, ChargenClothingBaseEffect>(), templates));

        return (pal, clothing, colors);
    }

    private sealed class FakeView(ChargenOptions options, uint heritageId) : IRuntimeCharacterCreationView
    {
        public RuntimeCharacterCreationSnapshot Snapshot { get; set; } = new(
            new RuntimeGenerationToken(1u),
            IsActive: true,
            Revision: 1,
            HeritageId: heritageId,
            GenderKey: (uint)GenderKey,
            Appearance: RuntimeCharacterCreationAppearance.Default,
            Template: RuntimeCharacterCreationSnapshot.TemplateUnset,
            Attributes: default,
            AttributeLockMask: 0u,
            TotalAttributeCredits: 0u,
            RemainingAttributeCredits: 0,
            TotalSkillCredits: 0u,
            RemainingSkillCredits: 0,
            Name: string.Empty,
            StartArea: -1,
            Slot: 0u,
            VerificationPending: false,
            LastLocalRefusal: default,
            LastRejection: null,
            LastCreated: null);

        public ChargenOptions Options { get; } = options;
        public ChargenSkillAdvancementClass GetSkillLevel(uint skillId) => ChargenSkillAdvancementClass.Inactive;
        public IDisposable Subscribe(IRuntimeCharacterCreationObserver observer) => NullSubscription.Instance;

        private sealed class NullSubscription : IDisposable
        {
            public static readonly NullSubscription Instance = new();
            public void Dispose() { }
        }
    }

    private static UiElement BuildPageRoot()
    {
        var page = new ElementInfo
        {
            Id = 0x100003D4u,
            Type = 3u,
            Width = 800f,
            Height = 500f,
        };

        page.Children.Add(SpinInfo(CharacterCreationAppearancePage.HairSpinId));
        page.Children.Add(SpinInfo(CharacterCreationAppearancePage.EyesSpinId));
        page.Children.Add(SpinInfo(CharacterCreationAppearancePage.SkinSpinId));
        page.Children.Add(SpinInfo(CharacterCreationAppearancePage.HeadgearSpinId));

        foreach (uint swatchId in CharacterCreationAppearancePage.SwatchIds)
            page.Children.Add(ButtonInfo(swatchId));
        foreach (uint overlayId in CharacterCreationAppearancePage.SwatchOverlayIds)
            page.Children.Add(ContainerInfo(overlayId));

        page.Children.Add(ContainerInfo(CharacterCreationAppearancePage.GradCircleId));

        return LayoutImporter.Build(page, _ => (0u, 0, 0), null).Root;
    }

    private static ElementInfo SpinInfo(uint id)
    {
        var spin = new ElementInfo { Id = id, Type = 1u, Width = 200f, Height = 24f };
        spin.Children.Add(new ElementInfo { Id = 0x1000030Au, Type = 1u, X = 80f, Width = 47f, Height = 24f });
        spin.Children.Add(new ElementInfo { Id = 0x1000030Bu, Type = 1u, X = 127f, Width = 47f, Height = 24f });
        return spin;
    }

    private static ElementInfo ButtonInfo(uint id) => new() { Id = id, Type = 1u, Width = 20f, Height = 20f };
    private static ElementInfo ContainerInfo(uint id) => new() { Id = id, Type = 3u, Width = 64f, Height = 64f };

    private static (CharacterCreationAppearancePage Page, FakeView View, UiElement Root) BuildPage(
        FakePalSetSource pal, FakeClothingTableSource clothing, FakeColorSource colors)
    {
        var view = new FakeView(MakeOptions(HeritageId, MakeGender()), HeritageId);
        var bindings = new CharacterCreationRuntimeBindings(
            () => view,
            _ => default,
            _ => default,
            _ => default,
            (_, _) => default,
            (_, _) => default,
            _ => default,
            _ => default,
            _ => default,
            _ => default,
            _ => default,
            () => { },
            SetAppearanceIndex: (_, _) => default);

        UiElement pageRoot = BuildPageRoot();
        var page = new CharacterCreationAppearancePage(pageRoot, bindings)
        {
            PalSetSource = pal,
            ClothingTableSource = clothing,
            PaletteColorSource = colors,
        };
        return (page, view, pageRoot);
    }

    private static UiButton Swatch(UiElement pageRoot, int index) =>
        Assert.IsType<UiButton>(UiElement.FindDescendant(
            pageRoot, CharacterCreationAppearancePage.SwatchIds[index]));

    private static UiDatElement GradCircle(UiElement pageRoot) =>
        Assert.IsType<UiDatElement>(UiElement.FindDescendant(
            pageRoot, CharacterCreationAppearancePage.GradCircleId));

    private static Vector4 ToVector4(ChargenSwatchRgb rgb) => new(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, 1f);

    [Fact]
    public void HairPart_PaintsBothSwatchesWithTheirOwnDistinctColors()
    {
        var (pal, clothing, colors) = MakeSources();
        (CharacterCreationAppearancePage page, FakeView view, UiElement root) = BuildPage(pal, clothing, colors);

        page.Refresh(view, view.Snapshot);

        Assert.Equal(ToVector4(HairColorA), Swatch(root, 0).Tint);
        Assert.True(Swatch(root, 0).Visible);
        Assert.Equal(ToVector4(HairColorB), Swatch(root, 1).Tint);
        Assert.True(Swatch(root, 1).Visible);
        Assert.Equal(Vector4.One, Swatch(root, 2).Tint);
        Assert.True(Swatch(root, 2).Visible);
    }

    [Fact]
    public void PartChange_RecomputesTheSwatchSet()
    {
        var (pal, clothing, colors) = MakeSources();
        (CharacterCreationAppearancePage page, FakeView view, UiElement root) = BuildPage(pal, clothing, colors);
        page.Refresh(view, view.Snapshot);
        Assert.Equal(ToVector4(HairColorA), Swatch(root, 0).Tint);

        UiButton eyesSpin = Assert.IsType<UiButton>(
            UiElement.FindDescendant(root, CharacterCreationAppearancePage.EyesSpinId));
        eyesSpin.OnClickAt!(180, 10);

        Assert.Equal(ToVector4(EyeColorA), Swatch(root, 0).Tint);
        Assert.Equal(ToVector4(EyeColorB), Swatch(root, 1).Tint);
    }

    [Fact]
    public void ColorChange_RetintsTheGradientDiscToTheNewlySelectedSwatch()
    {
        var (pal, clothing, colors) = MakeSources();
        (CharacterCreationAppearancePage page, FakeView view, UiElement root) = BuildPage(pal, clothing, colors);
        page.Refresh(view, view.Snapshot);
        Assert.Equal(Vector4.One, GradCircle(root).Tint);
        Assert.True(GradCircle(root).Visible);

        Swatch(root, 0).OnClick!();
        view.Snapshot = view.Snapshot with
        {
            Appearance = view.Snapshot.Appearance with { HairColor = 0u },
        };
        page.Refresh(view, view.Snapshot);
        Assert.Equal(ToVector4(HairColorA), GradCircle(root).Tint);
        Assert.True(GradCircle(root).Visible);

        Swatch(root, 1).OnClick!();
        view.Snapshot = view.Snapshot with
        {
            Appearance = view.Snapshot.Appearance with { HairColor = 1u },
        };
        page.Refresh(view, view.Snapshot);
        Assert.Equal(ToVector4(HairColorB), GradCircle(root).Tint);
    }

    [Fact]
    public void EyesPart_GradientDiscStaysVisibleButUntinted_ShowsPlugIconInstead()
    {
        var (pal, clothing, colors) = MakeSources();
        (CharacterCreationAppearancePage page, FakeView view, UiElement root) = BuildPage(pal, clothing, colors);
        view.Snapshot = view.Snapshot with
        {
            Appearance = view.Snapshot.Appearance with { EyeColor = 0u },
        };
        page.Refresh(view, view.Snapshot);

        UiButton eyesSpin = Assert.IsType<UiButton>(
            UiElement.FindDescendant(root, CharacterCreationAppearancePage.EyesSpinId));
        eyesSpin.OnClickAt!(180, 10);

        Assert.True(GradCircle(root).Visible);
        Assert.Equal(Vector4.One, GradCircle(root).Tint);
        Assert.Equal(ToVector4(EyeColorA), Swatch(root, 0).Tint);
    }

    [Fact]
    public void SkinPart_ShowsExactlyOneRepresentativeSwatchAndTintsTheDiscFromIt()
    {
        var (pal, clothing, colors) = MakeSources();
        (CharacterCreationAppearancePage page, FakeView view, UiElement root) = BuildPage(pal, clothing, colors);
        page.Refresh(view, view.Snapshot);

        UiButton skinSpin = Assert.IsType<UiButton>(
            UiElement.FindDescendant(root, CharacterCreationAppearancePage.SkinSpinId));
        skinSpin.OnClickAt!(10, 10);

        Assert.Equal(ToVector4(SkinColor), Swatch(root, 0).Tint);
        Assert.True(Swatch(root, 0).Visible);
        Assert.Equal(Vector4.One, Swatch(root, 1).Tint);
        Assert.True(Swatch(root, 1).Visible);
        Assert.Equal(ToVector4(SkinColor), GradCircle(root).Tint);
        Assert.True(GradCircle(root).Visible);
    }

    [Fact]
    public void HeadgearPart_ResolvesThroughTheEquippedGarment_UnsetShowsNoColor()
    {
        var (pal, clothing, colors) = MakeSources();
        (CharacterCreationAppearancePage page, FakeView view, UiElement root) = BuildPage(pal, clothing, colors);
        view.Snapshot = view.Snapshot with
        {
            Appearance = view.Snapshot.Appearance with { HeadgearStyle = 0u },
        };
        page.Refresh(view, view.Snapshot);

        UiButton headgearSpin = Assert.IsType<UiButton>(
            UiElement.FindDescendant(root, CharacterCreationAppearancePage.HeadgearSpinId));
        headgearSpin.OnClickAt!(180, 10);

        Assert.Equal(ToVector4(HeadgearColorA), Swatch(root, 0).Tint);

        view.Snapshot = view.Snapshot with
        {
            Appearance = view.Snapshot.Appearance with { HeadgearStyle = RuntimeCharacterCreationAppearance.Unset },
        };
        page.Refresh(view, view.Snapshot);
        Assert.Equal(Vector4.One, Swatch(root, 0).Tint);
        Assert.True(Swatch(root, 0).Visible);
    }

    [Fact]
    public void HeritageChange_RecomputesFromTheNewGendersOwnColorLists()
    {
        var (pal, clothing, colors) = MakeSources();
        (CharacterCreationAppearancePage page, FakeView view, UiElement root) = BuildPage(pal, clothing, colors);
        page.Refresh(view, view.Snapshot);
        Assert.Equal(ToVector4(HairColorA), Swatch(root, 0).Tint);

        const uint otherHairPalSetId = 0x0F00_00AAu;
        var otherHairColor = new ChargenSwatchRgb(200, 201, 202);
        pal.Add(otherHairPalSetId, 0x0400_00AAu);
        colors.Add(0x0400_00AAu, otherHairColor);
        const uint otherHeritageId = 2u;
        ChargenGenderOptions otherGender = MakeGender() with { HairColors = [otherHairPalSetId] };
        ChargenOptions otherOptions = MakeOptions(otherHeritageId, otherGender);
        var otherView = new FakeView(otherOptions, otherHeritageId);

        page.Refresh(otherView, otherView.Snapshot);

        Assert.Equal(ToVector4(otherHairColor), Swatch(root, 0).Tint);
    }

    [Fact]
    public void UnwiredSources_LeaveEverySwatchUntinted()
    {
        var view = new FakeView(MakeOptions(HeritageId, MakeGender()), HeritageId);
        var bindings = new CharacterCreationRuntimeBindings(
            () => view,
            _ => default,
            _ => default,
            _ => default,
            (_, _) => default,
            (_, _) => default,
            _ => default,
            _ => default,
            _ => default,
            _ => default,
            _ => default,
            () => { },
            SetAppearanceIndex: (_, _) => default);
        UiElement root = BuildPageRoot();
        var page = new CharacterCreationAppearancePage(root, bindings); // sources left null.

        page.Refresh(view, view.Snapshot);

        Assert.Equal(Vector4.One, GradCircle(root).Tint);
        for (int i = 0; i < CharacterCreationAppearancePage.SwatchIds.Length; i++)
            Assert.Equal(Vector4.One, Swatch(root, i).Tint);
    }
}
