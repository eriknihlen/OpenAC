using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class CharacterCreationLiveDatTests
{
    private static string DatDirectory =>
        Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    [InstalledDatFact]
    public void EnumTable5_ResolvesTheMasterShellRootAndChildren()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        uint layoutId = RetailDataIdResolver.Resolve(
            dats,
            CharacterCreationUiController.RootEnum,
            5u);
        Assert.NotEqual(0u, layoutId);
        Console.WriteLine(
            $"[CC4-DAT] category=5 enum=0x10000039 -> DID=0x{layoutId:X8}");

        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);
        Assert.Equal(
            CharacterCreationUiController.RootElementId,
            screen.Root.DatElementId);

        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats,
                layoutId,
                CharacterCreationUiController.RootElementId));
        Assert.Equal(800f, rootInfo.Width);
        Assert.Equal(600f, rootInfo.Height);

        Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.ProgressBarElementId));
        AssertButton(screen, CharacterCreationUiController.BackElementId);
        AssertButton(screen, CharacterCreationUiController.NextElementId);
        AssertButton(screen, CharacterCreationUiController.FinishElementId);
        AssertButton(screen, CharacterCreationUiController.HelpElementId);
        AssertButton(screen, CharacterCreationUiController.ExitElementId);
        AssertButton(screen, CharacterCreationUiController.RandomElementId);
        Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.MasterPageElementId));
        foreach (uint pageId in new[]
        {
            CharacterCreationUiController.HeritagePageElementId,
            CharacterCreationUiController.ProfessionPageElementId,
            CharacterCreationUiController.SkillsPageElementId,
            CharacterCreationUiController.AppearancePageElementId,
            CharacterCreationUiController.TownPageElementId,
            CharacterCreationUiController.SummaryPageElementId,
        })
        {
            Assert.IsAssignableFrom<UiElement>(screen.FindElement(pageId));
        }
        AssertButton(screen, CharacterCreationUiController.HeritageTabElementId);
        AssertButton(screen, CharacterCreationUiController.ProfessionTabElementId);
        AssertButton(screen, CharacterCreationUiController.SkillsTabElementId);
        AssertButton(screen, CharacterCreationUiController.AppearanceTabElementId);
        AssertButton(screen, CharacterCreationUiController.TownTabElementId);
        AssertButton(screen, CharacterCreationUiController.SummaryTabElementId);
    }

    [InstalledDatFact]
    public void MountsThroughTheControllerAgainstLiveResources()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats,
            CharacterCreationUiController.RootEnum,
            5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        var host = new UiRoot();
        var dialogs = MakeDialogFactory(dats, host);
        var bindings = new CharacterCreationRuntimeBindings(
            () => null,
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
            () => { });

        UiElement? ResolveTemplate(uint templateLayoutId, uint templateElementId) =>
            LayoutImporter.Import(
                dats, templateLayoutId, templateElementId, _ => (0u, 0, 0), null)?.Root;

        CharacterCreationUiController? controller =
            CharacterCreationUiController.CreateDetached(
                host, screen, ResolveTemplate, dialogs, bindings,
                new CharacterCreationUiController.DialogStrings(
                    "Are you sure?", "No name", "Unspent credits", "Randomize?", "Name too long"));
        Assert.NotNull(controller);
        controller!.AttachAndTick();
        controller.Dispose();
        dialogs.Dispose();
    }

    [InstalledDatFact]
    public void HeritagePage_HasAllThirteenRaceButtonsAndDescriptionText()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats,
            CharacterCreationUiController.RootEnum,
            5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement heritageRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.HeritagePageElementId));

        uint[] heritageButtonIds =
        [
            0x100003BFu, 0x100003C1u, 0x100003C2u, 0x100003C3u,
            0x10000590u, 0x100005A9u, 0x100005E8u, 0x100005F1u,
            0x100005C4u, 0x10000591u, 0x100005BFu, 0x100005C7u,
            0x100005C8u,
        ];
        foreach (uint buttonId in heritageButtonIds)
        {
            Assert.IsType<UiButton>(
                UiElement.FindDescendant(heritageRoot, buttonId));
        }
        Assert.IsType<UiText>(
            UiElement.FindDescendant(heritageRoot, 0x100003C4u));
    }

    [InstalledDatFact]
    public void HeritageDescription_MarginsMatchAuthoredInset_AndFirstLineOriginRespectsThem()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement heritageRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.HeritagePageElementId));
        UiText description = Assert.IsType<UiText>(
            UiElement.FindDescendant(heritageRoot, 0x100003C4u));

        Assert.Equal(9f, description.MarginLeft);
        Assert.Equal(26f, description.MarginRight);
        Assert.Equal(15f, description.MarginTop);
        Assert.Equal(15f, description.MarginBottom);

        float firstLineX = UiText.ContentOffsetX(
            description.Width, description.Padding,
            description.MarginLeft, description.MarginRight,
            lineWidth: 40f, centered: false, rightAligned: false);
        Assert.Equal(9f, firstLineX);
        Assert.NotEqual(0f, firstLineX);
    }

    [InstalledDatFact]
    public void ProfessionPage_HasTemplateButtonsSlidersAndDisplays()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats,
            CharacterCreationUiController.RootEnum,
            5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement professionRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.ProfessionPageElementId));

        uint[] templateButtonIds =
        [
            0x100003D9u, 0x100003DAu, 0x100003DBu,
            0x100003DCu, 0x100003DDu, 0x100003DEu, 0x100003DFu,
        ];
        foreach (uint buttonId in templateButtonIds)
        {
            Assert.IsType<UiButton>(
                UiElement.FindDescendant(professionRoot, buttonId));
        }

        uint[] sliderContainerIds =
        [
            0x100003E6u, 0x100003E7u, 0x100003E8u,
            0x100003E9u, 0x100003EAu, 0x100003EBu,
        ];
        foreach (uint containerId in sliderContainerIds)
        {
            UiElement container = Assert.IsAssignableFrom<UiElement>(
                UiElement.FindDescendant(professionRoot, containerId));
            Assert.IsType<UiScrollbar>(
                UiElement.FindDescendant(container, 0x100002EEu));
            Assert.IsType<UiField>(
                UiElement.FindDescendant(container, 0x100002EFu));
        }

        foreach (uint containerId in new[]
                 { 0x100003E2u, 0x100003E3u, 0x100003E4u, 0x100003E5u })
        {
            Assert.IsType<UiButton>(
                UiElement.FindDescendant(professionRoot, containerId));
        }
    }

    [InstalledDatFact]
    public void SkillsPage_HasListboxCreditsAndInfoPanes()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats,
            CharacterCreationUiController.RootEnum,
            5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement skillsRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.SkillsPageElementId));

        Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(skillsRoot, 0x100003F7u));
        Assert.IsType<UiButton>(
            UiElement.FindDescendant(skillsRoot, 0x100003F9u));
        Assert.IsType<UiText>(
            UiElement.FindDescendant(skillsRoot, 0x100003FBu));
        Assert.IsType<UiText>(
            UiElement.FindDescendant(skillsRoot, 0x100003FCu));
    }

    [InstalledDatFact]
    public void TownPage_HasFourStarterAreaButtons()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats,
            CharacterCreationUiController.RootEnum,
            5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement townRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.TownPageElementId));

        foreach (uint buttonId in new[] { 0x1000040Bu, 0x1000040Du, 0x1000040Eu, 0x1000040Fu })
        {
            Assert.IsType<UiButton>(
                UiElement.FindDescendant(townRoot, buttonId));
        }
        Assert.IsType<UiText>(
            UiElement.FindDescendant(townRoot, 0x10000409u));
    }

    [InstalledDatFact]
    public void TownPage_HoltburgButton_CaptionHonorsOwnRectAndRecolorsWhiteOnSelection()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement townRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.TownPageElementId));
        UiButton holtburg = AssertButton(townRoot, 0x1000040Du);

        Assert.Equal("Holtburg", holtburg.Label);
        Assert.Equal(UiButton.LabelAlignment.Center, holtburg.LabelAlign);
        Assert.Equal((0f, 4f, 100f, 37f), holtburg.LabelBox);

        holtburg.Selected = false;
        Assert.Equal(new Vector4(218f / 255f, 167f / 255f, 85f / 255f, 1f), holtburg.LabelColor);

        holtburg.Selected = true;
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), holtburg.LabelColor);
    }

    [InstalledDatFact]
    public void HeritagePage_Row_SelectedTogglesTheAuthoredRadioDotState()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement heritageRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.HeritagePageElementId));
        UiButton aluvian = AssertButton(heritageRoot, 0x100003BFu);

        Assert.Equal("Unselected", aluvian.ActiveState);
        aluvian.Selected = true;
        Assert.Equal("Selected", aluvian.ActiveState);
        Assert.Equal(RetailUiStateIds.Selected, aluvian.ActiveRetailStateId);
        aluvian.Selected = false;
        Assert.Equal("Unselected", aluvian.ActiveState);
    }

    [InstalledDatFact]
    public void ProfessionPage_TemplateButton_SelectedTogglesTheAuthoredIconState()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement professionRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.ProfessionPageElementId));
        UiButton template = AssertButton(professionRoot, 0x100003D9u);

        Assert.Equal("Unselected", template.ActiveState);
        template.Selected = true;
        Assert.Equal("Selected", template.ActiveState);
        template.Selected = false;
        Assert.Equal("Unselected", template.ActiveState);
    }

    [InstalledDatFact]
    public void AppearancePage_FaceClothesSubTabButtons_SelectedTogglesTheAuthoredState()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement appearanceRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.AppearancePageElementId));

        foreach (uint buttonId in new[]
                 {
                     CharacterCreationAppearancePage.FaceButtonId,
                     CharacterCreationAppearancePage.ClothesButtonId,
                 })
        {
            UiButton button = AssertButton(appearanceRoot, buttonId);
            Assert.Equal("Unselected", button.ActiveState);
            button.Selected = true;
            Assert.Equal("Selected", button.ActiveState);
            button.Selected = false;
            Assert.Equal("Unselected", button.ActiveState);
        }
    }

    [InstalledDatFact]
    public void AppearancePage_GenderButtons_SelectedTogglesTheAuthoredState()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement appearanceRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.AppearancePageElementId));

        foreach (uint buttonId in new[]
                 {
                     CharacterCreationAppearancePage.FemaleButtonId,
                     CharacterCreationAppearancePage.MaleButtonId,
                 })
        {
            UiButton button = AssertButton(appearanceRoot, buttonId);
            Assert.Equal("Unselected", button.ActiveState);
            button.Selected = true;
            Assert.Equal("Selected", button.ActiveState);
            button.Selected = false;
            Assert.Equal("Unselected", button.ActiveState);
        }
    }

    [InstalledDatFact]
    public void AppearancePage_SwatchOverlays_AllNinePresent()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement appearanceRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.AppearancePageElementId));

        foreach (uint overlayId in CharacterCreationAppearancePage.SwatchOverlayIds)
        {
            UiElement overlay = Assert.IsAssignableFrom<UiElement>(
                UiElement.FindDescendant(appearanceRoot, overlayId));
            Assert.True(overlay.Visible);
        }
    }

    [InstalledDatFact]
    public void AppearancePage_ZoomButtons_AuthorStandardNormalHighlightPair()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement appearanceRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.AppearancePageElementId));

        foreach (uint buttonId in new[]
                 {
                     CharacterCreationAppearancePage.ZoomInId,
                     CharacterCreationAppearancePage.ZoomOutId,
                 })
        {
            UiButton button = AssertButton(appearanceRoot, buttonId);
            Assert.Equal("Normal", button.ActiveState);
            Assert.True(button.TrySetRetailState(UiButtonStateMachine.Highlight));
            Assert.Equal("Highlight", button.ActiveState);
            Assert.True(button.TrySetRetailState(UiButtonStateMachine.Normal));
            Assert.Equal("Normal", button.ActiveState);
        }
    }

    [InstalledDatFact]
    public void RequiredChargenStringsResolve()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        var strings = new DatStringResolver(dats);
        const uint table = 0x23000002u;

        string[] keys =
        [
            "ID_CharGen_ExitWarning",
            "ID_CharGen_Heritage_StartingSkills_Header",
            "ID_CharGen_Heritage_StartingSkills",
            "ID_CharGen_Heritage_BonusSkills_Trained_Header",
            "ID_CharGen_AluvianText_BonusSkills_Trained",
            "ID_CharGen_GaruText_BonusSkills_Trained",
            "ID_CharGen_ShoText_BonusSkills_Trained",
            "ID_CharGen_ViaText_BonusSkills_Trained",
            "ID_CharGen_ShadText_BonusSkills_Trained",
            "ID_CharGen_GearText_BonusSkills_Trained",
            "ID_CharGen_AunTText_BonusSkills_Trained",
            "ID_CharGen_EmpText_BonusSkills_Trained",
            "ID_CharGen_UndText_BonusSkills_Trained",
            "ID_CharGen_TownHowTo",
            "ID_CharGen_HoltText",
            "ID_CharGen_ShoushiText",
            "ID_CharGen_YaraqText",
            "ID_CharGen_SanamarText",
            // GF-3: Profession template description keys.
            "ID_CharGen_CustomText",
            "ID_CharGen_BowText",
            "ID_CharGen_SwashText",
            "ID_CharGen_LifeText",
            "ID_CharGen_WarText",
            "ID_CharGen_WayText",
            "ID_CharGen_SoldierText",
            "ID_CharGen_HairStyle",
            "ID_CharGen_Eyes",
            "ID_CharGen_Skin",
            "ID_CharGen_GearText_HairButton",
            "ID_CharGen_GearText_EyesButton",
            "ID_CharGen_GearText_SkinButton",
            "ID_CharGen_OlthoiText_HairButton",
            "ID_CharGen_OlthoiText_EyesButton",
            "ID_CharGen_OlthoiText_SkinButton",
            "ID_CharGen_SummaryHowTo",
            "ID_CharGen_SummaryHowToEnd",
            "ID_CharGen_AluMaleNames",
            "ID_CharGen_AluFemaleNames",
            "ID_CharGen_GharuMaleNames",
            "ID_CharGen_GharuFemaleNames",
            "ID_CharGen_ShoMaleNames",
            "ID_CharGen_ShoFemaleNames",
            "ID_CharGen_ViaMaleNames",
            "ID_CharGen_ViaFemaleNames",
        ];
        foreach (string key in keys)
        {
            string? resolved = strings.Resolve(table, DatStringResolver.ComputeHash(key));
            Assert.True(resolved is not null, $"missing string: {key}");
        }
    }

    [InstalledDatFact]
    public void AppearancePage_HasGenderChoiceSpinsSwatchesShadeAndViewport()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement appearanceRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.AppearancePageElementId));

        AssertButton(appearanceRoot, CharacterCreationAppearancePage.FemaleButtonId);
        AssertButton(appearanceRoot, CharacterCreationAppearancePage.MaleButtonId);
        AssertButton(appearanceRoot, CharacterCreationAppearancePage.FaceButtonId);
        AssertButton(appearanceRoot, CharacterCreationAppearancePage.ClothesButtonId);
        Assert.IsAssignableFrom<UiElement>(
            UiElement.FindDescendant(appearanceRoot, CharacterCreationAppearancePage.FaceChoicesId));
        Assert.IsAssignableFrom<UiElement>(
            UiElement.FindDescendant(appearanceRoot, CharacterCreationAppearancePage.ClothesChoicesId));

        foreach (uint spinId in new[]
                 {
                     CharacterCreationAppearancePage.HairSpinId,
                     CharacterCreationAppearancePage.EyesSpinId,
                     CharacterCreationAppearancePage.NoseSpinId,
                     CharacterCreationAppearancePage.MouthSpinId,
                     CharacterCreationAppearancePage.SkinSpinId,
                     CharacterCreationAppearancePage.HeadgearSpinId,
                     CharacterCreationAppearancePage.ShirtSpinId,
                     CharacterCreationAppearancePage.TrousersSpinId,
                     CharacterCreationAppearancePage.FootwearSpinId,
                 })
        {
            UiButton spin = AssertButton(appearanceRoot, spinId);
            Assert.True(spin.ToggleBehavior, $"spin 0x{spinId:X8} must author ToggleBehavior for the current-part highlight to work.");
            Assert.True(
                spin.TrySetRetailState(UiButtonStateMachine.Highlight),
                $"spin 0x{spinId:X8} must accept a Highlight state request.");
            Assert.Equal("Highlight", spin.ActiveState);

            Assert.Equal(new Vector4(255f / 255f, 221f / 255f, 131f / 255f, 1f), spin.LabelColor);
            Assert.True(spin.Outline, $"spin 0x{spinId:X8} must outline its label in the Highlight state.");

            Assert.True(spin.TrySetRetailState(UiButtonStateMachine.Normal));
            Assert.Equal(new Vector4(218f / 255f, 167f / 255f, 85f / 255f, 1f), spin.LabelColor);
            Assert.False(spin.Outline, $"spin 0x{spinId:X8} must not outline its label in the Normal state.");
        }

        foreach (uint swatchId in CharacterCreationAppearancePage.SwatchIds)
            AssertButton(appearanceRoot, swatchId);
        UiScrollbar shadeScroll = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(appearanceRoot, CharacterCreationAppearancePage.ShadeScrollId));
        Assert.False(shadeScroll.Horizontal);
        Assert.True(shadeScroll.Height > shadeScroll.Width);
        Assert.IsType<UiDatElement>(
            UiElement.FindDescendant(appearanceRoot, CharacterCreationAppearancePage.GradCircleId));

        AssertButton(appearanceRoot, CharacterCreationAppearancePage.RotateClockwiseId);
        AssertButton(appearanceRoot, CharacterCreationAppearancePage.RotateCounterClockwiseId);
        AssertButton(appearanceRoot, CharacterCreationAppearancePage.ZoomInId);
        AssertButton(appearanceRoot, CharacterCreationAppearancePage.ZoomOutId);

        Assert.IsType<UiViewport>(
            UiElement.FindDescendant(appearanceRoot, CharacterCreationAppearancePage.ViewportId));
    }

    [InstalledDatFact]
    public void AppearancePage_SpinArrowGeometryIsUniformAcrossAllNineSpins()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));
        ElementInfo? appearanceInfo = FindInfo(
            rootInfo, CharacterCreationUiController.AppearancePageElementId);
        Assert.NotNull(appearanceInfo);

        foreach (uint spinId in new[]
                 {
                     CharacterCreationAppearancePage.HairSpinId,
                     CharacterCreationAppearancePage.EyesSpinId,
                     CharacterCreationAppearancePage.NoseSpinId,
                     CharacterCreationAppearancePage.MouthSpinId,
                     CharacterCreationAppearancePage.SkinSpinId,
                     CharacterCreationAppearancePage.HeadgearSpinId,
                     CharacterCreationAppearancePage.ShirtSpinId,
                     CharacterCreationAppearancePage.TrousersSpinId,
                     CharacterCreationAppearancePage.FootwearSpinId,
                 })
        {
            ElementInfo? spin = FindInfo(appearanceInfo!, spinId);
            Assert.NotNull(spin);
            Assert.Equal(200f, spin!.Width);

            ElementInfo? decrement = spin.Children.FirstOrDefault(c => c.Id == 0x1000030Au);
            ElementInfo? increment = spin.Children.FirstOrDefault(c => c.Id == 0x1000030Bu);
            Assert.NotNull(decrement);
            Assert.NotNull(increment);
            Assert.Equal(80f, decrement!.X);
            Assert.Equal(127f, increment!.X);
            Assert.Equal(47f, decrement.Width);
            Assert.Equal(47f, increment.Width);
        }
    }

    private static ElementInfo? FindInfo(ElementInfo node, uint id)
    {
        if (node.Id == id) return node;
        foreach (ElementInfo child in node.Children)
        {
            ElementInfo? found = FindInfo(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static RetailDialogFactory MakeDialogFactory(IDatReaderWriter dats, UiRoot host)
    {
        uint dialogDid = RetailDataIdResolver.Resolve(dats, 2u, 5u);
        ImportedLayout? CreateLayout(RetailDialogType type)
        {
            uint rootElementId = RetailDialogFactory.RootElementId(type);
            return rootElementId == 0u
                ? null
                : LayoutImporter.Import(
                    dats, dialogDid, rootElementId, _ => (0u, 0, 0), null);
        }
        return new RetailDialogFactory(host, CreateLayout);
    }

    [InstalledDatFact]
    public void SummaryPage_HasNameFieldListboxTemplatesAndViewport()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats,
            CharacterCreationUiController.RootEnum,
            5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement summaryRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.SummaryPageElementId));

        Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(summaryRoot, CharacterCreationSummaryPage.ScrollId));
        Assert.IsType<UiField>(
            UiElement.FindDescendant(summaryRoot, CharacterCreationSummaryPage.NameTextId));
        Assert.IsType<UiText>(
            UiElement.FindDescendant(summaryRoot, CharacterCreationSummaryPage.HowToTextId));
        Assert.IsType<UiViewport>(
            UiElement.FindDescendant(summaryRoot, CharacterCreationSummaryPage.ViewportId));

        UiTemplateListBox list = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(summaryRoot, CharacterCreationSummaryPage.ListBoxId));
        Assert.Equal(3, list.Templates.Count);

        UiElement? ResolveRow(int index) =>
            LayoutImporter.Import(
                dats,
                list.Templates[index].TemplateLayoutId,
                list.Templates[index].TemplateElementId,
                _ => (0u, 0, 0),
                null)?.Root;

        UiElement lineRow = Assert.IsAssignableFrom<UiElement>(ResolveRow(0));
        Assert.IsType<UiText>(UiElement.FindDescendant(lineRow, 0x100002F9u));

        UiElement headerRow = Assert.IsAssignableFrom<UiElement>(ResolveRow(1));
        Assert.IsType<UiText>(UiElement.FindDescendant(headerRow, 0x100000FEu));

        UiElement pairRow = Assert.IsAssignableFrom<UiElement>(ResolveRow(2));
        Assert.IsType<UiText>(UiElement.FindDescendant(pairRow, 0x100002FCu));
        Assert.IsType<UiText>(UiElement.FindDescendant(pairRow, 0x100002FDu));
    }

    [InstalledDatFact]
    public void SummaryNameField_AuthorsNoP0x17OnAnyState()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));
        ElementInfo nameField = Assert.IsType<ElementInfo>(
            FindInfo(rootInfo, CharacterCreationSummaryPage.NameTextId));

        Assert.False(
            nameField.TryGetEffectiveProperty(0x17u, out _),
            "the name field must not author a P0x17 caption on its effective "
            + "default state — if this starts failing, the DAT now carries an "
            + "authored placeholder and R2-8 should be revisited as a real fix.");
        foreach (var (stateId, state) in nameField.States)
        {
            Assert.False(
                state.Properties.Values.TryGetValue(0x17u, out var stateCaption)
                    && stateCaption.Kind == UiPropertyKind.StringInfo,
                $"the name field's state 0x{stateId:X} ('{state.Name}') must not "
                + "author a P0x17 caption either.");
        }

        Assert.True(nameField.TryGetEffectiveProperty(0x49u, out var tooltip));
        Assert.Equal(UiPropertyKind.StringInfo, tooltip.Kind);
        var strings = new DatStringResolver(dats);
        Assert.Equal(
            "Your name can be 32 characters long and cannot contain numbers or symbols.",
            strings.Resolve(tooltip.StringInfoValue));
    }

    [InstalledDatFact]
    public void SkillTable_MinLevelDistribution_NeverExceedsTrained()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        DatReaderWriter.DBObjs.SkillTable? skillTable =
            dats.Get<DatReaderWriter.DBObjs.SkillTable>(0x0E000004u);
        Assert.NotNull(skillTable);

        var byMinLevel = skillTable!.Skills.Values
            .GroupBy(skill => skill.MinLevel)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine(
            "[CC5-R3-DAT] SkillTable MinLevel distribution (level=count): "
            + string.Join(", ", byMinLevel.Select(p => $"{p.Key}={p.Value}"))
            + $" (total skills: {skillTable.Skills.Count})");

        Assert.True(
            byMinLevel.Keys.All(minLevel => minLevel <= 2),
            "A skill's MinLevel exceeded 2 (Trained) in the installed DAT — "
            + "RetailSkillFormula.CalculateChargenScore's Trained/Specialized "
            + "gate argument needs re-verification for this skill.");
    }

    [InstalledDatFact]
    public void SummaryPage_NonAdminNonEnvoyLabels_AuthorInvisibleTrue()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));

        foreach (uint targetId in new[] { 0x10000403u, 0x10000494u })
        {
            ElementInfo? found = FindInfo(rootInfo, targetId);
            Assert.NotNull(found);
            Assert.True(
                found!.Invisible,
                $"element 0x{targetId:X8} must author dat property 0x3B (Invisible) = true.");
        }
    }

    [InstalledDatFact]
    public void SummaryPage_NonAdminNonEnvoyLabels_HiddenAfterControllerMount()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        var host = new UiRoot();
        var dialogs = MakeDialogFactory(dats, host);
        var bindings = new CharacterCreationRuntimeBindings(
            () => null,
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
            () => { });

        UiElement? ResolveTemplate(uint templateLayoutId, uint templateElementId) =>
            LayoutImporter.Import(
                dats, templateLayoutId, templateElementId, _ => (0u, 0, 0), null)?.Root;

        CharacterCreationUiController? controller =
            CharacterCreationUiController.CreateDetached(
                host, screen, ResolveTemplate, dialogs, bindings,
                new CharacterCreationUiController.DialogStrings(
                    "Are you sure?", "No name", "Unspent credits", "Randomize?", "Name too long"));
        Assert.NotNull(controller);

        foreach (uint targetId in new[] { 0x10000403u, 0x10000494u })
        {
            UiElement found = Assert.IsAssignableFrom<UiElement>(
                UiElement.FindDescendant(controller!.Root, targetId));
            Assert.True(found.AuthoredInvisible, $"0x{targetId:X8} must carry AuthoredInvisible.");
            Assert.False(found.Visible, $"0x{targetId:X8} must be hidden after mount.");
        }

        controller!.Dispose();
        dialogs.Dispose();
    }

    [InstalledDatFact]
    public void SkillsPage_RealRowTemplate_HasNameLevelCostTextAndArrowButtons()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement skillsRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.SkillsPageElementId));
        UiTemplateListBox list = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(skillsRoot, 0x100003F7u));

        Assert.Equal(2, list.Templates.Count);

        UiTemplateListEntry realRowTemplate = list.Templates[1];
        Assert.Equal(0x100002FFu, realRowTemplate.TemplateElementId);

        UiElement? row = LayoutImporter.Import(
            dats,
            realRowTemplate.TemplateLayoutId,
            realRowTemplate.TemplateElementId,
            _ => (0u, 0, 0),
            null)?.Root;
        UiElement realRow = Assert.IsAssignableFrom<UiElement>(row);
        Assert.IsNotType<UiButton>(realRow);

        Assert.IsType<UiText>(UiElement.FindDescendant(realRow, 0x10000301u));
        Assert.IsType<UiText>(UiElement.FindDescendant(realRow, 0x10000302u));
        Assert.IsType<UiText>(UiElement.FindDescendant(realRow, 0x10000303u));
        Assert.IsType<UiText>(UiElement.FindDescendant(realRow, 0x10000306u));
        Assert.IsType<UiButton>(UiElement.FindDescendant(realRow, 0x10000304u));
        Assert.IsType<UiButton>(UiElement.FindDescendant(realRow, 0x10000305u));

        Assert.Equal(0x100002F4u, list.Templates[0].TemplateElementId);
    }

    [InstalledDatFact]
    public void SkillsPage_Listbox_HasAScrollbarLink_AndHeaderTemplateHasACaptionChild()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement skillsRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.SkillsPageElementId));
        UiTemplateListBox list = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(skillsRoot, 0x100003F7u));

        Console.WriteLine(
            $"[CC-Batch-F-DAT] Skills listbox ScrollbarElementId=0x{list.ScrollbarElementId:X8}");
        Assert.Equal(0x100003F8u, list.ScrollbarElementId);
        Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(skillsRoot, list.ScrollbarElementId));

        UiTemplateListEntry headerTemplate = list.Templates[0];
        Assert.Equal(0x100002F4u, headerTemplate.TemplateElementId);
        UiElement? headerRow = LayoutImporter.Import(
            dats,
            headerTemplate.TemplateLayoutId,
            headerTemplate.TemplateElementId,
            _ => (0u, 0, 0),
            null)?.Root;
        UiElement realHeaderRow = Assert.IsAssignableFrom<UiElement>(headerRow);
        Assert.IsType<UiButton>(UiElement.FindDescendant(realHeaderRow, 0x100002F6u));
    }

    [InstalledDatFact]
    public void MessageDialogCatalog_PopupMessageAndOkButton_AuthorNonzeroGeometry()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint dialogDid = RetailDataIdResolver.Resolve(dats, 2u, 5u);

        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, dialogDid, 0x24u));
        Assert.Equal(800f, rootInfo.Width);
        Assert.Equal(600f, rootInfo.Height);

        ElementInfo popup = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x3Du));
        Assert.True(popup.Width > 0f && popup.Height > 0f);

        ElementInfo message = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x3Eu));
        Assert.True(message.Width > 0f && message.Height > 0f);

        ElementInfo okButton = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x26u));
        Assert.True(okButton.Width > 0f && okButton.Height > 0f);
    }

    [InstalledDatFact]
    public void ProfessionAndSkillsDisplayButtons_OwnCaptionPlusOneMediaLessValueChild()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));

        (uint Button, uint ValueChild)[] shapes =
        [
            (0x100003E2u, 0x100002F1u),
            (0x100003E3u, 0x100002F3u), // Profession health
            (0x100003E4u, 0x100002F3u), // Profession stamina
            (0x100003E5u, 0x100002F3u), // Profession mana
            (0x100003F9u, 0x100002F3u),
        ];
        foreach ((uint buttonId, uint valueChildId) in shapes)
        {
            ElementInfo button = Assert.IsType<ElementInfo>(FindInfo(rootInfo, buttonId));
            Assert.Equal(1u, button.Type);
            Assert.True(
                button.TryGetEffectiveProperty(0x17u, out UiPropertyValue caption)
                && caption.Kind == UiPropertyKind.StringInfo,
                $"button 0x{buttonId:X8} must author its own P0x17 caption.");
            ElementInfo singleChild = Assert.Single(button.Children);
            Assert.Equal(valueChildId, singleChild.Id);
            Assert.Equal(12u, singleChild.Type);
            Assert.Empty(singleChild.StateMedia);
        }
    }

    [InstalledDatFact]
    public void ProfessionPage_SliderContainers_HaveNameLabelButtonChild()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);
        UiElement professionRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.ProfessionPageElementId));

        foreach (uint containerId in new[]
                 {
                     0x100003E6u, 0x100003E7u, 0x100003E8u,
                     0x100003E9u, 0x100003EAu, 0x100003EBu,
                 })
        {
            UiElement container = Assert.IsAssignableFrom<UiElement>(
                UiElement.FindDescendant(professionRoot, containerId));
            Assert.IsType<UiButton>(UiElement.FindDescendant(container, 0x100002EDu));
        }
    }

    [InstalledDatFact]
    public void HeritageAndProfessionBackdrops_AuthorEveryRetailState()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));

        ElementInfo heritageBackdrop = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x100003BEu));
        uint[] heritageStates =
        [
            0x10000021u, 0x10000022u, 0x10000023u, 0x10000024u, 0x10000058u,
            0x10000059u, 0x1000005Au, 0x1000005Bu, 0x1000005Cu, 0x1000005Du,
            0x1000005Eu, 0x1000005Fu, 0x10000060u,
        ];
        foreach (uint stateId in heritageStates)
            Assert.True(heritageBackdrop.States.ContainsKey(stateId), $"heritage backdrop missing state 0x{stateId:X8}");

        ElementInfo professionBackdrop = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x100003D8u));
        uint[] professionStates =
        [
            0x1000002Bu, 0x1000002Cu, 0x1000002Du,
            0x1000002Eu, 0x1000002Fu, 0x10000030u, 0x10000031u,
        ];
        foreach (uint stateId in professionStates)
            Assert.True(professionBackdrop.States.ContainsKey(stateId), $"profession backdrop missing state 0x{stateId:X8}");
    }

    [InstalledDatFact]
    public void DescriptionTextboxes_ShareTheSameGoldFrameChildTemplate()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));

        uint[] frameChildIds =
        [
            0x100002DEu, 0x100002DFu, 0x100002E0u, 0x100002E1u,
            0x100000E8u, 0x100002E2u, 0x100002E3u, 0x100000EAu,
        ];
        foreach (uint boxId in new[] { 0x100003E0u, 0x10000409u, 0x10000404u })
        {
            ElementInfo box = Assert.IsType<ElementInfo>(FindInfo(rootInfo, boxId));
            Assert.Equal(12u, box.Type);
            foreach (uint frameChildId in frameChildIds)
                Assert.Contains(box.Children, c => c.Id == frameChildId);
        }
        ElementInfo summaryHowTo = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x10000404u));
        Assert.Contains(summaryHowTo.Children, c => c.Id == 0x100002E7u);
    }

    [InstalledDatFact]
    public void DescriptionTextboxes_FramesAndScrollbarBuildAsRealWidgets()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        uint[] frameChildIds =
        [
            0x100002DEu, 0x100002DFu, 0x100002E0u, 0x100002E1u,
            0x100000E8u, 0x100002E2u, 0x100002E3u, 0x100000EAu,
        ];
        foreach (uint boxId in new[] { 0x100003E0u, 0x10000409u, 0x10000404u })
        {
            UiText box = Assert.IsType<UiText>(screen.FindElement(boxId));
            foreach (uint frameChildId in frameChildIds)
                Assert.NotNull(UiElement.FindDescendant(box, frameChildId));
        }

        UiText summaryHowTo = Assert.IsType<UiText>(screen.FindElement(0x10000404u));
        Assert.IsType<UiScrollbar>(UiElement.FindDescendant(summaryHowTo, 0x100002E7u));
    }

    [InstalledDatFact]
    public void HeritageDescription_ScrollbarBuildsAndLinksToTextScroll()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement heritageRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.HeritagePageElementId));
        UiText description = Assert.IsType<UiText>(
            UiElement.FindDescendant(heritageRoot, 0x100003C4u));
        UiScrollbar scroll = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(description, 0x100002E7u));

        var host = new UiRoot();
        var dialogs = MakeDialogFactory(dats, host);
        var bindings = new CharacterCreationRuntimeBindings(
            () => null,
            _ => default, _ => default, _ => default, (_, _) => default, (_, _) => default,
            _ => default, _ => default, _ => default, _ => default, _ => default, () => { });
        UiElement? ResolveTemplate(uint templateLayoutId, uint templateElementId) =>
            LayoutImporter.Import(
                dats, templateLayoutId, templateElementId, _ => (0u, 0, 0), null)?.Root;

        CharacterCreationUiController? controller =
            CharacterCreationUiController.CreateDetached(
                host, screen, ResolveTemplate, dialogs, bindings,
                new CharacterCreationUiController.DialogStrings(
                    "Are you sure?", "No name", "Unspent credits", "Randomize?", "Name too long"));
        Assert.NotNull(controller);
        controller!.AttachAndTick();

        Assert.Same(description.Scroll, scroll.Model);

        controller.Dispose();
        dialogs.Dispose();
    }

    [InstalledDatFact]
    public void SummaryListbox_ScrollbarBuildsAndLinksToListboxScroll()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiElement summaryRoot = Assert.IsAssignableFrom<UiElement>(
            screen.FindElement(CharacterCreationUiController.SummaryPageElementId));
        UiTemplateListBox list = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(summaryRoot, CharacterCreationSummaryPage.ListBoxId));
        Assert.Equal(CharacterCreationSummaryPage.ScrollId, list.ScrollbarElementId);
        UiScrollbar overviewScroll = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(summaryRoot, CharacterCreationSummaryPage.ScrollId));

        var host = new UiRoot();
        var dialogs = MakeDialogFactory(dats, host);
        var bindings = new CharacterCreationRuntimeBindings(
            () => null,
            _ => default, _ => default, _ => default, (_, _) => default, (_, _) => default,
            _ => default, _ => default, _ => default, _ => default, _ => default, () => { });
        UiElement? ResolveTemplate(uint templateLayoutId, uint templateElementId) =>
            LayoutImporter.Import(
                dats, templateLayoutId, templateElementId, _ => (0u, 0, 0), null)?.Root;

        CharacterCreationUiController? controller =
            CharacterCreationUiController.CreateDetached(
                host, screen, ResolveTemplate, dialogs, bindings,
                new CharacterCreationUiController.DialogStrings(
                    "Are you sure?", "No name", "Unspent credits", "Randomize?", "Name too long"));
        Assert.NotNull(controller);
        controller!.AttachAndTick();

        Assert.Same(list.Scroll, overviewScroll.Model);

        controller.Dispose();
        dialogs.Dispose();
    }

    [InstalledDatFact]
    public void SummaryHowToText_Aluvian_WithCorrectMarginInsetWidth_Overflows()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        var strings = new DatStringResolver(dats);
        const uint table = 0x23000002u;
        string? howTo = strings.Resolve(table, DatStringResolver.ComputeHash("ID_CharGen_SummaryHowTo"));
        string? names = strings.Resolve(table, DatStringResolver.ComputeHash("ID_CharGen_AluMaleNames"));
        string? howToEnd = strings.Resolve(table, DatStringResolver.ComputeHash("ID_CharGen_SummaryHowToEnd"));
        Assert.NotNull(howTo);
        Assert.NotNull(names);
        Assert.NotNull(howToEnd);
        string composed = howTo + names + howToEnd;

        Assert.True(dats.TryGet<DatReaderWriter.DBObjs.Font>(0x40000009u, out var font) && font is not null);
        var glyphs = new Dictionary<char, DatReaderWriter.Types.FontCharDesc>(font!.CharDescs.Count);
        foreach (var cd in font.CharDescs) glyphs[(char)cd.Unicode] = cd;
        var datFont = new UiDatFont(0, 0, 0, 0, 0, 0, font.MaxCharHeight, font.BaselineOffset, glyphs);

        var target = new UiText
        {
            Width = 247f,
            Height = 380f,
            DatFont = datFont,
            MarginLeft = 9f,
            MarginRight = 26f,
            MarginTop = 15f,
            MarginBottom = 15f,
        };
        var segments = new[] { new DatRichText.Segment(composed, Vector4.One) };
        var lines = DatRichText.Compose(target, segments);

        float viewHeight = target.Height - target.Padding - target.MarginTop - target.Padding - target.MarginBottom;
        float contentHeight = lines.Count * datFont.LineHeight;

        Assert.True(
            contentHeight > viewHeight,
            $"expected the correctly-inset composition ({lines.Count} lines, "
            + $"{contentHeight}px) to overflow the {viewHeight}px view — if it "
            + "doesn't, the how-to scrollbar's thumb has nothing to gate on "
            + "regardless of the R2-1 margin fix");
    }

    [InstalledDatFact]
    public void CoordinationAttributeLabel_AuthorsOneLineTrue()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));

        ElementInfo coordContainer = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x100003E8u));
        ElementInfo coordLabel = Assert.IsType<ElementInfo>(FindInfo(coordContainer, 0x100002EDu));

        Assert.True(coordLabel.TryGetEffectiveBool(0x20u, out bool oneLine) && oneLine);
    }

    [InstalledDatFact]
    public void SkillsCreditsButton_CaptionFitsFullWidth_ValueChildStartsAtMidpoint()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));
        var strings = new DatStringResolver(dats);

        ElementInfo skillsCredits = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x100003F9u));
        Assert.True(skillsCredits.TryGetEffectiveProperty(0x17u, out var caption));
        string? captionText = strings.Resolve(caption.StringInfoValue);
        Assert.Equal("Available Skill Credits", captionText);

        uint fontDid = skillsCredits.FontDid != 0 ? skillsCredits.FontDid : rootInfo.FontDid;
        Assert.True(dats.TryGet<DatReaderWriter.DBObjs.Font>(fontDid, out var font) && font is not null);
        var glyphs = new Dictionary<char, DatReaderWriter.Types.FontCharDesc>(font!.CharDescs.Count);
        foreach (var cd in font.CharDescs) glyphs[(char)cd.Unicode] = cd;
        var datFont = new UiDatFont(0, 0, 0, 0, 0, 0, font.MaxCharHeight, font.BaselineOffset, glyphs);
        float measured = datFont.MeasureWidth(captionText!);

        Assert.True(
            measured < skillsCredits.Width,
            $"caption measured {measured}px must fit the button's own full {skillsCredits.Width}px width");

        ElementInfo valueChild = Assert.Single(skillsCredits.Children);
        Assert.Equal(116f, valueChild.X);
    }

    [InstalledDatFact]
    public void SkillsCreditsButton_ValueBoxReflowsPastCaption_HealthValueBoxUnchanged()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiButton skillsCredits = Assert.IsType<UiButton>(screen.FindElement(0x100003F9u));
        Assert.Equal((197f, 0f, 34f, 28f), skillsCredits.ValueBox);
        Assert.Equal(UiButton.LabelAlignment.Right, skillsCredits.ValueAlign);

        UiButton health = Assert.IsType<UiButton>(screen.FindElement(0x100003E3u));
        Assert.Equal((116f, 0f, 34f, 28f), health.ValueBox);
    }

    [InstalledDatFact]
    public void SkillsInfoBoxTitleAndDescription_AuthoredBoxesOverlap()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));

        ElementInfo title = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x100003FBu));
        ElementInfo description = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x100003FCu));

        Assert.False(title.TryGetEffectiveProperty(0x15u, out _));
        Assert.False(description.TryGetEffectiveProperty(0x15u, out _));

        float titleBottom = title.Y + title.Height;
        float descriptionTop = description.Y;
        Assert.True(
            titleBottom > descriptionTop,
            $"expected the title's own box (Y={title.Y} H={title.Height}, bottom={titleBottom}) to "
            + $"overlap the description's box (Y={description.Y}) — if it no longer does, the "
            + "VerticalJustify.Top override in CharacterCreationSkillsPage may no longer be needed");
        Assert.True(description.Y > title.Y);
    }

    [InstalledDatFact]
    public void SkillsInfoBoxFrame_ShorterThanDescriptionPane()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));

        ElementInfo frame = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x100003FAu));
        ElementInfo description = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x100003FCu));

        float frameBottom = frame.Y + frame.Height;
        float paneBottom = description.Y + description.Height;
        Assert.True(
            frameBottom < paneBottom,
            $"expected the frame's own bottom (Y={frame.Y} H={frame.Height}, bottom={frameBottom}) to sit "
            + $"ABOVE the description pane's own raw bottom (Y={description.Y} H={description.Height}, "
            + $"bottom={paneBottom}) — if it no longer does, CharacterCreationSkillsPage's Height clamp "
            + "may no longer be needed");
    }

    [InstalledDatFact]
    public void SkillsListboxScrollbar_SingleSpriteThumbShape_BuildsWithNonZeroThumbSprite()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiScrollbar scrollbar = Assert.IsType<UiScrollbar>(screen.FindElement(0x100003F8u));
        Assert.False(scrollbar.Horizontal);
        Assert.NotEqual(0u, scrollbar.ThumbSprite);
        Assert.Equal(0u, scrollbar.ThumbTopSprite);
        Assert.Equal(0u, scrollbar.ThumbBotSprite);

        // Authored as a fixed 39 px thumb (not proportional) with a 37 px minimum.
        Assert.False(scrollbar.Proportional);
        Assert.Equal(39f, scrollbar.ThumbExtent);
        Assert.Equal(37f, scrollbar.MinThumbExtent);

        UiScrollbar heritage = Assert.IsType<UiScrollbar>(screen.FindElement(0x100002E7u));
        Assert.False(heritage.Proportional);
        Assert.Equal(39f, heritage.ThumbExtent);
    }

    [InstalledDatFact]
    public void ColorSpotAndGradDiskResources_ResolveToExpectedDimensions()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        (uint enumId, int width, int height)[] expected =
        [
            (0x1000000Du, 37, 44), // spot (active)
            (0x1000000Fu, 37, 44), // blank (blocked)
            (0x1000000Eu, 110, 112), // gradDisk
            (0x10000010u, 110, 112), // gradPlug
        ];
        foreach ((uint enumId, int width, int height) in expected)
        {
            uint did = RetailDataIdResolver.Resolve(dats, enumId, 7u);
            Assert.NotEqual(0u, did);
            Assert.True(dats.TryGet<DatReaderWriter.DBObjs.RenderSurface>(did, out var rs) && rs is not null);
            Assert.Equal(width, (int)rs!.Width);
            Assert.Equal(height, (int)rs.Height);
        }

        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats, layoutId, CharacterCreationUiController.RootElementId));

        uint spotDid = RetailDataIdResolver.Resolve(dats, 0x1000000Du, 7u);
        uint gradDiskDid = RetailDataIdResolver.Resolve(dats, 0x1000000Eu, 7u);

        ElementInfo spotElement = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x1000030Fu));
        Assert.Equal(37f, spotElement.Width);
        Assert.Equal(44f, spotElement.Height);
        var spotDirectState = Assert.Single(spotElement.StateMedia);
        Assert.Equal(string.Empty, spotDirectState.Key);
        Assert.Equal(spotDid, spotDirectState.Value.File);

        ElementInfo gradCircleElement = Assert.IsType<ElementInfo>(FindInfo(rootInfo, 0x1000030Eu));
        Assert.Equal(110f, gradCircleElement.Width);
        Assert.Equal(112f, gradCircleElement.Height);
        var gradDirectState = Assert.Single(gradCircleElement.StateMedia);
        Assert.Equal(string.Empty, gradDirectState.Key);
        Assert.Equal(gradDiskDid, gradDirectState.Value.File);
    }

    [InstalledDatFact]
    public void SpotTemplate_HasBlackCenterAndNonBlackRing_BlankTemplateHasNeitherBlack()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint spotDid = RetailDataIdResolver.Resolve(dats, 0x1000000Du, 7u);
        uint blankDid = RetailDataIdResolver.Resolve(dats, 0x1000000Fu, 7u);
        Assert.True(dats.TryGet<DatReaderWriter.DBObjs.RenderSurface>(spotDid, out var spotRs) && spotRs is not null);
        Assert.True(dats.TryGet<DatReaderWriter.DBObjs.RenderSurface>(blankDid, out var blankRs) && blankRs is not null);

        var spot = AcDream.Core.Textures.SurfaceDecoder.DecodeRenderSurface(spotRs!);
        var blank = AcDream.Core.Textures.SurfaceDecoder.DecodeRenderSurface(blankRs!);

        (int black, int nonBlackOpaque, int total) CountPixels(byte[] rgba)
        {
            int black = 0, nonBlackOpaque = 0, total = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                byte a = rgba[i + 3];
                if (a < 10) continue;
                total++;
                if (rgba[i] == 0 && rgba[i + 1] == 0 && rgba[i + 2] == 0) black++;
                else nonBlackOpaque++;
            }
            return (black, nonBlackOpaque, total);
        }

        var spotCounts = CountPixels(spot.Rgba8);
        var blankCounts = CountPixels(blank.Rgba8);

        Assert.True(spotCounts.black > 100, $"expected a real black center, got {spotCounts.black} black pixels");
        Assert.True(spotCounts.nonBlackOpaque > 100, $"expected a real non-black ring, got {spotCounts.nonBlackOpaque}");

        Assert.True(
            blankCounts.black < spotCounts.black / 10,
            $"blank template has {blankCounts.black} black pixels, expected far fewer than the spot's {spotCounts.black}");
    }

    private static void AssertButton(ImportedLayout layout, uint elementId) =>
        Assert.IsType<UiButton>(layout.FindElement(elementId));

    private static UiButton AssertButton(UiElement root, uint elementId) =>
        Assert.IsType<UiButton>(UiElement.FindDescendant(root, elementId));

    private static ImportedLayout BuildSelected(
        IDatReaderWriter dats,
        uint layoutDid,
        uint rootId)
    {
        ElementInfo info = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, layoutDid, rootId));
        return LayoutImporter.Build(
            info,
            _ => (0u, 0, 0),
            null,
            null,
            new DatStringResolver(dats).Resolve);
    }

    [InstalledDatFact]
    public void ShadeSlider_ThumbAuthorsItsOwnDirectStateSprite_BuildsWithNonZeroThumbSprite()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        uint layoutId = RetailDataIdResolver.Resolve(
            dats, CharacterCreationUiController.RootEnum, 5u);
        ImportedLayout screen = BuildSelected(
            dats, layoutId, CharacterCreationUiController.RootElementId);

        UiScrollbar shadeSlider = Assert.IsType<UiScrollbar>(screen.FindElement(0x10000321u));
        Assert.False(shadeSlider.Horizontal);
        Assert.NotEqual(0u, shadeSlider.ThumbSprite);
    }
}
