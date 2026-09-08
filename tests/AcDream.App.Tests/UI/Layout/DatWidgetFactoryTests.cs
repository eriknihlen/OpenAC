using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
namespace AcDream.App.Tests.UI.Layout;

public class DatWidgetFactoryTests
{
    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);

    // ── Test 1: Type 7 → UiMeter ─────────────────────────────────────────────

    [Fact]
    public void Type7_Meter_MakesUiMeter()
    {
        var e = DatWidgetFactory.Create(new ElementInfo { Type = 7, Width = 150, Height = 16 }, NoTex, null);
        Assert.IsType<UiMeter>(e);
    }

    [Fact]
    public void RetailRadarClass_MakesUiRadar()
    {
        var e = DatWidgetFactory.Create(
            new ElementInfo { Type = UiRadar.RetailClassId, Width = 120, Height = 140 },
            NoTex,
            null);

        Assert.IsType<UiRadar>(e);
    }


    [Theory]
    [InlineData(0x13u)]
    [InlineData(0x15u)]
    [InlineData(0x17u)]
    [InlineData(0x19u)]
    public void DialogRootTypes_MakeUiDialogRoot(uint type)
    {
        var e = DatWidgetFactory.Create(
            new ElementInfo { Type = type, Width = 800, Height = 600 }, NoTex, null);
        Assert.IsType<UiDialogRoot>(e);
    }

    // ── Test 2: Unknown type → UiDatElement fallback ─────────────────────────

    [Fact]
    public void UnknownType_FallsBackToGeneric()
    {
        var e = DatWidgetFactory.Create(new ElementInfo { Type = 999 }, NoTex, null);
        Assert.IsType<UiDatElement>(e);
    }

    [Fact]
    public void Type2_Dragbar_IsWindowMoveHandle_AndClaimsThePointer()
    {
        var e = DatWidgetFactory.Create(
            new ElementInfo { Type = 2, Width = 600, Height = 5 }, NoTex, null);

        Assert.IsType<UiDatElement>(e);
        Assert.True(e.WindowMoveHandle);
        Assert.False(e.ClickThrough);
    }


    [Fact]
    public void Type9_Resizebar_NoBoolsSet_DecodesToNoneEdges()
    {
        var e = DatWidgetFactory.Create(GripInfo(), NoTex, null);
        var grip = Assert.IsType<UiResizeGrip>(e);
        Assert.Equal(UiResizeGrip.Border.None, grip.BorderLocation);
        Assert.Equal(ResizeEdges.None, grip.Edges);
    }

    [Theory]
    [InlineData(false, false, false, true, UiResizeGrip.Border.Top, ResizeEdges.Top)]
    [InlineData(true, false, false, false, UiResizeGrip.Border.Bottom, ResizeEdges.Bottom)]
    [InlineData(false, true, false, false, UiResizeGrip.Border.Left, ResizeEdges.Left)]
    [InlineData(false, false, true, false, UiResizeGrip.Border.Right, ResizeEdges.Right)]
    [InlineData(false, true, false, true, UiResizeGrip.Border.UpperLeft, ResizeEdges.Left | ResizeEdges.Top)]
    [InlineData(false, false, true, true, UiResizeGrip.Border.UpperRight, ResizeEdges.Right | ResizeEdges.Top)]
    [InlineData(true, true, false, false, UiResizeGrip.Border.LowerLeft, ResizeEdges.Left | ResizeEdges.Bottom)]
    [InlineData(true, false, true, false, UiResizeGrip.Border.LowerRight, ResizeEdges.Right | ResizeEdges.Bottom)]
    public void Type9_Resizebar_DecodesEachOfTheEightAuthoredGrips(
        bool bottom, bool left, bool right, bool top,
        UiResizeGrip.Border expectedBorder, ResizeEdges expectedEdges)
    {
        var e = DatWidgetFactory.Create(GripInfo(bottom, left, right, top), NoTex, null);
        var grip = Assert.IsType<UiResizeGrip>(e);
        Assert.Equal(expectedBorder, grip.BorderLocation);
        Assert.Equal(expectedEdges, grip.Edges);
    }

    [Fact]
    public void Type9_Resizebar_IsNotClickThrough_SoItCanCaptureTheResizeDrag()
    {
        var e = DatWidgetFactory.Create(GripInfo(bottom: true), NoTex, null);
        var grip = Assert.IsType<UiResizeGrip>(e);
        Assert.False(grip.ClickThrough);
    }

    [Fact]
    public void DecodeBorderLocation_MatchesRetailBranchOrder_RightBeatsLeftBeatsTopBeatsBottom()
    {
        Assert.Equal(
            UiResizeGrip.Border.UpperRight,
            UiResizeGrip.DecodeBorderLocation(bottom: true, left: true, right: true, top: true));
        Assert.Equal(
            UiResizeGrip.Border.UpperLeft,
            UiResizeGrip.DecodeBorderLocation(bottom: true, left: true, right: false, top: true));
        Assert.Equal(
            UiResizeGrip.Border.Top,
            UiResizeGrip.DecodeBorderLocation(bottom: true, left: false, right: false, top: true));
    }

    private static ElementInfo GripInfo(bool bottom = false, bool left = false, bool right = false, bool top = false)
    {
        var info = new ElementInfo { Id = 0x1000069Bu, Type = 9, Width = 5, Height = 5 };
        var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        if (bottom) state.Properties.Values[0x2Au] = Bool(true);
        if (left) state.Properties.Values[0x2Bu] = Bool(true);
        if (right) state.Properties.Values[0x2Cu] = Bool(true);
        if (top) state.Properties.Values[0x2Du] = Bool(true);
        info.States[UiStateInfo.DirectStateId] = state;
        return info;
    }

    // ── Test 3: Type 12 → UiText (behavioral text widget) ────────────────────

    [Fact]
    public void Type12_Text_MakesUiText()
    {
        var e = DatWidgetFactory.Create(new ElementInfo { Type = 12, Width = 100, Height = 40 }, NoTex, null);
        var text = Assert.IsType<UiText>(e);
        Assert.False(text.Selectable);
        Assert.True(text.ClickThrough);
        Assert.False(text.AcceptsFocus);
        Assert.False(text.CapturesPointerDrag);
    }

    [Fact]
    public void Type12_EditableProperty_MakesUiFieldInPlace()
    {
        var info = TextInfo(
            (0x16u, Bool(true)),
            (0x20u, Bool(true)),
            (0x27u, Bool(true)),
            (0x1Eu, Integer(80)));

        var field = Assert.IsType<UiField>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.Equal(info.Id, field.ElementId);
        Assert.True(field.OneLine);
        Assert.True(field.Selectable);
        Assert.Equal(80, field.MaxCharacters);
    }

    [Fact]
    public void Type12_EditableField_FoldsAuthoredFocusRailsIntoTheWidget()
    {
        var info = TextInfo((0x16u, Bool(true)));
        info.Width = 306f;
        info.Height = 17f;
        var leftRail = new ElementInfo
        {
            Id = 0x10000017u, Type = 3u, X = 0f, Width = 1f, Height = 17f,
        };
        leftRail.StateMedia["Normal_focussed"] = (0x06004D67u, 1);
        var rightRail = new ElementInfo
        {
            Id = 0x10000018u, Type = 3u, X = 305f, Width = 1f, Height = 17f,
        };
        rightRail.StateMedia["Normal_focussed"] = (0x06004D67u, 1);
        info.Children.Add(leftRail);
        info.Children.Add(rightRail);

        var field = Assert.IsType<UiField>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.Equal(0x06004D67u, field.FocusRailLeftSprite);
        Assert.Equal(1f, field.FocusRailLeftWidth);
        Assert.Equal(0x06004D67u, field.FocusRailRightSprite);
        Assert.Equal(1f, field.FocusRailRightWidth);
    }

    [Fact]
    public void Type12_SelectableProperty_MakesSelectableUiText()
    {
        var text = Assert.IsType<UiText>(DatWidgetFactory.Create(
            TextInfo((0x27u, Bool(true))),
            NoTex,
            null));

        Assert.True(text.Selectable);
        Assert.False(text.ClickThrough);
        Assert.True(text.AcceptsFocus);
        Assert.True(text.CapturesPointerDrag);
    }

    // ── Test 4: Rect + anchors set from ElementInfo ───────────────────────────

    [Fact]
    public void RectAndAnchors_SetFromElementInfo()
    {
        var info = new ElementInfo
        {
            Type   = 3,
            X      = 5,   Y      = 21,
            Width  = 150, Height = 16,
            Left   = 1,   Top    = 1,
            Right  = 1,   Bottom = 0,
        };
        var e = DatWidgetFactory.Create(info, NoTex, null)!;
        Assert.Equal(5f,   e.Left);
        Assert.Equal(21f,  e.Top);
        Assert.Equal(150f, e.Width);
        Assert.Equal(16f,  e.Height);
        Assert.Equal(AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right, e.Anchors);
    }

    [Fact]
    public void ImportedDescendant_PreservesExactRawEdgePolicy()
    {
        var info = new ElementInfo
        {
            Type = 3,
            X = 10,
            Y = 20,
            Width = 30,
            Height = 40,
            Left = 4,
            Top = 3,
            Right = 1,
            Bottom = 0,
            OriginalParentWidth = 100,
            OriginalParentHeight = 200,
            HasOriginalParentSize = true,
        };

        var element = DatWidgetFactory.Create(info, NoTex, null)!;
        var policy = Assert.IsType<UiLayoutPolicy>(element.LayoutPolicy);

        Assert.Equal(4u, policy.LeftMode);
        Assert.Equal(3u, policy.TopMode);
        Assert.Equal(1u, policy.RightMode);
        Assert.Equal(0u, policy.BottomMode);
        Assert.Equal(new UiPixelRect(0, 0, 99, 199), policy.OriginalParent);
    }

    [Fact]
    public void ImportedRoot_HasNoRawEdgePolicy()
    {
        var element = DatWidgetFactory.Create(
            new ElementInfo { Type = 3, Width = 100, Height = 200 },
            NoTex,
            null)!;

        Assert.Null(element.LayoutPolicy);
    }

    [Fact]
    public void Create_PropagatesStateCursors()
    {
        var info = new ElementInfo { Type = 3 };
        info.StateCursors["Drag_rollover_accept"] = new UiCursorMedia(0x06008888u, 7, 8);

        var e = DatWidgetFactory.Create(info, NoTex, null)!;

        Assert.Equal(new UiCursorMedia(0x06008888u, 7, 8),
            e.CursorForState("Drag_rollover_accept", allowFallback: false));
    }

    // ── Test 5: ReadOrder propagated to ZOrder ───────────────────────────────

    [Fact]
    public void Create_PropagatesReadOrderToZOrder()
    {
        var e = DatWidgetFactory.Create(new ElementInfo { Type = 3, ReadOrder = 7 }, NoTex, null);
        Assert.Equal(7, e!.ZOrder);
    }

    // ── Test G1a: Type 12 always produces UiText (with or without own sprites) ──

    [Fact]
    public void DatWidgetFactory_Type12_AlwaysMakesUiText()
    {
        var withMedia = new ElementInfo { Type = 12, Width = 32, Height = 16,
            StateMedia = { ["Normal"] = (0x00001234u, 1) } };
        Assert.IsType<UiText>(DatWidgetFactory.Create(withMedia, NoTex, null));
        Assert.IsType<UiText>(DatWidgetFactory.Create(new ElementInfo { Type = 12 }, NoTex, null));
    }

    // ── Test 5c: Type 1 → UiButton ──────────────────────────────────────────

    [Fact]
    public void Type1_Button_MakesUiButton()
    {
        var e = DatWidgetFactory.Create(new ElementInfo { Type = 1, Width = 46, Height = 18 }, NoTex, null);
        Assert.IsType<UiButton>(e);
    }

    [Fact]
    public void BuildButton_OwnHJustifyLeft_NoTextChild_MultipleStatefulFaces_LabelAlignsLeft()
    {
        var info = new ElementInfo
        {
            Type = 1,
            Width = 160,
            Height = 16,
            HJustify = HJustify.Left,
        };
        info.States[1u] = new UiStateInfo { Id = 1u, Name = "Normal" };
        info.States[2u] = new UiStateInfo { Id = 2u, Name = "Normal_rollover" };
        info.States[3u] = new UiStateInfo { Id = 3u, Name = "Highlight" };
        for (int i = 0; i < 3; i++)
        {
            var face = new ElementInfo { Type = 3, ReadOrder = (uint)i };
            face.StateMedia["Normal_rollover"] = (0x06000000u + (uint)i, 1);
            info.Children.Add(face);
        }

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.Equal(UiButton.LabelAlignment.Left, button.LabelAlign);
        Assert.Equal(3f, button.LabelOffsetX);
    }

    [Fact]
    public void BuildButton_OwnHJustifyCenter_NoTextChild_StaysCentered()
    {
        var info = new ElementInfo
        {
            Type = 1,
            Width = 160,
            Height = 16,
            HJustify = HJustify.Center,
        };

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.Equal(UiButton.LabelAlignment.Center, button.LabelAlign);
    }

    [Fact]
    public void BuildButton_SingleFaceChild_LiftedCaptionWithOwnRect_HonorsLabelBoxNotFaceOffset()
    {
        uint stringId = 555u;
        var info = new ElementInfo { Type = 1, Width = 106, Height = 80 };
        info.States[1u] = new UiStateInfo { Id = 1u, Name = "Normal" };
        info.States[6u] = new UiStateInfo { Id = 6u, Name = "Highlight" };

        var caption = new ElementInfo
        {
            Type = 12,
            X = 0,
            Y = 4,
            Width = 100,
            Height = 37,
            HJustify = HJustify.Center,
        };
        caption.States[UiStateInfo.DirectStateId] = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        caption.States[UiStateInfo.DirectStateId].Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, stringId, 0, 0, 0, 0),
        };
        info.Children.Add(caption);

        var marker = new ElementInfo { Type = 3, X = 36, Y = 36, Width = 38, Height = 38 };
        marker.StateMedia["Normal"] = (0x06004D60u, 1);
        marker.StateMedia["Highlight"] = (0x06004D61u, 1);
        info.Children.Add(marker);

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value => value.StringId == stringId ? "Holtburg" : null));

        Assert.Equal("Holtburg", button.Label);
        Assert.Equal(UiButton.LabelAlignment.Center, button.LabelAlign);
        Assert.Equal((0f, 4f, 100f, 37f), button.LabelBox);
        Assert.Equal((36f, 36f, 38f, 38f), (button.FaceLeft, button.FaceTop, button.FaceWidth, button.FaceHeight));
    }

    [Fact]
    public void BuildButton_SingleFaceChild_DirectLabel_KeepsFaceRelativeOffset()
    {
        uint stringId = 777u;
        var info = new ElementInfo { Type = 1, Width = 305, Height = 32, HJustify = HJustify.Left };
        info.States[UiStateInfo.DirectStateId] = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        info.States[UiStateInfo.DirectStateId].Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, stringId, 0, 0, 0, 0),
        };
        info.States[RetailUiStateIds.Unselected] = new UiStateInfo { Id = RetailUiStateIds.Unselected, Name = "Unselected" };
        info.States[RetailUiStateIds.Selected] = new UiStateInfo { Id = RetailUiStateIds.Selected, Name = "Selected" };

        var dot = new ElementInfo { Type = 3, Width = 32, Height = 32 };
        dot.StateMedia["Unselected"] = (0x06006E35u, 1);
        dot.StateMedia["Selected"] = (0x06006E21u, 1);
        info.Children.Add(dot);

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value => value.StringId == stringId ? "Aluvian" : null));

        Assert.Equal("Aluvian", button.Label);
        Assert.Null(button.LabelBox);
        Assert.Equal(UiButton.LabelAlignment.Left, button.LabelAlign);
        Assert.Equal(36f, button.LabelOffsetX); // face.X(0) + face.Width(32) + 4
    }

    [Fact]
    public void BuildButton_OwnCaptionPlusMediaLessTextChild_SurfacesValueSlotWithoutClobberingLabel()
    {
        uint captionStringId = 111u;
        var info = new ElementInfo { Type = 1, Width = 80, Height = 20 };
        info.States[UiStateInfo.DirectStateId] = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        info.States[UiStateInfo.DirectStateId].Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, captionStringId, 0, 0, 0, 0),
        };
        info.StateMedia[""] = (0x06000001u, 1);

        var valueChild = new ElementInfo { Type = 12, X = 5, Y = 2, Width = 60, Height = 16 };
        info.Children.Add(valueChild);

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value => value.StringId == captionStringId ? "Attribute Credits" : null));

        Assert.Equal("Attribute Credits", button.Label);
        Assert.Null(button.ValueLabel);
        Assert.Equal((5f, 2f, 60f, 16f), button.ValueBox);

        button.ValueLabel = "42";
        Assert.Equal("Attribute Credits", button.Label);
        Assert.Equal("42", button.ValueLabel);
    }

    [Fact]
    public void BuildButton_ValueChildBaseInheritedNarrowerParent_ReflowsToWiderButton()
    {
        uint captionStringId = 333u;
        var info = new ElementInfo { Type = 1, Width = 231, Height = 28 };
        info.States[UiStateInfo.DirectStateId] = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        info.States[UiStateInfo.DirectStateId].Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, captionStringId, 0, 0, 0, 0),
        };

        var valueChild = new ElementInfo
        {
            Type = 12,
            X = 116,
            Y = 0,
            Width = 34,
            Height = 28,
            Left = 2,
            Top = 1,
            Right = 1,
            Bottom = 1,
            OriginalParentWidth = 150,
            OriginalParentHeight = 28,
            HasOriginalParentSize = true,
            HJustify = HJustify.Right,
        };
        info.Children.Add(valueChild);

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value => value.StringId == captionStringId ? "Available Skill Credits" : null));

        Assert.Equal("Available Skill Credits", button.Label);
        // 116 + (231-150) = 197 -> width/height preserved (34/28).
        Assert.Equal((197f, 0f, 34f, 28f), button.ValueBox);
        Assert.Equal(UiButton.LabelAlignment.Right, button.ValueAlign);
    }

    [Fact]
    public void BuildButton_ValueChildOriginalParentMatchesActual_RectUnchanged()
    {
        uint captionStringId = 334u;
        var info = new ElementInfo { Type = 1, Width = 150, Height = 28 };
        info.States[UiStateInfo.DirectStateId] = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        info.States[UiStateInfo.DirectStateId].Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, captionStringId, 0, 0, 0, 0),
        };

        var valueChild = new ElementInfo
        {
            Type = 12,
            X = 116,
            Y = 0,
            Width = 34,
            Height = 28,
            Left = 2,
            Top = 1,
            Right = 1,
            Bottom = 1,
            OriginalParentWidth = 150,
            OriginalParentHeight = 28,
            HasOriginalParentSize = true,
        };
        info.Children.Add(valueChild);

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value => value.StringId == captionStringId ? "Health" : null));

        Assert.Equal((116f, 0f, 34f, 28f), button.ValueBox);
    }

    [Fact]
    public void BuildButton_LiftedCaption_NeverSurfacesValueSlot()
    {
        uint stringId = 222u;
        var info = new ElementInfo { Type = 1, Width = 106, Height = 80 };
        info.States[1u] = new UiStateInfo { Id = 1u, Name = "Normal" };
        info.States[6u] = new UiStateInfo { Id = 6u, Name = "Highlight" };

        var caption = new ElementInfo { Type = 12, X = 0, Y = 4, Width = 100, Height = 37 };
        caption.States[UiStateInfo.DirectStateId] = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        caption.States[UiStateInfo.DirectStateId].Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, stringId, 0, 0, 0, 0),
        };
        info.Children.Add(caption);

        var marker = new ElementInfo { Type = 3, X = 36, Y = 36, Width = 38, Height = 38 };
        marker.StateMedia["Normal"] = (0x06004D60u, 1);
        marker.StateMedia["Highlight"] = (0x06004D61u, 1);
        info.Children.Add(marker);

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value => value.StringId == stringId ? "Holtburg" : null));

        Assert.Equal("Holtburg", button.Label);
        Assert.Null(button.ValueBox);
        Assert.Null(button.ValueLabel);
    }

    // ── Test 5b: Type 11 → UiScrollbar ──────────────────────────────────────

    [Fact]
    public void Type11_Scrollbar_MakesUiScrollbar()
    {
        var e = DatWidgetFactory.Create(new ElementInfo { Type = 11, Width = 16, Height = 68 }, NoTex, null);
        Assert.IsType<UiScrollbar>(e);
    }

    [Fact]
    public void Type11_HorizontalScrollbar_usesDirectRetailTrackAndThumbChildren()
    {
        const uint Track = 0x06004CF6u;
        const uint Thumb = 0x06005DC3u;
        var info = new ElementInfo { Type = 11, Id = 0x100001A4u, Width = 90, Height = 14 };
        var track = new ElementInfo { Id = 4u, Type = 3u, Width = 90, Height = 14 };
        track.States[UiStateInfo.DirectStateId] = new UiStateInfo
            { Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(Track, 1) };
        var thumb = new ElementInfo { Id = 1u, Type = 3u, Width = 16, Height = 14 };
        thumb.States[UiStateInfo.DirectStateId] = new UiStateInfo
            { Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(Thumb, 1) };
        info.Children.Add(track);
        info.Children.Add(thumb);

        var bar = Assert.IsType<UiScrollbar>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.True(bar.Horizontal);
        Assert.Equal(Track, bar.TrackSprite);
        Assert.Equal(Thumb, bar.ThumbSprite);
        Assert.Equal(0u, bar.UpSprite);
        Assert.Equal(0u, bar.DownSprite);
    }

    [Fact]
    public void Type11_HorizontalScrollbar_ImportsAuthoredArrowButtonsAndHideDisabled()
    {
        const uint DecrementId = 0x10000071u;
        const uint IncrementId = 0x10000072u;
        const uint DecrementSprite = 0x06004CDCu;
        const uint IncrementSprite = 0x06004CDEu;
        const uint DecrementRollover = 0x06004CDDu;
        const uint IncrementRollover = 0x06004CDFu;
        var decrement = new ElementInfo
        {
            Id = DecrementId, Type = 1u, X = 63f, Width = 23f, Height = 36f,
        };
        decrement.States[UiStateInfo.DirectStateId] = new UiStateInfo
        {
            Id = UiStateInfo.DirectStateId,
            Image = new UiImageMedia(DecrementSprite, 1),
        };
        decrement.StateMedia["Normal"] = (DecrementSprite, 1);
        decrement.StateMedia["Normal_rollover"] = (DecrementRollover, 1);
        decrement.StateMedia["Normal_pressed"] = (DecrementSprite, 1);
        var increment = new ElementInfo
        {
            Id = IncrementId, Type = 1u, X = 0f, Width = 23f, Height = 36f,
        };
        increment.States[UiStateInfo.DirectStateId] = new UiStateInfo
        {
            Id = UiStateInfo.DirectStateId,
            Image = new UiImageMedia(IncrementSprite, 1),
        };
        increment.StateMedia["Normal"] = (IncrementSprite, 1);
        increment.StateMedia["Normal_rollover"] = (IncrementRollover, 1);
        increment.StateMedia["Normal_pressed"] = (IncrementSprite, 1);
        var info = new ElementInfo
        {
            Type = 11u,
            Id = SpellcastingUiController.FavoriteScrollbarId,
            Width = 685f,
            Height = 36f,
            Children = [decrement, increment],
        };
        var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        state.Properties.Values[0x77u] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = IncrementId };
        state.Properties.Values[0x78u] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = DecrementId };
        state.Properties.Values[0x79u] = Bool(true);
        info.States[UiStateInfo.DirectStateId] = state;

        var bar = Assert.IsType<UiScrollbar>(
            DatWidgetFactory.Create(info, NoTex, null));

        Assert.True(bar.Horizontal);
        Assert.True(bar.HideWhenDisabled);
        Assert.Equal(23f, bar.DecrementButtonExtent);
        Assert.Equal(23f, bar.IncrementButtonExtent);
        Assert.Equal(IncrementSprite, bar.UpSprite);
        Assert.Equal(IncrementRollover, bar.UpRolloverSprite);
        Assert.Equal(IncrementSprite, bar.UpPressedSprite);
        Assert.Equal(DecrementSprite, bar.DownSprite);
        Assert.Equal(DecrementRollover, bar.DownRolloverSprite);
        Assert.Equal(DecrementSprite, bar.DownPressedSprite);
        Assert.Equal(0u, bar.TrackSprite);
        Assert.Equal(0u, bar.ThumbSprite);
    }

    [Fact]
    public void Type11_VerticalScrollbar_SingleSpriteThumbWithNoSliceChildren_SetsThumbSprite()
    {
        const uint Thumb = 0x06005A11u;
        const uint DecrementId = 0x10000071u;
        const uint IncrementId = 0x10000072u;
        var decrement = new ElementInfo { Id = DecrementId, Type = 1u, Y = 83f, Width = 37f, Height = 17f };
        decrement.StateMedia["Normal"] = (0x06004C69u, 1);
        var increment = new ElementInfo { Id = IncrementId, Type = 1u, Y = 0f, Width = 37f, Height = 17f };
        increment.StateMedia["Normal"] = (0x06004C6Cu, 1);
        var thumb = new ElementInfo { Id = 1u, Type = 1u, Width = 37f, Height = 39f };
        thumb.StateMedia["Normal"] = (Thumb, 1);
        thumb.StateMedia["Normal_rollover"] = (0x06005A12u, 1);
        thumb.StateMedia["Normal_pressed"] = (0x06005A13u, 1);
        thumb.States[1u] = new UiStateInfo { Id = 1u, Name = "Normal", Image = new UiImageMedia(Thumb, 1) };

        var info = new ElementInfo
        {
            Type = 11u,
            Width = 37f,
            Height = 307f,
            Children = [decrement, increment, thumb],
        };
        var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        state.Properties.Values[0x77u] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = IncrementId };
        state.Properties.Values[0x78u] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = DecrementId };
        info.States[UiStateInfo.DirectStateId] = state;

        var bar = Assert.IsType<UiScrollbar>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.False(bar.Horizontal);
        Assert.Equal(Thumb, bar.ThumbSprite);
        Assert.Equal(0u, bar.ThumbTopSprite);
        Assert.Equal(0u, bar.ThumbBotSprite);
        Assert.Equal(0x06005A12u, bar.ThumbRolloverSprite);
        Assert.Equal(0x06005A13u, bar.ThumbPressedSprite);
    }

    [Fact]
    public void Type11_VerticalScrollbar_SeatsButtonsByDesignation_NotAuthoredPosition()
    {
        const uint DecrementId = 0x10000071u; // DOWN arrow, authored at Y=0
        const uint IncrementId = 0x10000072u; // UP arrow, authored at Y=32
        var decrement = new ElementInfo { Id = DecrementId, Type = 1u, Y = 0f, Width = 16f, Height = 16f };
        decrement.StateMedia["Normal"] = (0x06004C69u, 1);
        decrement.StateMedia["Normal_rollover"] = (0x06004C6Au, 1);
        decrement.StateMedia["Normal_pressed"] = (0x06004C6Bu, 1);
        var increment = new ElementInfo { Id = IncrementId, Type = 1u, Y = 32f, Width = 16f, Height = 16f };
        increment.StateMedia["Normal"] = (0x06004C6Cu, 1);
        increment.StateMedia["Normal_rollover"] = (0x06004C6Du, 1);
        increment.StateMedia["Normal_pressed"] = (0x06004C6Eu, 1);

        var info = new ElementInfo
        {
            Type = 11u,
            Width = 16f,
            Height = 48f,
            Children = [decrement, increment],
        };
        var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        state.Properties.Values[0x77u] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = IncrementId };
        state.Properties.Values[0x78u] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = DecrementId };
        info.States[UiStateInfo.DirectStateId] = state;

        var bar = Assert.IsType<UiScrollbar>(DatWidgetFactory.Create(info, NoTex, null));

        // Top slot = the increment designee's UP-arrow media.
        Assert.Equal(0x06004C6Cu, bar.UpSprite);
        Assert.Equal(0x06004C6Du, bar.UpRolloverSprite);
        Assert.Equal(0x06004C6Eu, bar.UpPressedSprite);
        // Bottom slot = the decrement designee's DOWN-arrow media.
        Assert.Equal(0x06004C69u, bar.DownSprite);
        Assert.Equal(0x06004C6Au, bar.DownRolloverSprite);
        Assert.Equal(0x06004C6Bu, bar.DownPressedSprite);
    }

    [Fact]
    public void Type11_VerticalScrollbar_ThumbWithSliceChildren_StillUsesSliceMedia()
    {
        const uint Top = 0x06004C60u;
        const uint Mid = 0x06004C63u;
        const uint Bot = 0x06004C66u;
        var decrement = new ElementInfo { Id = 0x10000071u, Type = 1u, Y = 0f, Width = 16f, Height = 16f };
        decrement.StateMedia["Normal"] = (0x06004C69u, 1);
        var increment = new ElementInfo { Id = 0x10000072u, Type = 1u, Y = 32f, Width = 16f, Height = 16f };
        increment.StateMedia["Normal"] = (0x06004C6Cu, 1);
        var thumb = new ElementInfo { Id = 1u, Type = 1u, Width = 16f, Height = 16f };
        var topCap = new ElementInfo { Id = 0x10000364u, Type = 3u, Y = 0f, Width = 16f, Height = 3f };
        topCap.StateMedia["Normal"] = (Top, 1);
        topCap.States[1u] = new UiStateInfo { Id = 1u, Name = "Normal", Image = new UiImageMedia(Top, 1) };
        var mid = new ElementInfo { Id = 0x10000365u, Type = 3u, Y = 3f, Width = 16f, Height = 10f };
        mid.StateMedia["Normal"] = (Mid, 1);
        mid.StateMedia["Normal_rollover"] = (0x06004C64u, 1);
        mid.StateMedia["Normal_pressed"] = (0x06004C65u, 1);
        mid.States[1u] = new UiStateInfo { Id = 1u, Name = "Normal", Image = new UiImageMedia(Mid, 1) };
        var botCap = new ElementInfo { Id = 0x10000366u, Type = 3u, Y = 13f, Width = 16f, Height = 3f };
        botCap.StateMedia["Normal"] = (Bot, 1);
        botCap.States[1u] = new UiStateInfo { Id = 1u, Name = "Normal", Image = new UiImageMedia(Bot, 1) };
        thumb.Children.Add(topCap);
        thumb.Children.Add(mid);
        thumb.Children.Add(botCap);

        var info = new ElementInfo
        {
            Type = 11u,
            Width = 16f,
            Height = 73f,
            Children = [decrement, increment, thumb],
        };
        var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        state.Properties.Values[0x77u] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = 0x10000072u };
        state.Properties.Values[0x78u] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = 0x10000071u };
        info.States[UiStateInfo.DirectStateId] = state;

        var bar = Assert.IsType<UiScrollbar>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.False(bar.Horizontal);
        Assert.Equal(Top, bar.ThumbTopSprite);
        Assert.Equal(Mid, bar.ThumbSprite);
        Assert.Equal(Bot, bar.ThumbBotSprite);
        Assert.Equal(0x06004C64u, bar.ThumbRolloverSprite);
        Assert.Equal(0x06004C65u, bar.ThumbPressedSprite);
    }

    [Fact]
    public void RetailToolbarFixture_buildsEditableStackEntry_andAuthoredHorizontalSlider()
    {
        ImportedLayout layout = FixtureLoader.LoadToolbar();

        var entry = Assert.IsType<UiField>(layout.FindElement(0x100001A3u));
        Assert.True(entry.RightAligned);
        var bar = Assert.IsType<UiScrollbar>(layout.FindElement(0x100001A4u));
        Assert.True(bar.Horizontal);
        Assert.Equal(0x06004CF6u, bar.TrackSprite);
        Assert.Equal(0x06005DC3u, bar.ThumbSprite);
    }


    [Fact]
    public void Type3_NotRegistered_FallsBackToGeneric()
    {
        var e = DatWidgetFactory.Create(new ElementInfo { Type = 3, Width = 200, Height = 16 }, NoTex, null);
        Assert.IsType<UiDatElement>(e);
    }

    // ── Test 5d: Type 6 → UiMenu ─────────────────────────────────────────────

    [Fact]
    public void Type6_Menu_MakesUiMenu()
    {
        var e = DatWidgetFactory.Create(new ElementInfo { Type = 6, Width = 46, Height = 18 }, NoTex, null);
        Assert.IsType<UiMenu>(e);
    }


    [Fact]
    public void Create_buildsUiItemList_forItemListClassId()
    {
        var info = new AcDream.App.UI.Layout.ElementInfo { Id = 0x100001A7u, Type = 0x10000031u, Width = 32, Height = 32 };
        var w = AcDream.App.UI.Layout.DatWidgetFactory.Create(info, _ => (0u, 0, 0), null);
        Assert.IsType<AcDream.App.UI.UiItemList>(w);
    }


    [Fact]
    public void BuildMeter_SingleImageShape_ReadsDirectStateFromElementAndFillChild()
    {
        const uint BackFile = 0x0600193Eu;  // health back-track (from toolbar dump)
        const uint FillFile = 0x0600193Fu;  // health fill (from toolbar dump)

        // Meter element: Type 7, own DirectState = back-track sprite.
        var meter = new ElementInfo { Type = 7, Id = 0x100001A1u, Width = 140, Height = 31 };
        meter.StateMedia[""] = (BackFile, 1);

        var fillContainer = new ElementInfo { Type = 3, ReadOrder = 1 };
        fillContainer.StateMedia[""] = (FillFile, 1);
        meter.Children.Add(fillContainer);

        var e = DatWidgetFactory.Create(meter, NoTex, null);

        var m = Assert.IsType<UiMeter>(e);
        Assert.Equal(BackFile, m.BackTile);
        Assert.Equal(0u,       m.BackLeft);
        Assert.Equal(0u,       m.BackRight);
        Assert.Equal(FillFile, m.FrontTile);
        Assert.Equal(0u,       m.FrontLeft);
        Assert.Equal(0u,       m.FrontRight);
    }

    [Fact]
    public void BuildMeter_StatefulSingleImageShape_MapsNumericFillStatesAndHidesDirectRange()
    {
        const uint TrackFile = 0x06004D0Bu;
        const uint JumpState = 0x10000042u;
        const uint JumpFile = 0x06001354u;
        const uint RangeFile = 0x0600715Eu;

        var meter = new ElementInfo { Type = 7, Id = 0x10000034u, Width = 600, Height = 15 };
        meter.StateMedia[""] = (TrackFile, 1);

        var fills = new ElementInfo { Type = 3, Id = 2u, ReadOrder = 2 };
        fills.States[JumpState] = new UiStateInfo { Id = JumpState, Name = "JumpMode" };
        fills.StateMedia["JumpMode"] = (JumpFile, 1);
        meter.Children.Add(fills);

        var range = new ElementInfo { Type = 3, Id = 0x100005EEu, ReadOrder = 1 };
        range.StateMedia[""] = (RangeFile, 1);
        meter.Children.Add(range);

        var result = Assert.IsType<UiMeter>(DatWidgetFactory.Create(meter, NoTex, null));

        Assert.Equal(TrackFile, result.BackTile);
        Assert.Equal(0u, result.FrontTile);
        Assert.True(result.TrySetRetailState(JumpState));
        Assert.Equal(JumpState, result.ActiveRetailStateId);
        Assert.Equal(JumpFile, result.FrontTile);
        Assert.NotEqual(RangeFile, result.BackTile);
        Assert.NotEqual(RangeFile, result.FrontTile);
    }


    [Fact]
    public void MeterSliceExtraction_ReadsGrandchildImageIds_IgnoresOverlay()
    {
        const uint BackL = 0x0600747Eu, BackT = 0x0600747Fu, BackR = 0x06007480u;
        const uint FrontL = 0x06007481u, FrontT = 0x06007482u, FrontR = 0x06007483u;
        const uint OverlayFile = 0x06007490u;

        var backChild = new ElementInfo { Type = 3, ReadOrder = 0 };
        backChild.Children.Add(new ElementInfo { X = 0,   StateMedia = { [""] = (BackL,  1) } });
        backChild.Children.Add(new ElementInfo { X = 10,  StateMedia = { [""] = (BackT,  1) } });
        backChild.Children.Add(new ElementInfo { X = 140, StateMedia = { [""] = (BackR,  1) } });

        var frontChild = new ElementInfo { Type = 3, ReadOrder = 1 };
        frontChild.Children.Add(new ElementInfo { X = 0,   StateMedia = { [""] = (FrontL, 1) } });
        frontChild.Children.Add(new ElementInfo { X = 10,  StateMedia = { [""] = (FrontT, 1) } });
        frontChild.Children.Add(new ElementInfo { X = 140, StateMedia = { [""] = (FrontR, 1) } });
        // Expand-detail overlay: named state only — NO DirectState "" — must be ignored.
        frontChild.Children.Add(new ElementInfo
        {
            X = 0,
            StateMedia = { ["ShowDetail"] = (OverlayFile, 3) }
        });

        var meter = new ElementInfo { Type = 7, Width = 150, Height = 16 };
        meter.Children.Add(backChild);
        meter.Children.Add(frontChild);

        var e = DatWidgetFactory.Create(meter, NoTex, null);

        var m = Assert.IsType<UiMeter>(e);
        Assert.Equal(BackL,  m.BackLeft);
        Assert.Equal(BackT,  m.BackTile);
        Assert.Equal(BackR,  m.BackRight);
        Assert.Equal(FrontL, m.FrontLeft);
        Assert.Equal(FrontT, m.FrontTile);
        Assert.Equal(FrontR, m.FrontRight);
        Assert.NotEqual(OverlayFile, m.FrontRight);
        Assert.NotEqual(OverlayFile, m.FrontTile);
    }

    // ── Justification build-time application (new for importer Fix A) ────────

    [Fact]
    public void BuildText_HJustifyCenter_SetsCentered()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, HJustify = HJustify.Center };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));
        Assert.True(t.Centered);
        Assert.False(t.RightAligned);
        Assert.Equal(VJustify.Top, t.VerticalJustify);
    }

    [Fact]
    public void BuildText_UnauthoredJustification_UsesRetailNearEdges()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20 };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.False(t.Centered);
        Assert.False(t.RightAligned);
        Assert.Equal(VJustify.Top, t.VerticalJustify);
    }

    [Fact]
    public void BuildText_HJustifyRight_SetsRightAligned()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, HJustify = HJustify.Right };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));
        Assert.False(t.Centered);
        Assert.True(t.RightAligned);
    }

    [Fact]
    public void BuildText_HJustifyLeft_SetsNeitherCenteredNorRight()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, HJustify = HJustify.Left };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));
        Assert.False(t.Centered);
        Assert.False(t.RightAligned);
    }

    /// <summary>
    /// A Type-12 text element with VJustify=Top must produce a UiText with
    /// VerticalJustify=Top at build time.
    /// </summary>
    [Fact]
    public void BuildText_VJustifyTop_SetsVerticalJustifyTop()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 55, VJustify = VJustify.Top };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));
        Assert.Equal(VJustify.Top, t.VerticalJustify);
    }

    [Fact]
    public void BuildText_ControllerOverrideWinsAfterBuild()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, HJustify = HJustify.Center };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));
        Assert.True(t.Centered);   // build-time value
        t.Centered = false;
        Assert.False(t.Centered);  // override wins
    }

    // ── DefaultColor from dat FontColor (importer Fix B) ─────────────────────

    [Fact]
    public void BuildText_FontColorPresent_SetsDefaultColor()
    {
        var gold = new Vector4(1f, 0.82f, 0.36f, 1f);
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, FontColor = gold };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));
        Assert.Equal(gold, t.DefaultColor);
    }

    [Fact]
    public void BuildText_RetailConstructorDefaultsToZeroMargins()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20 };
        var text = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.Equal(0f, text.Padding);
        Assert.False(text.OneLine);
    }

    [Fact]
    public void BuildText_AuthoredLineTracksStateFontColor()
    {
        var info = new ElementInfo
        {
            Type = 12,
            Width = 100,
            Height = 20,
            DefaultStateId = RetailUiStateIds.Closed,
            DefaultStateName = "Closed",
        };
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        direct.Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, 1, 2, 0, 1, 0),
        };
        var closed = new UiStateInfo { Id = RetailUiStateIds.Closed, Name = "Closed" };
        closed.Properties.Values[0x1Bu] = Color(127, 127, 127);
        var open = new UiStateInfo { Id = RetailUiStateIds.Open, Name = "Open" };
        open.Properties.Values[0x1Bu] = Color(204, 204, 204);
        info.States[UiStateInfo.DirectStateId] = direct;
        info.States[RetailUiStateIds.Closed] = closed;
        info.States[RetailUiStateIds.Open] = open;

        var text = Assert.IsType<UiText>(DatWidgetFactory.Create(
            info, NoTex, null, stringResolve: _ => "Spells"));
        Assert.Equal(127f / 255f, text.LinesProvider()[0].Color.X, 5);

        Assert.True(text.TrySetRetailState(RetailUiStateIds.Open));

        Assert.Equal(204f / 255f, text.LinesProvider()[0].Color.X, 5);
    }

    [Fact]
    public void BuildText_MultilineAuthored_WordWrapsToLiveWidth()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 80 };
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        direct.Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, 1, 2, 0, 1, 0),
        };
        info.States[UiStateInfo.DirectStateId] = direct;
        var text = Assert.IsType<UiText>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: _ => "one two three four five\nsix"));

        var lines = text.LinesProvider!();
        Assert.True(lines.Count >= 3);
        Assert.All(lines, line => Assert.True(line.Text.Length <= 12));
        Assert.Equal(
            "one two three four five six",
            string.Join(" ", lines.Select(static line => line.Text)));

        // The provider tracks the LIVE width — widening re-wraps.
        text.Width = 400f;
        lines = text.LinesProvider!();
        Assert.Equal(2, lines.Count);
        Assert.Equal("one two three four five", lines[0].Text);
        Assert.Equal("six", lines[1].Text);
    }

    [Fact]
    public void BuildText_PerStateAuthoredStrings_SwapWithRetailState()
    {
        var info = new ElementInfo { Type = 12, Width = 270, Height = 24 };
        var online = new UiStateInfo { Id = 0x10000054u, Name = "Online" };
        online.Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            // (Token, StringId, TableId, ...) — StringId 2 = 'Online'.
            StringInfoValue = new UiStringInfoValue(0, 2, 1, 0, 1, 0),
        };
        online.Properties.Values[0x1Bu] = Color(0, 255, 0);
        var offline = new UiStateInfo { Id = 0x10000055u, Name = "Offline" };
        offline.Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, 3, 1, 0, 1, 0),
        };
        info.States[0x10000054u] = online;
        info.States[0x10000055u] = offline;

        var text = Assert.IsType<UiText>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: si => si.StringId == 2u ? "Online" : "Offline"));

        Assert.True(text.TrySetRetailState(0x10000054u));
        var line = Assert.Single(text.LinesProvider!());
        Assert.Equal("Online", line.Text);
        Assert.Equal(1f, line.Color.Y, 3); // authored green rides the state

        Assert.True(text.TrySetRetailState(0x10000055u));
        line = Assert.Single(text.LinesProvider!());
        Assert.Equal("Offline", line.Text);
    }

    [Fact]
    public void BuildMeter_StatefulFill_AbsorbsCaptionChildPerStateStrings()
    {
        var meter = new ElementInfo { Id = 0x34, Type = 7, Width = 600, Height = 15 };
        meter.StateMedia[""] = (0x06004D0Bu, 1);

        var fill = new ElementInfo { Id = 2, Type = 3, Width = 600, Height = 15 };
        fill.States[0x10000042u] = new UiStateInfo { Id = 0x10000042u, Name = "JumpMode" };
        fill.StateMedia["JumpMode"] = (0x06001354u, 1);
        meter.Children.Add(fill);

        var caption = new ElementInfo { Id = 0x35, Type = 12, Width = 600, Height = 15 };
        var jumpText = new UiStateInfo { Id = 0x10000042u, Name = "JumpMode" };
        jumpText.Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, 1, 2, 0, 1, 0),
        };
        jumpText.Properties.Values[0x14u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Enum,
            UnsignedValue = 0x3u,
        };
        caption.States[0x10000042u] = jumpText;
        meter.Children.Add(caption);

        var m = Assert.IsType<UiMeter>(DatWidgetFactory.Create(
            meter, NoTex, null, stringResolve: _ => "Height"));

        Assert.Null(m.ActiveStateLabel);
        Assert.True(m.TrySetRetailState(0x10000042u));
        Assert.Equal("Height", m.ActiveStateLabel);
        Assert.Equal(UiMeterLabelAlign.Right, m.ActiveStateLabelAlign);
        Assert.Equal(0x06001354u, m.FrontTile);
    }

    [Fact]
    public void HorizontalScrollbar_PreservesNestedCombatMeterFillSprite()
    {
        var track = new ElementInfo { Id = 4, Type = 3, Width = 507, Height = 14 };
        track.StateMedia[""] = (0x06001919u, 1);
        track.States[UiStateInfo.DirectStateId] = new UiStateInfo
            { Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(0x06001919u, 1) };
        var thumb = new ElementInfo { Id = 1, Type = 1, Width = 12, Height = 14 };
        thumb.StateMedia[""] = (0x06001923u, 1);
        thumb.States[UiStateInfo.DirectStateId] = new UiStateInfo
            { Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(0x06001923u, 1) };
        var fill = new ElementInfo { Id = 2, Type = 3, Width = 507, Height = 14 };
        fill.StateMedia[""] = (0x06001200u, 1);
        fill.States[UiStateInfo.DirectStateId] = new UiStateInfo
            { Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(0x06001200u, 1) };
        var range = new ElementInfo
        {
            Id = 0x100005EFu,
            Type = 3,
            X = 63,
            Width = 407,
            Height = 14,
            OriginalParentWidth = 507,
            OriginalParentHeight = 14,
            HasOriginalParentSize = true,
        };
        range.StateMedia[""] = (0x0600715Eu, 1);
        range.States[UiStateInfo.DirectStateId] = new UiStateInfo
            { Id = UiStateInfo.DirectStateId, Image = new UiImageMedia(0x0600715Eu, 1) };
        var meter = new ElementInfo
        {
            Id = CombatUiController.PowerControlId + 1,
            Type = 7,
            Width = 507,
            Height = 14,
            Children = [fill, range],
        };
        var meterState = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        meterState.Properties.Values[0x6Fu] = new UiPropertyValue
            { Kind = UiPropertyKind.Enum, UnsignedValue = 1u };
        meter.States[UiStateInfo.DirectStateId] = meterState;
        var info = new ElementInfo
        {
            Id = CombatUiController.PowerControlId,
            Type = 11,
            Width = 507,
            Height = 14,
            Children = [track, meter, thumb],
        };

        var bar = Assert.IsType<UiScrollbar>(
            DatWidgetFactory.Create(info, NoTex, null));

        Assert.True(bar.Horizontal);
        Assert.Equal(0x06001919u, bar.TrackSprite);
        Assert.Equal(0x06001923u, bar.ThumbSprite);
        Assert.Equal(0x06001200u, bar.ScalarFillSprite);
        Assert.Equal(0x0600715Eu, bar.ScalarRangeSprite);
        Assert.NotNull(bar.ScalarRangeLayoutPolicy);
        Assert.False(bar.ScalarFillFromRight);
    }

    private static ElementInfo TextInfo(params (uint Id, UiPropertyValue Value)[] properties)
    {
        var info = new ElementInfo { Id = 0x10000016u, Type = 12, Width = 100, Height = 20 };
        var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        foreach (var property in properties)
            state.Properties.Values[property.Id] = property.Value;
        info.States[UiStateInfo.DirectStateId] = state;
        return info;
    }

    private static UiPropertyValue Bool(bool value)
        => new() { Kind = UiPropertyKind.Bool, BoolValue = value };

    private static UiPropertyValue Integer(int value)
        => new() { Kind = UiPropertyKind.Integer, IntegerValue = value };

    private static UiPropertyValue Color(byte red, byte green, byte blue)
        => new()
        {
            Kind = UiPropertyKind.Color,
            ColorValue = new UiColorValue(blue, green, red, 255),
        };

    [Fact]
    public void BuildText_FontColorAbsent_DefaultColorIsWhite()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, FontColor = null };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));
        Assert.Equal(Vector4.One, t.DefaultColor);
    }

    [Fact]
    public void BuildText_ControllerExplicitLineColor_UnaffectedByDefaultColor()
    {
        var parchment = new Vector4(0.92f, 0.90f, 0.82f, 1f);
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, FontColor = parchment };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null));

        var gold = new Vector4(1f, 0.82f, 0.36f, 1f);
        t.LinesProvider = () => new[] { new UiText.Line("text", gold) };

        Assert.Equal(gold, t.LinesProvider()[0].Color);
        Assert.Equal(parchment, t.DefaultColor);
    }
}
