using System.Numerics;
using System.Text.Json;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

[Collection(PlayerPhysicsQuantumCaptureCollection.Name)]
public sealed class PlayerPhysicsQuantumCaptureTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"acdream-player-physics-{Guid.NewGuid():N}.jsonl");

    public PlayerPhysicsQuantumCaptureTests() =>
        PlayerPhysicsQuantumCapture.ResetForTest();

    [Fact]
    public void DisabledCapture_DoesNotCreateAFile()
    {
        PhysicsBody body = CreateBody();

        Assert.False(PlayerPhysicsQuantumCapture.IsEnabled);
        _ = PlayerPhysicsQuantumCapture.Snapshot(body);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void EnabledCapture_WritesCompleteOrderedQuantum()
    {
        PhysicsBody body = CreateBody();
        PlayerPhysicsQuantumCapture.CapturePath = _path;

        PlayerPhysicsBodyTraceSnapshot start =
            PlayerPhysicsQuantumCapture.Snapshot(body);
        body.Velocity = new Vector3(2f, 3f, -4f);
        PlayerPhysicsBodyTraceSnapshot pre =
            PlayerPhysicsQuantumCapture.Snapshot(body);
        body.UpdatePhysicsInternal(0.05f);
        PlayerPhysicsBodyTraceSnapshot post =
            PlayerPhysicsQuantumCapture.Snapshot(body);
        body.Velocity = new Vector3(1f, 1.5f, 0.2f);
        PlayerPhysicsBodyTraceSnapshot committed =
            PlayerPhysicsQuantumCapture.Snapshot(body);

        PlayerPhysicsQuantumCapture.Log(
            dt: 0.05f,
            cellBefore: 0xAAB40011,
            cellAfter: 0xAAB40012,
            input: new MovementInput(Forward: true, Run: true),
            rootAndManagerDelta: new Vector3(0.1f, 0.2f, 0.3f),
            candidateMoved: true,
            quantumStart: start,
            preIntegration: pre,
            postIntegration: post,
            resolve: new PlayerPhysicsResolveTraceSnapshot(
                new Vector3(4f, 5f, 6f),
                0xAAB40012,
                Ok: true,
                IsOnGround: true,
                InContact: true,
                OnWalkable: true,
                CollisionNormalValid: true,
                CollisionNormal: Vector3.UnitZ),
            postCommit: committed);
        PlayerPhysicsQuantumCapture.Close();

        string[] lines = File.ReadAllLines(_path);
        Assert.Single(lines);

        using JsonDocument json = JsonDocument.Parse(lines[0]);
        JsonElement root = json.RootElement;
        Assert.Equal(0, root.GetProperty("sequence").GetInt64());
        Assert.Equal(0.05f, root.GetProperty("dt").GetSingle());
        Assert.Equal(0xAAB40011u, root.GetProperty("cellBefore").GetUInt32());
        Assert.Equal(0xAAB40012u, root.GetProperty("cellAfter").GetUInt32());
        Assert.True(root.GetProperty("input").GetProperty("forward").GetBoolean());
        Assert.True(root.GetProperty("input").GetProperty("run").GetBoolean());
        Assert.Equal(2f,
            root.GetProperty("preIntegration")
                .GetProperty("velocity")
                .GetProperty("x")
                .GetSingle());
        Assert.True(root.GetProperty("resolve").GetProperty("onWalkable").GetBoolean());
        Assert.Equal(0.2f,
            root.GetProperty("postCommit")
                .GetProperty("velocity")
                .GetProperty("z")
                .GetSingle());
    }

    [Fact]
    public void ResetForTest_ClosesWriterAndRestartsSequence()
    {
        PhysicsBody body = CreateBody();
        PlayerPhysicsQuantumCapture.CapturePath = _path;
        PlayerPhysicsBodyTraceSnapshot snapshot =
            PlayerPhysicsQuantumCapture.Snapshot(body);

        WriteMinimal(snapshot);
        PlayerPhysicsQuantumCapture.ResetForTest();
        PlayerPhysicsQuantumCapture.CapturePath = _path;
        WriteMinimal(snapshot);
        PlayerPhysicsQuantumCapture.Close();

        string[] lines = File.ReadAllLines(_path);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            using JsonDocument json = JsonDocument.Parse(line);
            Assert.Equal(0, json.RootElement.GetProperty("sequence").GetInt64());
        });
    }

    public void Dispose()
    {
        PlayerPhysicsQuantumCapture.ResetForTest();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private void WriteMinimal(PlayerPhysicsBodyTraceSnapshot snapshot) =>
        PlayerPhysicsQuantumCapture.Log(
            0.04f,
            1,
            1,
            default,
            Vector3.Zero,
            false,
            snapshot,
            snapshot,
            snapshot,
            new PlayerPhysicsResolveTraceSnapshot(
                snapshot.Position,
                1,
                true,
                true,
                true,
                true,
                false,
                Vector3.Zero),
            snapshot);

    private static PhysicsBody CreateBody() => new()
    {
        Position = new Vector3(1f, 2f, 3f),
        Velocity = new Vector3(4f, 5f, 6f),
        Acceleration = new Vector3(0f, 0f, -9.8f),
        GroundNormal = Vector3.UnitZ,
        ContactPlaneValid = true,
        ContactPlane = new Plane(Vector3.UnitZ, -3f),
        Friction = 0.95f,
        Elasticity = 0.05f,
        State = PhysicsStateFlags.Gravity,
        TransientState =
            TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
    };
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlayerPhysicsQuantumCaptureCollection
{
    public const string Name = "Player physics quantum capture";
}
