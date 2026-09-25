using AcDream.App.Plugins;
using AcDream.App.UI;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.Plugins;

public class BufferedUiRegistryTests
{
    [Fact]
    public void Drain_YieldsBufferedRegistrationsOnceThenEmpty()
    {
        var reg = new BufferedUiRegistry();
        reg.AddMarkupPanel("a.xml", new object());
        reg.AddMarkupPanel("b.xml", new object());

        var drained = reg.Drain();
        Assert.Equal(2, drained.Count);
        Assert.Equal("a.xml", drained[0].MarkupPath);
        Assert.Equal("b.xml", drained[1].MarkupPath);

        Assert.Empty(reg.Drain());
    }

    [Fact]
    public void HasUndrained_TracksRegistrationsMadeAfterADrain()
    {
        var reg = new BufferedUiRegistry();
        Assert.False(reg.HasUndrained);

        reg.AddMarkupPanel("a.xml", new object());
        Assert.True(reg.HasUndrained);
        reg.Drain();
        Assert.False(reg.HasUndrained);

        // A window registered once the UI is up (a server-fed panel) is what
        // the runtime polls for; draining again picks up only that one.
        reg.AddMarkupPanel("late.xml", new object());
        Assert.True(reg.HasUndrained);
        var late = reg.Drain();
        Assert.Equal("late.xml", Assert.Single(late).MarkupPath);
        Assert.False(reg.HasUndrained);
    }

    [Fact]
    public void ScopedRegistrationTokenRemovesAnAlreadyMountedElement()
    {
        var registry = new BufferedUiRegistry();
        IDisposable registration = registry.RegisterMarkupPanel(
            "plugin.xml",
            new object());
        BufferedUiRegistry.Pending pending = Assert.Single(registry.Drain());
        var root = new UiRoot();
        var element = new UiPanel();
        root.AddChild(element);
        registry.CompleteMount(pending, root, element);
        bool windowRemoved = false;
        registry.CompleteWindowMount(pending, () => windowRemoved = true);

        Assert.Contains(element, root.Children);
        Assert.Equal(1, registry.RegistrationCount);

        registration.Dispose();

        Assert.DoesNotContain(element, root.Children);
        Assert.Equal(0, registry.RegistrationCount);
        Assert.True(windowRemoved);
    }

    [Fact]
    public void FirstClassPanelCarriesManifestOwnerAndStableWindowIdentity()
    {
        var registry = new BufferedUiRegistry();
        var descriptor = new PluginPanelDescriptor("main", "Tank")
        {
            IconText = "MT",
            StartVisible = false,
        };

        registry.RegisterPanel(
            new PluginUiOwner("edwards.tank", "Tank"),
            descriptor,
            "tank.xml",
            new object());

        BufferedUiRegistry.Pending pending = Assert.Single(registry.Drain());
        Assert.Equal("edwards.tank", pending.Owner.Id);
        Assert.Same(descriptor, pending.Descriptor);
        Assert.Equal("plugin:edwards.tank:main", pending.WindowName);
        Assert.False(pending.Descriptor.StartVisible);
    }

    [Fact]
    public void InlinePanelContentHasAnIndependentlyRemovableLifetime()
    {
        var registry = new BufferedUiRegistry();
        IDisposable token = registry.RegisterPanelContent(
            new PluginUiOwner("edwards.tank", "Tank"),
            new PluginPanelDescriptor("meta-status", "Status"),
            "<panel w=\"100\" h=\"50\" />",
            new object());

        BufferedUiRegistry.Pending pending = Assert.Single(registry.Drain());
        Assert.Equal("<panel w=\"100\" h=\"50\" />", pending.MarkupContent);
        Assert.Equal("plugin:edwards.tank:meta-status", pending.WindowName);

        token.Dispose();
        Assert.Equal(0, registry.RegistrationCount);
    }

    [Fact]
    public void LateWindowPublicationCleansUpAfterConcurrentDisposal()
    {
        var registry = new BufferedUiRegistry();
        IDisposable token = registry.RegisterMarkupPanel("late.xml", new object());
        BufferedUiRegistry.Pending pending = Assert.Single(registry.Drain());
        token.Dispose();
        bool cleaned = false;

        registry.CompleteWindowMount(pending, () => cleaned = true);

        Assert.True(cleaned);
        Assert.Equal(0, registry.RegistrationCount);
    }

