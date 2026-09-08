using System.Numerics;
using System.Text.Json;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Gameplay;

internal static class PlayerPhysicsQuantumCapture
{
    internal static string? CapturePath { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_CAPTURE_PLAYER_QUANTA");

    internal static bool IsEnabled => !string.IsNullOrWhiteSpace(CapturePath);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly object s_writerLock = new();
    private static StreamWriter? s_writer;
    private static long s_sequence;
    private static bool s_processExitHooked;

    internal static PlayerPhysicsBodyTraceSnapshot Snapshot(PhysicsBody body) =>
        new(
            Position: body.Position,
            Orientation: body.Orientation,
            Velocity: body.Velocity,
            Acceleration: body.Acceleration,
            GroundNormal: body.GroundNormal,
            ContactPlaneValid: body.ContactPlaneValid,
            ContactPlane: body.ContactPlane,
            Friction: body.Friction,
            Elasticity: body.Elasticity,
            State: (uint)body.State,
            TransientState: (uint)body.TransientState,
            FramesStationaryFall: body.FramesStationaryFall);

    internal static void Log(
        float dt,
        uint cellBefore,
        uint cellAfter,
        MovementInput input,
        Vector3 rootAndManagerDelta,
        bool candidateMoved,
        PlayerPhysicsBodyTraceSnapshot quantumStart,
        PlayerPhysicsBodyTraceSnapshot preIntegration,
        PlayerPhysicsBodyTraceSnapshot postIntegration,
        PlayerPhysicsResolveTraceSnapshot resolve,
        PlayerPhysicsBodyTraceSnapshot postCommit)
    {
        string? path = CapturePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        var record = new PlayerPhysicsQuantumTraceRecord(
            Sequence: Interlocked.Increment(ref s_sequence) - 1,
            TimestampTicks: System.Diagnostics.Stopwatch.GetTimestamp(),
            Dt: dt,
            CellBefore: cellBefore,
            CellAfter: cellAfter,
            Input: new PlayerMovementInputTraceSnapshot(
                input.Forward,
                input.Backward,
                input.StrafeLeft,
                input.StrafeRight,
                input.TurnLeft,
                input.TurnRight,
                input.Run,
                input.Jump),
            RootAndManagerDelta: rootAndManagerDelta,
            CandidateMoved: candidateMoved,
            QuantumStart: quantumStart,
            PreIntegration: preIntegration,
            PostIntegration: postIntegration,
            Resolve: resolve,
            PostCommit: postCommit);

        string json = JsonSerializer.Serialize(record, s_jsonOptions);
        lock (s_writerLock)
        {
            EnsureWriter_NoLock(path);
            s_writer!.WriteLine(json);
            s_writer.Flush();
        }
    }

    internal static void Close()
    {
        lock (s_writerLock)
        {
            s_writer?.Dispose();
            s_writer = null;
        }
    }

    internal static void ResetForTest()
    {
        Close();
        CapturePath = null;
        Interlocked.Exchange(ref s_sequence, 0);
    }

    private static void EnsureWriter_NoLock(string path)
    {
        if (s_writer is not null)
            return;

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        s_writer = new StreamWriter(new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read))
        {
            AutoFlush = false,
        };

        if (!s_processExitHooked)
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Close();
            s_processExitHooked = true;
        }
    }
}

internal sealed record PlayerPhysicsQuantumTraceRecord(
    long Sequence,
    long TimestampTicks,
    float Dt,
    uint CellBefore,
    uint CellAfter,
    PlayerMovementInputTraceSnapshot Input,
    Vector3 RootAndManagerDelta,
    bool CandidateMoved,
    PlayerPhysicsBodyTraceSnapshot QuantumStart,
    PlayerPhysicsBodyTraceSnapshot PreIntegration,
    PlayerPhysicsBodyTraceSnapshot PostIntegration,
    PlayerPhysicsResolveTraceSnapshot Resolve,
    PlayerPhysicsBodyTraceSnapshot PostCommit);

internal readonly record struct PlayerMovementInputTraceSnapshot(
    bool Forward,
    bool Backward,
    bool StrafeLeft,
    bool StrafeRight,
    bool TurnLeft,
    bool TurnRight,
    bool Run,
    bool Jump);

internal readonly record struct PlayerPhysicsBodyTraceSnapshot(
    Vector3 Position,
    Quaternion Orientation,
    Vector3 Velocity,
    Vector3 Acceleration,
    Vector3 GroundNormal,
    bool ContactPlaneValid,
    Plane ContactPlane,
    float Friction,
    float Elasticity,
    uint State,
    uint TransientState,
    int FramesStationaryFall);

internal readonly record struct PlayerPhysicsResolveTraceSnapshot(
    Vector3 Position,
    uint CellId,
    bool Ok,
    bool IsOnGround,
    bool InContact,
    bool OnWalkable,
    bool CollisionNormalValid,
    Vector3 CollisionNormal);
