using System.Collections.Concurrent;
using System.Threading;
using AcDream.Content.Pak;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using Microsoft.Extensions.Logging;

namespace AcDream.Content;

public enum PreparedAssetPresence
{
    Missing,
    Available,
    Corrupt,
}

public enum PreparedAssetReadStatus
{
    Missing,
    Loaded,
    Corrupt,
}

public sealed record PreparedEnvCellSchema(
    uint EnvironmentId,
    ushort CellStructure,
    IReadOnlyList<ushort> Surfaces);

public readonly record struct PreparedAssetRequest(
    PakAssetType Type,
    uint SourceFileId,
    ulong RuntimeObjectId,
    PreparedEnvCellSchema? EnvCell = null)
{
    public static PreparedAssetRequest GfxObj(uint fileId) =>
        new(PakAssetType.GfxObjMesh, fileId, fileId);

    public static PreparedAssetRequest Setup(uint fileId) =>
        new(PakAssetType.SetupMesh, fileId, fileId);

    public static PreparedAssetRequest EnvCellGeometry(
        uint sourceCellId,
        ulong runtimeGeometryId,
        uint environmentId,
        ushort cellStructure,
        IReadOnlyList<ushort> surfaces) =>
        new(
            PakAssetType.EnvCellMesh,
            sourceCellId,
            runtimeGeometryId,
            new PreparedEnvCellSchema(
                environmentId,
                cellStructure,
                surfaces));
}

public readonly record struct PreparedAssetReadResult(
    PreparedAssetReadStatus Status,
    ObjectMeshData? Data)
{
    public static PreparedAssetReadResult Missing =>
        new(PreparedAssetReadStatus.Missing, null);

    public static PreparedAssetReadResult Corrupt =>
        new(PreparedAssetReadStatus.Corrupt, null);

    public static PreparedAssetReadResult Loaded(ObjectMeshData data) =>
        new(PreparedAssetReadStatus.Loaded, data);
}

public readonly record struct PreparedAssetSourceStats(
    long Probes,
    long Reads,
    long Loaded,
    long Missing,
    long Corrupt);

public readonly record struct PreparedAssetCatalogIdentity(
    uint PortalIteration,
    uint CellIteration,
    uint HighResIteration,
    uint LanguageIteration,
    uint BakeToolVersion)
{
    public static PreparedAssetCatalogIdentity From(IDatReaderWriter dats)
        => From(dats, PakFormat.CurrentBakeToolVersion);

    public static PreparedAssetCatalogIdentity From(
        IDatReaderWriter dats,
        uint bakeToolVersion)
    {
        ArgumentNullException.ThrowIfNull(dats);
        if (bakeToolVersion == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bakeToolVersion));
        }

        return new(
            checked((uint)dats.PortalIteration),
            checked((uint)dats.CellIteration),
            checked((uint)dats.HighResIteration),
            checked((uint)dats.LanguageIteration),
            bakeToolVersion);
    }
}

public interface IPreparedAssetSource : IDisposable
{
    PreparedAssetPresence Probe(PakAssetType type, uint sourceFileId);

    PreparedAssetReadResult Read(
        in PreparedAssetRequest request,
        CancellationToken cancellationToken = default);

    PreparedAssetSourceStats Stats { get; }

    CacheStats DecodedTextureCacheStats { get; }

    long MappedVirtualBytes => 0;
}

internal static class PreparedAssetRequestContract
{
    public static void ValidateType(PakAssetType type)
    {
        if (type is not (
            PakAssetType.GfxObjMesh or
            PakAssetType.SetupMesh or
            PakAssetType.EnvCellMesh))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "unknown prepared asset type");
        }
    }

    public static void Validate(in PreparedAssetRequest request)
    {
        ValidateType(request.Type);
        if (request.Type == PakAssetType.EnvCellMesh
            && request.EnvCell is null)
        {
            throw new ArgumentException(
                "EnvCell prepared requests must retain their source schema.",
                nameof(request));
        }
    }

    public static bool Matches(
        in PreparedAssetRequest request,
        ObjectMeshData data)
    {
        bool expectedSetup = request.Type == PakAssetType.SetupMesh;
        return data.ObjectId == request.RuntimeObjectId
            && data.IsSetup == expectedSetup;
    }
}

