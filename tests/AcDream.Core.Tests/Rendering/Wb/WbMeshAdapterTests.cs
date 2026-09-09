using System;
using AcDream.App.Rendering.Wb;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class WbMeshAdapterTests
{
    [Fact]
    public void Construct_WithNullGpuDevice_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WbMeshAdapter(
                gpuDevice: null!,
                dats: null!,
                logger: NullLogger<WbMeshAdapter>.Instance));
    }

    [Fact]
    public void Dispose_OnUninitializedAdapter_DoesNotThrow()
    {
        var adapter = WbMeshAdapter.CreateUninitialized();
        adapter.Dispose();  // no-op when fields are null
        adapter.Dispose();  // idempotent
    }

    [Fact]
    public void IncrementRefCount_OnUninitializedAdapter_NoOps()
    {
        var adapter = WbMeshAdapter.CreateUninitialized();
        // Should not throw, even though there's no underlying mesh manager.
        adapter.IncrementRefCount(0x01000001ul);
    }

    [Fact]
    public void DecrementRefCount_OnUninitializedAdapter_NoOps()
    {
        var adapter = WbMeshAdapter.CreateUninitialized();
        adapter.DecrementRefCount(0x01000001ul);
    }

    [Fact]
    public void GetRenderData_OnUninitializedAdapter_ReturnsNull()
    {
        var adapter = WbMeshAdapter.CreateUninitialized();
        Assert.Null(adapter.GetRenderData(0x01000001ul));
    }

    [Fact]
    public void Tick_OnUninitializedAdapter_DoesNotThrow()
    {
        var adapter = WbMeshAdapter.CreateUninitialized();
        adapter.Tick();  // no-op, no throw
        adapter.Tick();  // idempotent
    }

    [Fact]
    public void Tick_AfterDispose_DoesNotThrow()
    {
        var adapter = WbMeshAdapter.CreateUninitialized();
        adapter.Dispose();
        adapter.Tick();  // no-op, no throw
    }
}