    [Fact]
    public void ScopedViewsExposeOnlyTheirOwnNamedControls()
    {
        var registry = new BufferedUiRegistry();
        var owner = new PluginUiOwner("edwards.tank", "Tank");
        registry.RegisterPanelContent(
            owner,
            new PluginPanelDescriptor("meta", "Status View"),
            "<panel />",
            new object());
        BufferedUiRegistry.Pending pending = Assert.Single(registry.Drain());
        var root = new UiRoot();
        var panel = new UiPanel();
        var button = new UiSimpleButton { Name = "Action", Text = "Old" };
        panel.AddChild(button);
        root.AddChild(panel);
        registry.CompleteMount(pending, root, panel);

        Assert.True(registry.ViewExists(owner, "Status View"));
        Assert.True(registry.IsViewVisible(owner, "meta"));
        Assert.True(registry.ControlExists(owner, "Status View", "Action"));
        Assert.True(registry.SetControlLabel(owner, "Status View", "Action", "New"));
        Assert.Equal("New", button.Text);
        Assert.True(registry.SetControlVisible(
            owner, "Status View", "Action", false));
        Assert.False(button.Visible);
        Assert.False(registry.ViewExists(
            new PluginUiOwner("another.plugin", "Other"), "Status View"));
    }

    /// <summary>
    /// A plugin reopens its own window after the player closed it with the
    /// close button. Before this the window stayed shut: the close cleared
    /// the host's own request, which the plugin's bound visibility could not
    /// reach. Driven through the real window manager and the real visibility
    /// controller a mounted plugin window gets.
    ///
    /// Mutation check (2026-09-25): forwarding ShowPanel to the hide seam
    /// turned this red at "shown again".
    /// </summary>
    [Fact]
    public void APluginReopensItsOwnWindowAfterThePlayerClosedIt()
    {
        var registry = new BufferedUiRegistry();
        var owner = new PluginUiOwner("acdream.test", "Test");
        registry.RegisterPanelContent(
            owner,
            new PluginPanelDescriptor("main", "Main"),
            "<panel />",
            new object());
        BufferedUiRegistry.Pending pending = Assert.Single(registry.Drain());

        var root = new UiRoot { Width = 1280f, Height = 720f };
        bool bindingVisible = true;
        var frame = new UiPanel { Width = 200f, Height = 100f };
        var visibility = new PluginWindowVisibilityController(
            () => bindingVisible, startVisible: true);
        frame.VisibleSource = visibility.ShouldBeVisible;
        frame.Visible = visibility.ShouldBeVisible();
        root.AddChild(frame);
        registry.CompleteMount(pending, root, frame);
        root.WindowManager.Register(pending.WindowName, frame, frame, visibility);
        registry.CompleteWindowMount(
            pending, () => root.WindowManager.Unregister(pending.WindowName));

        // Not bound yet: nothing to show it with.
        Assert.False(registry.ShowPanel(owner, "main"));
        registry.BindPluginWindowControl(root.ShowWindow, root.HideWindow);

        root.Tick(0.016d, 16L);
        Assert.True(registry.IsViewVisible(owner, "main"));

        // The player's close button.
        Assert.True(root.WindowManager.Close(pending.WindowName));
        root.Tick(0.016d, 32L);
        Assert.False(registry.IsViewVisible(owner, "main"));
        // The plugin's own binding cannot bring it back, even when it turns
        // off and on again...
        bindingVisible = false;
        root.Tick(0.016d, 40L);
        bindingVisible = true;
        root.Tick(0.016d, 48L);
        Assert.False(registry.IsViewVisible(owner, "main"));

        // ...and ShowPanel does.
        Assert.True(registry.ShowPanel(owner, "Main"));
        root.Tick(0.016d, 64L);
        Assert.True(registry.IsViewVisible(owner, "main"), "shown again");

        Assert.True(registry.HidePanel(owner, "main"));
        root.Tick(0.016d, 80L);
        Assert.False(registry.IsViewVisible(owner, "main"));

        // Only this plugin's own windows, by id or title.
        Assert.False(registry.ShowPanel(new PluginUiOwner("another", "Other"), "main"));
        Assert.False(registry.ShowPanel(owner, "missing"));

        registry.UnbindClientWindowControl();
        Assert.False(registry.ShowPanel(owner, "main"));
    }

