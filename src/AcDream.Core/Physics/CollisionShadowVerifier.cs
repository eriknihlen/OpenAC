using System.Globalization;
using System.Numerics;
using System.Text.Json;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

public readonly record struct CollisionShadowStats(
    long Queries,
    long Samples,
    long Matches,
    long Mismatches,
    long Faults);

internal sealed class CollisionShadowVerifier
{
    private readonly int _sampleEvery;
    private readonly string _artifactDirectory;
    private readonly Transition _shadowTransition = new();
    private long _queries;
    private long _samples;
    private long _matches;
    private long _mismatches;
    private long _faults;
    private bool _flatPass;
    private bool _graphPass;

    public CollisionShadowVerifier(
        int sampleEvery,
        string artifactDirectory)
    {
        if (sampleEvery <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleEvery));
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        _sampleEvery = sampleEvery;
        _artifactDirectory = Path.GetFullPath(artifactDirectory);
    }

    internal bool IsFlatPass => _flatPass;
    internal bool IsGraphPass => _graphPass;

    internal CollisionShadowStats Stats => new(
        _queries,
        _samples,
        _matches,
        _mismatches,
        _faults);

    internal bool TrySample(out long sample)
    {
        if (_flatPass || _graphPass)
        {
            sample = 0;
            return false;
        }

        long query = ++_queries;
        if (query % _sampleEvery != 0)
        {
            sample = 0;
            return false;
        }

        sample = ++_samples;
        return true;
    }

    internal Transition PrepareShadow(Transition source)
    {
        _shadowTransition.CopyFrom(source);
        return _shadowTransition;
    }

    internal void BeginFlatPass()
    {
        if (_flatPass || _graphPass)
            throw new InvalidOperationException(
                "Collision shadow passes cannot overlap.");
        _flatPass = true;
    }

    internal void EndFlatPass()
    {
        if (!_flatPass)
            throw new InvalidOperationException(
                "No collision shadow flat pass is active.");
        _flatPass = false;
    }

    internal void BeginGraphPass()
    {
        if (_flatPass || _graphPass)
            throw new InvalidOperationException(
                "Collision shadow passes cannot overlap.");
        _graphPass = true;
    }

    internal void EndGraphPass()
    {
        if (!_graphPass)
            throw new InvalidOperationException(
                "No collision shadow graph pass is active.");
        _graphPass = false;
    }

    internal void RecordBoolean(
        long sample,
        string kind,
        uint sourceId,
        bool graph,
        bool flat,
        string input)
    {
        if (graph == flat)
        {
            _matches++;
            return;
        }

        RecordMismatch(
            sample,
            kind,
            sourceId,
            "Result",
            graph.ToString(),
            flat.ToString(),
            input);
    }

    internal void RecordSphere(
        long sample,
        string kind,
        uint sourceId,
        FlatCollisionSphere graph,
        FlatCollisionSphere flat)
    {
        if (Exact(graph.Origin, flat.Origin) &&
            Exact(graph.Radius, flat.Radius))
        {
            _matches++;
            return;
        }

        RecordMismatch(
            sample,
            kind,
            sourceId,
            "RootBoundingSphere",
            Format(graph),
            Format(flat),
            string.Empty);
    }

    internal void RecordTransition(
        long sample,
        string kind,
        uint sourceId,
        TransitionState graphState,
        TransitionState flatState,
        Transition graph,
        Transition flat,
        string input)
    {
        if (graphState != flatState)
        {
            RecordMismatch(
                sample,
                kind,
                sourceId,
                "TransitionState",
                graphState.ToString(),
                flatState.ToString(),
                input);
            return;
        }

        if (TransitionExactComparer.TryFindDifference(
                graph,
                flat,
                out string path,
                out string graphValue,
                out string flatValue))
        {
            RecordMismatch(
                sample,
                kind,
                sourceId,
                path,
                graphValue,
                flatValue,
                input);
            return;
        }

        _matches++;
    }

    internal void RecordFault(
        long sample,
        string kind,
        uint sourceId,
        Exception fault,
        string input,
        string authority = "graph")
    {
        _faults++;
        WriteArtifact(new CollisionShadowMismatchArtifact(
            1,
            sample,
            kind,
            $"0x{sourceId:X8}",
            authority == "flat" ? "GraphFault" : "FlatFault",
            $"{authority}-authoritative",
            $"{fault.GetType().FullName}: {fault.Message}",
            input));
    }

    private void RecordMismatch(
        long sample,
        string kind,
        uint sourceId,
        string difference,
        string graph,
        string flat,
        string input)
    {
        _mismatches++;
        WriteArtifact(new CollisionShadowMismatchArtifact(
            1,
            sample,
            kind,
            $"0x{sourceId:X8}",
            difference,
            graph,
            flat,
            input));
    }

    private void WriteArtifact(CollisionShadowMismatchArtifact artifact)
    {
        Directory.CreateDirectory(_artifactDirectory);
        string path = Path.Combine(
            _artifactDirectory,
            FormattableString.Invariant(
                $"collision-shadow-{artifact.Sample:D8}.json"));
        string json = JsonSerializer.Serialize(
            artifact,
            CollisionShadowJsonContext.Default
                .CollisionShadowMismatchArtifact);
        File.WriteAllText(path, json);
    }

    internal static string FormatInput(
        Vector3 center,
        float radius) =>
        $"center={Format(center)};radius={Bits(radius)}";

    internal static string FormatInput(
        Vector3 min,
        Vector3 max) =>
        $"min={Format(min)};max={Format(max)}";

    internal static string FormatInput(
        Vector3 center,
        float radius,
        bool hasSphere1,
        Vector3 sphere1Center,
        float sphere1Radius,
        Vector3 currentCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld,
        Vector3 worldOrigin) =>
        $"sphere0={Format(center)}/{Bits(radius)};" +
        $"sphere1={(hasSphere1 ? Format(sphere1Center) : "none")}/" +
        $"{Bits(sphere1Radius)};current={Format(currentCenter)};" +
        $"localZ={Format(localSpaceZ)};scale={Bits(scale)};" +
        $"rotation={Format(localToWorld)};origin={Format(worldOrigin)}";

    private static string Format(FlatCollisionSphere sphere) =>
        $"{Format(sphere.Origin)}/{Bits(sphere.Radius)}";

    private static string Format(Vector3 value) =>
        $"{Bits(value.X)},{Bits(value.Y)},{Bits(value.Z)}";

    private static string Format(Quaternion value) =>
        $"{Bits(value.X)},{Bits(value.Y)},{Bits(value.Z)},{Bits(value.W)}";

    private static string Bits(float value) =>
        $"0x{BitConverter.SingleToInt32Bits(value):X8}";

    private static bool Exact(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) ==
        BitConverter.SingleToInt32Bits(right);

    private static bool Exact(Vector3 left, Vector3 right) =>
        Exact(left.X, right.X) &&
        Exact(left.Y, right.Y) &&
        Exact(left.Z, right.Z);
}

