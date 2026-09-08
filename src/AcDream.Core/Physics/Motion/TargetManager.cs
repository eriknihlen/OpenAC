using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AcDream.Core.Physics.Motion;

public sealed class TargetManager
{
    public const double ThrottleSeconds = 0.5;

    public const double StalenessSeconds = 10.0;

    private readonly IPhysicsObjHost _host;

    private TargetInfo? _targetInfo;
    private IPhysicsObjHost? _targetHost;                     // exact App incarnation token
    private Dictionary<uint, TargettedVoyeurInfo>? _voyeurTable;
    private double _lastUpdateTime;

    public TargetManager(IPhysicsObjHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    public TargetInfo? TargetInfo => _targetInfo;

    public IReadOnlyDictionary<uint, TargettedVoyeurInfo>? VoyeurTable => _voyeurTable;

    public double GetTargetQuantum() => _targetInfo?.Quantum ?? 0.0;

    public IPhysicsObjHost? GetRelationshipTarget(uint objectId) =>
        _targetInfo is { } info && info.ObjectId == objectId
            ? _targetHost
            : null;

    // ── watcher role ───────────────────────────────────────────────────────

    public void SetTarget(uint contextId, uint objectId, float radius, double quantum)
    {
        ClearTarget();

        if (objectId == 0)
        {
            var cleared = new TargetInfo(
                ObjectId: 0, Status: TargetStatus.TimedOut,
                TargetPosition: default, InterpolatedPosition: default,
                ContextId: contextId);
            _host.HandleUpdateTarget(cleared);
            return;
        }

        _targetInfo = new TargetInfo(
            ObjectId: objectId, Status: TargetStatus.Undefined,
            TargetPosition: default, InterpolatedPosition: default,
            ContextId: contextId, Radius: radius, Quantum: quantum,
            LastUpdateTime: _host.CurTime);

        var target = _host.GetObjectA(objectId);
        _targetHost = target;
        target?.AddVoyeur(_host, radius, quantum);
    }

    public void SetTargetQuantum(double quantum)
    {
        if (_targetInfo is not { } ti)
            return;

        _targetInfo = ti with { Quantum = quantum };
        var target = _targetHost ?? _host.GetObjectA(ti.ObjectId);
        _targetHost = target;
        target?.AddVoyeur(_host, ti.Radius, quantum);
    }

    public void ClearTarget()
    {
        if (_targetInfo is not { } ti)
            return;

        var target = _targetHost ?? _host.GetObjectA(ti.ObjectId);
        target?.RemoveVoyeur(_host.Id, _host);
        _targetInfo = null;
        _targetHost = null;
    }

    public void ReceiveUpdate(TargetInfo update, IPhysicsObjHost sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (_targetInfo is not { } ti
            || ti.ObjectId != update.ObjectId
            || _targetHost is null
            || !ReferenceEquals(_targetHost, sender))
            return;

        Vector3 interpHeading = update.InterpolatedPosition.Frame.Origin
                              - _host.Position.Frame.Origin;
        if (MoveToMath.NormalizeCheckSmall(ref interpHeading))
            interpHeading = Vector3.UnitZ;

        var updated = ti with
        {
            Radius = update.Radius,
            Quantum = update.Quantum,
            TargetPosition = update.TargetPosition,
            InterpolatedPosition = update.InterpolatedPosition,
            Velocity = update.Velocity,
            Status = update.Status,
            InterpolatedHeading = interpHeading,
            LastUpdateTime = _host.CurTime,
        };
        _targetInfo = updated;

        _host.HandleUpdateTarget(updated);

        if (update.Status == TargetStatus.ExitWorld)
            ClearTarget();
    }

    // ── watched role ───────────────────────────────────────────────────────

    public void AddVoyeur(IPhysicsObjHost watcher, float radius, double quantum)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        uint watcherId = watcher.Id;
        _voyeurTable ??= new Dictionary<uint, TargettedVoyeurInfo>();

        if (_voyeurTable.TryGetValue(watcherId, out var existing))
        {
            if (ReferenceEquals(existing.WatcherHost, watcher))
            {
                existing.Radius = radius;
                existing.Quantum = quantum;
                return;
            }

            // A later INSTANCE_TS reused the GUID. Replace the pointer-like
            // subscription; the retiring watcher can no longer remove it via
            // its exact identity token.
            _voyeurTable.Remove(watcherId);
        }

        var voyeur = new TargettedVoyeurInfo(
            watcherId,
            radius,
            quantum,
            watcher);
        _voyeurTable[watcherId] = voyeur;
        SendVoyeurUpdate(voyeur, _host.Position, TargetStatus.Ok);
    }

    public bool RemoveVoyeur(uint watcherId, IPhysicsObjHost expectedWatcher)
    {
        ArgumentNullException.ThrowIfNull(expectedWatcher);
        if (_voyeurTable is null
            || !_voyeurTable.TryGetValue(watcherId, out var existing)
            || !ReferenceEquals(existing.WatcherHost, expectedWatcher))
        {
            return false;
        }

        return _voyeurTable.Remove(watcherId);
    }

    public void HandleTargetting()
    {
        if (_host.PhysicsTimerTime - _lastUpdateTime < ThrottleSeconds)
            return;

        if (_targetInfo is { } ti)
        {
            if (ti.Status == TargetStatus.Undefined
                && ti.LastUpdateTime + StalenessSeconds < _host.CurTime)
            {
                var timedOut = ti with { Status = TargetStatus.TimedOut };
                _targetInfo = timedOut;
                _host.HandleUpdateTarget(timedOut);
            }
        }

        if (_voyeurTable != null)
        {
            foreach (var voyeur in _voyeurTable.Values.ToList())
                CheckAndUpdateVoyeur(voyeur);
        }

        _lastUpdateTime = _host.PhysicsTimerTime;
    }

    public void CheckAndUpdateVoyeur(TargettedVoyeurInfo voyeur)
    {
        Position newPos = GetInterpolatedPosition(voyeur.Quantum);
        float drift = Vector3.Distance(
            newPos.Frame.Origin, voyeur.LastSentPosition.Frame.Origin);
        if (drift > voyeur.Radius)
            SendVoyeurUpdate(voyeur, newPos, TargetStatus.Ok);
    }

    public Position GetInterpolatedPosition(double quantum)
    {
        var pos = _host.Position;
        Vector3 origin = pos.Frame.Origin + _host.Velocity * (float)quantum;
        return new Position(pos.ObjCellId, origin, pos.Frame.Orientation);
    }

    public void SendVoyeurUpdate(TargettedVoyeurInfo voyeur, Position pos, TargetStatus status)
    {
        voyeur.LastSentPosition = pos;

        var info = new TargetInfo(
            ObjectId: _host.Id,
            Status: status,
            TargetPosition: _host.Position,
            InterpolatedPosition: pos,               // the extrapolated position
            ContextId: 0,
            Radius: voyeur.Radius,
            Quantum: voyeur.Quantum,
            Velocity: _host.Velocity);

        voyeur.WatcherHost.ReceiveTargetUpdate(info, _host);
    }

    public void NotifyVoyeurOfEvent(TargetStatus status)
    {
        if (_voyeurTable == null)
            return;

        foreach (var voyeur in _voyeurTable.Values.ToList())
            SendVoyeurUpdate(voyeur, _host.Position, status);
    }

    public void NotifyVoyeurOfEventAndClear(TargetStatus status)
    {
        if (_voyeurTable == null)
            return;

        foreach (var voyeur in _voyeurTable.Values.ToList())
            SendVoyeurUpdate(voyeur, _host.Position, status);
        _voyeurTable.Clear();
    }
}
