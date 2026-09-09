using AcDream.App.UI;
using AcDream.App.UI.Layout;
namespace AcDream.App.Tests.UI;

public class UiButtonTests
{
    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);
    private bool _clicked;

    [Fact]
    public void Click_InvokesOnClick()
    {
        var b = new UiButton(new ElementInfo { Type = 1, Width = 46, Height = 18 }, NoTex)
        { OnClick = () => _clicked = true };
        b.OnEvent(new UiEvent(0, null, UiEventType.Click));
        Assert.True(_clicked);
    }

    [Fact]
    public void Click_ProvidesLocalCoordinatesToPositionAwareHandler()
    {
        (int X, int Y) clicked = default;
        var b = new UiButton(new ElementInfo { Type = 1, Width = 46, Height = 18 }, NoTex)
        {
            OnClickAt = (x, y) => clicked = (x, y),
        };

        b.OnEvent(new UiEvent(0, b, UiEventType.Click, Data1: 17, Data2: 9));

        Assert.Equal((17, 9), clicked);
    }

    [Fact]
    public void DoubleClick_IsOptInAndDisabledButtonsSwallowWithoutInvoking()
    {
        int activations = 0;
        var button = new UiButton(
            new ElementInfo { Type = 1, Width = 46, Height = 18 },
            NoTex);
        var doubleClick = new UiEvent(
            0,
            button,
            UiEventType.DoubleClick);

        Assert.False(button.OnEvent(doubleClick));

        button.OnDoubleClick = () => activations++;
        Assert.True(button.OnEvent(doubleClick));
        Assert.Equal(1, activations);

        button.Enabled = false;
        Assert.True(button.OnEvent(doubleClick));
        Assert.Equal(1, activations);
    }

    [Fact]
    public void PointerDownAndUp_InvokeDistinctTransitionHandlers()
    {
        var transitions = new List<string>();
        var b = new UiButton(new ElementInfo { Type = 1, Width = 20, Height = 20 }, NoTex)
        {
            Width = 20,
            Height = 20,
            OnPressed = () => transitions.Add("pressed"),
            OnReleased = () => transitions.Add("released"),
        };

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseDown, Data1: 5, Data2: 5));
        b.OnEvent(new UiEvent(0, b, UiEventType.MouseUp, Data1: 30, Data2: 30));

        Assert.Equal(["pressed", "released"], transitions);
    }

    [Fact]
    public void NotClickThrough_SoItReceivesClicks()
    {
        var b = new UiButton(new ElementInfo { Type = 1 }, NoTex);
        Assert.False(b.ClickThrough);
    }

    [Fact]
    public void PointerTransitions_UseRetailNormalStates()
    {
        var b = ButtonWithStates("Normal", "Normal_rollover", "Normal_pressed");

        b.OnEvent(new UiEvent(0, b, UiEventType.HoverEnter));
        Assert.Equal("Normal_rollover", b.ActiveState);

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseDown, Data1: 5, Data2: 5));
        Assert.Equal("Normal_pressed", b.ActiveState);

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseMove, Data1: 50, Data2: 50));
        Assert.Equal("Normal_rollover", b.ActiveState);

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseMove, Data1: 5, Data2: 5));
        Assert.Equal("Normal_pressed", b.ActiveState);

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseUp, Data1: 5, Data2: 5));
        Assert.Equal("Normal_rollover", b.ActiveState);
    }

    [Fact]
    public void ToggleRelease_SelectsHighlightState()
    {
        var info = ButtonInfo("Normal", "Highlight");
        AddBoolProperty(info, 0x0Bu, true);
        var b = CreateButton(info);

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseDown, Data1: 5, Data2: 5));
        b.OnEvent(new UiEvent(0, b, UiEventType.MouseUp, Data1: 5, Data2: 5));

        Assert.True(b.Selected);
        Assert.Equal("Highlight", b.ActiveState);
    }

    [Fact]
    public void SuppressSelfToggle_PressReleaseDoesNotFlipSelected()
    {
        var info = ButtonInfo("Normal", "Highlight");
        AddBoolProperty(info, 0x0Bu, true);
        var b = CreateButton(info);
        b.SuppressSelfToggle = true;

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseDown, Data1: 5, Data2: 5));
        b.OnEvent(new UiEvent(0, b, UiEventType.MouseUp, Data1: 5, Data2: 5));

        Assert.False(b.Selected);
        Assert.Equal("Normal", b.ActiveState);

        b.Selected = true;
        Assert.True(b.Selected);
        Assert.Equal("Highlight", b.ActiveState);
    }

    [Fact]
    public void Property0x0DDoesNotDisableTheButton()
    {
        var info = ButtonInfo("Normal", "Ghosted");
        AddBoolProperty(info, 0x0Du, true);
        var b = CreateButton(info);
        b.OnClick = () => _clicked = true;

        b.OnEvent(new UiEvent(0, b, UiEventType.Click));

        Assert.True(b.Enabled);
        Assert.True(_clicked);
    }

    [Fact]
    public void AnExplicitGhostedStateStillSuppressesTheClick()
    {
        var info = ButtonInfo("Normal", "Ghosted");
        var b = CreateButton(info);
        b.OnClick = () => _clicked = true;

        b.TrySetRetailState(UiButtonStateMachine.Ghosted);
        b.OnEvent(new UiEvent(0, b, UiEventType.Click));

        Assert.False(b.Enabled);
        Assert.False(_clicked);
    }

    [Fact]
    public void MissingStandardState_PreservesCustomSemanticState()
    {
        var info = ButtonInfo("LockedUI");
        info.DefaultStateName = "LockedUI";
        var b = CreateButton(info);

        b.OnEvent(new UiEvent(0, b, UiEventType.HoverEnter));
        b.OnEvent(new UiEvent(0, b, UiEventType.MouseDown, Data1: 5, Data2: 5));

        Assert.Equal("LockedUI", b.ActiveState);
    }

    [Fact]
    public void PropertyOnlyPressedState_PreservesDrawableNormalFace()
    {
        var info = ButtonInfo("Normal", "Highlight");
        info.States[UiButtonStateMachine.NormalPressed] = new UiStateInfo
        {
            Id = UiButtonStateMachine.NormalPressed,
            Name = "Normal_pressed",
        };
        var b = CreateDrawableButton(info);
        Assert.Equal(1u, DrawnFaceFile(b)); // ButtonInfo assigns "Normal" file 1

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseDown, Data1: 5, Data2: 5));

        Assert.Equal("Normal_pressed", b.ActiveState);
        Assert.Equal(1u, DrawnFaceFile(b));
    }

    [Fact]
    public void DirectStateCascade_WithoutRealMedia_DoesNotBlankAnAlreadyResolvedState()
    {
        var info = ButtonInfo("Normal", "Highlight");
        AddBoolProperty(info, 0x13u, true); // RolloverEnabled — populates States[DirectStateId]
        var b = CreateButton(info);
        Assert.Equal("Normal", b.ActiveState);

        bool ok = b.TrySetRetailState(UiStateInfo.DirectStateId);

        Assert.False(ok);
        Assert.Equal("Normal", b.ActiveState);
    }

    [Fact]
    public void DirectStateTransition_WithRealMedia_StillSucceeds()
    {
        var info = ButtonInfo("Normal");
        info.StateMedia[""] = (7u, 1);
        var b = CreateButton(info);

        bool ok = b.TrySetRetailState(UiStateInfo.DirectStateId);

        Assert.True(ok);
        Assert.Equal("", b.ActiveState);
    }

    [Fact]
    public void HotClick_FiresImmediatelyRepeatsAndSuppressesReleaseClick()
    {
        var info = ButtonInfo("Normal");
        AddBoolProperty(info, 0x0Fu, true);
        AddFloatProperty(info, 0x10u, 0.10f);
        AddFloatProperty(info, 0x11u, 0.05f);
        int clicks = 0;
        var b = CreateButton(info);
        b.OnClick = () => clicks++;

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseDown, Data1: 5, Data2: 5));
        Assert.Equal(1, clicks);
        b.OnGlobalUiTime(1.00);
        b.OnGlobalUiTime(1.09);
        Assert.Equal(1, clicks);
        b.OnGlobalUiTime(1.11);
        Assert.Equal(2, clicks);

        b.OnEvent(new UiEvent(0, b, UiEventType.MouseUp, Data1: 5, Data2: 5));
        b.OnEvent(new UiEvent(0, b, UiEventType.Click, Data1: 5, Data2: 5));
        Assert.Equal(2, clicks);
    }

    [Fact]
    public void CustomSelectionPair_MediaDirectlyOnButton_SelectedTogglesActiveState()
    {
        var info = ButtonInfo("Unselected", "Selected");
        var b = CreateButton(info);

        Assert.Equal("Unselected", b.ActiveState);

        b.Selected = true;
        Assert.Equal("Selected", b.ActiveState);

        b.Selected = false;
        Assert.Equal("Unselected", b.ActiveState);
    }

    [Fact]
    public void CustomSelectionPair_MediaOnFaceChild_SelectedTogglesActiveState()
    {
        var info = new ElementInfo { Type = 1, Width = 305, Height = 32 };
        info.States[UiButtonStateMachine.NormalPressed] = new UiStateInfo
        {
            Id = UiButtonStateMachine.NormalPressed,
            Name = "Normal_pressed",
        };
        info.States[RetailUiStateIds.Unselected] = new UiStateInfo
        {
            Id = RetailUiStateIds.Unselected,
            Name = "Unselected",
        };
        info.States[RetailUiStateIds.Selected] = new UiStateInfo
        {
            Id = RetailUiStateIds.Selected,
            Name = "Selected",
        };
        info.DefaultStateName = "Unselected";

        var dot = new ElementInfo { Type = 3, Width = 32, Height = 32 };
        dot.StateMedia["Unselected"] = (0x06006E35u, 1);
        dot.StateMedia["Selected"] = (0x06006E21u, 1);
        info.Children.Add(dot);

        var b = Assert.IsType<UiButton>(DatWidgetFactory.Create(info, NoTex, null));

        Assert.Equal("Unselected", b.ActiveState);

        b.Selected = true;
        Assert.Equal("Selected", b.ActiveState);
        Assert.Equal(RetailUiStateIds.Selected, b.ActiveRetailStateId);

        b.Selected = false;
        Assert.Equal("Unselected", b.ActiveState);
    }

    [Fact]
    public void CustomSelectionPair_Absent_StandardToggleBehaviorUnchanged()
    {
        var info = ButtonInfo("Normal", "Highlight");
        AddBoolProperty(info, 0x0Bu, true);
        var b = CreateButton(info);

        b.Selected = true;
        Assert.Equal("Highlight", b.ActiveState);

        b.Selected = false;
        Assert.Equal("Normal", b.ActiveState);
    }

    [Fact]
    public void PerStateLabelStyle_PropertyOnlyHighlight_CommitsAndKeepsFace()
    {
        var info = ButtonInfo("Normal"); // Highlight authors NO media...
        info.States[UiButtonStateMachine.Highlight] = new UiStateInfo
        {
            Id = UiButtonStateMachine.Highlight,
            Name = "Highlight",       // ...but IS authored (property-only)
        };
        AddBoolProperty(info, 0x0Bu, true); // ToggleBehavior
        var b = CreateDrawableButton(info);
        b.Label = "Hair Style";
        b.LabelColor = new System.Numerics.Vector4(1f, 1f, 1f, 1f);

        var colors = new Dictionary<uint, System.Numerics.Vector4>
        {
            [UiButtonStateMachine.Normal] = new(218f / 255f, 167f / 255f, 85f / 255f, 1f),
            [UiButtonStateMachine.Highlight] = new(255f / 255f, 221f / 255f, 131f / 255f, 1f),
        };
        var outlines = new Dictionary<uint, bool>
        {
            [UiButtonStateMachine.Normal] = false,
            [UiButtonStateMachine.Highlight] = true,
        };
        b.SetPerStateLabelStyle(colors, outlines);

        Assert.Equal(colors[UiButtonStateMachine.Normal], b.LabelColor);
        Assert.False(b.Outline);
        Assert.Equal(1u, DrawnFaceFile(b)); // "Normal" art (file 1)

        b.Selected = true;

        Assert.Equal("Highlight", b.ActiveState);
        Assert.Equal(colors[UiButtonStateMachine.Highlight], b.LabelColor);
        Assert.True(b.Outline);
        Assert.Equal(1u, DrawnFaceFile(b));
    }

    [Fact]
    public void PerStateLabelStyle_Absent_ExternalLabelColorAssignmentSurvivesStateChanges()
    {
        var info = ButtonInfo("Normal", "Highlight");
        AddBoolProperty(info, 0x0Bu, true);
        var b = CreateButton(info);
        var externalColor = new System.Numerics.Vector4(1f, 0.92f, 0.72f, 1f);
        b.LabelColor = externalColor;

        b.Selected = true;
        Assert.Equal("Highlight", b.ActiveState);
        Assert.Equal(externalColor, b.LabelColor);

        b.Selected = false;
        Assert.Equal(externalColor, b.LabelColor);
    }


    private static float BitmapMeasure(string text) => text.Length * 8f;

    [Fact]
    public void WrapBlockLines_SingleLineThatFits_MatchesPriorOneLineGeometry()
    {
        var lines = UiButton.WrapBlockLines(
            "Health", BitmapMeasure, lineHeight: 24f,
            boxX: 0f, boxY: 0f, boxWidth: 150f, boxHeight: 50f,
            UiButton.LabelAlignment.Left, leftOffset: 3f);

        Assert.Single(lines);
        Assert.Equal("Health", lines[0].Text);
        Assert.Equal(3f, lines[0].X);              // boxX + leftOffset
        Assert.Equal((50f - 24f) * 0.5f, lines[0].Y);
    }

    /// <summary>
    /// R2-2: an authored newline (already normalized to a real '\n' by
    /// DatWidgetFactory's ResolveAuthoredString) splits into stacked lines
    /// even when EACH half individually fits the box — "Attribute\nCredits"
    /// must become two lines, not one literal run.
    /// </summary>
    [Fact]
    public void WrapBlockLines_EmbeddedNewline_ProducesTwoStackedLines()
    {
        var lines = UiButton.WrapBlockLines(
            "Attribute\nCredits", BitmapMeasure, lineHeight: 24f,
            boxX: 0f, boxY: 0f, boxWidth: 90f, boxHeight: 50f,
            UiButton.LabelAlignment.Left, leftOffset: 3f);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Attribute", lines[0].Text);
        Assert.Equal("Credits", lines[1].Text);
        Assert.Equal(1f, lines[0].Y);
        Assert.Equal(25f, lines[1].Y); // startY + 1*lineHeight
    }

    [Fact]
    public void WrapBlockLines_LongSingleParagraph_NeverWordWraps()
    {
        var lines = UiButton.WrapBlockLines(
            "Available Skill Credits", BitmapMeasure, lineHeight: 24f,
            boxX: 0f, boxY: 0f, boxWidth: 113f, boxHeight: 28f,
            UiButton.LabelAlignment.Left, leftOffset: 3f);

        Assert.Single(lines);
        Assert.Equal("Available Skill Credits", lines[0].Text);
    }

    [Fact]
    public void WrapBlockLines_SingleWordNarrowerThanBoxButWiderThanOffsetAdjustedWidth_StaysOneLine()
    {
        var lines = UiButton.WrapBlockLines(
            "Coordination", BitmapMeasure, lineHeight: 24f,
            boxX: 0f, boxY: 0f, boxWidth: 115f, boxHeight: 24f,
            UiButton.LabelAlignment.Left, leftOffset: 3f);

        Assert.Single(lines);
        Assert.Equal("Coordination", lines[0].Text);
    }

    [Fact]
    public void BuildButton_OwnCaptionWithCoexistingValueBox_ConfinesLabelWidthBeforeValueBox()
    {
        uint captionStringId = 333u;
        var info = new ElementInfo { Type = 1, Width = 231, Height = 28 };
        info.States[UiStateInfo.DirectStateId] = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        info.States[UiStateInfo.DirectStateId].Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, captionStringId, 0, 0, 0, 0),
        };
        info.StateMedia[""] = (0x06000001u, 1);

        var valueChild = new ElementInfo { Type = 12, X = 116, Y = 0, Width = 34, Height = 28 };
        info.Children.Add(valueChild);

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value => value.StringId == captionStringId ? "Available Skill Credits" : null));

        Assert.Equal("Available Skill Credits", button.Label);
        Assert.Equal((116f, 0f, 34f, 28f), button.ValueBox);

        float confinedWidth = System.MathF.Min(button.Width, button.ValueBox!.Value.X - 0f);
        Assert.Equal(116f, confinedWidth);
        Assert.True(confinedWidth < button.Width, "the confined width must be narrower than the full button");
    }


    [Fact]
    public void BuildButton_OwnCaption_PassesSourceDecodedTextThrough()
    {
        uint stringId = 444u;
        var info = new ElementInfo { Type = 1, Width = 150, Height = 50 };
        info.States[UiStateInfo.DirectStateId] = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        info.States[UiStateInfo.DirectStateId].Properties.Values[0x17u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, stringId, 0, 0, 0, 0),
        };

        var button = Assert.IsType<UiButton>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value => value.StringId == stringId
                ? "Attribute\n Credits \\not-an-escape"
                : null));

        Assert.Equal("Attribute\n Credits \\not-an-escape", button.Label);
    }

    private static UiButton ButtonWithStates(params string[] states)
    {
        var info = ButtonInfo(states);
        AddBoolProperty(info, 0x13u, true);
        return CreateButton(info);
    }


    private static ElementInfo MapNoteShapedInfo(bool passToChildren = true)
    {
        var info = new ElementInfo { Type = 1, Width = 10, Height = 10 };
        AddBoolProperty(info, 0x13u, true);   // P0x13 RolloverEnabled
        info.States[UiButtonStateMachine.Normal] = new UiStateInfo
        {
            Id = UiButtonStateMachine.Normal,
            Name = "Normal",
            PassToChildren = passToChildren,
        };
        info.States[UiButtonStateMachine.NormalRollover] = new UiStateInfo
        {
            Id = UiButtonStateMachine.NormalRollover,
            Name = "Normal_rollover",
            PassToChildren = passToChildren,
        };
        return info;
    }

    private static UiDatElement HighlightChild()
    {
        var info = new ElementInfo { Type = 3, Width = 10, Height = 10 };
        var normal = new UiStateInfo { Id = 1, Name = "Normal" };
        normal.Properties.Values[0x3Bu] = new UiPropertyValue
        { Kind = UiPropertyKind.Bool, BoolValue = true };
        var rollover = new UiStateInfo { Id = 2, Name = "Normal_rollover" };
        rollover.Properties.Values[0x3Bu] = new UiPropertyValue
        { Kind = UiPropertyKind.Bool, BoolValue = false };
        info.States[1] = normal;
        info.States[2] = rollover;
        return new UiDatElement(info, NoTex);
    }

    [Fact]
    public void PassToChildrenStates_CascadeToStatefulChildren_DrivingPerStateInvisible()
    {
        var b = CreateButton(MapNoteShapedInfo());
        UiDatElement kid = HighlightChild();
        b.AddChild(kid);
        Assert.True(kid.Visible);

        Assert.True(b.TrySetRetailState(UiButtonStateMachine.Normal));
        Assert.False(kid.Visible);

        b.OnEvent(new UiEvent(0, b, UiEventType.HoverEnter));
        Assert.True(kid.Visible);   // Normal_rollover: P0x3B=false -> shown

        b.OnEvent(new UiEvent(0, b, UiEventType.HoverLeave));
        Assert.False(kid.Visible);  // back to Normal: P0x3B=true -> hidden
    }

    [Fact]
    public void StatesWithoutPassToChildren_DoNotCascade()
    {
        var b = CreateButton(MapNoteShapedInfo(passToChildren: false));
        UiDatElement kid = HighlightChild();
        b.AddChild(kid);

        b.TrySetRetailState(UiButtonStateMachine.Normal);
        b.OnEvent(new UiEvent(0, b, UiEventType.HoverEnter));

        Assert.True(kid.Visible);
    }

    [Fact]
    public void UiDatElement_TrySetRetailState_HonorsPerStateInvisible()
    {
        UiDatElement kid = HighlightChild();
        Assert.True(kid.Visible);

        Assert.True(kid.TrySetRetailState(1u));
        Assert.False(kid.Visible);

        Assert.True(kid.TrySetRetailState(2u));
        Assert.True(kid.Visible);
    }

    [Fact]
    public void TrySetRetailState_AppliesInvisibleToButtonNamedAndDirectStates()
    {
        var info = ButtonInfo("Normal", "Ghosted");
        info.StateMedia[""] = (99u, 1);
        info.States[UiStateInfo.DirectStateId] = StateWithInvisible(
            UiStateInfo.DirectStateId, "", invisible: true);
        info.States[UiButtonStateMachine.Normal] = StateWithInvisible(
            UiButtonStateMachine.Normal, "Normal", invisible: false);
        info.States[UiButtonStateMachine.Ghosted] = StateWithInvisible(
            UiButtonStateMachine.Ghosted, "Ghosted", invisible: true);
        var button = CreateButton(info);

        button.Visible = false;
        Assert.True(button.TrySetRetailState(UiButtonStateMachine.Normal));
        Assert.True(button.Visible);

        Assert.True(button.TrySetRetailState(UiButtonStateMachine.Ghosted));
        Assert.False(button.Visible);

        button.Visible = true;
        Assert.True(button.TrySetRetailState(UiStateInfo.DirectStateId));
        Assert.False(button.Visible);
    }

    [Fact]
    public void UiText_ReturningToDirectState_RestoresItsInvisibleProperty()
    {
        var info = new ElementInfo { Type = 12, Width = 20, Height = 20 };
        info.States[UiStateInfo.DirectStateId] = StateWithInvisible(
            UiStateInfo.DirectStateId, "", invisible: true);
        info.States[UiButtonStateMachine.Normal] = StateWithInvisible(
            UiButtonStateMachine.Normal, "Normal", invisible: false);
        var text = new UiText();
        text.ConfigureDatState(info);

        text.Visible = true;
        Assert.True(text.TrySetRetailState(UiStateInfo.DirectStateId));
        Assert.False(text.Visible);
        Assert.True(text.TrySetRetailState(UiButtonStateMachine.Normal));
        Assert.True(text.Visible);
        Assert.True(text.TrySetRetailState(UiStateInfo.DirectStateId));
        Assert.False(text.Visible);
    }

    private static UiStateInfo StateWithInvisible(
        uint id,
        string name,
        bool invisible)
    {
        var state = new UiStateInfo { Id = id, Name = name };
        state.Properties.Values[0x3Bu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Bool,
            BoolValue = invisible,
        };
        return state;
    }

    private static ElementInfo ButtonInfo(params string[] states)
    {
        var info = new ElementInfo { Type = 1, Width = 20, Height = 20 };
        uint file = 1;
        foreach (string state in states)
            info.StateMedia[state] = (file++, 1);
        if (states.Length > 0)
            info.DefaultStateName = states[0];
        return info;
    }

    private static void AddBoolProperty(ElementInfo info, uint id, bool value)
    {
        if (!info.States.TryGetValue(UiStateInfo.DirectStateId, out var state))
        {
            state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
            info.States[UiStateInfo.DirectStateId] = state;
        }
        state.Properties.Values[id] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Bool,
            BoolValue = value,
        };
    }

    private static void AddFloatProperty(ElementInfo info, uint id, float value)
    {
        if (!info.States.TryGetValue(UiStateInfo.DirectStateId, out var state))
        {
            state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
            info.States[UiStateInfo.DirectStateId] = state;
        }
        state.Properties.Values[id] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Float,
            FloatValue = value,
        };
    }

    private static UiButton CreateButton(ElementInfo info)
        => new(info, NoTex) { Width = info.Width, Height = info.Height };

    [Fact]
    public void MultiSegmentFace_CommittedStateWithoutMedia_DrawsInsteadOfThrowing()
    {
        var info = new ElementInfo { Type = 1, Width = 32, Height = 16 };
        var button = new UiButton(
            info,
            static file => (file, 8, 8),
            mediaInfo: null,
            faceSegments: [new ElementInfo { Type = 1, Width = 16, Height = 16 }])
        {
            Width = info.Width,
            Height = info.Height,
        };

        Assert.Equal(0u, DrawnFaceFile(button));
    }


    private sealed class NullGpuFrameSource
        : AcDream.App.Rendering.ICurrentGpuFrameSource
    {
        public AcDream.App.Rendering.Gpu.IGpuFrame? CurrentFrame => null;
    }

    /// <summary>Identity resolve (texture handle == file id) so the drawn
    /// face file is directly observable through the recording renderer.</summary>
    private static UiButton CreateDrawableButton(ElementInfo info)
        => new(info, static file => (file, 8, 8))
        {
            Width = info.Width,
            Height = info.Height,
        };

    private static uint DrawnFaceFile(UiButton button)
    {
        var device = new AcDream.App.Tests.Rendering.Gpu.RecordingGpuDevice();
        var renderer = new AcDream.App.Rendering.TextRenderer(
            device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new System.Numerics.Vector2(200f, 200f));
        var ctx = new UiRenderContext(
            renderer, new System.Numerics.Vector2(200f, 200f));
        button.DrawSelfAndChildren(ctx);
        return renderer.DebugSpriteSegmentVerts.Count == 0
            ? 0u
            : renderer.DebugSpriteSegmentVerts[0].Item1;
    }
}
