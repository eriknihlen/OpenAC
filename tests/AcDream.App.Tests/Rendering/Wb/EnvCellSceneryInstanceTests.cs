using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Wb;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public class EnvCellSceneryInstanceTests
{
    [Fact]
    public void Instance_DefaultConstruct_HasZeroFields()
    {
        var s = new EnvCellSceneryInstance();
        Assert.Equal(0UL, s.ObjectId);
        Assert.False(s.IsBuilding);
        Assert.False(s.IsSetup);
        Assert.False(s.IsEntryCell);
    }

    [Fact]
    public void Instance_AssignFields_RoundTrip()
    {
        var t = Matrix4x4.CreateTranslation(1, 2, 3);
        var s = new EnvCellSceneryInstance
        {
            ObjectId = 0x01000123,
            IsBuilding = true,
            IsSetup = false,
            IsEntryCell = true,
            WorldPosition = new Vector3(1, 2, 3),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
            Transform = t,
        };
        Assert.Equal(0x01000123UL, s.ObjectId);
        Assert.True(s.IsBuilding);
        Assert.True(s.IsEntryCell);
        Assert.Equal(new Vector3(1, 2, 3), s.WorldPosition);
        Assert.Equal(t, s.Transform);
    }

    [Fact]
    public void Landblock_Construct_StartsEmpty()
    {
        var lb = new EnvCellLandblock { GridX = 0xA9, GridY = 0xB4 };
        Assert.Empty(lb.StaticPartGroups);
        Assert.Empty(lb.BuildingPartGroups);
        Assert.Empty(lb.Instances);
        Assert.Empty(lb.EnvCellBounds);
        Assert.False(lb.InstancesReady);
        Assert.False(lb.GpuReady);
        Assert.False(lb.MeshDataReady);
    }

    [Fact]
    public void Landblock_AddInstanceToBuildingPartGroups_PreservesOrder()
    {
        var lb = new EnvCellLandblock();
        if (!lb.BuildingPartGroups.TryGetValue(0x01000001UL, out var list))
        {
            list = new List<InstanceData>();
            lb.BuildingPartGroups[0x01000001UL] = list;
        }
        list.Add(default);
        list.Add(default);
        Assert.Single(lb.BuildingPartGroups);
        Assert.Equal(2, lb.BuildingPartGroups[0x01000001UL].Count);
    }

}
