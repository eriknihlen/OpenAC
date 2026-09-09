using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Core.Selection;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.Tests.UI.Layout;

public sealed class UiWindowDrawCaptureSweepTests
{
    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (RecordingGpuDevice device, TextRenderer renderer, UiRenderContext ctx) MakeContext(
        float w, float h)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(w, h));
        var ctx = new UiRenderContext(renderer, new Vector2(w, h));
        return (device, renderer, ctx);
    }

    private static int TotalVertexCount(TextRenderer renderer)
    {
        int total = 0;
        foreach (var seg in renderer.DebugSpriteSegmentVerts)
            total += seg.Verts.Count / 8;
        return total;
    }

    public static IEnumerable<object[]> Windows()
    {
        yield return new object[] { "Character" };
        yield return new object[] { "Chat" };
        yield return new object[] { "Vendor" };
        yield return new object[] { "Options" };
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void MountedWindow_DrawsAVertexFloor_AndItsLiveKeySpriteId(string window)
    {
        (UiElement drawRoot, uint keySprite, int vertexFloor) = window switch
        {
            "Character" => BuildCharacter(),
            "Chat" => BuildChat(),
            "Vendor" => BuildVendor(),
            "Options" => BuildOptions(),
            _ => throw new ArgumentOutOfRangeException(nameof(window), window, null),
        };

        Assert.NotEqual(0u, keySprite);

        var (_, renderer, ctx) = MakeContext(1600f, 1200f);
        drawRoot.DrawSelfAndChildren(ctx);

        int vertices = TotalVertexCount(renderer);
        Assert.True(
            vertices >= vertexFloor,
            $"{window}: expected at least {vertexFloor} drawn vertices, got {vertices} " +
            "-- a resolve-wiring regression (CH6a/b BLOCKER 1's class) would show up as a " +
            "near-zero count here.");
        Assert.Contains(
            renderer.DebugSpriteSegmentVerts,
            s => s.Texture == keySprite);
    }

    private static (UiElement drawRoot, uint keySprite, int vertexFloor) BuildCharacter()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCharacterInfos(), id => (id, 8, 8), null);
        CharacterStatController.Bind(
            layout, SampleData.SampleCharacter, spriteResolve: id => (id, 8, 8));

        var screen = new UiRoot { Width = 1600f, Height = 1200f };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            screen,
            layout.Root,
            id => (id, 8, 8),
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Character,
                Chrome = RetailWindowChrome.NineSlice,
                ContentHeight = 362f,
                MinWidth = 310f,
                MaxWidth = 310f,
                MinHeight = 372f,
                MaxHeight = 1000f,
                ResizeX = false,
                ResizeY = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
            });

        return (handle.OuterFrame, RetailChromeSprites.TopEdge, 250);   // observed baseline 588
    }

    private static (UiElement drawRoot, uint keySprite, int vertexFloor) BuildChat()
    {
        var infos = FixtureLoader.LoadChatInfos();
        ImportedLayout layout = LayoutImporter.Build(infos, id => (id, 8, 8), null);
        var controller = ChatWindowController.Bind(
            infos,
            layout,
            new ChatVM(new ChatLog()),
            () => NullCommandBus.Instance,
            new ChatWindowState(),
            null,
            null,
            id => (id, 8, 8));
        Assert.NotNull(controller);

        var root = new UiRoot { Width = 1600f, Height = 1200f };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            controller!.Root,
            id => (id, 8, 8),
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Chat,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f,
                Top = 10f,
                DatConstraintSource = controller.DatWindowInfo,
            });
        controller.AttachWindow(handle);

        // Key sprite: read LIVE off the bound scrollbar's own TrackSprite --
        // the scrollbar always draws its track whenever the window does, so
        // this tracks whatever the real fixture actually authors rather than
        // a hardcoded literal.
        return (handle.OuterFrame, controller.Scrollbar.TrackSprite, 80);   // observed baseline 162
    }

    private static (UiElement drawRoot, uint keySprite, int vertexFloor) BuildVendor()
    {
        ImportedLayout layout = FixtureLoader.LoadVendor();
        var screen = new UiRoot { Width = 1600f, Height = 1200f };
        RetailWindowHandle window = RetailWindowFrame.Mount(
            screen,
            layout.Root,
            id => (id, 8, 8),
            new RetailWindowFrame.Options
            {
                WindowName = "vendor-sweep",
                Chrome = RetailWindowChrome.Imported,
                Visible = true,
            });

        var objects = new ClientObjectTable();
        var itemInteraction = new ItemInteractionController(
            objects,
            new RuntimeInteractionTransactionState(new InventoryTransactionState(objects)),
            new InteractionState(),
            playerGuid: static () => 0u,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null);
        VendorUiController? controller = VendorUiController.Bind(
            layout,
            new VendorState(),
            window,
            static (_, iconId, _, _, _) => iconId,
            objects,
            static () => 0u,
            itemInteraction,
            new SelectionState(),
            new StackSplitQuantityState(),
            datFont: null,
            debugFont: null,
            id => (id, 8, 8));
        Assert.NotNull(controller);

        var typeMenu = Assert.IsType<UiMenu>(layout.FindElement(VendorUiController.TypeFilterMenuId));
        return (window.OuterFrame, typeMenu.NormalSprite, 25);   // observed baseline 54
    }

    private static (UiElement drawRoot, uint keySprite, int vertexFloor) BuildOptions()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadOptionsPanelHostInfos(), id => (id, 8, 8), null);
        var callbacks = new OptionsPanelController.Callbacks(
            Toggle: () => { },
            RequestExitToCharacterSelection: () => { },
            ExitGame: () => { },
            UseMouseTurningSettings: () => { },
            DisplaySystemMessage: _ => { });
        OptionsPanelController? controller = OptionsPanelController.Bind(
            layout, callbacks, resolveSprite: id => (id, 8, 8));
        Assert.NotNull(controller);
        controller!.ActivateTabs();

        return (layout.Root, RetailChromeSprites.CenterFill, 120);   // observed baseline 240
    }
}
