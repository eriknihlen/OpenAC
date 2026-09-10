using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public class UiItemSlotTests
{
    [Fact]
    public void IsLeafWidget()
        => Assert.True(new UiItemSlot().ConsumesDatChildren);

    [Fact]
    public void DefaultEmptySprite_isToolbarBorder()
        => Assert.Equal(0x060074CFu, new UiItemSlot().EmptySprite);

    [Fact]
    public void DefaultWaitingSprite_isRetailGhostMesh()
        => Assert.Equal(0x0600109Au, new UiItemSlot().WaitingSprite);

    [Fact]
    public void Empty_whenNoItem()
    {
        var s = new UiItemSlot();
        Assert.Equal(0u, s.ItemId);
        Assert.Equal(0u, s.IconTexture);
    }

    [Fact]
    public void SetItem_setsIdAndTexture()
    {
        var s = new UiItemSlot();
        s.SetItem(0x5001u, 0x99u);
        Assert.Equal(0x5001u, s.ItemId);
        Assert.Equal(0x99u, s.IconTexture);
    }

    [Fact]
    public void Clear_afterSetItem_resetsToEmpty()
    {
        var s = new UiItemSlot();
        s.SetItem(0x5001u, 0x99u);
        s.Clear();
        Assert.Equal(0u, s.ItemId);
        Assert.Equal(0u, s.IconTexture);
    }

    [Fact]
    public void DoubleClick_invokesDoubleClicked()
    {
        var s = new UiItemSlot();
        bool fired = false;
        s.DoubleClicked = () => fired = true;

        s.OnEvent(new UiEvent(0u, s, UiEventType.DoubleClick));

        Assert.True(fired);
    }

    [Fact]
    public void CatalogSlot_keeps_catalog_identity_separate_from_object_guid()
    {
        var slot = new UiCatalogSlot { EntryId = 1234u, CatalogIconTexture = 99u };

        Assert.Equal(1234u, slot.EntryId);
        Assert.Equal(0u, slot.ItemId);
        Assert.False(slot.IsDragSource);
        Assert.Null(slot.GetDragPayload());
    }


    [Fact]
    public void AllowDragSource_DefaultsTrue_OccupiedSlotIsADragSource()
    {
        var s = new UiItemSlot();
        s.SetItem(0x5001u, 0x99u);

        Assert.True(s.IsDragSource);
        Assert.NotNull(s.GetDragPayload());
    }

    [Fact]
    public void AllowDragSource_False_OccupiedSlotIsNeverADragSource()
    {
        var s = new UiItemSlot { AllowDragSource = false };
        s.SetItem(0x5001u, 0x99u);

        Assert.False(s.IsDragSource);
    }


    [Fact]
    public void ShortcutNum_defaultIsMinusOne()
    {
        var s = new UiItemSlot();
        Assert.Equal(-1, s.ShortcutNum);
    }

    [Fact]
    public void ShortcutGhosted_defaultsFalse()
    {
        var s = new UiItemSlot();
        Assert.False(s.ShortcutGhosted);
    }

    [Fact]
    public void SetShortcutNum_setsIndexAndGhostedState()
    {
        var s = new UiItemSlot();
        s.SetShortcutNum(3, ghosted: true);
        Assert.Equal(3, s.ShortcutNum);
        Assert.True(s.ShortcutGhosted);
    }

    [Fact]
    public void SetShortcutNum_regularState()
    {
        var s = new UiItemSlot();
        s.SetShortcutNum(0, ghosted: false);
        Assert.Equal(0, s.ShortcutNum);
        Assert.False(s.ShortcutGhosted);
    }

    [Fact]
    public void ClearShortcutNum_setsMinusOne()
    {
        var s = new UiItemSlot();
        s.SetShortcutNum(5, ghosted: false);
        s.ClearShortcutNum();
        Assert.Equal(-1, s.ShortcutNum);
    }


    private static readonly uint[] Regular = { 0x10u, 0x11u, 0x12u };
    private static readonly uint[] Ghosted = { 0x20u, 0x21u, 0x22u };
    private static readonly uint[] Empty = { 0x30u, 0x31u, 0x32u };

    [Fact]
    public void ActiveDigitArray_emptySlot_returnsEmptyDigits()
    {
        var s = new UiItemSlot { RegularDigits = Regular, GhostedDigits = Ghosted, EmptyDigits = Empty };
        s.SetShortcutNum(0, ghosted: false);
        // ItemId == 0 → EmptyDigits
        Assert.Same(Empty, s.ActiveDigitArray());
    }

    [Fact]
    public void ActiveDigitArray_emptySlot_ghosted_stillReturnsEmptyDigits()
    {
        var s = new UiItemSlot { RegularDigits = Regular, GhostedDigits = Ghosted, EmptyDigits = Empty };
        s.SetShortcutNum(0, ghosted: true);
        // ItemId == 0 → EmptyDigits regardless of stance
        Assert.Same(Empty, s.ActiveDigitArray());
    }

    [Fact]
    public void ActiveDigitArray_occupiedSlot_regular_returnsRegularDigits()
    {
        var s = new UiItemSlot { RegularDigits = Regular, GhostedDigits = Ghosted, EmptyDigits = Empty };
        s.SetItem(0x5001u, 0x99u);
        s.SetShortcutNum(0, ghosted: false);
        Assert.Same(Regular, s.ActiveDigitArray());
    }

    [Fact]
    public void ActiveDigitArray_occupiedSlot_ghosted_returnsGhostedDigits()
    {
        var s = new UiItemSlot { RegularDigits = Regular, GhostedDigits = Ghosted, EmptyDigits = Empty };
        s.SetItem(0x5001u, 0x99u);
        s.SetShortcutNum(0, ghosted: true);
        Assert.Same(Ghosted, s.ActiveDigitArray());
    }

    [Fact]
    public void ActiveDigitArray_emptySlot_nullEmptyDigits_returnsNull()
    {
        var s = new UiItemSlot { RegularDigits = Regular, GhostedDigits = Ghosted, EmptyDigits = null };
        s.SetShortcutNum(0, ghosted: false);
        Assert.Null(s.ActiveDigitArray());
    }

    [Fact]
    public void Selected_defaultsFalse() => Assert.False(new UiItemSlot().Selected);

    [Fact]
    public void IsOpenContainer_defaultsFalse() => Assert.False(new UiItemSlot().IsOpenContainer);

    [Fact]
    public void SelectedSprite_default_isGreenYellowSquare()
        => Assert.Equal(0x06004D21u, new UiItemSlot().SelectedSprite);

    [Fact]
    public void OpenContainerSprite_default_isTriangle()
        => Assert.Equal(0x06005D9Cu, new UiItemSlot().OpenContainerSprite);

    [Fact]
    public void CapacityFill_defaultsHidden() => Assert.Equal(-1f, new UiItemSlot().CapacityFill);

    [Fact]
    public void CapacityBackSprite_default() => Assert.Equal(0x06004D22u, new UiItemSlot().CapacityBackSprite);

    [Fact]
    public void CapacityFrontSprite_default() => Assert.Equal(0x06004D23u, new UiItemSlot().CapacityFrontSprite);

    [Fact]
    public void StructureFill_defaultsHidden() => Assert.Equal(-1f, new UiItemSlot().StructureFill);

    [Fact]
    public void StructureSprites_defaultToTheAuthoredStructureMeter()
    {
        var slot = new UiItemSlot();

        Assert.Equal(0x06004D24u, slot.StructureBackSprite);
        Assert.Equal(0x06004D25u, slot.StructureFrontSprite);
    }

    [Fact]
    public void ActiveCooldownSprite_selects_the_exact_one_based_step()
    {
        uint[] sprites =
        [
            0x06000001u, 0x06000002u, 0x06000003u, 0x06000004u, 0x06000005u,
            0x06000006u, 0x06000007u, 0x06000008u, 0x06000009u, 0x0600000Au,
        ];
        var slot = new UiItemSlot
        {
            CooldownSprites = sprites,
            CooldownStepProvider = id => id == 0x5001u ? 6 : 0,
        };
        slot.SetItem(0x5001u, 99u);

        Assert.Equal(0x06000006u, slot.ActiveCooldownSprite());
    }

    [Fact]
    public void ActiveCooldownSprite_isHidden_for_empty_or_inactive_slot()
    {
        var slot = new UiItemSlot
        {
            CooldownSprites = Enumerable.Range(1, 10).Select(i => (uint)i).ToArray(),
            CooldownStepProvider = _ => 0,
        };

        Assert.Equal(0u, slot.ActiveCooldownSprite());
        slot.SetItem(0x5001u, 99u);
        Assert.Equal(0u, slot.ActiveCooldownSprite());
    }
}