internal sealed record CollisionShadowMismatchArtifact(
    int SchemaVersion,
    long Sample,
    string Kind,
    string SourceId,
    string Difference,
    string Graph,
    string Flat,
    string Input);

[System.Text.Json.Serialization.JsonSerializable(
    typeof(CollisionShadowMismatchArtifact))]
internal sealed partial class CollisionShadowJsonContext :
    System.Text.Json.Serialization.JsonSerializerContext;

internal static class TransitionExactComparer
{
    internal static bool TryFindDifference(
        Transition graph,
        Transition flat,
        out string path,
        out string graphValue,
        out string flatValue)
    {
        if (CompareObjectInfo(
                graph.ObjectInfo,
                flat.ObjectInfo,
                out path,
                out graphValue,
                out flatValue))
        {
            return true;
        }

        if (CompareSpherePath(
                graph.SpherePath,
                flat.SpherePath,
                out path,
                out graphValue,
                out flatValue))
        {
            return true;
        }

        return CompareCollisionInfo(
            graph.CollisionInfo,
            flat.CollisionInfo,
            out path,
            out graphValue,
            out flatValue);
    }

    private static bool CompareObjectInfo(
        ObjectInfo a,
        ObjectInfo b,
        out string path,
        out string av,
        out string bv)
    {
        if (Different("ObjectInfo.State", a.State, b.State, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.StepUpHeight", a.StepUpHeight, b.StepUpHeight, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.StepDownHeight", a.StepDownHeight, b.StepDownHeight, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.Ethereal", a.Ethereal, b.Ethereal, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.StepDown", a.StepDown, b.StepDown, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.Scale", a.Scale, b.Scale, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.MoverPhysicsState", a.MoverPhysicsState, b.MoverPhysicsState, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.TargetId", a.TargetId, b.TargetId, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.SelfEntityId", a.SelfEntityId, b.SelfEntityId, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.MoverHasGravity", a.MoverHasGravity, b.MoverHasGravity, out path, out av, out bv)) return true;
        if (Different("ObjectInfo.VelocityKilled", a.VelocityKilled, b.VelocityKilled, out path, out av, out bv)) return true;
        return Equal(out path, out av, out bv);
    }

    private static bool CompareCollisionInfo(
        CollisionInfo a,
        CollisionInfo b,
        out string path,
        out string av,
        out string bv)
    {
        if (Different("CollisionInfo.ContactPlaneValid", a.ContactPlaneValid, b.ContactPlaneValid, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.ContactPlane", a.ContactPlane, b.ContactPlane, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.ContactPlaneCellId", a.ContactPlaneCellId, b.ContactPlaneCellId, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.ContactPlaneIsWater", a.ContactPlaneIsWater, b.ContactPlaneIsWater, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.LastKnownContactPlaneValid", a.LastKnownContactPlaneValid, b.LastKnownContactPlaneValid, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.LastKnownContactPlane", a.LastKnownContactPlane, b.LastKnownContactPlane, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.LastKnownContactPlaneCellId", a.LastKnownContactPlaneCellId, b.LastKnownContactPlaneCellId, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.LastKnownContactPlaneIsWater", a.LastKnownContactPlaneIsWater, b.LastKnownContactPlaneIsWater, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.SlidingNormalValid", a.SlidingNormalValid, b.SlidingNormalValid, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.SlidingNormal", a.SlidingNormal, b.SlidingNormal, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.CollisionNormalValid", a.CollisionNormalValid, b.CollisionNormalValid, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.CollisionNormal", a.CollisionNormal, b.CollisionNormal, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.CollidedWithEnvironment", a.CollidedWithEnvironment, b.CollidedWithEnvironment, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.FramesStationaryFall", a.FramesStationaryFall, b.FramesStationaryFall, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.AdjustOffset", a.AdjustOffset, b.AdjustOffset, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.LastCollidedObjectGuid", a.LastCollidedObjectGuid, b.LastCollidedObjectGuid, out path, out av, out bv)) return true;
        if (Different("CollisionInfo.ContactPlaneWriteCount", a.ContactPlaneWriteCount, b.ContactPlaneWriteCount, out path, out av, out bv)) return true;
        if (DifferentList("CollisionInfo.CollideObjectGuids", a.CollideObjectGuids, b.CollideObjectGuids, out path, out av, out bv)) return true;
        return Equal(out path, out av, out bv);
    }

    private static bool CompareSpherePath(
        SpherePath a,
        SpherePath b,
        out string path,
        out string av,
        out string bv)
    {
        if (Different("SpherePath.NumSphere", a.NumSphere, b.NumSphere, out path, out av, out bv)) return true;
        if (DifferentSpheres("SpherePath.LocalSphere", a.LocalSphere, b.LocalSphere, out path, out av, out bv)) return true;
        if (DifferentSpheres("SpherePath.GlobalSphere", a.GlobalSphere, b.GlobalSphere, out path, out av, out bv)) return true;
        if (DifferentSpheres("SpherePath.GlobalCurrCenter", a.GlobalCurrCenter, b.GlobalCurrCenter, out path, out av, out bv)) return true;
        if (Different("SpherePath.BeginPos", a.BeginPos, b.BeginPos, out path, out av, out bv)) return true;
        if (Different("SpherePath.EndPos", a.EndPos, b.EndPos, out path, out av, out bv)) return true;
        if (Different("SpherePath.CurPos", a.CurPos, b.CurPos, out path, out av, out bv)) return true;
        if (Different("SpherePath.CheckPos", a.CheckPos, b.CheckPos, out path, out av, out bv)) return true;
        if (Different("SpherePath.BeginOrientation", a.BeginOrientation, b.BeginOrientation, out path, out av, out bv)) return true;
        if (Different("SpherePath.EndOrientation", a.EndOrientation, b.EndOrientation, out path, out av, out bv)) return true;
        if (Different("SpherePath.CurOrientation", a.CurOrientation, b.CurOrientation, out path, out av, out bv)) return true;
        if (Different("SpherePath.CheckOrientation", a.CheckOrientation, b.CheckOrientation, out path, out av, out bv)) return true;
        if (Different("SpherePath.CurCellId", a.CurCellId, b.CurCellId, out path, out av, out bv)) return true;
        if (Different("SpherePath.CheckCellId", a.CheckCellId, b.CheckCellId, out path, out av, out bv)) return true;
        if (Different("SpherePath.CarriedBlockOrigin", a.CarriedBlockOrigin, b.CarriedBlockOrigin, out path, out av, out bv)) return true;
        if (Different("SpherePath.GlobalOffset", a.GlobalOffset, b.GlobalOffset, out path, out av, out bv)) return true;
        if (Different("SpherePath.StepUp", a.StepUp, b.StepUp, out path, out av, out bv)) return true;
        if (Different("SpherePath.StepUpNormal", a.StepUpNormal, b.StepUpNormal, out path, out av, out bv)) return true;
        if (Different("SpherePath.Collide", a.Collide, b.Collide, out path, out av, out bv)) return true;
        if (Different("SpherePath.StepDown", a.StepDown, b.StepDown, out path, out av, out bv)) return true;
        if (Different("SpherePath.StepDownAmt", a.StepDownAmt, b.StepDownAmt, out path, out av, out bv)) return true;
        if (Different("SpherePath.WalkInterp", a.WalkInterp, b.WalkInterp, out path, out av, out bv)) return true;
        if (Different("SpherePath.WalkableValid", a.WalkableValid, b.WalkableValid, out path, out av, out bv)) return true;
        if (Different("SpherePath.WalkablePlane", a.WalkablePlane, b.WalkablePlane, out path, out av, out bv)) return true;
        if (DifferentVectors("SpherePath.WalkableVertices", a.WalkableVertices, b.WalkableVertices, out path, out av, out bv)) return true;
        if (Different("SpherePath.WalkableUp", a.WalkableUp, b.WalkableUp, out path, out av, out bv)) return true;
        if (Different("SpherePath.WalkableAllowance", a.WalkableAllowance, b.WalkableAllowance, out path, out av, out bv)) return true;
        if (Different("SpherePath.LastWalkableValid", a.LastWalkableValid, b.LastWalkableValid, out path, out av, out bv)) return true;
        if (Different("SpherePath.LastWalkablePlane", a.LastWalkablePlane, b.LastWalkablePlane, out path, out av, out bv)) return true;
        if (DifferentVectors("SpherePath.LastWalkableVertices", a.LastWalkableVertices, b.LastWalkableVertices, out path, out av, out bv)) return true;
        if (Different("SpherePath.LastWalkableUp", a.LastWalkableUp, b.LastWalkableUp, out path, out av, out bv)) return true;
        if (Different("SpherePath.BackupCheckPos", a.BackupCheckPos, b.BackupCheckPos, out path, out av, out bv)) return true;
        if (Different("SpherePath.BackupCheckCellId", a.BackupCheckCellId, b.BackupCheckCellId, out path, out av, out bv)) return true;
        if (Different("SpherePath.NegPolyHit", a.NegPolyHit, b.NegPolyHit, out path, out av, out bv)) return true;
        if (Different("SpherePath.NegStepUp", a.NegStepUp, b.NegStepUp, out path, out av, out bv)) return true;
        if (Different("SpherePath.NegCollisionNormal", a.NegCollisionNormal, b.NegCollisionNormal, out path, out av, out bv)) return true;
        if (Different("SpherePath.CheckWalkable", a.CheckWalkable, b.CheckWalkable, out path, out av, out bv)) return true;
        if (Different("SpherePath.InsertType", a.InsertType, b.InsertType, out path, out av, out bv)) return true;
        if (Different("SpherePath.PlacementAllowsSliding", a.PlacementAllowsSliding, b.PlacementAllowsSliding, out path, out av, out bv)) return true;
        if (Different("SpherePath.ObstructionEthereal", a.ObstructionEthereal, b.ObstructionEthereal, out path, out av, out bv)) return true;
        if (Different("SpherePath.BldgCheck", a.BldgCheck, b.BldgCheck, out path, out av, out bv)) return true;
        if (Different("SpherePath.HitsInteriorCell", a.HitsInteriorCell, b.HitsInteriorCell, out path, out av, out bv)) return true;
        if (DifferentList("SpherePath.CellCandidates", a.CellCandidates.OrderedIds, b.CellCandidates.OrderedIds, out path, out av, out bv)) return true;
        return Equal(out path, out av, out bv);
    }

    private static bool DifferentSpheres(
        string pathPrefix,
        Sphere[] a,
        Sphere[] b,
        out string path,
        out string av,
        out string bv)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (Different($"{pathPrefix}[{i}].Origin", a[i].Origin, b[i].Origin, out path, out av, out bv)) return true;
            if (Different($"{pathPrefix}[{i}].Radius", a[i].Radius, b[i].Radius, out path, out av, out bv)) return true;
        }
        return Equal(out path, out av, out bv);
    }

    private static bool DifferentVectors(
        string pathPrefix,
        Vector3[]? a,
        Vector3[]? b,
        out string path,
        out string av,
        out string bv)
    {
        if (a is null || b is null)
        {
            if (a is null && b is null)
                return Equal(out path, out av, out bv);
            return Difference(pathPrefix, a is null ? "null" : "array", b is null ? "null" : "array", out path, out av, out bv);
        }
        if (a.Length != b.Length)
            return Difference(pathPrefix + ".Length", a.Length.ToString(CultureInfo.InvariantCulture), b.Length.ToString(CultureInfo.InvariantCulture), out path, out av, out bv);
        for (int i = 0; i < a.Length; i++)
            if (Different($"{pathPrefix}[{i}]", a[i], b[i], out path, out av, out bv)) return true;
        return Equal(out path, out av, out bv);
    }

    private static bool DifferentList<T>(
        string pathPrefix,
        IReadOnlyList<T> a,
        IReadOnlyList<T> b,
        out string path,
        out string av,
        out string bv)
    {
        if (a.Count != b.Count)
            return Difference(pathPrefix + ".Count", a.Count.ToString(CultureInfo.InvariantCulture), b.Count.ToString(CultureInfo.InvariantCulture), out path, out av, out bv);
        for (int i = 0; i < a.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i]))
                return Difference($"{pathPrefix}[{i}]", Format(a[i]), Format(b[i]), out path, out av, out bv);
        return Equal(out path, out av, out bv);
    }

