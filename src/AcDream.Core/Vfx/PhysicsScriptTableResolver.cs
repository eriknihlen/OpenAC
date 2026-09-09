using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Vfx;

public sealed class PhysicsScriptTableResolver
{
    private readonly Func<uint, PhysicsScriptTable?> _loadTable;

    public PhysicsScriptTableResolver(Func<uint, PhysicsScriptTable?> loadTable)
        => _loadTable = loadTable ?? throw new ArgumentNullException(nameof(loadTable));

    /// <summary>
    /// Returns the selected PhysicsScript DID, or null for a missing table or
    /// type, an unordered/above-range intensity, or an invalid script DID.
    /// </summary>
    public uint? Resolve(uint tableDid, uint rawScriptType, float intensity)
        => Resolve(tableDid, rawScriptType, intensity, out _);

    public uint? Resolve(
        uint tableDid,
        uint rawScriptType,
        float intensity,
        out Exception? loadFailure)
    {
        loadFailure = null;
        if (!IsPhysicsScriptTableDid(tableDid))
            return null;

        PhysicsScriptTable? table;
        try
        {
            table = _loadTable(tableDid);
        }
        catch (Exception error)
        {
            loadFailure = error;
            return null;
        }
        if (table is null
            || table.Id != tableDid
            || !table.ScriptTable.TryGetValue(
                unchecked((PlayScript)rawScriptType),
                out PhysicsScriptTableData? data))
        {
            return null;
        }

        foreach (ScriptAndModData entry in data.Scripts)
        {
            if (intensity <= entry.Mod)
            {
                uint scriptDid = entry.ScriptId.DataId;
                return IsPhysicsScriptDid(scriptDid) ? scriptDid : null;
            }
        }

        return null;
    }

    public static bool IsPhysicsScriptDid(uint did) =>
        // AC data IDs encode the DBObj type in the high byte; the remaining
        // 24 bits are the file index and must not be truncated to 16 bits.
        (did & 0xFF000000u) == 0x33000000u;

    public static bool IsPhysicsScriptTableDid(uint did) =>
        (did & 0xFF000000u) == 0x34000000u;
}
