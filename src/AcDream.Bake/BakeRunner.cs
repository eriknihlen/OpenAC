using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.Bake;

/// <summary>Options for one bake run (parsed from CLI args by Program, or built directly by tests).</summary>
public sealed record BakeOptions
{
    public required string DatDir { get; init; }
    public required string OutPath { get; init; }
    public HashSet<uint>? IdFilter { get; init; }
    public HashSet<uint>? LandblockFilter { get; init; }
    public int Threads { get; init; } = System.Environment.ProcessorCount;
    public CancellationToken CancellationToken { get; init; }
    public IBakeProgressSink? Progress { get; init; }
}

public sealed record BakeReport
{
    public required PakHeader Header { get; init; }
    public required int GfxObjKeys { get; init; }
    public required int SetupKeys { get; init; }
    public required int EnvCellKeys { get; init; }
    public required int UniqueEnvCellGeometries { get; init; }
    public required int EnvCellAliases { get; init; }
    public required int GfxObjCollisionKeys { get; init; }
    public required int UniqueGfxObjCollisions { get; init; }
    public required int GfxObjCollisionAliases { get; init; }
    public required int SetupCollisionKeys { get; init; }
    public required int UniqueSetupCollisions { get; init; }
    public required int SetupCollisionAliases { get; init; }
    public required int CellStructureCollisionKeys { get; init; }
    public required int UniqueCellStructureCollisions { get; init; }
    public required int CellStructureCollisionAliases { get; init; }
    public required int EnvCellTopologyKeys { get; init; }
    public required int TexturePayloadKeys { get; init; }
    public required int SideStagedKeys { get; init; }
    public int SideStagedDuplicateKeys { get; init; }
    public required int PhysicalBlobs { get; init; }
    public required int TotalKeys { get; init; }
    public required int Failures { get; init; }
    public required TimeSpan ExtractionAndWriteElapsed { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required long OutputBytes { get; init; }
    public required long PeakWorkingSetBytes { get; init; }
    public required long PeakPrivateBytes { get; init; }
    public required long DecodedPayloadBytes { get; init; }
    public required long StoredPayloadBytes { get; init; }
    public required int CompressedBlobs { get; init; }
    public required IReadOnlyDictionary<PakAssetType, int> TypeCounts
    {
        get;
        init;
    }

    public double EnvCellDedupRatio =>
        UniqueEnvCellGeometries == 0 ? 0 : (double)EnvCellKeys / UniqueEnvCellGeometries;

    public double PayloadCompressionRatio =>
        StoredPayloadBytes == 0 ? 0 : (double)DecodedPayloadBytes / StoredPayloadBytes;
}

public static class BakeRunner
{
    /// <summary>
    /// Items extracted in parallel per batch before sequential sorted write.
    /// Bounds peak decoded output while keeping workers busy.
    /// </summary>
    private const int BatchSize = 16;
    private const int TransientCollectionStride = 2_048;

    public static int Run(BakeOptions options)
    {
        RunDetailed(options);
        return 0;
    }

    public static BakeReport RunDetailed(BakeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Threads <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "thread count must be positive");

        options.CancellationToken.ThrowIfCancellationRequested();
        options.Progress?.Started(PakFormat.CurrentBakeToolVersion, options.OutPath);
        var totalStopwatch = Stopwatch.StartNew();
        var report = BakeOutputTransaction.WriteValidateAndPublish(
            options.OutPath,
            temporaryPath => RunCore(
                options with { OutPath = temporaryPath },
                options.OutPath),
            (temporaryPath, result) => BakeArtifactValidator.Validate(
                temporaryPath,
                result.Header,
                result.TotalKeys,
                result.TypeCounts),
            options.CancellationToken);
        totalStopwatch.Stop();

        report = report with
        {
            Elapsed = totalStopwatch.Elapsed,
            OutputBytes = new FileInfo(options.OutPath).Length,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            PeakPrivateBytes = Process.GetCurrentProcess().PeakPagedMemorySize64,
        };

        PrintSummary(report, options.OutPath);
        options.Progress?.Completed(
            report.Header.BakeToolVersion,
            report.OutputBytes,
            report.Failures);
        return report;
    }