    [Fact]
    public void AWindowNotYetOnScreenCannotBeShown()
    {
        var registry = new BufferedUiRegistry();
        var owner = new PluginUiOwner("acdream.test", "Test");
        registry.BindPluginWindowControl(static _ => true, static _ => true);
        registry.RegisterPanelContent(
            owner,
            new PluginPanelDescriptor("main", "Main"),
            "<panel />",
            new object());

        Assert.False(registry.ShowPanel(owner, "main"));
        Assert.False(registry.HidePanel(owner, "main"));
    }

    [Fact]
    public void ClientWindowControlIsUnavailableUntilBound()
    {
        var registry = new BufferedUiRegistry();

        Assert.False(registry.ToggleClientWindow(PluginClientWindow.Inventory));
        Assert.False(registry.ShowClientWindow(PluginClientWindow.Inventory));
        Assert.False(registry.HideClientWindow(PluginClientWindow.Inventory));
        Assert.False(registry.IsClientWindowVisible(PluginClientWindow.Inventory));
    }

    [Fact]
    public void ClientWindowControlForwardsToTheBoundRetailSeamOnceBound()
    {
        var registry = new BufferedUiRegistry();
        var visible = new Dictionary<PluginClientWindow, bool>();

        registry.BindClientWindowControl(
            toggle: w =>
            {
                visible[w] = !visible.GetValueOrDefault(w);
                return visible[w];
            },
            show: w =>
            {
                visible[w] = true;
                return true;
            },
            hide: w =>
            {
                visible[w] = false;
                return true;
            },
            isVisible: w => visible.GetValueOrDefault(w));

        // Mirrors the input action's ToggleInventory: the first press shows
        // the window, the second hides it.
        Assert.True(registry.ToggleClientWindow(PluginClientWindow.Inventory));
        Assert.True(registry.IsClientWindowVisible(PluginClientWindow.Inventory));
        Assert.False(registry.ToggleClientWindow(PluginClientWindow.Inventory));
        Assert.False(registry.IsClientWindowVisible(PluginClientWindow.Inventory));

        Assert.True(registry.ShowClientWindow(PluginClientWindow.Character));
        Assert.True(registry.IsClientWindowVisible(PluginClientWindow.Character));
        Assert.True(registry.HideClientWindow(PluginClientWindow.Character));
        Assert.False(registry.IsClientWindowVisible(PluginClientWindow.Character));
    }

    [Fact]
    public void BindClientWindowControlRejectsNullDelegates()
    {
        var registry = new BufferedUiRegistry();
        bool True(PluginClientWindow _) => true;

        Assert.Throws<ArgumentNullException>(() =>
            registry.BindClientWindowControl(null!, True, True, True));
        Assert.Throws<ArgumentNullException>(() =>
            registry.BindClientWindowControl(True, null!, True, True));
        Assert.Throws<ArgumentNullException>(() =>
            registry.BindClientWindowControl(True, True, null!, True));
        Assert.Throws<ArgumentNullException>(() =>
            registry.BindClientWindowControl(True, True, True, null!));
    }

    [Fact]
    public void UnbindClientWindowControl_ClearsAllFourDelegates()
    {
        var registry = new BufferedUiRegistry();
        registry.BindClientWindowControl(
            toggle: _ => true,
            show: _ => true,
            hide: _ => true,
            isVisible: _ => true);

        Assert.True(registry.ToggleClientWindow(PluginClientWindow.Inventory));

        registry.UnbindClientWindowControl();

        Assert.False(registry.ToggleClientWindow(PluginClientWindow.Inventory));
        Assert.False(registry.ShowClientWindow(PluginClientWindow.Inventory));
        Assert.False(registry.HideClientWindow(PluginClientWindow.Inventory));
        Assert.False(registry.IsClientWindowVisible(PluginClientWindow.Inventory));
    }
}
