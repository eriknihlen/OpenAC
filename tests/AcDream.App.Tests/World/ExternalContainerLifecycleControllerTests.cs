using AcDream.App.World;
using AcDream.Core.Items;

namespace AcDream.App.Tests.World;

public sealed class ExternalContainerLifecycleControllerTests
{
    [Fact]
    public void ReplacementRequest_RetiresPreviousRootBeforeViewContents()
    {
        var state = new ExternalContainerState();
        var objects = new ClientObjectTable();
        var retired = new List<uint>();
        using var controller = new ExternalContainerLifecycleController(state, objects, retired.Add);
        objects.AddOrUpdate(new ClientObject { ObjectId = 100u });
        objects.ReplaceContents(10u, new[] { new ContainerContentEntry(100u, 0u) });

        state.RequestOpen(10u);
        state.ApplyViewContents(10u);
        state.RequestOpen(20u);

        Assert.Equal(new uint[] { 10u }, retired);
        Assert.Empty(objects.GetContents(10u));

        state.ApplyViewContents(20u);
        state.ApplyViewContents(20u);

        Assert.Equal(new uint[] { 10u }, retired);
        Assert.Empty(objects.GetContents(10u));
        Assert.NotNull(objects.Get(100u));
    }

    [Fact]
    public void AuthoredCloseAndReset_DoNotSendNoLongerViewing()
    {
        var state = new ExternalContainerState();
        var objects = new ClientObjectTable();
        var retired = new List<uint>();
        using var controller = new ExternalContainerLifecycleController(state, objects, retired.Add);

        state.RequestOpen(10u);
        state.ApplyViewContents(10u);
        objects.ReplaceContents(10u, new[] { new ContainerContentEntry(100u, 0u) });
        state.ApplyClose(10u);
        Assert.Empty(objects.GetContents(10u));
        state.RequestOpen(20u);
        state.ApplyViewContents(20u);
        objects.ReplaceContents(20u, new[] { new ContainerContentEntry(200u, 0u) });
        state.Reset();

        Assert.Empty(retired);
        Assert.Empty(objects.GetContents(20u));
    }
}
