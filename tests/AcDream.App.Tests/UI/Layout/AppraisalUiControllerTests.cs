using System.Numerics;
using AcDream.App.Spells;
using AcDream.Content;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.UI.Layout;

public sealed class AppraisalUiControllerTests
{
    private const uint ObjectId = 0x50000001u;
    private static (uint, int, int) NoTexture(uint _) => (0u, 0, 0);

    [Fact]
    public void InspectKeyClosesVisibleWindowAndAllowsNextInspectToOpenIt()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Chainmail Basinet",
            Type = ItemType.Clothing,
        });
        var sent = new List<uint>();
        using var interaction = NewInteraction(objects, sent);
        int shown = 0;
        int closed = 0;
        using AppraisalUiController controller = Bind(
            layout, objects, interaction, new CombatState(), [], [],
            () => shown++, () => closed++)!;

        Assert.False(controller.HandleInputAction(InputAction.SelectionExamine));
        Assert.True(interaction.ExamineSelectedOrEnterMode(ObjectId));
        Assert.True(controller.Apply(Parsed(new PropertyBundle())));
        controller.OnShown();

        Assert.False(controller.HandleInputAction(InputAction.SelectRight));
        Assert.True(controller.HandleInputAction(InputAction.SelectionExamine));
        Assert.Equal(1, closed);
        Assert.Single(sent);
        Assert.Equal(0, interaction.BusyCount);
        controller.OnHidden();

        Assert.False(controller.HandleInputAction(InputAction.SelectionExamine));
        Assert.True(interaction.ExamineSelectedOrEnterMode(ObjectId));
        Assert.True(controller.Apply(Parsed(new PropertyBundle())));
        controller.OnShown();
        Assert.Equal(2, shown);
        Assert.Equal(2, sent.Count);
        Assert.True(controller.HandleInputAction(InputAction.SelectionExamine));
        Assert.Equal(2, closed);
    }

    [Fact]
    public void ItemResponse_UsesAuthoredItemSubviewTitleAndScrollbars()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Atlan Weapon",
            Type = ItemType.MeleeWeapon,
            Value = 1250,
            Burden = 350,
            ContainerId = 0x50000002u,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Inscribable,
        });
        var sent = new List<uint>();
        var inscriptions = new List<(uint ObjectId, string Text)>();
        var messages = new List<string>();
        using var interaction = NewInteraction(objects, sent);
        var combat = new CombatState();
        int shown = 0;
        int closed = 0;
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            combat,
            inscriptions,
            messages,
            () => shown++,
            () => closed++)!;

        Assert.True(interaction.ExamineSelectedOrEnterMode(ObjectId));
        var properties = new PropertyBundle();
        properties.Ints[19u] = 1_250;
        properties.Ints[5u] = 350;
        properties.Strings[16u] = "A finely balanced weapon.";
        properties.Strings[7u] = "Remember the fallen.";
        properties.Strings[8u] = "Tester";

        Assert.True(controller.Apply(Parsed(properties)));

        Assert.Equal(1, shown);
        Assert.Equal(AppraisalView.Item, controller.ActiveView);
        Assert.Equal(0, interaction.BusyCount);
        UiText title = Assert.IsType<UiText>(
            layout.FindElement(AppraisalUiController.TitleId));
        Assert.Equal("Atlan Weapon", Assert.Single(title.LinesProvider()).Text);
        UiText itemText = Assert.IsType<UiText>(
            layout.FindElement(AppraisalUiController.ItemTextId));
        Assert.Equal(VJustify.Top, itemText.VerticalJustify);
        Assert.Equal(
            [
                new Vector4(1f, 1f, 1f, 1f),
                new Vector4(0f, 1f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f),
            ],
            itemText.FontColorPalette);
        string report = string.Join('\n', itemText.LinesProvider().Select(line => line.Text));
        Assert.Contains("Value: 1,250", report);
        Assert.Contains("Burden: 350", report);
        Assert.Contains("A finely balanced weapon.", report);
        UiScrollbar scrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(AppraisalUiController.ItemScrollbarId));
        Assert.Same(itemText.Scroll, scrollbar.Model);
        UiField inscription = Assert.IsType<UiField>(
            layout.FindElement(AppraisalUiController.InscriptionTextId));
        Assert.Contains("Remember the fallen", inscription.Text);
        Assert.True(inscription.Editable);
        UiText signature = Assert.IsType<UiText>(
            layout.FindElement(AppraisalUiController.SignatureTextId));
        Assert.Equal("--Tester", Assert.Single(signature.LinesProvider()).Text);
        Assert.Empty(inscriptions);
        Assert.Empty(messages);

        ((UiButton)layout.FindElement(AppraisalUiController.CloseId)!).OnClick!.Invoke();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void ItemResponse_TitleUsesRetailMaterialDecoratedAppropriateName()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Steel Toed Boots",
            Type = ItemType.Clothing,
            MaterialType = 77u,
        });
        using var interaction = NewInteraction(objects, []);
        var names = new RetailAppraisalNameResolver(
            new Dictionary<uint, string> { [77u] = "Reed Shark Hide" },
            new CreatureDisplayNameResolver(new Dictionary<uint, string>()));
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            itemNames: names)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        Assert.True(controller.Apply(Parsed(new PropertyBundle())));

        UiText title = Assert.IsType<UiText>(
            layout.FindElement(AppraisalUiController.TitleId));
        Assert.Equal(
            "Reed Shark Hide Steel Toed Boots",
            Assert.Single(title.LinesProvider()).Text);
    }

    [Fact]
    public void CreatureResponse_SelectsCreatureSubviewAndRefreshesInCombatWithoutBusy()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Drudge Slinker",
            Type = ItemType.Creature,
        });
        var sent = new List<uint>();
        using var interaction = NewInteraction(objects, sent);
        var combat = new CombatState();
        int shown = 0;
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            combat,
            [],
            [],
            () => shown++,
            () => { })!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Ints[25u] = 12;
        var creature = new AppraiseInfoParser.CreatureProfile(
            Flags: 0,
            Health: 80,
            HealthMax: 100,
            Strength: null,
            Endurance: null,
            Quickness: null,
            Coordination: null,
            Focus: null,
            Self: null,
            Stamina: null,
            Mana: null,
            StaminaMax: null,
            ManaMax: null,
            AttributeHighlights: null,
            AttributeColors: null);

        Assert.True(controller.Apply(Parsed(properties, creature)));
        Assert.Equal(AppraisalView.Creature, controller.ActiveView);
        Assert.Equal(1, shown);
        controller.OnShown();
        Assert.False(layout.FindElement(AppraisalUiController.ItemPanelId)!.Visible);
        Assert.True(layout.FindElement(AppraisalUiController.CreaturePanelId)!.Visible);

        combat.SetCombatMode(CombatMode.Melee);
        controller.Tick(0.74);
        Assert.Single(sent);
        controller.Tick(0.01);
        Assert.Equal(new[] { ObjectId, ObjectId }, sent);
        Assert.Equal(0, interaction.BusyCount);

        controller.OnHidden();
        controller.Tick(0.75);
        Assert.Equal(new[] { ObjectId, ObjectId }, sent);

        Assert.True(controller.Apply(Parsed(properties, creature)));
        Assert.Equal(1, shown);
    }

    [Fact]
    public void CreatureResponse_UsesRetailHeaderAndNineOrderedTemplateRows()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Specter",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        UiText characterLabel = Assert.IsType<UiText>(
            layout.FindElement(0x1000014Au));
        UiText levelLabel = Assert.IsType<UiText>(
            layout.FindElement(0x1000014Bu));
        characterLabel.LinesProvider = () =>
            [new UiText.Line("Character", characterLabel.DefaultColor)];
        levelLabel.LinesProvider = () =>
            [new UiText.Line("Level", levelLabel.DefaultColor)];
        var templates = new CreatureAppraisalRowTemplateFactory(
            FixtureLoader.LoadExaminationRowTemplateInfos(),
            NoTexture,
            defaultFont: null);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            templates,
            new CreatureDisplayNameResolver(
                new Dictionary<uint, string> { [77u] = "Ghost" }))!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Ints[2u] = 77;
        properties.Ints[25u] = 80;
        properties.Ints[0x133u] = 35;
        properties.Ints[0x13Au] = 4;
        var profile = new AppraiseInfoParser.CreatureProfile(
            Flags: 0x08u,
            Health: 295u,
            HealthMax: 295u,
            Strength: 120u,
            Endurance: 190u,
            Quickness: 190u,
            Coordination: 190u,
            Focus: 330u,
            Self: 350u,
            Stamina: 190u,
            Mana: 550u,
            StaminaMax: 190u,
            ManaMax: 550u,
            AttributeHighlights: (ushort)0,
            AttributeColors: (ushort)0);

        Assert.True(controller.Apply(Parsed(properties, profile)));

        Assert.Equal(
            "Character",
            Assert.Single(characterLabel.LinesProvider()).Text);
        Assert.Equal("Level", Assert.Single(levelLabel.LinesProvider()).Text);
        Assert.Equal(
            "80",
            Assert.Single(((UiText)layout.FindElement(
                AppraisalUiController.CreatureLevelValueId)!)
                .LinesProvider()).Text);
        Assert.Equal(
            "Ghost",
            Assert.Single(((UiText)layout.FindElement(
                AppraisalUiController.CreatureDisplayNameId)!)
                .LinesProvider()).Text);

        UiElement host = layout.FindElement(
            AppraisalUiController.CreatureStatsListId)!;
        UiElement creaturePanel = layout.FindElement(
            AppraisalUiController.CreaturePanelId)!;
        UiViewport viewport = Assert.IsType<UiViewport>(
            layout.FindElement(AppraisalUiController.CreatureViewportId));
        UiItemList background = Assert.Single(
            host.Children.OfType<UiItemList>());
        UiItemList list = Assert.Single(
            creaturePanel.Children.OfType<UiItemList>(),
            candidate => candidate.Top == host.Top);
        Assert.Equal(0f, background.Left);
        Assert.Equal(CreatureAppraisalLayeredList.TextInset, list.Left);
        Assert.True(host.ZOrder < viewport.ZOrder);
        Assert.True(viewport.ZOrder < list.ZOrder);
        Assert.Same(background.Scroll, list.Scroll);
        Assert.Equal(9, list.GetNumUIItems());
        string[] labels = new string[9];
        string[] values = new string[9];
        for (int index = 0; index < 9; index++)
        {
            UiTemplateListSlot row =
                Assert.IsType<UiTemplateListSlot>(list.GetItem(index));
            labels[index] = Assert.Single(
                ((UiText)row.Content.FindElement(
                    CreatureAppraisalRowTemplateFactory.LabelId)!)
                .LinesProvider()).Text;
            values[index] = Assert.Single(
                ((UiText)row.Content.FindElement(
                    CreatureAppraisalRowTemplateFactory.ValueId)!)
                .LinesProvider()).Text;
        }
        Assert.Equal(
            [
                "Strength", "Endurance", "Coordination", "Quickness",
                "Focus", "Self", "Health", "Stamina", "Mana",
            ],
            labels);
        Assert.Equal(
            [
                "120", "190", "190", "190", "330", "350",
                "295/295 (100 %)", "190/190", "550/550",
            ],
            values);

        UiElement extraHost = layout.FindElement(
            AppraisalUiController.CreatureExtraListId)!;
        UiItemList extra = Assert.Single(
            creaturePanel.Children.OfType<UiItemList>(),
            candidate => candidate.Top == extraHost.Top);
        Assert.Equal(3, extra.GetNumUIItems());
        UiTemplateListSlot rating =
            Assert.IsType<UiTemplateListSlot>(extra.GetItem(1));
        Assert.Equal(
            "Dmg/CritDmg",
            Assert.Single(((UiText)rating.Content.FindElement(
                CreatureAppraisalRowTemplateFactory.LabelId)!)
                .LinesProvider()).Text);
        Assert.Equal(
            "Rating: 35/4",
            Assert.Single(((UiText)rating.Content.FindElement(
                CreatureAppraisalRowTemplateFactory.ValueId)!)
                .LinesProvider()).Text);
        Assert.True(extraHost.ZOrder < viewport.ZOrder);
        Assert.True(viewport.ZOrder < extra.ZOrder);
    }

    [Fact]
    public void VisibleExaminationFollowsSelectionAcrossSubviewsAndClosesOnClear()
    {
        const uint otherObjectId = 0x50000003u;
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Drudge",
            Type = ItemType.Creature,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = otherObjectId,
            Name = "Sword",
            Type = ItemType.MeleeWeapon,
        });
        var selection = new SelectionState();
        var sent = new List<uint>();
        int closed = 0;
        using var interaction = NewInteraction(objects, sent);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => closed++,
            selection: selection)!;

        selection.Select(ObjectId, SelectionChangeSource.World);
        interaction.ExamineSelectedOrEnterMode(ObjectId);
        Assert.True(controller.Apply(Parsed(
            new PropertyBundle(),
            new AppraiseInfoParser.CreatureProfile(
                Flags: 0u,
                Health: 1u,
                HealthMax: 1u,
                Strength: null,
                Endurance: null,
                Quickness: null,
                Coordination: null,
                Focus: null,
                Self: null,
                Stamina: null,
                Mana: null,
                StaminaMax: null,
                ManaMax: null,
                AttributeHighlights: null,
                AttributeColors: null))));
        controller.OnShown();

        selection.Select(otherObjectId, SelectionChangeSource.Inventory);
        Assert.Equal(new[] { ObjectId, otherObjectId }, sent);
        Assert.True(controller.Apply(Parsed(
            new PropertyBundle(),
            guid: otherObjectId)));
        Assert.Equal(AppraisalView.Item, controller.ActiveView);

        selection.Clear(SelectionChangeSource.World);
        Assert.Equal(1, closed);

        controller.OnHidden();
        selection.Select(ObjectId, SelectionChangeSource.World);
        Assert.Equal(new[] { ObjectId, otherObjectId }, sent);
    }


    [Fact]
    public void CharacterResponse_ComposesRetailHeaderIdentityBlock()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
            PublicWeenieBitfield = 0x20u, // PWD bit 5 -> IsPK
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            resolveCharacterTitle: titleId => titleId == 13u ? "War Mage" : null)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Ints[0x71u] = 2;   // Gender: Female
        properties.Ints[0xBCu] = 1;   // HeritageGroup: Aluvian
        properties.Ints[0x105u] = 13;
        properties.Ints[30u] = 5;     // AllegianceRank >= 1
        properties.Strings[47u] = "The Empire"; // AllegianceName

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Character, controller.ActiveView);

        Assert.Equal("Female Aluvian", HeaderText(layout, 0x10000150u));
        Assert.Equal("War Mage", HeaderText(layout, 0x10000151u));
        Assert.Equal("Player Killer", HeaderText(layout, 0x10000152u));
        Assert.Equal("The Empire", HeaderText(layout, 0x1000053Au));
    }


    [Fact]
    public void CharacterResponse_TitleBarPrefixesAllegianceRankTitle()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { })!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";
        properties.Ints[0x1Eu] = 3;  // AllegianceRank
        properties.Ints[0xBCu] = 1;  // HeritageGroup: Aluvian
        properties.Ints[0x71u] = 2;  // Gender: Female

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Character, controller.ActiveView);
        // Aluvian female rank 3 = "Baroness" (AllegianceRankTitleTableTests
        // pins the table itself); GetFullName's single-space separator.
        Assert.Equal("Baroness Dww", HeaderText(layout, AppraisalUiController.TitleId));
    }

    [Fact]
    public void CharacterResponse_TitleBarPlainNameWhenRankAbsent()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { })!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Character, controller.ActiveView);
        Assert.Equal("Dww", HeaderText(layout, AppraisalUiController.TitleId));
    }

    [Fact]
    public void CreatureResponse_TitleBarNeverGetsAllegianceRankPrefix()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Drudge",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { })!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Ints[0x1Eu] = 3;
        properties.Ints[0xBCu] = 1;
        properties.Ints[0x71u] = 2;

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Creature, controller.ActiveView);
        Assert.Equal("Drudge", HeaderText(layout, AppraisalUiController.TitleId));
    }

    [Fact]
    public void CharacterResponse_HeritageFallsBackToCreatureTypeWhenGroupIsZero()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Something Odd",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            creatureNames: new CreatureDisplayNameResolver(
                new Dictionary<uint, string> { [42u] = "Olthoi Guardian" }))!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Ints[2u] = 42;
        properties.Ints[0xBCu] = 0;
        properties.Strings[5u] = "Template";

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Character, controller.ActiveView);
        Assert.Equal("Olthoi Guardian", HeaderText(layout, 0x10000150u));
    }

    [Fact]
    public void CharacterResponse_TitleFallsBackToTemplateStringWhenTitleIdAbsent()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { })!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Test Template"; // both the fallback text AND the view marker

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Character, controller.ActiveView);
        Assert.Equal("Test Template", HeaderText(layout, 0x10000151u));
    }

    [Fact]
    public void CharacterResponse_TitleClearsWhenIdUnresolvableAndTemplateAbsent()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            // Never resolves any title id.
            resolveCharacterTitle: _ => null)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Ints[0x105u] = 999;

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Character, controller.ActiveView);
        Assert.Equal(string.Empty, HeaderText(layout, 0x10000151u));
    }

    [Theory]
    [InlineData(0x20u, "Player Killer")]
    [InlineData(0x02000000u, "Player Killer Lite")]
    [InlineData(0u, "Non-Player Killer")]
    public void CharacterResponse_PlayerKillerLineReflectsLocalObjectPwdBits(
        uint publicWeenieBitfield,
        string expected)
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
            PublicWeenieBitfield = publicWeenieBitfield,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { })!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(expected, HeaderText(layout, 0x10000152u));
    }

    [Fact]
    public void MissingAssessedObject_PlayerKillerElementStaysClearedAndApplyReturnsFalse()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { })!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";

        UiText pk = Assert.IsType<UiText>(layout.FindElement(0x10000152u));
        Func<IReadOnlyList<UiText.Line>> providerBeforeApply = pk.LinesProvider;

        Assert.False(controller.Apply(Parsed(properties, MinimalCreatureProfile())));

        Assert.Same(providerBeforeApply, pk.LinesProvider);
    }

    [Fact]
    public void AllegianceElement_ClearsUnlessRankIsAtLeastOne()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { })!;
        interaction.ExamineSelectedOrEnterMode(ObjectId);

        var noRank = new PropertyBundle();
        noRank.Strings[5u] = "Template";
        noRank.Strings[47u] = "The Empire";
        Assert.True(controller.Apply(Parsed(noRank, MinimalCreatureProfile())));
        Assert.Equal(string.Empty, HeaderText(layout, 0x1000053Au));

        var zeroRank = new PropertyBundle();
        zeroRank.Strings[5u] = "Template";
        zeroRank.Strings[47u] = "The Empire";
        zeroRank.Ints[30u] = 0;
        Assert.True(controller.Apply(Parsed(zeroRank, MinimalCreatureProfile())));
        Assert.Equal(string.Empty, HeaderText(layout, 0x1000053Au));

        var gatedRank = new PropertyBundle();
        gatedRank.Strings[5u] = "Template";
        gatedRank.Strings[47u] = "The Empire";
        gatedRank.Ints[30u] = 1;
        Assert.True(controller.Apply(Parsed(gatedRank, MinimalCreatureProfile())));
        Assert.Equal("The Empire", HeaderText(layout, 0x1000053Au));
    }

    [Fact]
    public void FailedAssess_StillRendersHeaderLinesFromPresentTableEntries()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
            PublicWeenieBitfield = 0x02000000u, // PKLite
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            resolveCharacterTitle: titleId => titleId == 13u ? "War Mage" : null)!;
        interaction.ExamineSelectedOrEnterMode(ObjectId);

        var properties = new PropertyBundle();
        properties.Ints[0x71u] = 1;   // Gender: Male
        properties.Ints[0xBCu] = 1;   // HeritageGroup: Aluvian
        properties.Ints[0x105u] = 13;
        properties.Ints[30u] = 5;     // AllegianceRank
        properties.Strings[47u] = "The Empire";

        Assert.True(controller.Apply(
            Parsed(properties, MinimalCreatureProfile(), success: false)));

        Assert.Equal("Male Aluvian", HeaderText(layout, 0x10000150u));
        Assert.Equal("War Mage", HeaderText(layout, 0x10000151u));
        Assert.Equal("Player Killer Lite", HeaderText(layout, 0x10000152u));
        Assert.Equal("The Empire", HeaderText(layout, 0x1000053Au));
    }

    [Fact]
    public void CreatureResponse_HeaderIdentityElementsUnaffectedByPlayerFix()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Specter",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            creatureNames: new CreatureDisplayNameResolver(
                new Dictionary<uint, string> { [77u] = "Ghost" }))!;
        interaction.ExamineSelectedOrEnterMode(ObjectId);

        var properties = new PropertyBundle();
        properties.Ints[2u] = 77;
        properties.Ints[25u] = 80;

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Creature, controller.ActiveView);

        Assert.Equal("Ghost", HeaderText(layout, AppraisalUiController.CreatureDisplayNameId));
        Assert.Equal(string.Empty, HeaderText(layout, 0x10000150u));
        Assert.Equal(string.Empty, HeaderText(layout, 0x10000151u));
        Assert.Equal(string.Empty, HeaderText(layout, 0x10000152u));
        Assert.Equal(string.Empty, HeaderText(layout, 0x1000053Au));
    }

    [Fact]
    public void FailedMonsterAssess_DoesNotEmitInventedAssessmentIncompleteLiteral()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Drudge",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            creatureNames: new CreatureDisplayNameResolver(
                new Dictionary<uint, string> { [11u] = "Drudge" }))!;
        interaction.ExamineSelectedOrEnterMode(ObjectId);

        var properties = new PropertyBundle();
        properties.Ints[2u] = 11;

        Assert.True(controller.Apply(
            Parsed(properties, MinimalCreatureProfile(), success: false)));
        Assert.Equal(AppraisalView.Creature, controller.ActiveView);

        Assert.Equal(string.Empty, HeaderText(layout, 0x1000053Au));
    }


    [Fact]
    public void CharacterResponse_ArmorLevelTrioPopulatesExtraListThroughRealBinding()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        var templates = new CreatureAppraisalRowTemplateFactory(
            FixtureLoader.LoadExaminationRowTemplateInfos(),
            NoTexture,
            defaultFont: null);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            templates)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";
        var armorLevels = new AppraiseInfoParser.ArmorLevel(
            Head: 100, Chest: 110, Abdomen: 120,
            UpperArm: 130, LowerArm: 140, Hand: 150,
            UpperLeg: 160, LowerLeg: 170, Foot: 180);

        Assert.True(controller.Apply(Parsed(
            properties, MinimalCreatureProfile(), armorLevels: armorLevels)));
        Assert.Equal(AppraisalView.Character, controller.ActiveView);

        UiItemList extra = CreatureExtraList(layout);
        Assert.Equal(5, extra.GetNumUIItems());
        Assert.Equal(("", ""), ExtraRow(extra, 0));
        Assert.Equal(
            ("Head/Chest/Groin", "AL: 100/110/120"), ExtraRow(extra, 1));
        Assert.Equal(
            ("Bicep/Wrist/Hand", "AL: 130/140/150"), ExtraRow(extra, 2));
        Assert.Equal(
            ("Thigh/Shin/Foot", "AL: 160/170/180"), ExtraRow(extra, 3));
        Assert.Equal(
            ("* = Unenchantable", string.Empty), ExtraRow(extra, 4));
    }

    [Fact]
    public void CharacterResponse_CombatRefreshRetainsArmorLevelRows()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        var sent = new List<uint>();
        using var interaction = NewInteraction(objects, sent);
        var combat = new CombatState();
        var templates = new CreatureAppraisalRowTemplateFactory(
            FixtureLoader.LoadExaminationRowTemplateInfos(),
            NoTexture,
            defaultFont: null);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            combat,
            [],
            [],
            () => { },
            () => { },
            templates)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";
        var firstArmorLevels = new AppraiseInfoParser.ArmorLevel(
            Head: 50, Chest: 60, Abdomen: 70,
            UpperArm: 80, LowerArm: 90, Hand: 100,
            UpperLeg: 110, LowerLeg: 120, Foot: 130);

        Assert.True(controller.Apply(Parsed(
            properties, MinimalCreatureProfile(), armorLevels: firstArmorLevels)));
        controller.OnShown();
        int sentBeforeRefresh = sent.Count;

        combat.SetCombatMode(CombatMode.Melee);
        controller.Tick(0.75);
        Assert.Equal(sentBeforeRefresh + 1, sent.Count);

        var secondArmorLevels = new AppraiseInfoParser.ArmorLevel(
            Head: 51, Chest: 61, Abdomen: 71,
            UpperArm: 81, LowerArm: 91, Hand: 101,
            UpperLeg: 111, LowerLeg: 121, Foot: 131);
        Assert.True(controller.Apply(Parsed(
            properties, MinimalCreatureProfile(), armorLevels: secondArmorLevels)));

        UiItemList extraAfterSecond = CreatureExtraList(layout);
        Assert.Equal(5, extraAfterSecond.GetNumUIItems());
        Assert.Equal(
            ("Head/Chest/Groin", "AL: 51/61/71"), ExtraRow(extraAfterSecond, 1));
        Assert.Equal(
            ("Bicep/Wrist/Hand", "AL: 81/91/101"), ExtraRow(extraAfterSecond, 2));
        Assert.Equal(
            ("Thigh/Shin/Foot", "AL: 111/121/131"), ExtraRow(extraAfterSecond, 3));

        Assert.True(controller.Apply(Parsed(
            properties, MinimalCreatureProfile(), armorLevels: null)));

        UiItemList extraAfterThird = CreatureExtraList(layout);
        Assert.Equal(1, extraAfterThird.GetNumUIItems());
        Assert.Equal(
            ("* = Unenchantable", string.Empty), ExtraRow(extraAfterThird, 0));
    }


    [Fact]
    public void CharacterResponse_SocietyAllegianceAndFellowshipRenderThroughRealBinding()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        var templates = new CreatureAppraisalRowTemplateFactory(
            FixtureLoader.LoadExaminationRowTemplateInfos(),
            NoTexture,
            defaultFont: null);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            templates,
            localFactionBits: () => 0x1)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";
        properties.Ints[281u] = 0x1;
        properties.Ints[287u] = 50; // society rank -> Initiate band
        properties.Ints[30u] = 5; // AllegianceRank >= 1
        properties.Strings[21u] = "Monarch Title"; // no PatronsTitle -> Monarch-only row
        properties.Strings[10u] = "Fellows";

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));
        Assert.Equal(AppraisalView.Character, controller.ActiveView);

        UiItemList extra = CreatureExtraList(layout);
        Assert.Equal(4, extra.GetNumUIItems());
        Assert.Equal(
            ("Society:", "Celestial Hand ~ Initiate"), ExtraRow(extra, 0));
        Assert.Equal(("Monarch:", "Monarch Title"), ExtraRow(extra, 1));
        Assert.Equal(("Fellowship:", "Fellows"), ExtraRow(extra, 2));
        Assert.Equal(
            ("* = Unenchantable", string.Empty), ExtraRow(extra, 3));
    }

    [Fact]
    public void CharacterResponse_LocalFactionBitsDefaultsToFactionlessWhenBindingSuppliesNone()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Dww",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        var templates = new CreatureAppraisalRowTemplateFactory(
            FixtureLoader.LoadExaminationRowTemplateInfos(),
            NoTexture,
            defaultFont: null);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            templates)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";
        properties.Ints[281u] = 0x1;

        Assert.True(controller.Apply(Parsed(properties, MinimalCreatureProfile())));

        UiItemList extra = CreatureExtraList(layout);
        Assert.Equal(
            ("Society:", "Celestial Hand"), ExtraRow(extra, 0));
    }

    [Fact]
    public void CharacterResponse_WorstCaseExtrasCombination_DoesNotThrowAndViewportGateStaysOpen()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Worstcase",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        var templates = new CreatureAppraisalRowTemplateFactory(
            FixtureLoader.LoadExaminationRowTemplateInfos(),
            NoTexture,
            defaultFont: null);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            templates,
            resolveCharacterTitle: titleId => titleId == 13u ? "War Mage" : null,
            localFactionBits: () => 0x1)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[5u] = "Template";
        properties.Ints[0x105] = 13;
        properties.Ints[113] = 1; // Gender: male
        properties.Ints[188] = 1; // HeritageGroup: Aluvian
        properties.Ints[281] = 0x1;
        properties.Ints[287] = 50; // Society rank
        properties.Ints[30] = 5; // AllegianceRank >= 1
        properties.Ints[35] = 12; // AllegianceFollowers (unused once titles present)
        properties.Strings[21u] = "Monarch Title";
        properties.Strings[35u] = "Patron Title"; // different from Monarch -> two rows
        properties.Ints[0x133] = 10; // DamageRating
        properties.Ints[0x134] = 10; // DamageResistRating
        properties.Ints[0x15E] = 10; // DotResistRating
        properties.Strings[10u] = "Fellows";
        properties.Strings[43u] = "1/1/2003"; // DateOfBirth
        properties.Ints[125] = 100000; // Age (seconds in Dereth)
        properties.Ints[181] = 7;
        properties.Ints[192] = 3; // FishingSkill
        properties.Ints[43u] = 2; // NumDeaths (int table, same numeric id as DateOfBirth string id)
        properties.Ints[262] = 5; // NumCharacterTitles
        var armorLevels = new AppraiseInfoParser.ArmorLevel(
            Head: 100, Chest: 110, Abdomen: 120,
            UpperArm: 130, LowerArm: 140, Hand: 150,
            UpperLeg: 160, LowerLeg: 170, Foot: 180);

        bool applied = controller.Apply(Parsed(
            properties, MinimalCreatureProfile(), armorLevels: armorLevels));

        Assert.True(applied);
        Assert.Equal(AppraisalView.Character, controller.ActiveView);
        Assert.NotEqual(0u, controller.CurrentObjectId);

        UiElement creaturePanel = layout.FindElement(
            AppraisalUiController.CreaturePanelId)!;
        UiElement viewportHost = layout.FindElement(
            AppraisalUiController.CreatureViewportId)!;
        Assert.True(creaturePanel.Visible);
        for (UiElement? current = viewportHost; current is not null; current = current.Parent)
            Assert.True(current.Visible, $"ancestor 0x{current.EventId:X8} is not Visible");

        UiItemList extra = CreatureExtraList(layout);
        int rowCount = extra.GetNumUIItems();
        UiElement extraHost = layout.FindElement(
            AppraisalUiController.CreatureExtraListId)!;
        float contentHeight = rowCount * 20f;
        Console.WriteLine(
            $"[AS-GF1] worst-case extras: {rowCount} rows, "
            + $"{contentHeight}px content vs extraHost authored "
            + $"{extraHost.Height}px at default window size.");

        Assert.True(rowCount > 15, $"expected a long worst-case list, got {rowCount} rows");
        Assert.True(contentHeight > extraHost.Height * 2,
            $"expected content ({contentHeight}px) to badly overflow the "
            + $"authored host ({extraHost.Height}px)");

        UiItemSlot lastRow = Assert.IsType<UiTemplateListSlot>(
            extra.GetItem(rowCount - 1));
        Assert.False(lastRow.Visible, "expected the last row to start below the fold");
        extra.OnEvent(new UiEvent(
            extra.EventId, extra, UiEventType.Scroll, Data0: -1000));
        Assert.True(extra.Scroll.ScrollY > 0, "expected the wheel event to move the scroll offset");
        extra.LayoutCells();
        Assert.True(lastRow.Visible, "expected scrolling to reveal the last row");
    }

    [Fact]
    public void CreatureResponse_NeverGainsArmorLevelTrioOrLegend()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Specter",
            Type = ItemType.Creature,
        });
        using var interaction = NewInteraction(objects, []);
        var templates = new CreatureAppraisalRowTemplateFactory(
            FixtureLoader.LoadExaminationRowTemplateInfos(),
            NoTexture,
            defaultFont: null);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => { },
            () => { },
            templates)!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var armorLevels = new AppraiseInfoParser.ArmorLevel(
            Head: 100, Chest: 110, Abdomen: 120,
            UpperArm: 130, LowerArm: 140, Hand: 150,
            UpperLeg: 160, LowerLeg: 170, Foot: 180);
        var properties = new PropertyBundle();
        properties.Ints[281u] = 0x1; // Faction1Bits
        properties.Ints[30u] = 5; // AllegianceRank
        properties.Strings[21u] = "Monarch Title";
        properties.Strings[10u] = "Fellows";
        properties.Ints[43u] = 2; // NumDeaths

        Assert.True(controller.Apply(Parsed(
            properties,
            MinimalCreatureProfile(),
            armorLevels: armorLevels)));
        Assert.Equal(AppraisalView.Creature, controller.ActiveView);

        UiItemList extra = CreatureExtraList(layout);
        Assert.Equal(0, extra.GetNumUIItems());
    }

    [Fact]
    public void ResponseForNeitherPendingNorCurrent_IsIgnored()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = ObjectId, Name = "Item" });
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => throw new InvalidOperationException("must not show"),
            () => { })!;

        Assert.False(controller.Apply(Parsed(new PropertyBundle())));
        Assert.Equal(0u, controller.CurrentObjectId);
    }

    [Fact]
    public void EmptyOwnedInscription_FocusEditAndBlur_SendsRetailTransactionOnce()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Sword",
            Type = ItemType.MeleeWeapon,
            ContainerId = 0x50000002u,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Inscribable,
        });
        var inscriptions = new List<(uint ObjectId, string Text)>();
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            inscriptions,
            [],
            () => { },
            () => { })!;

        interaction.ExamineSelectedOrEnterMode(ObjectId);
        Assert.True(controller.Apply(Parsed(new PropertyBundle())));

        UiField field = Assert.IsType<UiField>(
            layout.FindElement(AppraisalUiController.InscriptionTextId));
        UiText signature = Assert.IsType<UiText>(
            layout.FindElement(AppraisalUiController.SignatureTextId));
        Assert.True(field.Editable);
        Assert.Equal("<Inscribe here>", field.Text);

        field.OnEvent(new UiEvent(field.EventId, field, UiEventType.FocusGained));
        Assert.Equal(string.Empty, field.Text);
        Assert.Equal("--Tester", Assert.Single(signature.LinesProvider()).Text);
        foreach (char c in "For glory")
            field.InsertChar(c);
        field.OnEvent(new UiEvent(field.EventId, field, UiEventType.FocusLost));

        Assert.Equal(
            new[] { (ObjectId, "For glory") },
            inscriptions);
        Assert.Equal("--Tester", Assert.Single(signature.LinesProvider()).Text);

        field.OnEvent(new UiEvent(field.EventId, field, UiEventType.FocusGained));
        field.OnEvent(new UiEvent(field.EventId, field, UiEventType.FocusLost));
        Assert.Single(inscriptions);
    }

    [Fact]
    public void ExistingInscription_ClearOnBlur_SendsEmptyAndRestoresPlaceholder()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Sword",
            Type = ItemType.MeleeWeapon,
            ContainerId = 0x50000002u,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Inscribable,
        });
        var inscriptions = new List<(uint ObjectId, string Text)>();
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            inscriptions,
            [],
            () => { },
            () => { })!;
        interaction.ExamineSelectedOrEnterMode(ObjectId);
        var properties = new PropertyBundle();
        properties.Strings[7u] = "Old words";
        properties.Strings[8u] = "Tester";
        Assert.True(controller.Apply(Parsed(properties)));

        UiField field = Assert.IsType<UiField>(
            layout.FindElement(AppraisalUiController.InscriptionTextId));
        UiText signature = Assert.IsType<UiText>(
            layout.FindElement(AppraisalUiController.SignatureTextId));
        field.SetText(string.Empty);
        field.OnEvent(new UiEvent(field.EventId, field, UiEventType.FocusLost));

        Assert.Equal(new[] { (ObjectId, string.Empty) }, inscriptions);
        Assert.Equal("<Inscribe here>", field.Text);
        Assert.Equal(string.Empty, Assert.Single(signature.LinesProvider()).Text);
    }

    [Fact]
    public void InscriptionPermissionMessages_MatchRetail()
    {
        ImportedLayout otherScribeLayout = FixtureLoader.LoadExamination();
        var otherScribeObjects = new ClientObjectTable();
        otherScribeObjects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Sword",
            ContainerId = 0x50000002u,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Inscribable,
        });
        var messages = new List<string>();
        using var otherInteraction = NewInteraction(otherScribeObjects, []);
        using AppraisalUiController otherController = Bind(
            otherScribeLayout,
            otherScribeObjects,
            otherInteraction,
            new CombatState(),
            [],
            messages,
            () => { },
            () => { })!;
        otherInteraction.ExamineSelectedOrEnterMode(ObjectId);
        var otherProperties = new PropertyBundle();
        otherProperties.Strings[7u] = "Hands off";
        otherProperties.Strings[8u] = "Other";
        Assert.True(otherController.Apply(Parsed(otherProperties)));
        UiField otherField = Assert.IsType<UiField>(
            otherScribeLayout.FindElement(AppraisalUiController.InscriptionTextId));
        Assert.False(otherField.Editable);
        otherField.OnEvent(new UiEvent(
            otherField.EventId,
            otherField,
            UiEventType.Click));
        Assert.Equal("Only Other can change the inscription", Assert.Single(messages));

        ImportedLayout unownedLayout = FixtureLoader.LoadExamination();
        var unownedObjects = new ClientObjectTable();
        unownedObjects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Sword",
            ContainerId = 0x60000000u,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Inscribable,
        });
        messages.Clear();
        using var unownedInteraction = NewInteraction(unownedObjects, []);
        using AppraisalUiController unownedController = Bind(
            unownedLayout,
            unownedObjects,
            unownedInteraction,
            new CombatState(),
            [],
            messages,
            () => { },
            () => { })!;
        unownedInteraction.ExamineSelectedOrEnterMode(ObjectId);
        Assert.True(unownedController.Apply(Parsed(new PropertyBundle())));
        UiField unownedField = Assert.IsType<UiField>(
            unownedLayout.FindElement(AppraisalUiController.InscriptionTextId));
        unownedField.OnEvent(new UiEvent(
            unownedField.EventId,
            unownedField,
            UiEventType.Click));
        Assert.Equal(
            "Item must be in your inventory to inscribe.",
            Assert.Single(messages));
    }

    [Fact]
    public void NonInscribableItem_HidesEditorAndBackgroundReportsRetailMessage()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Component",
            ContainerId = 0x50000002u,
        });
        var messages = new List<string>();
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            messages,
            () => { },
            () => { })!;
        interaction.ExamineSelectedOrEnterMode(ObjectId);
        Assert.True(controller.Apply(Parsed(new PropertyBundle())));

        UiField field = Assert.IsType<UiField>(
            layout.FindElement(AppraisalUiController.InscriptionTextId));
        UiText signature = Assert.IsType<UiText>(
            layout.FindElement(AppraisalUiController.SignatureTextId));
        Assert.False(field.Visible);
        Assert.False(signature.Visible);

        UiDatElement background = Assert.IsType<UiDatElement>(
            layout.FindElement(AppraisalUiController.InscriptionBackgroundId));
        background.OnClick!.Invoke();
        Assert.Equal("This item is not inscribable.", Assert.Single(messages));
    }

    [Fact]
    public void HookProfileControlsVisibilityButPublicFlagStillControlsEditing()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Hooked Decoration",
            ContainerId = 0x50000002u,
            HookItemTypes = (uint)ItemType.Misc,
            HookType = 1u,
        });
        var messages = new List<string>();
        using var interaction = NewInteraction(objects, []);
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            messages,
            () => { },
            () => { })!;
        interaction.ExamineSelectedOrEnterMode(ObjectId);

        AppraiseInfoParser.Parsed appraisal = Parsed(new PropertyBundle()) with
        {
            Flags = AppraiseInfoParser.IdentifyResponseFlags.HookProfile,
            HookProfile = new AppraiseInfoParser.HookProfile(
                Flags: 1u,
                ValidLocations: 0u,
                AmmoType: 0u),
        };
        Assert.True(controller.Apply(appraisal));

        UiField field = Assert.IsType<UiField>(
            layout.FindElement(AppraisalUiController.InscriptionTextId));
        Assert.True(field.Visible);
        Assert.False(field.Editable);
        field.OnEvent(new UiEvent(field.EventId, field, UiEventType.Click));
        Assert.Equal("This item is not inscribable.", Assert.Single(messages));
    }

    [Fact]
    public void Examination_IsAnIndependentFloatyWindow_NotSharedMainPanelContent()
    {
        Assert.False(
            RetailPanelCatalog.TryGetPanelId(WindowNames.Examination, out _));
        Assert.DoesNotContain(
            RetailPanelCatalog.MountedPanels,
            mounted => mounted.WindowName == WindowNames.Examination);
    }

    [Fact]
    public void SpellExamination_UsesAuthoredLocalSubviewWithoutSelectingOrAppraisingSpellId()
    {
        ImportedLayout layout = FixtureLoader.LoadExamination();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ObjectId,
            Name = "Selected Drudge",
            Type = ItemType.Creature,
        });
        var sent = new List<uint>();
        using var interaction = NewInteraction(objects, sent);
        var selection = new SelectionState();
        selection.Select(ObjectId, SelectionChangeSource.World);
        SpellMetadata metadata = ExaminedSpell();
        var spellbook = new Spellbook(SpellTable.Create([metadata]));
        var components = new[]
        {
            new SpellExamineComponent(
                10u,
                new SpellComponentDescriptor(100u, "Lead Scarab", 0u, 0x06000010u),
                Owned: true),
            new SpellExamineComponent(
                11u,
                new SpellComponentDescriptor(101u, "Malar Herb", 1u, 0x06000011u),
                Owned: false),
        };
        var componentTemplate = new SpellExamineComponentTemplateFactory(
            new ElementInfo
            {
                Id = SpellExamineComponentTemplateFactory.TemplateId,
                Type = 3,
                Width = 20,
                Height = 20,
            },
            NoTexture,
            defaultFont: null);
        var resolvedComponentIcons = new List<uint>();
        int shown = 0;
        using AppraisalUiController controller = Bind(
            layout,
            objects,
            interaction,
            new CombatState(),
            [],
            [],
            () => shown++,
            () => { },
            selection: selection,
            spellbook: spellbook,
            resolveSpellIcon: id => id + 1_000u,
            resolveComponentIcon: did =>
            {
                resolvedComponentIcons.Add(did);
                return did + 2_000u;
            },
            spellComponents: _ => components,
            magicSkill: _ => 200u,
            spellComponentTemplates: componentTemplate)!;

        Assert.True(interaction.ExamineSelectedOrEnterMode(ObjectId));
        Assert.Equal(1, interaction.BusyCount);

        Assert.True(controller.ExamineSpell(metadata.SpellId));

        Assert.Equal(AppraisalView.Spell, controller.ActiveView);
        Assert.Equal(1, shown);
        Assert.Equal(ObjectId, selection.SelectedObjectId);
        Assert.Equal(new uint[] { ObjectId, 0u }, sent);
        Assert.Equal(0, interaction.BusyCount);
        Assert.Equal(0u, controller.CurrentObjectId);
        Assert.True(layout.FindElement(AppraisalUiController.SpellPanelId)!.Visible);
        Assert.False(layout.FindElement(AppraisalUiController.ItemPanelId)!.Visible);
        Assert.False(layout.FindElement(AppraisalUiController.CreaturePanelId)!.Visible);

        AssertSpellText(
            layout,
            AppraisalUiController.TitleId,
            "Incantation of Test");
        AssertSpellText(
            layout,
            AppraisalUiController.SpellSchoolTextId,
            "School: War Magic");
        AssertSpellText(
            layout,
            AppraisalUiController.SpellManaTextId,
            "Mana: 50 + 14 per target");
        AssertSpellText(
            layout,
            AppraisalUiController.SpellDurationTextId,
            "Duration: 1 min.");
        AssertSpellText(
            layout,
            AppraisalUiController.SpellRangeTextId,
            "Range: 82.0 yds.");
        string display = string.Join(
            '\n',
            ((UiText)layout.FindElement(
                AppraisalUiController.SpellDisplayTextId)!)
                .LinesProvider()
                .Select(line => line.Text));
        Assert.Contains("A projected retail spell.", display);
        Assert.Contains("COMPONENTS:", display);
        Assert.Contains("Lead Scarab", display);
        Assert.Contains("Malar Herb", display);

        UiElement iconHost = layout.FindElement(
            AppraisalUiController.SpellIconId)!;
        UiTextureElement icon = Assert.Single(
            iconHost.Children.OfType<UiTextureElement>());
        Assert.Equal(metadata.SpellId + 1_000u, icon.Texture);
        Assert.Equal(
            new uint[] { 0x06000010u, 0x06000011u },
            resolvedComponentIcons);
        UiElement formula = layout.FindElement(
            AppraisalUiController.SpellFormulaListId)!;
        Assert.Equal(
            new uint[] { 0x06000010u + 2_000u, 0x06000011u + 2_000u },
            formula.Children
                .OfType<UiDatElement>()
                .Select(cell => cell.RuntimeImageTexture!.Value));
    }

    private static AppraisalUiController? Bind(
        ImportedLayout layout,
        ClientObjectTable objects,
        ItemInteractionController interaction,
        CombatState combat,
        List<(uint ObjectId, string Text)> inscriptions,
        List<string> messages,
        Action show,
        Action close,
        CreatureAppraisalRowTemplateFactory? creatureRows = null,
        CreatureDisplayNameResolver? creatureNames = null,
        SelectionState? selection = null,
        RetailAppraisalNameResolver? itemNames = null,
        Spellbook? spellbook = null,
        Func<uint, uint>? resolveSpellIcon = null,
        Func<uint, uint>? resolveComponentIcon = null,
        Func<uint, IReadOnlyList<SpellExamineComponent>>? spellComponents = null,
        Func<MagicSchool, uint>? magicSkill = null,
        SpellExamineComponentTemplateFactory? spellComponentTemplates = null,
        Func<uint, string?>? resolveCharacterTitle = null,
        Func<int>? localFactionBits = null)
        => AppraisalUiController.Bind(
            layout,
            objects,
            interaction,
            selection ?? new SelectionState(),
            combat,
            spellbook ?? new Spellbook(),
            () => "Tester",
            (objectId, text) => inscriptions.Add((objectId, text)),
            messages.Add,
            show,
            close,
            creatureRows,
            creatureNames,
            itemNames,
            resolveSpellIcon,
            resolveComponentIcon,
            spellComponents,
            magicSkill,
            spellComponentTemplates,
            resolveCharacterTitle,
            localFactionBits);

    private static ItemInteractionController NewInteraction(
        ClientObjectTable objects,
        List<uint> sent)
        => new(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: () => 0x50000002u,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            sendExamine: sent.Add);

    private static AppraiseInfoParser.Parsed Parsed(
        PropertyBundle properties,
        AppraiseInfoParser.CreatureProfile? creature = null,
        uint guid = ObjectId,
        bool success = true,
        AppraiseInfoParser.ArmorLevel? armorLevels = null)
        => new(
            Guid: guid,
            Flags: (creature is null
                ? AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable
                : AppraiseInfoParser.IdentifyResponseFlags.CreatureProfile)
                | (armorLevels is null
                    ? AppraiseInfoParser.IdentifyResponseFlags.None
                    : AppraiseInfoParser.IdentifyResponseFlags.ArmorLevels),
            Success: success,
            Properties: properties,
            SpellBook: [],
            ArmorProfile: null,
            CreatureProfile: creature,
            WeaponProfile: null,
            HookProfile: null,
            ArmorLevels: armorLevels,
            ArmorEnchantments: null,
            WeaponEnchantments: null,
            ResistEnchantments: null);

    private static AppraiseInfoParser.CreatureProfile MinimalCreatureProfile()
        => new(
            Flags: 0,
            Health: 1u,
            HealthMax: 1u,
            Strength: null,
            Endurance: null,
            Quickness: null,
            Coordination: null,
            Focus: null,
            Self: null,
            Stamina: null,
            Mana: null,
            StaminaMax: null,
            ManaMax: null,
            AttributeHighlights: null,
            AttributeColors: null);

    private static string HeaderText(ImportedLayout layout, uint elementId)
    {
        UiText text = Assert.IsType<UiText>(layout.FindElement(elementId));
        return string.Join(
            '\n', text.LinesProvider().Select(line => line.Text));
    }

    private static UiItemList CreatureExtraList(ImportedLayout layout)
    {
        UiElement extraHost = layout.FindElement(
            AppraisalUiController.CreatureExtraListId)!;
        UiElement creaturePanel = layout.FindElement(
            AppraisalUiController.CreaturePanelId)!;
        return Assert.Single(
            creaturePanel.Children.OfType<UiItemList>(),
            candidate => candidate.Top == extraHost.Top);
    }

    private static (string Label, string Value) ExtraRow(
        UiItemList extra, int index)
    {
        var slot = Assert.IsType<UiTemplateListSlot>(extra.GetItem(index));
        string label = Assert.Single(((UiText)slot.Content.FindElement(
            CreatureAppraisalRowTemplateFactory.LabelId)!)
            .LinesProvider()).Text;
        string value = Assert.Single(((UiText)slot.Content.FindElement(
            CreatureAppraisalRowTemplateFactory.ValueId)!)
            .LinesProvider()).Text;
        return (label, value);
    }

    private static void AssertSpellText(
        ImportedLayout layout,
        uint elementId,
        string expected)
    {
        UiText text = Assert.IsType<UiText>(layout.FindElement(elementId));
        Assert.Equal(
            expected,
            string.Join('\n', text.LinesProvider().Select(line => line.Text)));
    }

    private static SpellMetadata ExaminedSpell()
        => new(
            SpellId: 42u,
            Name: "Incantation of Test",
            School: "War Magic",
            Family: 1u,
            IconId: 0x06001234u,
            SpellWords: "Malar Aether",
            Duration: 90f,
            ManaCost: 50,
            IsDebuff: false,
            IsFellowship: false,
            Description: "A projected retail spell.",
            SortKey: 0,
            Difficulty: 0,
            Flags: 0u,
            Generation: 6,
            IsFastWindup: false,
            IsOffensive: true,
            IsUntargeted: false,
            Speed: 0f,
            CasterEffect: 0u,
            TargetEffect: 0u,
            TargetMask: 0u,
            SpellType: 0)
        {
            SchoolId = MagicSchool.WarMagic,
            BaseRangeConstant = 40f,
            BaseRangeModifier = 0.25f,
            ManaModifier = 14u,
            FormulaComponents = [10u, 11u],
        };
}
