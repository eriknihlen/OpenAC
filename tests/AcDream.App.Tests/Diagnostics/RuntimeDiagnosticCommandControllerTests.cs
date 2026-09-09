using System.Numerics;
using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.Diagnostics;

[Collection(AcDream.App.Tests.World.WorldEnvironmentControllerCollection.Name)]
public sealed class RuntimeDiagnosticCommandControllerTests
{
    [Fact]
    public void Handle_RoutesCanonicalEnvironmentSceneAndDumpOwners()
    {
        var source = new FakeNearbySource();
        var logs = new List<string>();
        var toasts = new List<string>();
        var scene = new WorldSceneDebugState();
        var controller = new RuntimeDiagnosticCommandController(
            new WorldEnvironmentController(),
            scene,
            pointer: null,
            new NearbyWorldDiagnosticDumper(source, logs.Add),
            toasts.Add);

        Assert.True(controller.Handle(InputAction.AcdreamToggleCollisionWires));
        Assert.True(scene.CollisionWireframesVisible);
        Assert.True(controller.Handle(InputAction.AcdreamCycleTimeOfDay));
        Assert.True(controller.Handle(InputAction.AcdreamCycleWeather));
        Assert.True(controller.Handle(InputAction.AcdreamDumpNearby));
        Assert.True(controller.Handle(InputAction.AcdreamSensitivityDown));
        Assert.True(controller.Handle(InputAction.AcdreamSensitivityUp));
        Assert.False(controller.Handle(InputAction.MovementForward));

        Assert.Contains("Collision wireframes ON", toasts);
        Assert.Contains("Time override = 0.00", toasts);
        Assert.Contains("Weather = Overcast", toasts);
        Assert.Equal(3, toasts.Count);
        Assert.Contains(logs, line => line.StartsWith(
            "=== F3 DEBUG DUMP ===", StringComparison.Ordinal));
    }

    [Fact]
    public void NearbyDump_PreservesLandblockCoordinatesRangeAndDistanceOrder()
    {
        var source = new FakeNearbySource
        {
            ObserverPositionValue = new Vector3(200f, -1f, 3f),
            LiveCenterXValue = 10,
            LiveCenterYValue = 20,
            TotalShadowObjectsValue = 4,
        };
        source.Entities.Add(Entity(2, new Vector3(203f, -1f, 3f)));
        source.Entities.Add(Entity(1, new Vector3(201f, -1f, 3f)));
        source.Entities.Add(Entity(3, new Vector3(216f, -1f, 3f)));
        source.Shadows.Add(new ShadowEntry(
            EntityId: 5,
            GfxObjId: 6,
            Position: new Vector3(202f, -1f, 3f),
            Rotation: Quaternion.Identity,
            Radius: 0.5f,
            CollisionType: ShadowCollisionType.Sphere));
        var lines = new List<string>();

        new NearbyWorldDiagnosticDumper(source, lines.Add).Dump();

        Assert.Contains("landblock=0x0B13FFFF local=(8.00,191.00)", lines[0]);
        Assert.Contains("total shadow objects: 4", lines[0]);
        Assert.Equal("  VISIBLE entities within 15m: 2", lines[1]);
        Assert.Contains("id=0x00000001", lines[2]);
        Assert.Contains("id=0x00000002", lines[3]);
        Assert.Equal("  SHADOW objects within 15m: 1", lines[4]);
        Assert.Contains("id=0x00000005", lines[5]);
    }

