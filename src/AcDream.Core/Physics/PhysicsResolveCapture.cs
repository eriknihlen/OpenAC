using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Threading;

namespace AcDream.Core.Physics;


public static class PhysicsResolveCapture
{
    public static string? CapturePath { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_CAPTURE_RESOLVE");

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(CapturePath);

    private static int _tickCounter;
    private static StreamWriter? _writer;
    private static readonly object _writerLock = new();

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented        = false,
        IncludeFields        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static PhysicsBodySnapshot Snapshot(PhysicsBody body) => new(
        Position:              body.Position,
        Orientation:           body.Orientation,
        Velocity:              body.Velocity,
        Acceleration:          body.Acceleration,
        Omega:                 body.Omega,
        GroundNormal:          body.GroundNormal,
        SlidingNormal:         body.SlidingNormal,
        ContactPlaneValid:     body.ContactPlaneValid,
        ContactPlane:          body.ContactPlane,
        ContactPlaneCellId:    body.ContactPlaneCellId,
        ContactPlaneIsWater:   body.ContactPlaneIsWater,
        WalkablePolygonValid:  body.WalkablePolygonValid,
        WalkablePlane:         body.WalkablePlane,
        WalkableVertices:      body.WalkableVertices is null
                                   ? null
                                   : (Vector3[])body.WalkableVertices.Clone(),
        WalkableUp:            body.WalkableUp,
        Elasticity:            body.Elasticity,
        Friction:              body.Friction,
        State:                 (uint)body.State,
        TransientState:        (uint)body.TransientState,
        LastUpdateTime:        body.LastUpdateTime);

    public static void LogCall(
        ResolveCallInputs    input,
        PhysicsBodySnapshot? bodyBefore,
        ResolveCallResult    result,
        PhysicsBodySnapshot? bodyAfter)
    {
        if (string.IsNullOrWhiteSpace(CapturePath))
            return;

        var record = new ResolveCaptureRecord(
            Tick:        Interlocked.Increment(ref _tickCounter) - 1,
            TimestampMs: (long)(System.Diagnostics.Stopwatch.GetTimestamp()
                                 * 1000.0 / System.Diagnostics.Stopwatch.Frequency),
            Input:       input,
            BodyBefore:  bodyBefore,
            Result:      result,
            BodyAfter:   bodyAfter);

        string json = JsonSerializer.Serialize(record, s_jsonOptions);

        lock (_writerLock)
        {
            EnsureWriter_NoLock();
            _writer!.WriteLine(json);
            _writer.Flush();
        }
    }

    public static void Close()
    {
        lock (_writerLock)
        {
            if (_writer is not null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    public static void ResetTickCounter() =>
        Interlocked.Exchange(ref _tickCounter, 0);

    public static void ResetForTest()
    {
        Close();
        CapturePath = null;
        Interlocked.Exchange(ref _tickCounter, 0);
    }

    private static void EnsureWriter_NoLock()
    {
        if (_writer is not null)
            return;

        var path = CapturePath!;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Append mode — multiple sessions can accumulate in the same file
        // (the user can split by Tick=0 boundaries later).
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        _writer = new StreamWriter(stream)
        {
            AutoFlush = false,
        };

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Close();
    }
}


public sealed record ResolveCaptureRecord(
    int                  Tick,
    long                 TimestampMs,
    ResolveCallInputs    Input,
    PhysicsBodySnapshot? BodyBefore,
    ResolveCallResult    Result,
    PhysicsBodySnapshot? BodyAfter);

public sealed record ResolveCallInputs(
    Vector3 CurrentPos,
    Vector3 TargetPos,
    uint    CellId,
    float   SphereRadius,
    float   SphereHeight,
    float   StepUpHeight,
    float   StepDownHeight,
    bool    IsOnGround,
    uint    MoverFlags,
    uint    MovingEntityId);

public sealed record ResolveCallResult(
    Vector3 Position,
    uint    CellId,
    bool    IsOnGround,
    bool    CollisionNormalValid,
    Vector3 CollisionNormal);

public sealed record PhysicsBodySnapshot(
    Vector3    Position,
    Quaternion Orientation,
    Vector3    Velocity,
    Vector3    Acceleration,
    Vector3    Omega,
    Vector3    GroundNormal,
    Vector3    SlidingNormal,
    bool       ContactPlaneValid,
    Plane      ContactPlane,
    uint       ContactPlaneCellId,
    bool       ContactPlaneIsWater,
    bool       WalkablePolygonValid,
    Plane      WalkablePlane,
    Vector3[]? WalkableVertices,
    Vector3    WalkableUp,
    float      Elasticity,
    float      Friction,
    uint       State,
    uint       TransientState,
    double     LastUpdateTime);
