using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.UI.Abstractions;

namespace AcDream.App.Tests.UI;

public class UiMenuTests
{

    private static readonly UiMenu.MenuItem[] ChannelItems =
    {
        new("Squelch (ignore)",      (object?)null),
        new("Tell to Selected",      (object?)null),
        new("Chat to All",           (object?)ChatChannelKind.Say),
        new("Tell to Fellows",       (object?)ChatChannelKind.Fellowship),
        new("Tell to General Chat",  (object?)ChatChannelKind.General),
        new("Tell to LFG Chat",      (object?)ChatChannelKind.Lfg),
        new("Tell to Society Chat",  (object?)ChatChannelKind.Society),
        new("Tell to Monarch",       (object?)ChatChannelKind.Monarch),
        new("Tell to Patron",        (object?)ChatChannelKind.Patron),
        new("Tell to Vassals",       (object?)ChatChannelKind.Vassals),
        new("Tell to Allegiance",    (object?)ChatChannelKind.Allegiance),
        new("Tell to Trade Chat",    (object?)ChatChannelKind.Trade),
        new("Tell to Roleplay Chat", (object?)ChatChannelKind.Roleplay),
        new("Tell to Olthoi Chat",   (object?)ChatChannelKind.Olthoi),
    };

    private static bool ChannelAvailable(object? p)
        => p is not ChatChannelKind ch
           || ch is ChatChannelKind.Say or ChatChannelKind.General
                  or ChatChannelKind.Trade or ChatChannelKind.Lfg;

    private UiMenu MakeMenu() => new UiMenu
    {
        Width = 80f, Height = 18f,
        Items = ChannelItems,
        Selected = (object?)ChatChannelKind.Say,
        EnabledProvider = ChannelAvailable,
    };

    [Fact]
    public void Items_HasExpected14Entries()
    {
        Assert.Equal(14, ChannelItems.Length);
    }

    [Fact]
    public void Items_FirstEntry_IsSquelch_Special()
    {
        Assert.Equal("Squelch (ignore)", ChannelItems[0].Label);
        Assert.Null(ChannelItems[0].Payload);
    }

    [Fact]
    public void Items_LastEntry_IsOlthoi()
    {
        var last = ChannelItems[^1];
        Assert.Equal("Tell to Olthoi Chat", last.Label);
        Assert.Equal(ChatChannelKind.Olthoi, last.Payload);
    }

    [Fact]
    public void Items_ContainAll12ChannelKinds()
    {
        var kinds = new HashSet<ChatChannelKind>(
            ChannelItems.Where(i => i.Payload is ChatChannelKind).Select(i => (ChatChannelKind)i.Payload!));
        foreach (var k in new[]
        {
            ChatChannelKind.Say, ChatChannelKind.General, ChatChannelKind.Trade, ChatChannelKind.Lfg,
            ChatChannelKind.Fellowship, ChatChannelKind.Allegiance, ChatChannelKind.Patron,
            ChatChannelKind.Vassals, ChatChannelKind.Monarch, ChatChannelKind.Roleplay,
            ChatChannelKind.Society, ChatChannelKind.Olthoi,
        })
            Assert.Contains(k, kinds);
    }

    [Fact]
    public void DefaultSelected_IsNull_OnBlankMenu()
    {
        Assert.Null(new UiMenu().Selected);
    }