    [Fact]
    public void DeferredSlot_ClaimsKnownActionsBeforeBindAndForwardsAfterBind()
    {
        var slot = new RuntimeDiagnosticCommandSlot();
        var target = new FakeCommands();

        Assert.True(slot.Handle(InputAction.AcdreamDumpNearby));
        Assert.False(slot.Handle(InputAction.MovementForward));
        slot.CycleTimeOfDay();

        slot.Bind(target);
        slot.Bind(target);
        Assert.True(slot.Handle(InputAction.AcdreamDumpNearby));
        slot.CycleTimeOfDay();
        slot.CycleWeather();
        slot.ToggleCollisionWireframes();

        Assert.Equal(4, target.CallCount);
        Assert.Throws<InvalidOperationException>(
            () => slot.Bind(new FakeCommands()));
        slot.Unbind(target);
        slot.CycleWeather();
        Assert.Equal(4, target.CallCount);

        slot.Bind(target);
        Action copied = slot.CycleWeather;
        slot.Deactivate();
        copied();
        Assert.Equal(4, target.CallCount);
        Assert.Throws<ObjectDisposedException>(() => slot.Bind(target));
    }

    [Fact]
    public void DeferredSlotOwnedBindingReleasesExactlyAndAllowsRebind()
    {
        var slot = new RuntimeDiagnosticCommandSlot();
        var first = new FakeCommands();
        var second = new FakeCommands();

        IDisposable firstBinding = slot.BindOwned(first);
        Assert.Throws<InvalidOperationException>(() => slot.BindOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = slot.BindOwned(second);
        firstBinding.Dispose();
        slot.CycleWeather();

        Assert.Equal(0, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public void NullToast_DoesNotSuppressDiagnosticStateChanges()
    {
        var environment = new WorldEnvironmentController();
        var scene = new WorldSceneDebugState();
        var sensitivity = new FakeSensitivity();
        var controller = new RuntimeDiagnosticCommandController(
            environment,
            scene,
            sensitivity,
            new NearbyWorldDiagnosticDumper(new FakeNearbySource()),
            toast: null);

        controller.CycleTimeOfDay();
        controller.CycleWeather();
        controller.ToggleCollisionWireframes();
        Assert.True(controller.Handle(InputAction.AcdreamSensitivityDown));
        Assert.True(controller.Handle(InputAction.AcdreamSensitivityUp));

        Assert.Equal(0f, environment.DayFraction);
        Assert.Equal(WeatherKind.Overcast, environment.Weather.Kind);
        Assert.True(scene.CollisionWireframesVisible);
        Assert.Equal(2, sensitivity.AdjustCount);
    }

    private static WorldEntity Entity(uint id, Vector3 position) => new()
    {
        Id = id,
        SourceGfxObjOrSetupId = id + 100,
        Position = position,
        Rotation = Quaternion.Identity,
        MeshRefs = [],
    };

    private sealed class FakeNearbySource : INearbyWorldDiagnosticSource
    {
        public Vector3 ObserverPositionValue { get; set; }
        public int LiveCenterXValue { get; set; }
        public int LiveCenterYValue { get; set; }
        public int TotalShadowObjectsValue { get; set; }
        public List<WorldEntity> Entities { get; } = [];
        public List<ShadowEntry> Shadows { get; } = [];

        public Vector3 ObserverPosition => ObserverPositionValue;
        public int LiveCenterX => LiveCenterXValue;
        public int LiveCenterY => LiveCenterYValue;
        public int TotalShadowObjects => TotalShadowObjectsValue;
        public IReadOnlyList<WorldEntity> WorldEntities => Entities;
        public IEnumerable<ShadowEntry> ShadowEntries => Shadows;
    }

    private sealed class FakeCommands : IRuntimeDiagnosticCommands
    {
        public int CallCount { get; private set; }
        public bool Handle(InputAction action)
        {
            CallCount++;
            return true;
        }
        public void CycleTimeOfDay() => CallCount++;
        public void CycleWeather() => CallCount++;
        public void ToggleCollisionWireframes() => CallCount++;
    }

    private sealed class FakeSensitivity : IPointerSensitivityCommands
    {
        public int AdjustCount { get; private set; }

        public string AdjustSensitivity(float factor)
        {
            AdjustCount++;
            return $"sensitivity {factor}";
        }
    }
}