    private static BakeReport RunCore(BakeOptions options, string publishedOutputPath)
    {
        var cancellationToken = options.CancellationToken;

        Console.WriteLine("acdream-bake");
        Console.WriteLine($"dat dir: {options.DatDir}");
        Console.WriteLine($"out:     {publishedOutputPath}");
        Console.WriteLine($"threads: {options.Threads}");
        Console.WriteLine();

        var stopwatch = Stopwatch.StartNew();
        using var dats = new DatCollection(options.DatDir, DatAccessType.Read);
        using var datReaderWriter = new DatCollectionAdapter(dats);
        var extractorLogger = new ConsoleErrorLogger(nameof(MeshExtractor));
        var sideStaged = new ConcurrentQueue<ObjectMeshData>();
        var extractor = new MeshExtractor(
            datReaderWriter,
            extractorLogger,
            data => sideStaged.Enqueue(data));

        var failures = new ConcurrentBag<(PakAssetType Type, uint FileId, string Reason)>();


        var gfxObjIds = dats.GetAllIdsOfType<GfxObj>().OrderBy(id => id).ToList();
        var setupIds = dats.GetAllIdsOfType<Setup>().OrderBy(id => id).ToList();
        var envCellIds = EnumerateEnvCellIds(dats, options.LandblockFilter);

        if (options.IdFilter is not null)
        {
            gfxObjIds = gfxObjIds.Where(options.IdFilter.Contains).ToList();
            setupIds = setupIds.Where(options.IdFilter.Contains).ToList();
            envCellIds = envCellIds.Where(options.IdFilter.Contains).ToList();
        }

        var envCatalogBuilder = new EnvCellBakeCatalogBuilder();
        foreach (uint fileId in envCellIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!datReaderWriter.Cell.TryGet<EnvCell>(fileId, out var envCell) || envCell is null)
            {
                failures.Add((PakAssetType.EnvCellMesh, fileId, "EnvCell DAT record was not found"));
                continue;
            }

            envCatalogBuilder.Add(
                new EnvCellBakeSource(
                    fileId,
                    envCell.EnvironmentId,
                    envCell.CellStructure,
                    envCell.Surfaces));
        }

        var envCatalog = envCatalogBuilder.Build();
        var ordinaryWork = new List<(ulong Key, PakAssetType Type, uint FileId)>(
            gfxObjIds.Count + setupIds.Count);
        ordinaryWork.AddRange(
            gfxObjIds.Select(
                id => (PakKey.Compose(PakAssetType.GfxObjMesh, id), PakAssetType.GfxObjMesh, id)));
        ordinaryWork.AddRange(
            setupIds.Select(
                id => (PakKey.Compose(PakAssetType.SetupMesh, id), PakAssetType.SetupMesh, id)));
        var scheduledMeshKeys = ordinaryWork
            .Select(item => item.Key)
            .ToHashSet();
        ordinaryWork.Sort((a, b) => a.Key.CompareTo(b.Key));

        Console.WriteLine(
            $"enumerated: {gfxObjIds.Count:N0} GfxObj, {setupIds.Count:N0} Setup, " +
            $"{envCellIds.Count:N0} EnvCell");
        Console.WriteLine(
            $"EnvCell catalog: {envCatalog.CellCount:N0} valid cells -> " +
            $"{envCatalog.UniqueGeometryCount:N0} unique geometries + " +
            $"{envCatalog.AliasCount:N0} aliases " +
            $"({(envCatalog.UniqueGeometryCount == 0 ? 0 : (double)envCatalog.CellCount / envCatalog.UniqueGeometryCount):F1}x)");
        Console.WriteLine();

        // ---- sorted-batch extraction and deterministic write ---------------

        var header = new PakHeader
        {
            FormatVersion = PakFormat.CurrentFormatVersion,
            PortalIteration = (uint)dats.Portal.Iteration!.CurrentIteration,
            CellIteration = (uint)dats.Cell.Iteration!.CurrentIteration,
            HighResIteration = (uint)dats.HighRes.Iteration!.CurrentIteration,
            LanguageIteration = (uint)dats.Local.Iteration!.CurrentIteration,
            BakeToolVersion = PakFormat.CurrentBakeToolVersion,
        };

        var writtenKeys = new HashSet<ulong>();
        var sideStagedByKey = new Dictionary<ulong, ObjectMeshData>();
        long completed = 0;
        int totalExtractionJobs = ordinaryWork.Count + envCatalog.UniqueGeometryCount;
        int gfxObjWritten = 0;
        int setupWritten = 0;
        int envCellKeysWritten = 0;
        int envCellGeometriesWritten = 0;
        int sideStagedDuped = 0;
        var lastProgressReport = Stopwatch.StartNew();

