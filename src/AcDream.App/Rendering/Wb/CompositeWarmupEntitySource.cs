using AcDream.App.Streaming;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Wb;

internal sealed class CompositeWarmupEntitySource
{
    private readonly GpuWorldState _world;
    private readonly List<WorldEntity> _entities = [];
    private bool _initialized;
    private ulong _worldGeneration;
    private uint _destinationCell;
    private int _radius;

    public CompositeWarmupEntitySource(GpuWorldState world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public IReadOnlyList<WorldEntity> Entities => _entities;
    public ulong Generation => _worldGeneration;

    public void Refresh(uint destinationCell, int radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        ulong worldGeneration = _world.FlatViewGeneration;
        if (_initialized
            && _worldGeneration == worldGeneration
            && _destinationCell == destinationCell
            && _radius == radius)
        {
            return;
        }

        _world.CopyPublishedEntitiesNearLandblockForReadiness(
            destinationCell,
            radius,
            _entities);
        _worldGeneration = worldGeneration;
        _destinationCell = destinationCell;
        _radius = radius;
        _initialized = true;
    }

    public void Reset()
    {
        _entities.Clear();
        _initialized = false;
        _worldGeneration = 0;
        _destinationCell = 0;
        _radius = 0;
    }
}