    [Fact]
    public void Select_AvailableLeftColumnItem_FiresOnSelect()
    {
        var menu = MakeMenu();
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open

        object? fired = null;
        menu.OnSelect = p => { fired = p; if (p is ChatChannelKind) menu.Selected = p; };

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, -76)));
        Assert.Equal(ChatChannelKind.Say, fired);
        Assert.Equal(ChatChannelKind.Say, menu.Selected);
    }

    [Fact]
    public void Select_AvailableRightColumnItem_FiresOnSelect()
    {
        var menu = MakeMenu();
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open

        object? fired = null;
        menu.OnSelect = p => { fired = p; if (p is ChatChannelKind) menu.Selected = p; };

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 200, -42)));
        Assert.Equal(ChatChannelKind.Trade, fired);
        Assert.Equal(ChatChannelKind.Trade, menu.Selected);
    }

    [Fact]
    public void Select_SpecialItem_FiresNull_LeavesSelectionUnchanged()
    {
        var menu = MakeMenu();   // Selected = Say
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open

        bool fired = false; object? firedPayload = "sentinel";
        menu.OnSelect = p => { fired = true; firedPayload = p; if (p is ChatChannelKind) menu.Selected = p; };

        // "Squelch (ignore)" is index 0 = left col, row 0 (null payload), white/enabled.
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, -110)));
        Assert.True(fired);                                   // the pick IS reported...
        Assert.Null(firedPayload);                            // ...with the special's null payload
        Assert.Equal(ChatChannelKind.Say, menu.Selected);    // ...but selection is unchanged (deferred no-op)
    }

    [Fact]
    public void Select_UnavailableChannel_DoesNotFire()
    {
        var menu = MakeMenu();
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open
        int fired = 0;
        menu.OnSelect = _ => fired++;

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, -60)));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void EnabledProvider_Overrides_DefaultGate()
    {
        // Override: all items enabled (even Fellowship which is normally greyed).
        var menu = new UiMenu
        {
            Width = 80f, Height = 18f,
            Items = ChannelItems,
            Selected = (object?)ChatChannelKind.Say,
            EnabledProvider = _ => true,
        };
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open

        object? fired = null;
        menu.OnSelect = p => fired = p;

        // With every item enabled, "Tell to Fellows" (idx 3, row 3) now fires.
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, -60)));
        Assert.Equal(ChatChannelKind.Fellowship, fired);
    }


    private const int Border = 5;

    private static UiMenu.MenuItem[] MakeCategoryItems(int count)
        => System.Linq.Enumerable.Range(0, count)
            .Select(i => new UiMenu.MenuItem($"Category {i}", (object?)i))
            .ToArray();

    private static UiMenu MakeScrollableMenu(int itemCount = 18) => new UiMenu
    {
        Width = 100f, Height = 18f,
        Items = MakeCategoryItems(itemCount),
        Selected = (object?)0,
        Scrollable = true,
        RowsPerColumn = 6,
        RowHeight = 18f,
        ColumnWidth = 100f,
        ScrollbarWidth = 16f,
        ScrollButtonExtent = 16f,
    };

    private static int RawY(UiMenu menu, float iy)
    {
        float outerH = menu.RowsPerColumn * menu.RowHeight + 2 * Border;
        return (int)(iy - outerH + Border);
    }

    /// <summary>Raw event Data1 ("lx") for a point at popup-interior-local X <paramref name="ix"/>.</summary>
    private static int RawX(float ix) => (int)(ix + Border);

    [Fact]
    public void Scrollable_18Categories_ConfiguresScrollExtentsFromAuthoredGeometry()
    {
        var menu = MakeScrollableMenu(18);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5));   // open

        menu.OnEvent(new UiEvent(0, menu, UiEventType.Scroll, Data0: 0));

        Assert.Equal(18 * 18, menu.PopupScroll.ContentHeight);   // 18 items * 18px row height
        Assert.Equal(6 * 18, menu.PopupScroll.ViewHeight);       // 6 visible rows (the authored window)
        Assert.True(menu.PopupScroll.HasOverflow);               // 18 > 6 -> scrollbar warranted
    }

    [Fact]
    public void Scrollable_ClickInFirstVisibleRow_SelectsItemZero_ThroughTheRealHitPath()
    {
        var menu = MakeScrollableMenu(18);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5));   // open
        object? fired = null;
        menu.OnSelect = p => fired = p;

        int ly = RawY(menu, menu.RowHeight / 2f);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, RawX(10), ly)));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Scrollable_ClickInScrollbarColumn_DoesNotSelectAnItem_AndKeepsThePopupOpen()
    {
        var menu = MakeScrollableMenu(18);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5));   // open
        var fired = new List<object?>();
        menu.OnSelect = p => fired.Add(p);

        // Middle of the scrollbar TRACK (below the up-button, above the down-button).
        int scrollbarMidX = RawX(menu.ColumnWidth + menu.ScrollbarWidth / 2f);
        int trackMidY = RawY(menu, menu.RowsPerColumn * menu.RowHeight / 2f);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, scrollbarMidX, trackMidY)));

        Assert.Empty(fired);

        int rowLy = RawY(menu, menu.RowHeight / 2f);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, RawX(10), rowLy)));
        Assert.Single(fired);
    }

    [Fact]
    public void Scrollable_DownButtonClick_AdvancesByOneRow_AndSubsequentClickPicksTheAdvancedItem()
    {
        var menu = MakeScrollableMenu(18);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5));   // open

        int downX = RawX(menu.ColumnWidth + menu.ScrollbarWidth / 2f);
        int downY = RawY(menu, menu.RowsPerColumn * menu.RowHeight - menu.ScrollButtonExtent / 2f);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, downX, downY)));

        Assert.Equal((int)menu.RowHeight, menu.PopupScroll.ScrollY);   // scrolled exactly one row

        object? fired = null;
        menu.OnSelect = p => fired = p;
        // Row 0's ON-SCREEN position now shows item index 1 (the window advanced).
        int ly = RawY(menu, menu.RowHeight / 2f);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, RawX(10), ly)));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Scrollable_UpButtonClick_ReversesAPriorDownScroll()
    {
        var menu = MakeScrollableMenu(18);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5));

        int scrollbarMidX = RawX(menu.ColumnWidth + menu.ScrollbarWidth / 2f);
        int downY = RawY(menu, menu.RowsPerColumn * menu.RowHeight - menu.ScrollButtonExtent / 2f);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, scrollbarMidX, downY));
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, scrollbarMidX, downY));
        Assert.Equal((int)(2 * menu.RowHeight), menu.PopupScroll.ScrollY);

        int upY = RawY(menu, menu.ScrollButtonExtent / 2f);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, scrollbarMidX, upY)));

        Assert.Equal((int)menu.RowHeight, menu.PopupScroll.ScrollY);
    }

    [Fact]
    public void Scrollable_MouseWheel_ScrollsWhilePopupIsOpen()
    {
        var menu = MakeScrollableMenu(18);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5));   // open

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.Scroll, Data0: -1)));

        Assert.Equal((int)menu.RowHeight, menu.PopupScroll.ScrollY);
    }

    [Fact]
    public void Scrollable_ThumbDrag_MovesScrollPosition_AndReleaseKeepsThePopupOpen()
    {
        var menu = MakeScrollableMenu(18);
        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5));   // open

        int trackTopY = (int)menu.ScrollButtonExtent;
        float trackLen = menu.RowsPerColumn * menu.RowHeight - 2 * menu.ScrollButtonExtent;
        menu.OnEvent(new UiEvent(0, menu, UiEventType.Scroll, Data0: 0));
        var (thumbY, thumbH) = UiScrollbar.ThumbRect(menu.PopupScroll, trackTopY, trackLen);

        int scrollbarMidX = RawX(menu.ColumnWidth + menu.ScrollbarWidth / 2f);
        int pressY = RawY(menu, thumbY + thumbH / 2f);   // press inside the thumb
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, scrollbarMidX, pressY)));

        // Drag most of the way down the track.
        int dragToIy = (int)(trackTopY + trackLen - thumbH / 2f);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseMove, 0, scrollbarMidX, RawY(menu, dragToIy))));

        Assert.True(menu.PopupScroll.ScrollY > 0);
        Assert.True(menu.PopupScroll.PositionRatio > 0.5f);

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseUp, 0, scrollbarMidX, RawY(menu, dragToIy))));
        object? fired = null;
        menu.OnSelect = p => fired = p;
        int ly = RawY(menu, menu.RowHeight / 2f);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, RawX(10), ly)));
        Assert.NotNull(fired);
    }


    [Fact]
    public void ArrowCapSprite_FlipsBetweenClosedAndOpen_OnToggle()
    {
        var menu = new UiMenu
        {
            Width = 80f, Height = 18f,
            Items = ChannelItems,
            ArrowCapClosedSprite = 111u,
            ArrowCapOpenSprite = 222u,
        };

        Assert.Equal(111u, menu.CurrentArrowCapSprite);

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open
        Assert.Equal(222u, menu.CurrentArrowCapSprite);

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, -1)));
        Assert.Equal(111u, menu.CurrentArrowCapSprite);
    }

    [Fact]
    public void ArrowCapSprite_DefaultsToZero_NoOpForMenusThatDontAuthorOne()
    {
        var menu = new UiMenu();
        Assert.Equal(0u, menu.CurrentArrowCapSprite);
        Assert.Equal(0u, menu.ArrowCapClosedSprite);
        Assert.Equal(0u, menu.ArrowCapOpenSprite);
    }

    [Fact]
    public void OpenUpward_DefaultsTrue_PreservingChatsExistingUpwardGeometry()
    {
        Assert.True(new UiMenu().OpenUpward);
    }

    [Fact]
    public void OpenUpward_False_MovesThePopupBelowTheButton_NotAbove()
    {
        var menu = new UiMenu
        {
            Width = 80f, Height = 18f,
            Items = ChannelItems,
            Selected = (object?)ChatChannelKind.Say,
            EnabledProvider = ChannelAvailable,
            OpenUpward = false,
        };

        object? fired = null;
        menu.OnSelect = p => fired = p;

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, -76)));
        Assert.Null(fired);

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open
        const int border = 5;
        float iy = 2 * menu.RowHeight + menu.RowHeight / 2f;
        int ly = (int)(menu.Height + iy + border);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, ly)));
        Assert.Equal(ChatChannelKind.Say, fired);
    }

    [Fact]
    public void OpenUpward_False_HitTestCoversTheButtonAndTheDownwardPopup()
    {
        var menu = new UiMenu { Width = 80f, Height = 18f, Items = ChannelItems, OpenUpward = false };
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open

        float outerH = menu.RowsPerColumn * menu.RowHeight + 2 * 5;
        int belowPopupBottom = (int)(menu.Height + outerH) + 1;   // just past the popup's own bottom edge
        // Outside the popup entirely -> falls through to the button-toggle path.
        object? fired = null;
        menu.OnSelect = p => fired = p;
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, belowPopupBottom)));
        Assert.Null(fired);
    }

    [Fact]
    public void TextIndent_And_ButtonTextIndent_DefaultToChatsBakedIconOffsets()
    {
        var menu = new UiMenu();
        Assert.Equal(19f, menu.TextIndent);
        Assert.Equal(20f, menu.ButtonTextIndent);
    }

    [Fact]
    public void TextIndent_And_ButtonTextIndent_AreSettable_ForIconLessMenus()
    {
        var menu = new UiMenu { TextIndent = 0f, ButtonTextIndent = 0f };
        Assert.Equal(0f, menu.TextIndent);
        Assert.Equal(0f, menu.ButtonTextIndent);
    }


    private static UiMenu MakeScrollableMenu(int itemCount, bool sizeToContent) => new UiMenu
    {
        Width = 120f, Height = 18f,
        Scrollable = true,
        OpenUpward = false,
        RowsPerColumn = 6,
        RowHeight = 18f,
        ColumnWidth = 100f,
        PopupSizeToContent = sizeToContent,
        Items = Enumerable.Range(0, itemCount)
            .Select(i => new UiMenu.MenuItem($"Item {i}", (object?)i)).ToArray(),
    };

    [Fact]
    public void SizeToContent_ShrinksThePopupToTheItemCount()
    {
        // 3 items: interior 3*18, plus the 5px bevel top+bottom.
        Assert.Equal(3 * 18f + 10f, MakeScrollableMenu(3, sizeToContent: true).PopupOuterHeight);
        Assert.Equal(6 * 18f + 10f, MakeScrollableMenu(3, sizeToContent: false).PopupOuterHeight);
    }

    [Fact]
    public void SizeToContent_GrowsPastTheFixedWindow_AndTheLastRowIsPickable()
    {
        UiMenu menu = MakeScrollableMenu(9, sizeToContent: true);
        Assert.Equal(9 * 18f + 10f, menu.PopupOuterHeight);

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));   // open
        Assert.True(menu.IsOpen);

        object? fired = null;
        menu.OnSelect = p => fired = p;
        // Row 8 (the 9th item) sits past the old 6-row window: it must be
        // directly pickable with NO scroll. Downward popup: interior starts
        // at Height + border.
        float ly = menu.Height + 5f + 8 * menu.RowHeight + menu.RowHeight / 2f;
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, (int)ly)));
        Assert.Equal(8, fired);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void SizeToContent_WithAuthoredHideWhenDisabled_HidesTheScrollbarAndItsInput()
    {
        UiMenu menu = MakeScrollableMenu(3, sizeToContent: true);
        menu.PopupScrollbarHideWhenDisabled = true;
        Assert.Equal(menu.ColumnWidth + 2 * 5f, menu.PopupOuterWidth);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5))); // open

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.Scroll, Data0: 0)));
        Assert.False(menu.PopupScroll.HasOverflow);
        Assert.False(menu.IsPopupScrollbarPresentationVisible);

        float lx = 5f + menu.ColumnWidth + menu.ScrollbarWidth / 2f;
        float ly = menu.Height + 5f + menu.RowHeight / 2f;
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, (int)lx, (int)ly)));
        Assert.Equal(0, menu.PopupScroll.ScrollY);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void FixedViewport_WithOverflow_KeepsHideWhenDisabledScrollbarVisible()
    {
        UiMenu menu = MakeScrollableMenu(18, sizeToContent: false);
        menu.PopupScrollbarHideWhenDisabled = true;
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5))); // open
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.Scroll, Data0: 0)));

        Assert.True(menu.PopupScroll.HasOverflow);
        Assert.True(menu.IsPopupScrollbarPresentationVisible);
        Assert.Equal(menu.ColumnWidth + menu.ScrollbarWidth + 2 * 5f, menu.PopupOuterWidth);
    }

    [Fact]
    public void EmptyItems_ButtonClickDoesNotOpen()
    {
        UiMenu menu = MakeScrollableMenu(0, sizeToContent: true);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void MenuRetailBehaviorFlags_DefaultOff_ForMenusThatDoNotAuthorThem()
    {
        var menu = new UiMenu();
        Assert.False(menu.ButtonTextCentered);
        Assert.False(menu.ItemTextCentered);
        Assert.False(menu.PopupSizeToContent);
        Assert.False(menu.PopupScrollbarHideWhenDisabled);
    }

    [Fact]
    public void ButtonFace_FlicksPressedOnClick_NotLatchedWhileOpen()
    {
        UiMenu menu = MakeMenu();
        menu.NormalSprite = 10u;
        menu.PressedSprite = 20u;

        Assert.Equal(10u, menu.CurrentFaceSpriteForTest);

        // Press on the face: pressed art while the button is held.
        menu.OnEvent(new UiEvent(0u, menu, UiEventType.MouseDown, Data1: 5, Data2: 5));
        Assert.True(menu.IsOpen);
        Assert.Equal(20u, menu.CurrentFaceSpriteForTest);

        // Release: the flick ends — face returns to normal WHILE open.
        menu.OnEvent(new UiEvent(0u, menu, UiEventType.MouseUp, Data1: 5, Data2: 5));
        Assert.True(menu.IsOpen);
        Assert.Equal(10u, menu.CurrentFaceSpriteForTest);

        menu.OnEvent(new UiEvent(0u, menu, UiEventType.MouseDown, Data1: 5, Data2: 5));
        Assert.False(menu.IsOpen);
        Assert.Equal(20u, menu.CurrentFaceSpriteForTest);
        menu.OnEvent(new UiEvent(0u, menu, UiEventType.MouseUp, Data1: 5, Data2: 5));
        Assert.Equal(10u, menu.CurrentFaceSpriteForTest);
    }
}