    private static bool Different<T>(
        string candidatePath,
        T a,
        T b,
        out string path,
        out string av,
        out string bv)
    {
        if (Exact(a, b))
            return Equal(out path, out av, out bv);
        return Difference(candidatePath, Format(a), Format(b), out path, out av, out bv);
    }

    private static bool Exact<T>(T a, T b)
    {
        if (a is float af && b is float bf)
            return BitConverter.SingleToInt32Bits(af) == BitConverter.SingleToInt32Bits(bf);
        if (a is Vector3 av && b is Vector3 bv)
            return Exact(av.X, bv.X) && Exact(av.Y, bv.Y) && Exact(av.Z, bv.Z);
        if (a is Quaternion aq && b is Quaternion bq)
            return Exact(aq.X, bq.X) && Exact(aq.Y, bq.Y) &&
                Exact(aq.Z, bq.Z) && Exact(aq.W, bq.W);
        if (a is Plane ap && b is Plane bp)
            return Exact(ap.Normal, bp.Normal) && Exact(ap.D, bp.D);
        return EqualityComparer<T>.Default.Equals(a, b);
    }

    private static string Format<T>(T value) => value switch
    {
        null => "null",
        float f => $"0x{BitConverter.SingleToInt32Bits(f):X8}",
        Vector3 v => $"{Format(v.X)},{Format(v.Y)},{Format(v.Z)}",
        Quaternion q => $"{Format(q.X)},{Format(q.Y)},{Format(q.Z)},{Format(q.W)}",
        Plane p => $"{Format(p.Normal)}/{Format(p.D)}",
        IFormattable formattable => formattable.ToString(
            null,
            CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool Difference(
        string candidatePath,
        string a,
        string b,
        out string path,
        out string av,
        out string bv)
    {
        path = candidatePath;
        av = a;
        bv = b;
        return true;
    }

    private static bool Equal(
        out string path,
        out string av,
        out string bv)
    {
        path = string.Empty;
        av = string.Empty;
        bv = string.Empty;
        return false;
    }
}
