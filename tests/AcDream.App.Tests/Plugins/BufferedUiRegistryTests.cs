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
        var descriptor = new PluginPanelDescriptor("main", "MossTank")
        {
            IconText = "MT",
            StartVisible = false,
        };

        registry.RegisterPanel(
            new PluginUiOwner("acdream.mosstank", "MossTank"),
            descriptor,
            "mosstank.xml",
            new object());

        BufferedUiRegistry.Pending pending = Assert.Single(registry.Drain());
        Assert.Equal("acdream.mosstank", pending.Owner.Id);
        Assert.Same(descriptor, pending.Descriptor);
        Assert.Equal("plugin:acdream.mosstank:main", pending.WindowName);
        Assert.False(pending.Descriptor.StartVisible);
    }

    [Fact]
    public void InlinePanelContentHasAnIndependentlyRemovableLifetime()
    {
        var registry = new BufferedUiRegistry();
        IDisposable token = registry.RegisterPanelContent(
            new PluginUiOwner("acdream.mosstank", "MossTank"),
            new PluginPanelDescriptor("meta-status", "Status"),
            "<panel w=\"100\" h=\"50\" />",
            new object());

        BufferedUiRegistry.Pending pending = Assert.Single(registry.Drain());
        Assert.Equal("<panel w=\"100\" h=\"50\" />", pending.MarkupContent);
        Assert.Equal("plugin:acdream.mosstank:meta-status", pending.WindowName);

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
        var owner = new PluginUiOwner("acdream.mosstank", "MossTank");
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
}