        using (var writer = new PakWriter(options.OutPath, header))
        {
            for (int batchStart = 0; batchStart < ordinaryWork.Count; batchStart += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = ordinaryWork.Skip(batchStart).Take(BatchSize).ToArray();
                var batchResults =
                    new ConcurrentBag<(ulong Key, PakAssetType Type, ObjectMeshData Data)>();

                Parallel.ForEach(
                    batch,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.Threads,
                        CancellationToken = cancellationToken,
                    },
                    item =>
                    {
                        var (key, type, fileId) = item;
                        try
                        {
                            bool isSetup = type == PakAssetType.SetupMesh;
                            var data = extractor.PrepareMeshData(fileId, isSetup, cancellationToken);
                            if (data is not null)
                                batchResults.Add((key, type, data));
                            else
                                failures.Add((type, fileId, "extractor returned null"));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            failures.Add((type, fileId, exception.Message));
                        }
                        finally
                        {
                            Interlocked.Increment(ref completed);
                        }
                    });

                foreach (var (key, type, data) in batchResults.OrderBy(result => result.Key))
                {
                    writer.AddBlob(key, data);
                    writtenKeys.Add(key);
                    if (type == PakAssetType.GfxObjMesh)
                        gfxObjWritten++;
                    else
                        setupWritten++;
                }

                sideStagedDuped += DrainSideStaged(
                    sideStaged,
                    sideStagedByKey,
                    writtenKeys,
                    scheduledMeshKeys);
                CompactTransientBakeBuffersIfDue(
                    batchStart + batch.Length,
                    final: batchStart + BatchSize >= ordinaryWork.Count);
                ReportProgressIfDue(
                    completed,
                    totalExtractionJobs,
                    failures.Count,
                    stopwatch.Elapsed,
                    lastProgressReport,
                    options.Progress,
                    "mesh",
                    batchStart + BatchSize >= ordinaryWork.Count &&
                    envCatalog.UniqueGeometryCount == 0);
            }

            for (int batchStart = 0; batchStart < envCatalog.Groups.Count; batchStart += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = envCatalog.Groups.Skip(batchStart).Take(BatchSize).ToArray();
                var batchResults =
                    new ConcurrentBag<(EnvCellGeometryGroup Group, ObjectMeshData Data)>();

                Parallel.ForEach(
                    batch,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.Threads,
                        CancellationToken = cancellationToken,
                    },
                    group =>
                    {
                        try
                        {
                            uint environmentDid = 0x0D00_0000u | group.EnvironmentId;
                            if (!datReaderWriter.Portal.TryGet<DatEnvironment>(
                                    environmentDid,
                                    out var environment) ||
                                environment is null ||
                                !environment.Cells.TryGetValue(
                                    group.CellStructure,
                                    out var cellStruct))
                            {
                                failures.Add((
                                    PakAssetType.EnvCellMesh,
                                    group.FileIds[0],
                                    $"environment 0x{environmentDid:X8} cell structure " +
                                    $"{group.CellStructure} was not found"));
                                return;
                            }

                            var data = extractor.PrepareCellStructMeshData(
                                group.GeometryId,
                                cellStruct,
                                group.Surfaces,
                                Matrix4x4.Identity,
                                cancellationToken);
                            if (data is not null)
                                batchResults.Add((group, data));
                            else
                                failures.Add((
                                    PakAssetType.EnvCellMesh,
                                    group.FileIds[0],
                                    "unique cell-structure extraction returned null"));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            failures.Add((
                                PakAssetType.EnvCellMesh,
                                group.FileIds[0],
                                exception.Message));
                        }
                        finally
                        {
                            Interlocked.Increment(ref completed);
                        }
                    });

                foreach (var (group, data) in batchResults.OrderBy(result => result.Group.FileIds[0]))
                {
                    ulong primaryKey =
                        PakKey.Compose(PakAssetType.EnvCellMesh, group.FileIds[0]);
                    writer.AddBlob(primaryKey, data);
                    writtenKeys.Add(primaryKey);
                    envCellKeysWritten++;
                    envCellGeometriesWritten++;

                    for (int i = 1; i < group.FileIds.Length; i++)
                    {
                        ulong aliasKey =
                            PakKey.Compose(PakAssetType.EnvCellMesh, group.FileIds[i]);
                        writer.AddAlias(aliasKey, primaryKey);
                        writtenKeys.Add(aliasKey);
                        envCellKeysWritten++;
                    }
                }

