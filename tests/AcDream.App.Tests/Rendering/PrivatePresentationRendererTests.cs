using System.Reflection;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class PrivatePresentationRendererTests
{
    private static readonly RenderFrameInput Input = new(0.125d, 1600, 900);
    private static readonly WorldRenderFrameOutcome World = new(3, 12, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_PreservesPortalPaperdollGameplayDevToolsOrder(bool portalVisible)
    {
        List<string> calls = [];
        var portal = new Portal(calls);
        var paperdoll = new Paperdoll(calls);
        var gameplay = new GameplayUi(calls);
        var devTools = new DevTools(calls);
        var presentation = new PrivatePresentationRenderer(
            portal,
            new FoundationSource(portalVisible),
            paperdoll,
            gameplay,
            devTools);

        PrivatePresentationFrameOutcome outcome = presentation.Render(Input, World);

        Assert.Equal(
            ["portal", "paperdoll", "gameplay-ui", "devtools-render"],
            calls);
        Assert.Equal(new PrivatePresentationFrameOutcome(
            portalVisible,
            ScreenshotCaptured: false), outcome);
        Assert.Equal((Input.ViewportWidth, Input.ViewportHeight), portal.Size);
        Assert.Equal(Input, gameplay.Input);
        Assert.Equal(Input, devTools.Input);
    }

    [Fact]
    public void Render_OptionalLayersMayBeAbsentButPortalOutcomeRemainsObserved()
    {
        List<string> calls = [];
        var presentation = new PrivatePresentationRenderer(
            new Portal(calls),
            new FoundationSource(portalVisible: true),
            entityViewports: null,
            gameplayUi: null,
            devTools: null);

        PrivatePresentationFrameOutcome outcome = presentation.Render(Input, World);

        Assert.Equal(["portal"], calls);
        Assert.True(outcome.PortalViewportDrawn);
        Assert.False(outcome.ScreenshotCaptured);
    }

    [Fact]
    public void PrivateEntityViewportGroup_RendersEveryViewportInOrder()
    {
        List<string> calls = [];
        var group = new PrivateEntityViewportFrameGroup(
            new NamedViewport(calls, "paperdoll"),
            null,
            new NamedViewport(calls, "examination"));

        group.Render();

        Assert.Equal(["paperdoll", "examination"], calls);
    }

    [Fact]
    public void Render_ReportsThePreparedPortalSnapshotWhenDrawMutatesTheSource()
    {
        List<string> calls = [];
        var foundation = new MutableFoundationSource(portalVisible: true);
        var presentation = new PrivatePresentationRenderer(
            new MutatingPortal(calls, () => foundation.PortalVisible = false),
            foundation,
            entityViewports: null,
            gameplayUi: null,
            devTools: null);

        PrivatePresentationFrameOutcome outcome = presentation.Render(Input, World);

        Assert.Equal(["portal"], calls);
        Assert.False(foundation.PortalVisible);
        Assert.True(outcome.PortalViewportDrawn);
    }

    [Fact]
    public void Constructor_RequiresTheCanonicalPortalOwner()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PrivatePresentationRenderer(null!, new FoundationSource(false), null, null, null));
        Assert.Throws<ArgumentNullException>(() =>
            new PrivatePresentationRenderer(new Portal([]), null!, null, null, null));
    }

    [Fact]
    public void ExtractedPresentationOwner_RetainsNoDirectWindowOrDelegate()
    {
        FieldInfo[] fields = typeof(PrivatePresentationRenderer).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
        Assert.DoesNotContain(
            fields,
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(IPrivateFrameScreenshot));
    }

    [Fact]
    public void RetainedUiAdapter_UsesTheDocumentedRuntimeBindingBoundary()
    {
        FieldInfo[] adapterFields = typeof(RetainedGameplayUiFrame).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Equal(
            [
                typeof(AcDream.App.UI.RetailUiRuntime),
                typeof(Silk.NET.Input.IInputContext),
            ],
            adapterFields.Select(field => field.FieldType).OrderBy(type => type.FullName));
        Assert.DoesNotContain(
            adapterFields,
            field => field.FieldType == typeof(GameWindow)
                || typeof(Delegate).IsAssignableFrom(field.FieldType));

        FieldInfo bindings = typeof(AcDream.App.UI.RetailUiRuntime).GetField(
            "_bindings",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(AcDream.App.UI.RetailUiRuntimeBindings), bindings.FieldType);
        Assert.Contains(
            typeof(AcDream.App.UI.RetailUiRuntimeBindings).GetProperties(),
            property => property.PropertyType.Name.EndsWith("Bindings", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownSharedOwnerPaths_ArePinnedWithoutClaimingRecursivePurity()
    {
        static Type[] FieldTypes(Type owner) => owner.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Contains(
            typeof(AcDream.App.UI.RetailUiRuntime),
            FieldTypes(typeof(RetainedGameplayUiFrame)));
        Assert.Contains(
            typeof(AcDream.App.UI.UiViewport),
            FieldTypes(typeof(RetailPaperdollFrameView)));
        Assert.Contains(
            typeof(AcDream.App.UI.UiElement),
            FieldTypes(typeof(PaperdollInventoryVisibility)));
        Assert.Contains(
            typeof(AcDream.App.Settings.IRuntimeSettingsPreviewSource),
            FieldTypes(typeof(RuntimeWorldFrameSettingsPreview)));
        Assert.Contains(
            typeof(AcDream.App.Streaming.LocalPlayerTeleportController),
            FieldTypes(typeof(LocalPlayerPortalViewport)));
        Assert.Contains(
            typeof(AcDream.App.Streaming.ILocalPlayerTeleportInputLifetime),
            FieldTypes(typeof(AcDream.App.Streaming.LocalPlayerTeleportController)));

        Assert.DoesNotContain(
            FieldTypes(typeof(PrivateFrameScreenshot)),
            type => typeof(Delegate).IsAssignableFrom(type));
        Assert.Null(typeof(AcDream.App.Diagnostics.FrameScreenshotController).GetField(
            "_getSize",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private sealed class Portal(List<string> calls) : IPrivatePortalViewport
    {
        public (int Width, int Height) Size { get; private set; }

        public void Draw(int width, int height)
        {
            Size = (width, height);
            calls.Add("portal");
        }
    }

    private sealed class FoundationSource(bool portalVisible) : IRenderFrameFoundationSource
    {
        public RenderFrameFoundation Foundation { get; } = new(
            portalVisible,
            default,
            default);
    }

    private sealed class MutableFoundationSource(bool portalVisible) :
        IRenderFrameFoundationSource
    {
        public bool PortalVisible { get; set; } = portalVisible;

        public RenderFrameFoundation Foundation => new(
            PortalVisible,
            default,
            default);
    }

    private sealed class MutatingPortal(List<string> calls, Action mutate) :
        IPrivatePortalViewport
    {
        public void Draw(int width, int height)
        {
            calls.Add("portal");
            mutate();
        }
    }

    private sealed class Paperdoll(List<string> calls) : IPrivateEntityViewportFrame
    {
        public void Render() => calls.Add("paperdoll");
    }

    private sealed class NamedViewport(List<string> calls, string name) :
        IPrivateEntityViewportFrame
    {
        public void Render() => calls.Add(name);
    }

    private sealed class GameplayUi(List<string> calls) : IRetainedGameplayUiFrame
    {
        public RenderFrameInput Input { get; private set; }

        public void Render(double deltaSeconds, int width, int height)
        {
            Input = new RenderFrameInput(deltaSeconds, width, height);
            calls.Add("gameplay-ui");
        }
    }

    private sealed class DevTools(List<string> calls) : IDevToolsFrameLifecycle
    {
        public RenderFrameInput Input { get; private set; }

        public void BeginFrame(float deltaSeconds) => calls.Add("devtools-begin");

        public void AbortFrame() => calls.Add("devtools-abort");

        public void Render(double deltaSeconds, int viewportWidth, int viewportHeight)
        {
            Input = new RenderFrameInput(deltaSeconds, viewportWidth, viewportHeight);
            calls.Add("devtools-render");
        }
    }

}
