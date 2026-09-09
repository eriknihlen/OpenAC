using System.Collections.Concurrent;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.Content.Vfx;

public sealed class RetailPhysicsScriptLoader
{
    private readonly Func<uint, byte[]?> _readRaw;
    private readonly DatDatabase? _database;
    private readonly ConcurrentDictionary<uint, Lazy<DatPhysicsScript?>> _cache = new();

    public RetailPhysicsScriptLoader(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        _database = dats.Portal.Db;
        _readRaw = id => dats.Portal.TryGetFileBytes(id, out byte[]? bytes)
            ? bytes
            : null;
    }

    public RetailPhysicsScriptLoader(IDatDatabase portal)
    {
        ArgumentNullException.ThrowIfNull(portal);
        _database = portal.Db;
        _readRaw = id => portal.TryGetFileBytes(id, out byte[]? bytes)
            ? bytes
            : null;
    }

    public DatPhysicsScript? LoadPhysicsScript(uint id)
    {
        if (!AcDream.Core.Vfx.PhysicsScriptTableResolver.IsPhysicsScriptDid(id))
            return null;

        return _cache.GetOrAdd(
            id,
            key => new Lazy<DatPhysicsScript?>(
                () => LoadUncached(key),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private DatPhysicsScript? LoadUncached(uint id)
    {
        byte[]? bytes = _readRaw(id);
        if (bytes is null)
            return null;

        DatPhysicsScript script = Parse(bytes, _database);
        if (script.Id != id)
            throw new InvalidDataException(
                $"PhysicsScript entry 0x{id:X8} contained id 0x{script.Id:X8}.");
        return script;
    }

    public static DatPhysicsScript Parse(
        ReadOnlyMemory<byte> bytes,
        DatDatabase? database = null)
    {
        var reader = new DatBinReader(bytes, database);
        var script = new DatPhysicsScript
        {
            Id = reader.ReadUInt32(),
        };

        uint count = reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            double startTime = reader.ReadDouble();
            if (!double.IsFinite(startTime))
                throw new InvalidDataException(
                    $"PhysicsScript 0x{script.Id:X8} hook {i} has non-finite StartTime {startTime}.");
            script.ScriptData.Add(new PhysicsScriptData
            {
                StartTime = startTime,
                Hook = RetailAnimationHookReader.Read(reader),
            });
        }

        script.ScriptData.Sort(static (left, right) =>
            left.StartTime.CompareTo(right.StartTime));

        EnsureFullyConsumed(reader, nameof(DatPhysicsScript), script.Id);
        return script;
    }

    private static void EnsureFullyConsumed(DatBinReader reader, string type, uint id)
    {
        if (reader.Offset != reader.Length)
            throw new InvalidDataException(
                $"{type} 0x{id:X8} consumed {reader.Offset} of {reader.Length} bytes.");
    }
}
