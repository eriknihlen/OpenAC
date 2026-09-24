using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeWorldObjectPortalTests
{
    [Fact]
    public void PortalProjectionSharesKnownDestinationAndLevelLimits()
    {
        var portal = new ClientObject { ObjectId = 1u, Type = ItemType.Portal };
        portal.Properties.Strings[(uint)PropertyString.AppraisalPortalDestination] = "Holtburg";
        portal.Properties.Ints[(uint)PropertyInt.MinLevel] = 10;
        portal.Properties.Ints[(uint)PropertyInt.MaxLevel] = 30;

        PluginWorldObject projected = Project(portal);

        Assert.Equal("Holtburg", projected.PortalDestination);
        Assert.Equal(10, projected.PortalMinimumLevel);
        Assert.Equal(30, projected.PortalMaximumLevel);
    }

    [Fact]
    public void MissingOrUnrestrictedPortalDetailsRemainUnknown()
    {
        var portal = new ClientObject { ObjectId = 2u, Type = ItemType.Portal };
        portal.Properties.Strings[(uint)PropertyString.AppraisalPortalDestination] = " ";
        portal.Properties.Ints[(uint)PropertyInt.MinLevel] = 0;

        PluginWorldObject projected = Project(portal);

        Assert.Null(projected.PortalDestination);
        Assert.Null(projected.PortalMinimumLevel);
        Assert.Null(projected.PortalMaximumLevel);
    }

    [Fact]
    public void OtherObjectsDoNotExposePortalDetails()
    {
        var item = new ClientObject { ObjectId = 3u, Type = ItemType.Misc };
        item.Properties.Strings[(uint)PropertyString.AppraisalPortalDestination] = "Holtburg";
        item.Properties.Ints[(uint)PropertyInt.MinLevel] = 10;

        PluginWorldObject projected = Project(item);

        Assert.Null(projected.PortalDestination);
        Assert.Null(projected.PortalMinimumLevel);
    }

    private static PluginWorldObject Project(ClientObject item) =>
        RuntimeWorldObjectProjection.Project(
            null, item, 0u, new ClientObjectTable());
}