/// <summary>
/// Production prepared source backed by one immutable memory-mapped pak.
/// There is intentionally no DAT fallback.
/// </summary>
public sealed partial class PakPreparedAssetSource :
    IPreparedAssetSource,
    IPreparedCollisionSource
{
    private readonly PakReader _reader;
    private readonly Action<string>? _diagnosticSink;
    private readonly ConcurrentDictionary<ulong, byte> _loggedIdentityFaults = new();
    private long _probes;
    private long _reads;
    private long _loaded;
    private long _missing;
    private long _corrupt;

    public PakPreparedAssetSource(
        string path,
        PreparedAssetCatalogIdentity expected,
        Action<string>? diagnosticSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _diagnosticSink = diagnosticSink;
        _reader = new PakReader(path);
        try
        {
            ValidateHeader(path, _reader.Header, expected);
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public PakPreparedAssetSource(
        string path,
        IDatReaderWriter dats,
        Action<string>? diagnosticSink = null)
        : this(
            path,
            PreparedAssetCatalogIdentity.From(dats),
            diagnosticSink)
    {
    }

    public PreparedAssetSourceStats Stats =>
        new(
            Volatile.Read(ref _probes),
            Volatile.Read(ref _reads),
            Volatile.Read(ref _loaded),
            Volatile.Read(ref _missing),
            Volatile.Read(ref _corrupt));

    public CacheStats DecodedTextureCacheStats => default;

    public long MappedVirtualBytes => _reader.FileLength;

    public PreparedAssetPresence Probe(
        PakAssetType type,
        uint sourceFileId)
    {
        PreparedAssetRequestContract.ValidateType(type);
        Interlocked.Increment(ref _probes);
        ulong key = PakKey.Compose(type, sourceFileId);
        return _reader.ProbeEntry(key) switch
        {
            PakEntryState.Available => PreparedAssetPresence.Available,
            PakEntryState.Corrupt => PreparedAssetPresence.Corrupt,
            _ => PreparedAssetPresence.Missing,
        };
    }

    public PreparedAssetReadResult Read(
        in PreparedAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        PreparedAssetRequestContract.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _reads);

        ulong key = PakKey.Compose(request.Type, request.SourceFileId);
        PakObjectReadStatus status =
            _reader.ReadObjectMeshData(key, out ObjectMeshData? data);
        cancellationToken.ThrowIfCancellationRequested();

        if (status == PakObjectReadStatus.Missing)
        {
            Interlocked.Increment(ref _missing);
            return PreparedAssetReadResult.Missing;
        }
        if (status == PakObjectReadStatus.Corrupt || data is null)
        {
            Interlocked.Increment(ref _corrupt);
            return PreparedAssetReadResult.Corrupt;
        }

        if (!PreparedAssetRequestContract.Matches(request, data))
        {
            Interlocked.Increment(ref _corrupt);
            LogIdentityFaultOnce(
                key,
                $"prepared payload identity mismatch for key 0x{key:X16}: " +
                $"expected runtime=0x{request.RuntimeObjectId:X16}, " +
                $"type={request.Type}; payload runtime=0x{data.ObjectId:X16}, " +
                $"isSetup={data.IsSetup}");
            return PreparedAssetReadResult.Corrupt;
        }

        Interlocked.Increment(ref _loaded);
        return PreparedAssetReadResult.Loaded(data);
    }

    private static void ValidateHeader(
        string path,
        PakHeader actual,
        PreparedAssetCatalogIdentity expected)
    {
        var mismatches = new List<string>();
        if (actual.BakeToolVersion != expected.BakeToolVersion)
            mismatches.Add(
                $"bake tool {actual.BakeToolVersion} != {expected.BakeToolVersion}");
        if (actual.PortalIteration != expected.PortalIteration)
            mismatches.Add(
                $"portal iteration {actual.PortalIteration} != {expected.PortalIteration}");
        if (actual.CellIteration != expected.CellIteration)
            mismatches.Add(
                $"cell iteration {actual.CellIteration} != {expected.CellIteration}");
        if (actual.HighResIteration != expected.HighResIteration)
            mismatches.Add(
                $"high-res iteration {actual.HighResIteration} != {expected.HighResIteration}");
        if (actual.LanguageIteration != expected.LanguageIteration)
            mismatches.Add(
                $"language iteration {actual.LanguageIteration} != {expected.LanguageIteration}");

        if (mismatches.Count != 0)
        {
            throw new InvalidDataException(
                $"prepared asset package '{path}' does not match the installed DAT set: " +
                string.Join("; ", mismatches) +
                ". Re-bake acdream.pak with this client build and DAT install.");
        }
    }

    private void LogIdentityFaultOnce(ulong key, string message)
    {
        if (_loggedIdentityFaults.TryAdd(key, 0))
            (_diagnosticSink ?? Console.Error.WriteLine)(message);
    }

    public void Dispose() => _reader.Dispose();
}

public sealed class DatPreparedAssetSource : IPreparedAssetSource
{
    private readonly IDatReaderWriter _dats;
    private readonly MeshExtractor _extractor;
    private long _probes;
    private long _reads;
    private long _loaded;
    private long _missing;

    public DatPreparedAssetSource(
        IDatReaderWriter dats,
        ILogger logger,
        Action<ObjectMeshData>? sideStagedSink = null)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(logger);
        _dats = dats;
        _extractor = new MeshExtractor(dats, logger, sideStagedSink);
    }

    public PreparedAssetSourceStats Stats =>
        new(
            Volatile.Read(ref _probes),
            Volatile.Read(ref _reads),
            Volatile.Read(ref _loaded),
            Volatile.Read(ref _missing),
            0);

    public CacheStats DecodedTextureCacheStats =>
        _extractor.DecodedTextureCacheStats;

    public PreparedAssetPresence Probe(
        PakAssetType type,
        uint sourceFileId)
    {
        PreparedAssetRequestContract.ValidateType(type);
        Interlocked.Increment(ref _probes);
        DBObjType expected = type switch
        {
            PakAssetType.GfxObjMesh => DBObjType.GfxObj,
            PakAssetType.SetupMesh => DBObjType.Setup,
            PakAssetType.EnvCellMesh => DBObjType.EnvCell,
            _ => DBObjType.Unknown,
        };
        if (expected == DBObjType.Unknown)
            return PreparedAssetPresence.Missing;
        return _dats.TryResolvePreferred(
                sourceFileId,
                out _,
                out DBObjType actual)
            && actual == expected
            ? PreparedAssetPresence.Available
            : PreparedAssetPresence.Missing;
    }

    public PreparedAssetReadResult Read(
        in PreparedAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        PreparedAssetRequestContract.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _reads);

        ObjectMeshData? data;
        if (request.Type == PakAssetType.EnvCellMesh)
        {
            PreparedEnvCellSchema schema = request.EnvCell
                ?? throw new ArgumentException(
                    "EnvCell prepared requests must retain their source schema.",
                    nameof(request));
            uint environmentId = 0x0D00_0000u | schema.EnvironmentId;
            if (!_dats.Portal.TryGet<DatReaderWriter.DBObjs.Environment>(
                    environmentId,
                    out DatReaderWriter.DBObjs.Environment? environment)
                || environment is null
                || !environment.Cells.TryGetValue(
                    schema.CellStructure,
                    out DatReaderWriter.Types.CellStruct? cellStruct)
                || cellStruct is null)
            {
                Interlocked.Increment(ref _missing);
                return PreparedAssetReadResult.Missing;
            }

            data = _extractor.PrepareCellStructMeshData(
                request.RuntimeObjectId,
                cellStruct,
                schema.Surfaces,
                System.Numerics.Matrix4x4.Identity,
                cancellationToken);
        }
        else
        {
            data = _extractor.PrepareMeshData(
                request.SourceFileId,
                request.Type == PakAssetType.SetupMesh,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (data is null)
        {
            Interlocked.Increment(ref _missing);
            return PreparedAssetReadResult.Missing;
        }
        if (!PreparedAssetRequestContract.Matches(request, data))
        {
            throw new InvalidDataException(
                $"live-DAT prepared payload identity mismatch: expected " +
                $"runtime=0x{request.RuntimeObjectId:X16}, type={request.Type}; " +
                $"payload runtime=0x{data.ObjectId:X16}, isSetup={data.IsSetup}");
        }

        Interlocked.Increment(ref _loaded);
        return PreparedAssetReadResult.Loaded(data);
    }

    public void Dispose()
    {
    }
}
