using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailPanelUiControllerTests
{
    [Fact]
    public void ShowingPanel_HidesCurrent_AndClosingLeavesHostEmpty()
    {
        var visible = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["inventory"] = false,
            ["helpful"] = false,
        };
        var controller = Create(visible);
        controller.Register(7u, "inventory");
        controller.Register(4u, "helpful");

        Assert.True(controller.SetPanelVisibility(7u, true));
        Assert.True(visible["inventory"]);
        Assert.Equal(7u, controller.ActivePanelId);

        Assert.True(controller.SetPanelVisibility(4u, true));
        Assert.False(visible["inventory"]);
        Assert.True(visible["helpful"]);
        Assert.Equal(4u, controller.ActivePanelId);

        Assert.False(controller.TogglePanel(4u));
        Assert.False(visible["helpful"]);
        Assert.Null(controller.ActivePanelId);
    }

    [Fact]
    public void RestorePreviousProperty_ReopensOnlyNonRestoringPreviousPanel()
    {
        var visible = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["ordinary"] = false,
            ["transient"] = false,
        };
        var controller = Create(visible);
        controller.Register(1u, "ordinary", restorePrevious: false);
        controller.Register(2u, "transient", restorePrevious: true);

        controller.SetPanelVisibility(1u, true);
        controller.SetPanelVisibility(2u, true);
        Assert.False(visible["ordinary"]);
        Assert.True(visible["transient"]);

        controller.SetPanelVisibility(2u, false);
        Assert.True(visible["ordinary"]);
        Assert.False(visible["transient"]);
        Assert.Equal(1u, controller.ActivePanelId);
    }

    [Fact]
    public void ObservedPersistenceShow_UsesSameExclusiveLifecycle()
    {
        var visible = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["first"] = false,
            ["second"] = false,
        };
        var controller = Create(visible);
        controller.Register(1u, "first");
        controller.Register(2u, "second");
        controller.SetPanelVisibility(1u, true);

        visible["second"] = true;
        controller.ObserveWindowVisibility("second", visible: true);

        Assert.False(visible["first"]);
        Assert.True(visible["second"]);
        Assert.Equal(2u, controller.ActivePanelId);
    }

    [Fact]
    public void MainPanels_ShareOneCanonicalPlacementAcrossMoveCloseAndSwitch()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        RetailWindowHandle inventory = Mount(root, WindowNames.Inventory, 40f, 60f, 360f);
        RetailWindowHandle character = Mount(root, WindowNames.Character, 540f, 18f, 500f);
        RetailWindowHandle spellbook = Mount(root, WindowNames.Spellbook, 18f, 18f, 420f);
        using var controller = Create(root);
        controller.RegisterMainPanel(7u, WindowNames.Inventory, inventory);
        controller.RegisterMainPanel(11u, WindowNames.Character, character);
        controller.RegisterMainPanel(13u, WindowNames.Spellbook, spellbook);

        int characterMoves = 0;
        int spellbookMoves = 0;
        character.Moved += _ => characterMoves++;
        spellbook.Moved += _ => spellbookMoves++;

        controller.SetPanelVisibility(7u, visible: true);
        inventory.MoveTo(312f, 147f);
        inventory.ResizeTo(310f, 540f);

        Assert.Equal((312f, 147f), (character.Left, character.Top));
        Assert.Equal((312f, 147f), (spellbook.Left, spellbook.Top));
        Assert.Equal((310f, 540f), (character.Width, character.Height));
        Assert.Equal((310f, 540f), (spellbook.Width, spellbook.Height));
        Assert.True(characterMoves > 0);
        Assert.True(spellbookMoves > 0);

        controller.SetPanelVisibility(11u, visible: true);
        Assert.False(inventory.IsVisible);
        Assert.True(character.IsVisible);
        Assert.Equal((312f, 147f), (character.Left, character.Top));
        Assert.Equal((310f, 540f), (character.Width, character.Height));

        controller.SetPanelVisibility(11u, visible: false);
        controller.SetPanelVisibility(13u, visible: true);
        Assert.False(character.IsVisible);
        Assert.True(spellbook.IsVisible);
        Assert.Equal((312f, 147f), (spellbook.Left, spellbook.Top));
        Assert.Equal((310f, 540f), (spellbook.Width, spellbook.Height));
    }

    [Fact]
    public void Options_AndCharacter_ShareExclusiveMainPanelLifecycle()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        RetailWindowHandle character = Mount(root, WindowNames.Character, 540f, 18f);
        RetailWindowHandle options = Mount(root, WindowNames.Options, 150f, 80f);
        using var controller = Create(root);
        controller.RegisterMainPanel(RetailPanelCatalog.Character, WindowNames.Character, character);
        controller.RegisterMainPanel(RetailPanelCatalog.Options, WindowNames.Options, options);

        controller.SetPanelVisibility(RetailPanelCatalog.Character, visible: true);
        Assert.True(character.IsVisible);
        Assert.False(options.IsVisible);
        Assert.Equal(RetailPanelCatalog.Character, controller.ActivePanelId);

        controller.SetPanelVisibility(RetailPanelCatalog.Options, visible: true);
        Assert.False(character.IsVisible);
        Assert.True(options.IsVisible);
        Assert.Equal(RetailPanelCatalog.Options, controller.ActivePanelId);
    }

    [Fact]
    public void IndicatorDetailPanel_SharesPlacementAndRestoresPreviousPanel()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        RetailWindowHandle inventory = Mount(root, WindowNames.Inventory, 40f, 60f);
        RetailWindowHandle character = Mount(root, WindowNames.Character, 540f, 18f);
        RetailWindowHandle effect = Mount(root, WindowNames.PositiveEffects, 900f, 30f);
        using var controller = Create(root);
        controller.RegisterMainPanel(7u, WindowNames.Inventory, inventory);
        controller.RegisterMainPanel(11u, WindowNames.Character, character);
        controller.RegisterMainPanel(
            4u,
            WindowNames.PositiveEffects,
            effect,
            restorePrevious: true);

        controller.SetPanelVisibility(7u, visible: true);
        inventory.MoveTo(280f, 120f);
        inventory.ResizeTo(310f, 600f);
        controller.SetPanelVisibility(4u, visible: true);

        Assert.Equal((280f, 120f), (effect.Left, effect.Top));
        Assert.Equal((310f, 600f), (effect.Width, effect.Height));
        Assert.False(inventory.IsVisible);
        Assert.True(effect.IsVisible);

        controller.SetPanelVisibility(4u, visible: false);
        Assert.True(inventory.IsVisible);
        Assert.Equal((280f, 120f), (inventory.Left, inventory.Top));
        Assert.Equal((310f, 600f), (inventory.Width, inventory.Height));

        controller.SetPanelVisibility(11u, visible: true);
        Assert.Equal((280f, 120f), (character.Left, character.Top));
    }

    private static RetailPanelUiController Create(Dictionary<string, bool> visible)
        => new(
            name => visible[name],
            name =>
            {
                visible[name] = true;
                return true;
            },
            name =>
            {
                visible[name] = false;
                return true;
            });

    private static RetailPanelUiController Create(UiRoot root)
        => new(root.IsWindowVisible, root.ShowWindow, root.HideWindow);

    private static RetailWindowHandle Mount(
        UiRoot root,
        string name,
        float left,
        float top,
        float contentHeight = 500f)
    {
        var content = new UiPanel { Width = 300f, Height = contentHeight };
        return RetailWindowFrame.Mount(
            root,
            content,
            _ => (0u, 0, 0),
            new RetailWindowFrame.Options
            {
                WindowName = name,
                Left = left,
                Top = top,
                Visible = false,
                ConstrainDragToParent = true,
            });
    }
}