                sideStagedDuped += DrainSideStaged(
                    sideStaged,
                    sideStagedByKey,
                    writtenKeys,
                    scheduledMeshKeys);
                CompactTransientBakeBuffersIfDue(
                    batchStart + batch.Length,
                    final: batchStart + BatchSize >= envCatalog.Groups.Count);
                ReportProgressIfDue(
                    completed,
                    totalExtractionJobs,
                    failures.Count,
                    stopwatch.Elapsed,
                    lastProgressReport,
                    options.Progress,
                    "mesh",
                    batchStart + BatchSize >= envCatalog.Groups.Count);
            }

            int sideStagedWritten = 0;
            foreach (var (key, data) in sideStagedByKey.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (writtenKeys.Contains(key))
                {
                    sideStagedDuped++;
                    continue;
                }

                writer.AddBlob(key, data);
                writtenKeys.Add(key);
                sideStagedWritten++;
            }


            uint[] collisionGfxIds = gfxObjIds
                .Concat(
                    sideStagedByKey.Keys.Select(
                        key => PakKey.Decompose(key).FileId))
                .Distinct()
                .Order()
                .ToArray();
            var collisionWork =
                new List<(ulong Key, PakAssetType Type, uint FileId)>(
                    collisionGfxIds.Length + setupIds.Count);
            collisionWork.AddRange(
                collisionGfxIds.Select(
                    id => (
                        PakKey.Compose(PakAssetType.GfxObjCollision, id),
                        PakAssetType.GfxObjCollision,
                        id)));
            collisionWork.AddRange(
                setupIds.Select(
                    id => (
                        PakKey.Compose(PakAssetType.SetupCollision, id),
                        PakAssetType.SetupCollision,
                        id)));
            collisionWork.Sort((left, right) => left.Key.CompareTo(right.Key));

            var gfxCollisionAliases = new ExactPayloadAliasCatalog();
            var setupCollisionAliases = new ExactPayloadAliasCatalog();
            var cellStructureAliases = new ExactPayloadAliasCatalog();
            int gfxCollisionWritten = 0;
            int uniqueGfxCollisions = 0;
            int gfxCollisionAliasCount = 0;
            int setupCollisionWritten = 0;
            int uniqueSetupCollisions = 0;
            int setupCollisionAliasCount = 0;
            int cellStructureWritten = 0;
            int uniqueCellStructures = 0;
            int cellStructureAliasCount = 0;
            int envCellTopologyWritten = 0;
            long collisionCompleted = 0;
            int totalCollisionJobs =
                collisionWork.Count
                + envCatalog.UniqueGeometryCount
                + envCatalog.CellCount;
            var collisionStopwatch = Stopwatch.StartNew();
            lastProgressReport.Restart();

            Console.WriteLine();
            Console.WriteLine(
                $"collision catalog: {collisionGfxIds.Length:N0} GfxObj, " +
                $"{setupIds.Count:N0} Setup, " +
                $"{envCatalog.UniqueGeometryCount:N0} CellStruct source groups, " +
                $"{envCatalog.CellCount:N0} EnvCell topology records");

            for (int batchStart = 0;
                 batchStart < collisionWork.Count;
                 batchStart += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = collisionWork
                    .Skip(batchStart)
                    .Take(BatchSize)
                    .ToArray();
                var batchResults =
                    new ConcurrentBag<CollisionPayloadResult>();

                Parallel.ForEach(
                    batch,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.Threads,
                        CancellationToken = cancellationToken,
                    },
                    item =>
                    {
                        var (key, type, fileId) = item;
                        try
                        {
                            byte[] payload;
                            if (type == PakAssetType.GfxObjCollision)
                            {
                                if (!datReaderWriter.Portal.TryGet<GfxObj>(
                                        fileId,
                                        out GfxObj? gfxObj)
                                    || gfxObj is null)
                                {
                                    failures.Add((
                                        type,
                                        fileId,
                                        "GfxObj DAT record was not found"));
                                    return;
                                }

                                FlatGfxObjCollisionAsset asset =
                                    FlatCollisionAssetBuilder.FlattenGfxObj(
                                        gfxObj);
                                payload = FlatCollisionAssetSerializer.Serialize(
                                    asset,
                                    cancellationToken);
                            }
                            else
                            {
                                if (!datReaderWriter.Portal.TryGet<Setup>(
                                        fileId,
                                        out Setup? setup)
                                    || setup is null)
                                {
                                    failures.Add((
                                        type,
                                        fileId,
                                        "Setup DAT record was not found"));
                                    return;
                                }

                                FlatSetupCollision asset =
                                    FlatCollisionAssetBuilder.FlattenSetup(
                                        setup);
                                payload = FlatCollisionAssetSerializer.Serialize(
                                    asset,
                                    cancellationToken);
                            }

                            batchResults.Add(
                                new CollisionPayloadResult(
                                    key,
                                    type,
                                    fileId,
                                    payload));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            failures.Add((type, fileId, exception.Message));
                        }
                        finally
                        {
                            Interlocked.Increment(ref collisionCompleted);
                        }
                    });

                foreach (CollisionPayloadResult result in
                         batchResults.OrderBy(result => result.Key))
                {
                    ExactPayloadAliasCatalog aliases =
                        result.Type == PakAssetType.GfxObjCollision
                            ? gfxCollisionAliases
                            : setupCollisionAliases;
                    bool alias = WriteExactPayload(
                        writer,
                        result.Key,
                        result.Payload,
                        aliases);
                    writtenKeys.Add(result.Key);

                    if (result.Type == PakAssetType.GfxObjCollision)
                    {
                        gfxCollisionWritten++;
                        if (alias)
                            gfxCollisionAliasCount++;
                        else
                            uniqueGfxCollisions++;
                    }
                    else
                    {
                        setupCollisionWritten++;
                        if (alias)
                            setupCollisionAliasCount++;
                        else
                            uniqueSetupCollisions++;
                    }
                }

                ReportProgressIfDue(
                    collisionCompleted,
                    totalCollisionJobs,
                    failures.Count,
                    collisionStopwatch.Elapsed,
                    lastProgressReport,
                    options.Progress,
                    "collision",
                    final: false);
            }

            for (int groupBatchStart = 0;
                 groupBatchStart < envCatalog.Groups.Count;
                 groupBatchStart += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnvCellGeometryGroup[] groupBatch = envCatalog.Groups
                    .Skip(groupBatchStart)
                    .Take(BatchSize)
                    .ToArray();
                var structureResults =
                    new ConcurrentBag<CellStructurePayloadResult>();

                Parallel.ForEach(
                    groupBatch,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.Threads,
                        CancellationToken = cancellationToken,
                    },
                    group =>
                    {
                        try
                        {
                            uint environmentDid =
                                0x0D00_0000u | group.EnvironmentId;
                            if (!datReaderWriter.Portal.TryGet<DatEnvironment>(
                                    environmentDid,
                                    out DatEnvironment? environment)
                                || environment is null
                                || !environment.Cells.TryGetValue(
                                    group.CellStructure,
                                    out CellStruct? cellStruct)
                                || cellStruct is null)
                            {
                                failures.Add((
                                    PakAssetType.CellStructureCollision,
                                    group.FileIds[0],
                                    $"environment 0x{environmentDid:X8} cell " +
                                    $"structure {group.CellStructure} was not found"));
                                Interlocked.Add(
                                    ref collisionCompleted,
                                    group.FileIds.Length);
                                return;
                            }

                            FlatCellStructureCollisionAsset structure =
                                FlatCollisionAssetBuilder.FlattenCellStructure(
                                    cellStruct);
                            byte[] payload =
                                FlatCollisionAssetSerializer.Serialize(
                                    structure,
                                    cancellationToken);
                            structureResults.Add(
                                new CellStructurePayloadResult(
                                    group,
                                    structure,
                                    payload));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            failures.Add((
                                PakAssetType.CellStructureCollision,
                                group.FileIds[0],
                                exception.Message));
                            Interlocked.Add(
                                ref collisionCompleted,
                                group.FileIds.Length);
                        }
                        finally
                        {
                            Interlocked.Increment(ref collisionCompleted);
                        }
                    });

                foreach (CellStructurePayloadResult result in
                         structureResults.OrderBy(
                             result => result.Group.FileIds[0]))
                {
                    (int physical, int aliases) = WriteExactPayloadGroup(
                        writer,
                        PakAssetType.CellStructureCollision,
                        result.Group.FileIds,
                        result.Payload,
                        cellStructureAliases,
                        writtenKeys);
                    cellStructureWritten += result.Group.FileIds.Length;
                    uniqueCellStructures += physical;
                    cellStructureAliasCount += aliases;

                    for (int topologyBatchStart = 0;
                         topologyBatchStart < result.Group.FileIds.Length;
                         topologyBatchStart += BatchSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        uint[] topologyBatch = result.Group.FileIds
                            .Skip(topologyBatchStart)
                            .Take(BatchSize)
                            .ToArray();
                        var topologyResults =
                            new ConcurrentBag<CollisionPayloadResult>();

                        Parallel.ForEach(
                            topologyBatch,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = options.Threads,
                                CancellationToken = cancellationToken,
                            },
                            fileId =>
                            {
                                try
                                {
                                    if (!datReaderWriter.Cell.TryGet<EnvCell>(
                                            fileId,
                                            out EnvCell? envCell)
                                        || envCell is null)
                                    {
                                        failures.Add((
                                            PakAssetType.EnvCellTopology,
                                            fileId,
                                            "EnvCell DAT record was not found"));
                                        return;
                                    }

                                    FlatEnvCellTopology topology =
                                        FlatCollisionAssetBuilder
                                            .FlattenEnvCellTopology(
                                                fileId,
                                                envCell,
                                                result.Structure
                                                    .PortalPolygons);
                                    byte[] payload =
                                        FlatCollisionAssetSerializer.Serialize(
                                            topology,
                                            cancellationToken);
                                    topologyResults.Add(
                                        new CollisionPayloadResult(
                                            PakKey.Compose(
                                                PakAssetType.EnvCellTopology,
                                                fileId),
                                            PakAssetType.EnvCellTopology,
                                            fileId,
                                            payload));
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception exception)
                                {
                                    failures.Add((
                                        PakAssetType.EnvCellTopology,
                                        fileId,
                                        exception.Message));
                                }
                                finally
                                {
                                    Interlocked.Increment(
                                        ref collisionCompleted);
                                }
                            });

                        foreach (CollisionPayloadResult topology in
                                 topologyResults.OrderBy(
                                     result => result.Key))
                        {
                            writer.AddBlob(
                                topology.Key,
                                topology.Payload);
                            writtenKeys.Add(topology.Key);
                            envCellTopologyWritten++;
                        }

                        ReportProgressIfDue(
                            collisionCompleted,
                            totalCollisionJobs,
                            failures.Count,
                            collisionStopwatch.Elapsed,
                            lastProgressReport,
                            options.Progress,
                            "collision",
                            final: false);
                    }
                }
            }

            collisionStopwatch.Stop();
            ReportProgressIfDue(
                collisionCompleted,
                totalCollisionJobs,
                failures.Count,
                collisionStopwatch.Elapsed,
                lastProgressReport,
                options.Progress,
                "collision",
                final: true);

            writer.Finish();
            stopwatch.Stop();

            var typeCounts = new Dictionary<PakAssetType, int>
            {
                [PakAssetType.GfxObjMesh] =
                    gfxObjWritten + sideStagedWritten,
                [PakAssetType.SetupMesh] = setupWritten,
                [PakAssetType.EnvCellMesh] = envCellKeysWritten,
                [PakAssetType.GfxObjCollision] = gfxCollisionWritten,
                [PakAssetType.SetupCollision] = setupCollisionWritten,
                [PakAssetType.CellStructureCollision] =
                    cellStructureWritten,
                [PakAssetType.EnvCellTopology] = envCellTopologyWritten,
                [PakAssetType.TexturePayload] = writer.TextureBlobCount,
            };
            var report = new BakeReport
            {
                Header = header,
                GfxObjKeys = gfxObjWritten,
                SetupKeys = setupWritten,
                EnvCellKeys = envCellKeysWritten,
                UniqueEnvCellGeometries = envCellGeometriesWritten,
                EnvCellAliases = envCellKeysWritten - envCellGeometriesWritten,
                GfxObjCollisionKeys = gfxCollisionWritten,
                UniqueGfxObjCollisions = uniqueGfxCollisions,
                GfxObjCollisionAliases = gfxCollisionAliasCount,
                SetupCollisionKeys = setupCollisionWritten,
                UniqueSetupCollisions = uniqueSetupCollisions,
                SetupCollisionAliases = setupCollisionAliasCount,
                CellStructureCollisionKeys = cellStructureWritten,
                UniqueCellStructureCollisions = uniqueCellStructures,
                CellStructureCollisionAliases =
                    cellStructureAliasCount,
                EnvCellTopologyKeys = envCellTopologyWritten,
                TexturePayloadKeys = writer.TextureBlobCount,
                SideStagedKeys = sideStagedWritten,
                PhysicalBlobs = writer.PhysicalBlobCount,
                TotalKeys = writer.EntryCount,
                Failures = failures.Count,
                ExtractionAndWriteElapsed = stopwatch.Elapsed,
                Elapsed = stopwatch.Elapsed,
                OutputBytes = new FileInfo(options.OutPath).Length,
                PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                PeakPrivateBytes = Process.GetCurrentProcess().PeakPagedMemorySize64,
                DecodedPayloadBytes = writer.DecodedPayloadBytes,
                StoredPayloadBytes = writer.StoredPayloadBytes,
                CompressedBlobs = writer.CompressedBlobCount,
                TypeCounts = typeCounts,
            };

            report = report with { SideStagedDuplicateKeys = sideStagedDuped };
            PrintFailures(failures);
            return report;
        }
    }

    private static bool WriteExactPayload(
        PakWriter writer,
        ulong key,
        byte[] payload,
        ExactPayloadAliasCatalog aliases)
    {
        if (aliases.TryFind(payload, out ulong primaryKey))
        {
            writer.AddAlias(key, primaryKey);
            return true;
        }

        writer.AddBlob(key, payload);
        aliases.Add(key, payload);
        return false;
    }

    private static (int Physical, int Aliases) WriteExactPayloadGroup(
        PakWriter writer,
        PakAssetType type,
        IReadOnlyList<uint> fileIds,
        byte[] payload,
        ExactPayloadAliasCatalog aliases,
        HashSet<ulong> writtenKeys)
    {
        if (fileIds.Count == 0)
            throw new InvalidDataException("collision alias group is empty");

        int firstUnwrittenIndex = 0;
        ulong primaryKey;
        int physical;
        if (aliases.TryFind(payload, out primaryKey))
        {
            physical = 0;
        }
        else
        {
            primaryKey = PakKey.Compose(type, fileIds[0]);
            writer.AddBlob(primaryKey, payload);
            aliases.Add(primaryKey, payload);
            writtenKeys.Add(primaryKey);
            firstUnwrittenIndex = 1;
            physical = 1;
        }

        int aliasCount = 0;
        for (int i = firstUnwrittenIndex; i < fileIds.Count; i++)
        {
            ulong aliasKey = PakKey.Compose(type, fileIds[i]);
            writer.AddAlias(aliasKey, primaryKey);
            writtenKeys.Add(aliasKey);
            aliasCount++;
        }

        return (physical, aliasCount);
    }

    private static int DrainSideStaged(
        ConcurrentQueue<ObjectMeshData> sideStaged,
        Dictionary<ulong, ObjectMeshData> sideStagedByKey,
        HashSet<ulong> writtenKeys,
        HashSet<ulong> scheduledMeshKeys)
    {
        int duplicates = 0;
        while (sideStaged.TryDequeue(out var staged))
        {
            uint fileId = (uint)(staged.ObjectId & 0xFFFF_FFFFu);
            ulong key = PakKey.Compose(PakAssetType.GfxObjMesh, fileId);
            if (writtenKeys.Contains(key)
                || scheduledMeshKeys.Contains(key)
                || !sideStagedByKey.TryAdd(key, staged))
                duplicates++;
        }
        return duplicates;
    }

    private static void CompactTransientBakeBuffersIfDue(
        int completedItems,
        bool final)
    {
        if (!final && completedItems % TransientCollectionStride != 0)
            return;

        GCLargeObjectHeapCompactionMode previous =
            GCSettings.LargeObjectHeapCompactionMode;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: true);
        }
        finally
        {
            GCSettings.LargeObjectHeapCompactionMode = previous;
        }
    }

    private static void ReportProgressIfDue(
        long done,
        int total,
        int failures,
        TimeSpan elapsed,
        Stopwatch lastProgressReport,
        IBakeProgressSink? progress,
        string phase,
        bool final)
    {
        if (!final && lastProgressReport.Elapsed.TotalSeconds < 5)
            return;

        double rate = elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0;
        double etaSeconds = rate > 0 ? (total - done) / rate : 0;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long managedHeap = GC.GetGCMemoryInfo().HeapSizeBytes;
        long privateBytes = process.PrivateMemorySize64;
        BakeProgressReporter.Write(
            Console.Out,
            progress,
            phase,
            done,
            total,
            failures,
            elapsed,
            etaSeconds,
            privateBytes,
            managedHeap);
        lastProgressReport.Restart();
    }

    private static void PrintSummary(BakeReport report, string outputPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== bake summary ===");
        Console.WriteLine($"  GfxObj keys:      {report.GfxObjKeys:N0}");
        Console.WriteLine($"  Setup keys:       {report.SetupKeys:N0}");
        Console.WriteLine($"  EnvCell keys:     {report.EnvCellKeys:N0}");
        Console.WriteLine($"  EnvCell unique:   {report.UniqueEnvCellGeometries:N0}");
        Console.WriteLine($"  EnvCell aliases:  {report.EnvCellAliases:N0}");
        Console.WriteLine($"  EnvCell dedup:    {report.EnvCellDedupRatio:F1}x");
        Console.WriteLine(
            $"  Gfx collisions:   {report.GfxObjCollisionKeys:N0} keys, " +
            $"{report.UniqueGfxObjCollisions:N0} blobs, " +
            $"{report.GfxObjCollisionAliases:N0} aliases");
        Console.WriteLine(
            $"  Setup collisions: {report.SetupCollisionKeys:N0} keys, " +
            $"{report.UniqueSetupCollisions:N0} blobs, " +
            $"{report.SetupCollisionAliases:N0} aliases");
        Console.WriteLine(
            $"  Cell structures:  {report.CellStructureCollisionKeys:N0} keys, " +
            $"{report.UniqueCellStructureCollisions:N0} blobs, " +
            $"{report.CellStructureCollisionAliases:N0} aliases");
        Console.WriteLine(
            $"  Cell topologies:   {report.EnvCellTopologyKeys:N0}");
        Console.WriteLine(
            $"  texture payloads:  {report.TexturePayloadKeys:N0}");
        Console.WriteLine(
            $"  side-staged keys: {report.SideStagedKeys:N0} " +
            $"({report.SideStagedDuplicateKeys:N0} duplicate keys)");
        Console.WriteLine($"  physical blobs:   {report.PhysicalBlobs:N0}");
        Console.WriteLine($"  total keys:       {report.TotalKeys:N0}");
        Console.WriteLine(
            $"  compressed blobs: {report.CompressedBlobs:N0}; " +
            $"payload {report.DecodedPayloadBytes / 1024.0 / 1024.0:F1} -> " +
            $"{report.StoredPayloadBytes / 1024.0 / 1024.0:F1} MB " +
            $"({report.PayloadCompressionRatio:F2}x)");
        Console.WriteLine($"  failures:         {report.Failures:N0}");
        Console.WriteLine(
            $"  extract + write:  {report.ExtractionAndWriteElapsed.TotalSeconds:F1} s");
        Console.WriteLine($"  total validated:  {report.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine(
            $"  peak working set: {report.PeakWorkingSetBytes / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine(
            $"  peak private:     {report.PeakPrivateBytes / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"  output size:      {report.OutputBytes / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"  output path:      {outputPath}");
    }

    private static void PrintFailures(
        ConcurrentBag<(PakAssetType Type, uint FileId, string Reason)> failures)
    {
        if (failures.IsEmpty)
            return;

        Console.WriteLine();
        Console.WriteLine($"failures ({failures.Count}):");
        foreach (var (type, fileId, reason) in failures
                     .OrderBy(failure => failure.Type)
                     .ThenBy(failure => failure.FileId)
                     .Take(200))
        {
            Console.WriteLine($"  {type,-12} 0x{fileId:X8}: {reason}");
        }

        if (failures.Count > 200)
            Console.WriteLine($"  ... and {failures.Count - 200} more");
    }

    private static List<uint> EnumerateEnvCellIds(
        DatCollection dats,
        HashSet<uint>? landblockFilter)
    {
        var landblockInfoIds = new List<uint>();
        foreach (var file in dats.Cell.Tree)
        {
            if ((file.Id & 0xFFFFu) != 0xFFFEu)
                continue;

            uint landblockId = file.Id & 0xFFFF_0000u;
            if (landblockFilter is not null && !landblockFilter.Contains(landblockId))
                continue;
            landblockInfoIds.Add(file.Id);
        }

        landblockInfoIds.Sort();
        var envCellIds = new List<uint>();
        foreach (uint landblockInfoId in landblockInfoIds)
        {
            if (!dats.Cell.TryGet<LandBlockInfo>(landblockInfoId, out var info) ||
                info is null ||
                info.NumCells == 0)
            {
                continue;
            }

            uint firstCellId = (landblockInfoId & 0xFFFF_0000u) | 0x0100u;
            for (uint offset = 0; offset < info.NumCells; offset++)
                envCellIds.Add(firstCellId + offset);
        }

        return envCellIds;
    }

    private sealed record CollisionPayloadResult(
        ulong Key,
        PakAssetType Type,
        uint FileId,
        byte[] Payload);

    private sealed record CellStructurePayloadResult(
        EnvCellGeometryGroup Group,
        FlatCellStructureCollisionAsset Structure,
        byte[] Payload);
}
