using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class MarkupResizableAnchorTests
{
    private sealed class ListBinding
    {
        public IReadOnlyList<string> Items => ["A", "B", "C"];
        public int Selected { get; set; } = -1;
        public Action<int> OnSelect => value => Selected = value;
    }

    // ── resizable / minw / minh parse tests ─────────────────────────────────

    [Fact]
    public void Build_PanelWithoutResizableAttribute_IsFixedSizeByDefault()
    {
        const string xml = "<panel x=\"0\" y=\"0\" w=\"300\" h=\"200\"></panel>";
        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));

        Assert.False(panel.Resizable);
        Assert.False(panel.ResizeX);
        Assert.False(panel.ResizeY);
        Assert.Equal(300f, panel.MinWidth);
        Assert.Equal(200f, panel.MinHeight);
    }

    [Fact]
    public void Build_PanelResizableTrue_ArmsBothAxesAndDefaultsMinToAuthoredSize()
    {
        const string xml = "<panel x=\"0\" y=\"0\" w=\"300\" h=\"200\" resizable=\"true\"></panel>";
        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));

        Assert.True(panel.Resizable);
        Assert.True(panel.ResizeX);
        Assert.True(panel.ResizeY);
        Assert.Equal(300f, panel.MinWidth);
        Assert.Equal(200f, panel.MinHeight);
    }

    [Fact]
    public void Build_PanelResizableTrueWithMinwMinh_OverridesTheAuthoredSizeFloor()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"300\" h=\"200\" resizable=\"true\" minw=\"150\" minh=\"90\"></panel>";
        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));

        Assert.Equal(150f, panel.MinWidth);
        Assert.Equal(90f, panel.MinHeight);
    }

    [Fact]
    public void Build_PanelResizableTrueWithResizeAxisLock_NarrowsToOneAxis()
    {
        // The pre-existing resize="x"|"y"|"both"|"none" attribute still
        // layers on top of resizable="true" to narrow which axis actually
        // drags — it just can no longer be the SOLE switch (resizable is).
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"300\" h=\"200\" resizable=\"true\" resize=\"x\"></panel>";
        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));

        Assert.True(panel.Resizable);
        Assert.True(panel.ResizeX);
        Assert.False(panel.ResizeY);
    }

    // ── anchor grammar parse tests ───────────────────────────────────────────

    [Theory]
    [InlineData("group")]
    [InlineData("list")]
    [InlineData("menu")]
    [InlineData("field")]
    [InlineData("label")]
    [InlineData("button")]
    [InlineData("icon")]
    public void Build_ElementWithoutAnchorAttribute_DefaultsToLeftTop(string tag)
    {
        string xml = WrapSingle(tag, anchor: null);
        var panel = MarkupDocument.Build(xml, new ListBinding(), _ => (1u, 32, 32));

        UiElement element = panel.Children[0];
        Assert.Equal(AnchorEdges.Left | AnchorEdges.Top, element.Anchors);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("list")]
    [InlineData("menu")]
    [InlineData("field")]
    [InlineData("label")]
    [InlineData("button")]
    [InlineData("icon")]
    public void Build_ElementAnchorLeftRight_SetsBothHorizontalEdges(string tag)
    {
        string xml = WrapSingle(tag, anchor: "left right");
        var panel = MarkupDocument.Build(xml, new ListBinding(), _ => (1u, 32, 32));

        UiElement element = panel.Children[0];
        Assert.Equal(AnchorEdges.Left | AnchorEdges.Right, element.Anchors);
    }

    [Fact]
    public void Build_AnchorAllFourTokens_SetsEveryEdge()
    {
        string xml = WrapSingle("group", anchor: "left top right bottom");
        var panel = MarkupDocument.Build(xml, new ListBinding(), _ => (1u, 32, 32));

        Assert.Equal(
            AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right | AnchorEdges.Bottom,
            panel.Children[0].Anchors);
    }

    [Fact]
    public void Build_AnchorIsCaseInsensitiveAndOrderIndependent()
    {
        string xml = WrapSingle("button", anchor: "BOTTOM Right");
        var panel = MarkupDocument.Build(xml, new ListBinding(), _ => (1u, 32, 32));

        Assert.Equal(AnchorEdges.Bottom | AnchorEdges.Right, panel.Children[0].Anchors);
    }

    [Fact]
    public void Build_UnknownAnchorToken_ThrowsNamingTheElement()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button name=\"Fire1\" x=\"0\" y=\"0\" w=\"40\" h=\"20\" text=\"Go\" anchor=\"left frotz\"/>" +
            "</panel>";

        FormatException ex = Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32)));

        Assert.Contains("Fire1", ex.Message);
        Assert.Contains("frotz", ex.Message);
    }

    private static string WrapSingle(string tag, string? anchor)
    {
        string anchorAttr = anchor is null ? string.Empty : $" anchor=\"{anchor}\"";
        string inner = tag switch
        {
            "group" => $"<group x=\"10\" y=\"10\" w=\"100\" h=\"60\"{anchorAttr}></group>",
            "list" => $"<list x=\"10\" y=\"10\" w=\"100\" h=\"60\" items=\"{{Items}}\" " +
                      $"selected=\"{{Selected}}\" onchange=\"{{OnSelect}}\"{anchorAttr}/>",
            "menu" => $"<menu x=\"10\" y=\"10\" w=\"100\" h=\"20\" items=\"{{Items}}\"{anchorAttr}/>",
            "field" => $"<field x=\"10\" y=\"10\" w=\"100\" h=\"20\"{anchorAttr}/>",
            "label" => $"<label x=\"10\" y=\"10\" text=\"Hi\"{anchorAttr}/>",
            "button" => $"<button x=\"10\" y=\"10\" w=\"40\" h=\"20\" text=\"Go\"{anchorAttr}/>",
            "icon" => $"<icon x=\"10\" y=\"10\" w=\"32\" h=\"32\" did=\"0x06000001\"{anchorAttr}/>",
            _ => throw new ArgumentOutOfRangeException(nameof(tag)),
        };
        return "<panel x=\"0\" y=\"0\" w=\"200\" h=\"150\">" + inner + "</panel>";
    }

    // ── dynamic anchor re-layout tests (recording renderer) ─────────────────

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static UiRenderContext MakeContext(float w, float h)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(w, h));
        return new UiRenderContext(renderer, new Vector2(w, h));
    }

    [Fact]
    public void ResizingPanel_LeftRightList_WidensWithThePanel()
    {
        const string xml = """
            <panel x="0" y="0" w="300" h="200" resizable="true" minw="200" minh="150">
              <list anchor="left right" x="10" y="10" w="280" h="150"
                    items="{Items}" selected="{Selected}" onchange="{OnSelect}"/>
            </panel>
            """;
        var panel = MarkupDocument.Build(xml, new ListBinding(), _ => (1u, 32, 32));
        var list = Assert.IsType<UiMarkupList>(panel.Children[0]);

        UiRenderContext ctx = MakeContext(600f, 400f);
        panel.DrawSelfAndChildren(ctx);
        Assert.Equal(280f, list.Width);

        panel.Width = 500f;
        panel.DrawSelfAndChildren(ctx);

        Assert.Equal(480f, list.Width); // 500 - 10 - 10
        Assert.Equal(10f, list.Left);   // left margin preserved
    }

    [Fact]
    public void ResizingPanel_RightAnchoredButton_MovesWithTheRightEdge()
    {
        const string xml = """
            <panel x="0" y="0" w="300" h="200" resizable="true" minw="200" minh="150">
              <button anchor="right" x="250" y="10" w="40" h="20" text="X"/>
            </panel>
            """;
        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        UiRenderContext ctx = MakeContext(600f, 400f);
        panel.DrawSelfAndChildren(ctx);
        Assert.Equal(250f, button.Left); // unchanged: 10px margin to the right edge
        Assert.Equal(40f, button.Width); // fixed width — right-only anchor never stretches

        panel.Width = 500f;
        panel.DrawSelfAndChildren(ctx);

        Assert.Equal(450f, button.Left); // 500 - 10 - 40: follows the right edge
        Assert.Equal(40f, button.Width); // still fixed width
    }

    [Fact]
    public void ResizingPanel_GroupStretchesBothAxesAndNestedListFollowsGroupWidth()
    {
        const string xml = """
            <panel x="0" y="0" w="300" h="200" resizable="true" minw="200" minh="150">
              <group anchor="left top right bottom" x="10" y="10" w="280" h="180">
                <list anchor="left right" x="5" y="5" w="270" h="170"
                      items="{Items}" selected="{Selected}" onchange="{OnSelect}"/>
              </group>
            </panel>
            """;
        var panel = MarkupDocument.Build(xml, new ListBinding(), _ => (1u, 32, 32));
        var group = Assert.IsType<UiPanel>(panel.Children[0]);
        var list = Assert.IsType<UiMarkupList>(group.Children[0]);

        UiRenderContext ctx = MakeContext(600f, 400f);
        panel.DrawSelfAndChildren(ctx);
        Assert.Equal(280f, group.Width);
        Assert.Equal(180f, group.Height);
        Assert.Equal(270f, list.Width);

        panel.Width = 500f;
        panel.Height = 300f;
        panel.DrawSelfAndChildren(ctx);

        // Group stretches on both axes (10px margin preserved on every side).
        Assert.Equal(480f, group.Width);  // 500 - 10 - 10
        Assert.Equal(280f, group.Height); // 300 - 10 - 10
        // The nested list follows the GROUP's new width (5px margin to each
        // side of the group, not the panel).
        Assert.Equal(470f, list.Width);   // 480 - 5 - 5
    }

    // ── golden: a plain panel with none of the new attributes is unaffected ──

    [Fact]
    public void Build_PlainPanel_DrawsIdenticallyAcrossRepeatedBuilds()
    {
        const string xml = """
            <panel x="0" y="0" w="200" h="120" title="V">
              <group x="4" y="4" w="180" h="40" background="#FF102030">
                <button x="4" y="4" w="60" h="20" text="Go"/>
                <label x="4" y="28" text="Hi"/>
              </group>
              <list x="4" y="48" w="180" h="60" items="{Items}" selected="{Selected}" onchange="{OnSelect}"/>
            </panel>
            """;

        var panelA = MarkupDocument.Build(xml, new ListBinding(), _ => (7u, 32, 32));
        var panelB = MarkupDocument.Build(xml, new ListBinding(), _ => (7u, 32, 32));

        var deviceA = new RecordingGpuDevice();
        var rendererA = new TextRenderer(deviceA, new NullGpuFrameSource(), "unused");
        rendererA.Begin(new Vector2(400f, 300f));
        panelA.DrawSelfAndChildren(new UiRenderContext(rendererA, new Vector2(400f, 300f)));

        var deviceB = new RecordingGpuDevice();
        var rendererB = new TextRenderer(deviceB, new NullGpuFrameSource(), "unused");
        rendererB.Begin(new Vector2(400f, 300f));
        panelB.DrawSelfAndChildren(new UiRenderContext(rendererB, new Vector2(400f, 300f)));

        Assert.Equal(deviceA.Calls, deviceB.Calls);
        Assert.False(panelA.Resizable);
        Assert.Equal(AnchorEdges.Left | AnchorEdges.Top, panelA.Children[0].Anchors);
        Assert.Equal(AnchorEdges.Left | AnchorEdges.Top, panelB.Children[0].Anchors);
    }
}
