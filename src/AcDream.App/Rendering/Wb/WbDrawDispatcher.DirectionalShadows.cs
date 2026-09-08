using System.Numerics;
using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Scene;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Wb;

internal enum DirectionalShadowCasterMaterial : byte
{
    Opaque,
    AlphaCutout,
}

internal readonly record struct DirectionalShadowPreparedBatch(
    GpuTextureSlot TextureSlot,
    uint TextureLayer,
    CullMode CullMode,
    DirectionalShadowCasterMaterial Material,
    uint FoliageFlags = 0u);

internal readonly record struct DirectionalShadowPreparedRun(
    int StartCommand,
    int CommandCount,
    CullMode CullMode,
    DirectionalShadowCasterMaterial Material);

internal readonly record struct DirectionalShadowPreparationStats(
    int SourceCasters,
    int SourceMeshRefs,
    int SourceParts,
    int SourceBatches,
    int PreparedInstances,
    int PreparedOpaqueCommands,
    int PreparedAlphaCutoutCommands,
    int RejectedTransparentBatches,
    int RejectedFadedParts,
    int MissingMeshes,
    int UnresolvedAlphaCutoutTextures,
    int ActiveInstances = 0,
    int ActiveCommands = 0);

internal readonly record struct DirectionalShadowMeshGeometry(
    IGpuBuffer VertexBuffer,
    IGpuBuffer IndexBuffer);

internal readonly record struct DirectionalShadowTransformSource(
    bool Refreshable,
    int CasterIndex,
    int MeshIndex,
    bool IsSetupPart,
    Matrix4x4 SetupPartTransform)
{
    public static DirectionalShadowTransformSource Static(int casterIndex) =>
        new(
            false,
            casterIndex,
            MeshIndex: 0,
            IsSetupPart: false,
            SetupPartTransform: default);

    public static DirectionalShadowTransformSource Dynamic(
        int casterIndex,
        int meshIndex,
        bool isSetupPart,
        in Matrix4x4 setupPartTransform) =>
        new(
            true,
            casterIndex,
            meshIndex,
            isSetupPart,
            setupPartTransform);
}

internal sealed class DirectionalShadowPreparedDraws
{
    private DirectionalShadowSourceDraw[] _source = [];
    private Matrix4x4[] _transforms = [];
    private DirectionalShadowTransformSource[] _transformSources = [];
    private int[] _dynamicTransformSlots = [];
    private int[] _allDynamicTransformSlots = [];
    private int[] _firstDynamicTransformByCaster = [];
    private int[] _nextDynamicTransform = [];
    private int[] _denseChangedPoseByCaster = [];
    private RenderProjectionId[] _mappedCasterIds = [];
    private RenderProjectionClass[] _mappedCasterClasses = [];
    private bool[] _mappedCasterIdentityPresent = [];
    private DrawElementsIndirectCommand[] _commands = [];
    private DirectionalShadowPreparedBatch[] _batches = [];
    private DirectionalShadowPreparedRun[] _runs = [];
    private DrawElementsIndirectCommand[] _activeCommands = [];
    private DirectionalShadowPreparedBatch[] _activeBatches = [];
    private DirectionalShadowPreparedRun[] _activeRuns = [];
    private int[] _drawNextInGroup = [];
    private int[] _groupHead = [];
    private int[] _groupTail = [];
    private int[] _groupCountByGroup = [];
    private ulong[] _groupKeyHi = [];
    private ulong[] _groupKeyLo = [];
    private int[] _groupFirstDraw = [];
    private int[] _groupOrder = [];
    private readonly Dictionary<DirectionalShadowDrawKey, int> _groupByKey = [];
    private int _sourceCount;
    private int _commandCount;
    private int _runCount;
    private int _activeCommandCount;
    private int _activeRunCount;
    private int _dynamicTransformSlotCount;
    private int _allDynamicTransformSlotCount;
    private int _mappedCasterCount;
    private bool _building;
    private bool _retryClassificationNextFrame;

    public RenderSceneGeneration SourceGeneration { get; private set; }

    public ulong SourceCasterBuildSequence { get; private set; }

    public long SourceRenderDataAvailabilityVersion { get; private set; }

    public ulong SourceTranslucencyFadeRevision { get; private set; }

    public ulong BuildSequence { get; private set; }

    public ulong SourceCasterSelectionSequence { get; private set; }

    public ulong ActiveSelectionSequence { get; private set; }

    public int LastDynamicTransformRefreshCount { get; private set; }

    public bool LastDynamicTransformRefreshWasDense { get; private set; }

    public int OpaqueCommandCount { get; private set; }

    public int AlphaCutoutCommandCount => _commandCount - OpaqueCommandCount;

    public int OpaqueRunCount { get; private set; }

    public ReadOnlySpan<Matrix4x4> Transforms =>
        _transforms.AsSpan(0, _sourceCount);

    public ReadOnlySpan<int> DynamicTransformSlots =>
        _dynamicTransformSlots.AsSpan(0, _dynamicTransformSlotCount);

    public ReadOnlySpan<int> AllDynamicTransformSlots =>
        _allDynamicTransformSlots.AsSpan(0, _allDynamicTransformSlotCount);

    public ReadOnlySpan<DrawElementsIndirectCommand> Commands =>
        _commands.AsSpan(0, _commandCount);

    public ReadOnlySpan<DrawElementsIndirectCommand> OpaqueCommands =>
        _commands.AsSpan(0, OpaqueCommandCount);

    public ReadOnlySpan<DrawElementsIndirectCommand> AlphaCutoutCommands =>
        _commands.AsSpan(OpaqueCommandCount, AlphaCutoutCommandCount);

    public ReadOnlySpan<DirectionalShadowPreparedBatch> Batches =>
        _batches.AsSpan(0, _commandCount);

    public ReadOnlySpan<DirectionalShadowPreparedBatch> OpaqueBatches =>
        _batches.AsSpan(0, OpaqueCommandCount);

    public ReadOnlySpan<DirectionalShadowPreparedBatch> AlphaCutoutBatches =>
        _batches.AsSpan(OpaqueCommandCount, AlphaCutoutCommandCount);

    public ReadOnlySpan<DirectionalShadowPreparedRun> Runs =>
        _runs.AsSpan(0, _runCount);

    public ReadOnlySpan<DirectionalShadowPreparedRun> OpaqueRuns =>
        _runs.AsSpan(0, OpaqueRunCount);

    public ReadOnlySpan<DirectionalShadowPreparedRun> AlphaCutoutRuns =>
        _runs.AsSpan(OpaqueRunCount, _runCount - OpaqueRunCount);

    public int ActiveOpaqueCommandCount { get; private set; }

    public int ActiveAlphaCutoutCommandCount =>
        _activeCommandCount - ActiveOpaqueCommandCount;

    public int ActiveOpaqueRunCount { get; private set; }

    public ReadOnlySpan<DrawElementsIndirectCommand> ActiveCommands =>
        _activeCommands.AsSpan(0, _activeCommandCount);

    public ReadOnlySpan<DirectionalShadowPreparedBatch> ActiveBatches =>
        _activeBatches.AsSpan(0, _activeCommandCount);

    public ReadOnlySpan<DirectionalShadowPreparedRun> ActiveOpaqueRuns =>
        _activeRuns.AsSpan(0, ActiveOpaqueRunCount);

    public ReadOnlySpan<DirectionalShadowPreparedRun> ActiveAlphaCutoutRuns =>
        _activeRuns.AsSpan(
            ActiveOpaqueRunCount,
            _activeRunCount - ActiveOpaqueRunCount);

    public DirectionalShadowPreparationStats Stats { get; private set; }

    public long RetainedScratchBytes => checked(
        (long)_source.Length * Unsafe.SizeOf<DirectionalShadowSourceDraw>()
        + (long)_transforms.Length * Unsafe.SizeOf<Matrix4x4>()
        + (long)_transformSources.Length
            * Unsafe.SizeOf<DirectionalShadowTransformSource>()
        + (long)_dynamicTransformSlots.Length * sizeof(int)
        + (long)_allDynamicTransformSlots.Length * sizeof(int)
        + (long)_firstDynamicTransformByCaster.Length * sizeof(int)
        + (long)_nextDynamicTransform.Length * sizeof(int)
        + (long)_denseChangedPoseByCaster.Length * sizeof(int)
        + (long)_mappedCasterIds.Length * Unsafe.SizeOf<RenderProjectionId>()
        + (long)_mappedCasterClasses.Length
            * Unsafe.SizeOf<RenderProjectionClass>()
        + _mappedCasterIdentityPresent.Length
        + (long)_commands.Length * Unsafe.SizeOf<DrawElementsIndirectCommand>()
        + (long)_batches.Length * Unsafe.SizeOf<DirectionalShadowPreparedBatch>()
        + (long)_runs.Length * Unsafe.SizeOf<DirectionalShadowPreparedRun>()
        + (long)_activeCommands.Length * Unsafe.SizeOf<DrawElementsIndirectCommand>()
        + (long)_activeBatches.Length * Unsafe.SizeOf<DirectionalShadowPreparedBatch>()
        + (long)_activeRuns.Length * Unsafe.SizeOf<DirectionalShadowPreparedRun>()
        + (long)_drawNextInGroup.Length * sizeof(int)
        + (long)_groupHead.Length * sizeof(int)
        + (long)_groupTail.Length * sizeof(int)
        + (long)_groupCountByGroup.Length * sizeof(int)
        + (long)_groupKeyHi.Length * sizeof(ulong)
        + (long)_groupKeyLo.Length * sizeof(ulong)
        + (long)_groupFirstDraw.Length * sizeof(int)
        + (long)_groupOrder.Length * sizeof(int)
        + (long)_groupByKey.EnsureCapacity(0)
            * (sizeof(int)
                + Unsafe.SizeOf<KeyValuePair<DirectionalShadowDrawKey, int>>()));

    public bool RequiresTopologyBuild(
        RenderSceneGeneration generation,
        ulong casterBuildSequence,
        long renderDataAvailabilityVersion = 0,
        ulong translucencyFadeRevision = 0) =>
        SourceGeneration != generation
        || SourceCasterBuildSequence != casterBuildSequence
        || SourceRenderDataAvailabilityVersion
            != renderDataAvailabilityVersion
        || SourceTranslucencyFadeRevision != translucencyFadeRevision
        || _retryClassificationNextFrame;

    public bool TryBegin(
        RenderSceneGeneration generation,
        ulong casterBuildSequence,
        int estimatedInstances,
        long renderDataAvailabilityVersion = 0,
        ulong translucencyFadeRevision = 0)
    {
        if (_building)
            throw new InvalidOperationException(
                "A directional-shadow draw build is already active.");
        if (casterBuildSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(casterBuildSequence));
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedInstances);
        if (!RequiresTopologyBuild(
                generation,
                casterBuildSequence,
                renderDataAvailabilityVersion,
                translucencyFadeRevision))
        {
            return false;
        }

        EnsureCapacity(ref _source, estimatedInstances);
        _sourceCount = 0;
        _commandCount = 0;
        _runCount = 0;
        _activeCommandCount = 0;
        _activeRunCount = 0;
        _dynamicTransformSlotCount = 0;
        _allDynamicTransformSlotCount = 0;
        if (_mappedCasterCount != 0)
        {
            Array.Clear(
                _mappedCasterIdentityPresent,
                0,
                _mappedCasterCount);
        }
        _mappedCasterCount = 0;
        OpaqueCommandCount = 0;
        OpaqueRunCount = 0;
        ActiveOpaqueCommandCount = 0;
        ActiveOpaqueRunCount = 0;
        Stats = default;
        LastDynamicTransformRefreshCount = 0;
        LastDynamicTransformRefreshWasDense = false;
        _building = true;
        return true;
    }

    public void MapCasterIdentity(
        int casterIndex,
        RenderProjectionId id,
        RenderProjectionClass projectionClass)
    {
        if (!_building)
        {
            throw new InvalidOperationException(
                "Begin a directional-shadow draw build before mapping casters.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(casterIndex);
        int required = checked(casterIndex + 1);
        EnsureCapacity(ref _mappedCasterIds, required);
        EnsureCapacity(ref _mappedCasterClasses, required);
        EnsureCapacity(ref _mappedCasterIdentityPresent, required);
        if (_mappedCasterIdentityPresent[casterIndex]
            && (_mappedCasterIds[casterIndex] != id
                || _mappedCasterClasses[casterIndex] != projectionClass))
        {
            throw new InvalidOperationException(
                $"Directional-shadow caster slot {casterIndex} was mapped twice "
                + "with different projection identities.");
        }
        _mappedCasterIds[casterIndex] = id;
        _mappedCasterClasses[casterIndex] = projectionClass;
        _mappedCasterIdentityPresent[casterIndex] = true;
        _mappedCasterCount = Math.Max(_mappedCasterCount, required);
    }

    public void Add(
        uint firstIndex,
        int baseVertex,
        int indexCount,
        GpuTextureSlot textureSlot,
        uint textureLayer,
        CullMode cullMode,
        DirectionalShadowCasterMaterial material,
        in Matrix4x4 transform,
        uint foliageFlags = 0u)
    {
        DirectionalShadowTransformSource source = default;
        Add(
            firstIndex,
            baseVertex,
            indexCount,
            textureSlot,
            textureLayer,
            cullMode,
            material,
            in transform,
            in source,
            foliageFlags);
    }

    public void Add(
        uint firstIndex,
        int baseVertex,
        int indexCount,
        GpuTextureSlot textureSlot,
        uint textureLayer,
        CullMode cullMode,
        DirectionalShadowCasterMaterial material,
        in Matrix4x4 transform,
        in DirectionalShadowTransformSource transformSource,
        uint foliageFlags = 0u)
    {
        if (!_building)
            throw new InvalidOperationException(
                "Begin a directional-shadow draw build before adding batches.");
        if (indexCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(indexCount));
        if (material is DirectionalShadowCasterMaterial.AlphaCutout
            && !textureSlot.IsAssigned)
        {
            throw new ArgumentException(
                "An alpha-cutout caster requires an assigned texture slot.",
                nameof(textureSlot));
        }

        EnsureCapacity(ref _source, checked(_sourceCount + 1));
        _source[_sourceCount++] = new DirectionalShadowSourceDraw(
            new DirectionalShadowDrawKey(
                firstIndex,
                baseVertex,
                indexCount,
                material is DirectionalShadowCasterMaterial.AlphaCutout
                    ? textureSlot
                    : GpuTextureSlot.Unassigned,
                material is DirectionalShadowCasterMaterial.AlphaCutout
                    ? textureLayer
                    : 0u,
                cullMode,
                material,
                foliageFlags),
            transform,
            transformSource);
    }

    public void Complete(
        RenderSceneGeneration generation,
        ulong casterBuildSequence,
        in DirectionalShadowPreparationStats stats,
        long renderDataAvailabilityVersion = 0,
        ulong translucencyFadeRevision = 0)
    {
        if (!_building)
            throw new InvalidOperationException(
                "No directional-shadow draw build is active.");
        if (casterBuildSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(casterBuildSequence));

        int groupCount = 0;
        _groupByKey.Clear();
        EnsureCapacity(ref _drawNextInGroup, _sourceCount);
        EnsureCapacity(ref _groupHead, _sourceCount);
        EnsureCapacity(ref _groupTail, _sourceCount);
        EnsureCapacity(ref _groupCountByGroup, _sourceCount);
        EnsureCapacity(ref _groupKeyHi, _sourceCount);
        EnsureCapacity(ref _groupKeyLo, _sourceCount);
        EnsureCapacity(ref _groupFirstDraw, _sourceCount);
        EnsureCapacity(ref _groupOrder, _sourceCount);
        for (int i = 0; i < _sourceCount; i++)
        {
            DirectionalShadowDrawKey key = _source[i].Key;
            if (!_groupByKey.TryGetValue(key, out int group))
            {
                group = groupCount++;
                _groupByKey.Add(key, group);
                _groupHead[group] = i;
                _groupTail[group] = i;
                _groupCountByGroup[group] = 0;
                _groupFirstDraw[group] = i;
                _groupKeyHi[group] =
                    ((ulong)(byte)key.Material << 62)
                    | ((ulong)((uint)key.CullMode & 0x3u) << 60)
                    | ((ulong)key.FirstIndex << 28)
                    | ((ulong)(uint)key.BaseVertex & 0x0FFF_FFFFul);
                _groupKeyLo[group] =
                    ((ulong)Math.Min((uint)key.IndexCount, 0xF_FFFFu) << 44)
                    | ((ulong)key.TextureSlot.Index << 12)
                    | ((ulong)Math.Min(key.TextureLayer, 0x3FFu) << 2)
                    | (key.FoliageFlags & 0x3u);
            }
            else
            {
                _drawNextInGroup[_groupTail[group]] = i;
                _groupTail[group] = i;
            }
            _drawNextInGroup[i] = -1;
            _groupCountByGroup[group]++;
        }
        for (int g = 0; g < groupCount; g++)
            _groupOrder[g] = g;
        _groupOrder.AsSpan(0, groupCount).Sort(
            new GroupOrderComparer(_groupKeyHi, _groupKeyLo, _groupFirstDraw));
        EnsureCapacity(ref _transforms, _sourceCount);
        EnsureCapacity(ref _transformSources, _sourceCount);
        EnsureCapacity(ref _dynamicTransformSlots, _sourceCount);
        EnsureCapacity(ref _allDynamicTransformSlots, _sourceCount);
        EnsureCapacity(ref _nextDynamicTransform, _sourceCount);
        EnsureCapacity(ref _commands, _sourceCount);
        EnsureCapacity(ref _batches, _sourceCount);
        EnsureCapacity(ref _runs, _sourceCount);
        EnsureCapacity(ref _activeCommands, _sourceCount);
        EnsureCapacity(ref _activeBatches, _sourceCount);
        EnsureCapacity(ref _activeRuns, _sourceCount);

        int maxCasterIndex = -1;
        for (int index = 0; index < _sourceCount; index++)
        {
            DirectionalShadowTransformSource transformSource =
                _source[index].TransformSource;
            if (transformSource.Refreshable)
                maxCasterIndex = Math.Max(maxCasterIndex, transformSource.CasterIndex);
        }
        _mappedCasterCount = Math.Max(_mappedCasterCount, maxCasterIndex + 1);
        EnsureCapacity(ref _firstDynamicTransformByCaster, _mappedCasterCount);
        EnsureCapacity(ref _denseChangedPoseByCaster, _mappedCasterCount);
        EnsureCapacity(ref _mappedCasterIds, _mappedCasterCount);
        EnsureCapacity(ref _mappedCasterClasses, _mappedCasterCount);
        EnsureCapacity(ref _mappedCasterIdentityPresent, _mappedCasterCount);
        if (_mappedCasterCount != 0)
        {
            Array.Fill(
                _firstDynamicTransformByCaster,
                -1,
                0,
                _mappedCasterCount);
        }

        int transformIndex = 0;
        int commandIndex = 0;
        int opaqueCommands = 0;
        for (int orderIndex = 0; orderIndex < groupCount; orderIndex++)
        {
            int group = _groupOrder[orderIndex];
            DirectionalShadowDrawKey key = _source[_groupFirstDraw[group]].Key;
            int instanceCount = _groupCountByGroup[group];
            for (int draw = _groupHead[group]; draw >= 0; draw = _drawNextInGroup[draw])
            {
                _transforms[transformIndex++] = _source[draw].Transform;
                _transformSources[transformIndex - 1] =
                    _source[draw].TransformSource;
                if (_source[draw].TransformSource.Refreshable)
                {
                    int dynamicTransformIndex = transformIndex - 1;
                    DirectionalShadowTransformSource transformSource =
                        _source[draw].TransformSource;
                    if (transformSource.CasterIndex < 0)
                    {
                        throw new InvalidOperationException(
                            "A refreshable directional-shadow transform has a negative caster index.");
                    }
                    _allDynamicTransformSlots[_allDynamicTransformSlotCount++] =
                        dynamicTransformIndex;
                    _nextDynamicTransform[dynamicTransformIndex] =
                        _firstDynamicTransformByCaster[transformSource.CasterIndex];
                    _firstDynamicTransformByCaster[transformSource.CasterIndex] =
                        dynamicTransformIndex;
                }
            }

            _commands[commandIndex] = new DrawElementsIndirectCommand
            {
                Count = checked((uint)key.IndexCount),
                InstanceCount = checked((uint)instanceCount),
                FirstIndex = key.FirstIndex,
                BaseVertex = key.BaseVertex,
                BaseInstance = checked((uint)(transformIndex - instanceCount)),
            };
            _batches[commandIndex] = new DirectionalShadowPreparedBatch(
                key.TextureSlot,
                key.TextureLayer,
                key.CullMode,
                key.Material,
                key.FoliageFlags);
            if (key.Material is DirectionalShadowCasterMaterial.Opaque)
                opaqueCommands++;
            commandIndex++;
        }

        _commandCount = commandIndex;
        OpaqueCommandCount = opaqueCommands;
        int runCursor = 0;
        int runStart = 0;
        while (runStart < commandIndex)
        {
            DirectionalShadowPreparedBatch first = _batches[runStart];
            int runEnd = runStart + 1;
            while (runEnd < commandIndex
                   && _batches[runEnd].CullMode == first.CullMode
                   && _batches[runEnd].Material == first.Material)
            {
                runEnd++;
            }
            _runs[runCursor++] = new DirectionalShadowPreparedRun(
                runStart,
                runEnd - runStart,
                first.CullMode,
                first.Material);
            runStart = runEnd;
        }
        _runCount = runCursor;
        while (OpaqueRunCount < runCursor
               && _runs[OpaqueRunCount].Material is DirectionalShadowCasterMaterial.Opaque)
        {
            OpaqueRunCount++;
        }
        SourceGeneration = generation;
        SourceCasterBuildSequence = casterBuildSequence;
        SourceRenderDataAvailabilityVersion = renderDataAvailabilityVersion;
        SourceTranslucencyFadeRevision = translucencyFadeRevision;
        BuildSequence = checked(BuildSequence + 1);
        LastDynamicTransformRefreshCount = 0;
        LastDynamicTransformRefreshWasDense = false;
        _dynamicTransformSlotCount = 0;
        _retryClassificationNextFrame =
            stats.UnresolvedAlphaCutoutTextures != 0;
        Stats = stats with
        {
            PreparedInstances = _sourceCount,
            PreparedOpaqueCommands = opaqueCommands,
            PreparedAlphaCutoutCommands = commandIndex - opaqueCommands,
        };
        _building = false;
        RebuildActiveAll();
    }

    internal void ApplySelection(DirectionalShadowCasterFrame casters)
    {
        ArgumentNullException.ThrowIfNull(casters);
        if (casters.SelectionSequence == 0)
            return;
        if (SourceGeneration != casters.Generation
            || SourceCasterBuildSequence != casters.BuildSequence)
        {
            throw new InvalidOperationException(
                "Directional-shadow selection does not match prepared topology.");
        }
        if (SourceCasterSelectionSequence == casters.SelectionSequence)
            return;

        ApplySelection(casters.SelectedCasters, casters.SelectionSequence);
    }

    internal void ApplySelection(
        ReadOnlySpan<bool> selected,
        ulong casterSelectionSequence)
    {
        if (casterSelectionSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(casterSelectionSequence));
        if (SourceCasterSelectionSequence == casterSelectionSequence)
            return;

        _activeCommandCount = 0;
        int activeInstances = 0;
        for (int commandIndex = 0; commandIndex < _commandCount; commandIndex++)
        {
            DrawElementsIndirectCommand command = _commands[commandIndex];
            int first = checked((int)command.BaseInstance);
            int end = checked(first + (int)command.InstanceCount);
            int runStart = -1;
            for (int transformIndex = first; transformIndex < end; transformIndex++)
            {
                int casterIndex = _transformSources[transformIndex].CasterIndex;
                if ((uint)casterIndex >= (uint)selected.Length)
                {
                    throw new InvalidOperationException(
                        "Prepared directional-shadow instance has a stale caster slot.");
                }
                bool active = selected[casterIndex];
                if (active && runStart < 0)
                    runStart = transformIndex;
                if (!active && runStart >= 0)
                {
                    EmitActive(commandIndex, in command, runStart, transformIndex - runStart);
                    activeInstances += transformIndex - runStart;
                    runStart = -1;
                }
            }
            if (runStart >= 0)
            {
                EmitActive(commandIndex, in command, runStart, end - runStart);
                activeInstances += end - runStart;
            }
        }

        BuildActiveRuns();
        SourceCasterSelectionSequence = casterSelectionSequence;
        ActiveSelectionSequence = checked(ActiveSelectionSequence + 1);
        Stats = Stats with
        {
            ActiveInstances = activeInstances,
            ActiveCommands = _activeCommandCount,
        };
    }

    private void RebuildActiveAll()
    {
        EnsureCapacity(ref _activeCommands, _commandCount);
        EnsureCapacity(ref _activeBatches, _commandCount);
        EnsureCapacity(ref _activeRuns, _runCount);
        _commands.AsSpan(0, _commandCount).CopyTo(_activeCommands);
        _batches.AsSpan(0, _commandCount).CopyTo(_activeBatches);
        _activeCommandCount = _commandCount;
        BuildActiveRuns();
        SourceCasterSelectionSequence = 0;
        ActiveSelectionSequence = checked(ActiveSelectionSequence + 1);
        Stats = Stats with
        {
            ActiveInstances = _sourceCount,
            ActiveCommands = _activeCommandCount,
        };
    }

    private void EmitActive(
        int sourceCommandIndex,
        in DrawElementsIndirectCommand source,
        int baseInstance,
        int instanceCount)
    {
        int destination = _activeCommandCount++;
        _activeCommands[destination] = source with
        {
            BaseInstance = checked((uint)baseInstance),
            InstanceCount = checked((uint)instanceCount),
        };
        _activeBatches[destination] = _batches[sourceCommandIndex];
    }

    private void BuildActiveRuns()
    {
        _activeRunCount = 0;
        ActiveOpaqueCommandCount = 0;
        while (ActiveOpaqueCommandCount < _activeCommandCount
               && _activeBatches[ActiveOpaqueCommandCount].Material
                    is DirectionalShadowCasterMaterial.Opaque)
        {
            ActiveOpaqueCommandCount++;
        }

        int runStart = 0;
        while (runStart < _activeCommandCount)
        {
            DirectionalShadowPreparedBatch first = _activeBatches[runStart];
            int runEnd = runStart + 1;
            while (runEnd < _activeCommandCount
                   && _activeBatches[runEnd].CullMode == first.CullMode
                   && _activeBatches[runEnd].Material == first.Material)
            {
                runEnd++;
            }
            _activeRuns[_activeRunCount++] = new DirectionalShadowPreparedRun(
                runStart,
                runEnd - runStart,
                first.CullMode,
                first.Material);
            runStart = runEnd;
        }
        ActiveOpaqueRunCount = 0;
        while (ActiveOpaqueRunCount < _activeRunCount
               && _activeRuns[ActiveOpaqueRunCount].Material
                    is DirectionalShadowCasterMaterial.Opaque)
        {
            ActiveOpaqueRunCount++;
        }
    }

    public void RefreshDynamicTransforms(
        DirectionalShadowCasterFrame casters)
    {
        ArgumentNullException.ThrowIfNull(casters);
        if (casters.Stats.TransformJournalFullRefresh)
        {
            RefreshAllDynamicTransforms(casters.Casters);
            LastDynamicTransformRefreshWasDense = false;
            return;
        }
        RefreshDynamicTransforms(
            casters.ChangedCasterPoses,
            casters.Stats.DensityBulkRefresh);
    }

    internal void RefreshDynamicTransforms(
        ReadOnlySpan<DirectionalShadowCaster> currentCasters)
    {
        RefreshAllDynamicTransforms(currentCasters);
        LastDynamicTransformRefreshWasDense = false;
    }

    internal void RefreshDenseDynamicTransforms(
        ReadOnlySpan<DirectionalShadowCaster> currentCasters)
    {
        RefreshAllDynamicTransforms(currentCasters);
        LastDynamicTransformRefreshWasDense = true;
    }

    private void RefreshAllDynamicTransforms(
        ReadOnlySpan<DirectionalShadowCaster> currentCasters)
    {
        _dynamicTransformSlotCount = _allDynamicTransformSlotCount;
        _allDynamicTransformSlots.AsSpan(0, _allDynamicTransformSlotCount)
            .CopyTo(_dynamicTransformSlots);
        int refreshed = 0;
        for (int dynamicIndex = 0;
             dynamicIndex < _dynamicTransformSlotCount;
             dynamicIndex++)
        {
            int transformIndex = _dynamicTransformSlots[dynamicIndex];
            DirectionalShadowTransformSource source =
                _transformSources[transformIndex];
            if ((uint)source.CasterIndex >= (uint)currentCasters.Length)
            {
                throw new InvalidOperationException(
                    "Directional-shadow transform source has a stale caster index.");
            }

            RenderProjectionRecord projection =
                currentCasters[source.CasterIndex].Projection;
            IReadOnlyList<MeshRef> meshes = projection.EntityPayload.MeshRefs;
            if ((uint)source.MeshIndex >= (uint)meshes.Count)
            {
                throw new InvalidOperationException(
                    "Directional-shadow transform source has a stale mesh index.");
            }

            MeshRef mesh = meshes[source.MeshIndex];
            _transforms[transformIndex] = source.IsSetupPart
                ? WbDrawDispatcher.ComposePartWorldMatrix(
                    projection.Transform.LocalToWorld,
                    mesh.PartTransform,
                    source.SetupPartTransform)
                : mesh.PartTransform * projection.Transform.LocalToWorld;
            refreshed++;
        }

        LastDynamicTransformRefreshCount = refreshed;
    }

    internal void RefreshDynamicTransforms(
        ReadOnlySpan<DirectionalShadowCaster> currentCasters,
        ReadOnlySpan<int> changedCasterSlots)
    {
        _dynamicTransformSlotCount = 0;
        for (int changedIndex = 0;
             changedIndex < changedCasterSlots.Length;
             changedIndex++)
        {
            int casterIndex = changedCasterSlots[changedIndex];
            if ((uint)casterIndex >= (uint)currentCasters.Length)
            {
                throw new InvalidOperationException(
                    "Directional-shadow changed-caster slot is stale.");
            }
            if ((uint)casterIndex >= (uint)_mappedCasterCount)
                continue;

            for (int transformIndex = _firstDynamicTransformByCaster[casterIndex];
                 transformIndex >= 0;
                 transformIndex = _nextDynamicTransform[transformIndex])
            {
                RefreshTransform(currentCasters, transformIndex);
                _dynamicTransformSlots[_dynamicTransformSlotCount++] = transformIndex;
            }
        }
        _dynamicTransformSlots.AsSpan(0, _dynamicTransformSlotCount).Sort();
        LastDynamicTransformRefreshCount = _dynamicTransformSlotCount;
        LastDynamicTransformRefreshWasDense = false;
    }

    internal void RefreshDynamicTransforms(
        ReadOnlySpan<DirectionalShadowChangedPose> changedPoses,
        bool denseRefresh = false)
    {
        if (denseRefresh)
        {
            RefreshDenseDynamicTransforms(changedPoses);
            return;
        }

        _dynamicTransformSlotCount = 0;
        for (int changedIndex = 0;
             changedIndex < changedPoses.Length;
             changedIndex++)
        {
            ref readonly DirectionalShadowChangedPose changed =
                ref changedPoses[changedIndex];
            int casterIndex = changed.CasterIndex;
            if ((uint)casterIndex >= (uint)_mappedCasterCount
                || !_mappedCasterIdentityPresent[casterIndex])
            {
                throw new InvalidOperationException(
                    "Directional-shadow changed pose has a stale or unmapped caster index.");
            }

            ref readonly DirectionalShadowTransformSnapshot pose =
                ref changed.Snapshot;
            if (pose.Id != _mappedCasterIds[casterIndex]
                || pose.ProjectionClass != _mappedCasterClasses[casterIndex])
            {
                throw new InvalidOperationException(
                    $"Directional-shadow changed pose {pose.Id} does not match "
                    + $"retained caster {_mappedCasterIds[casterIndex]} at "
                    + $"slot {casterIndex}.");
            }

            for (int transformIndex = _firstDynamicTransformByCaster[casterIndex];
                 transformIndex >= 0;
                 transformIndex = _nextDynamicTransform[transformIndex])
            {
                RefreshTransform(
                    in pose,
                    casterIndex,
                    transformIndex);
                _dynamicTransformSlots[_dynamicTransformSlotCount++] =
                    transformIndex;
            }
        }
        _dynamicTransformSlots.AsSpan(0, _dynamicTransformSlotCount).Sort();
        LastDynamicTransformRefreshCount = _dynamicTransformSlotCount;
        LastDynamicTransformRefreshWasDense = denseRefresh;
    }

    private void RefreshDenseDynamicTransforms(
        ReadOnlySpan<DirectionalShadowChangedPose> changedPoses)
    {
        _dynamicTransformSlotCount = 0;
        int marked = 0;
        try
        {
            for (int changedIndex = 0;
                 changedIndex < changedPoses.Length;
                 changedIndex++)
            {
                ref readonly DirectionalShadowChangedPose changed =
                    ref changedPoses[changedIndex];
                int casterIndex = changed.CasterIndex;
                ValidateChangedPose(in changed, casterIndex);
                if (_denseChangedPoseByCaster[casterIndex] != 0)
                {
                    throw new InvalidOperationException(
                        "A dense directional-shadow refresh contains a duplicate caster slot.");
                }

                _denseChangedPoseByCaster[casterIndex] = changedIndex + 1;
                marked++;
            }

            for (int dynamicIndex = 0;
                 dynamicIndex < _allDynamicTransformSlotCount;
                 dynamicIndex++)
            {
                int transformIndex = _allDynamicTransformSlots[dynamicIndex];
                DirectionalShadowTransformSource source =
                    _transformSources[transformIndex];
                int poseIndex = _denseChangedPoseByCaster[source.CasterIndex] - 1;
                if (poseIndex < 0)
                    continue;

                ref readonly DirectionalShadowChangedPose changed =
                    ref changedPoses[poseIndex];
                RefreshTransform(
                    in changed.Snapshot,
                    changed.CasterIndex,
                    transformIndex);
                _dynamicTransformSlots[_dynamicTransformSlotCount++] =
                    transformIndex;
            }
        }
        finally
        {
            if (marked != 0)
            {
                for (int changedIndex = 0;
                     changedIndex < changedPoses.Length;
                     changedIndex++)
                {
                    int casterIndex = changedPoses[changedIndex].CasterIndex;
                    if ((uint)casterIndex < (uint)_denseChangedPoseByCaster.Length)
                        _denseChangedPoseByCaster[casterIndex] = 0;
                }
            }
        }

        LastDynamicTransformRefreshCount = _dynamicTransformSlotCount;
        LastDynamicTransformRefreshWasDense = true;
    }

    private void ValidateChangedPose(
        in DirectionalShadowChangedPose changed,
        int casterIndex)
    {
        if ((uint)casterIndex >= (uint)_mappedCasterCount
            || !_mappedCasterIdentityPresent[casterIndex])
        {
            throw new InvalidOperationException(
                "Directional-shadow changed pose has a stale or unmapped caster index.");
        }

        ref readonly DirectionalShadowTransformSnapshot pose =
            ref changed.Snapshot;
        if (pose.Id != _mappedCasterIds[casterIndex]
            || pose.ProjectionClass != _mappedCasterClasses[casterIndex])
        {
            throw new InvalidOperationException(
                $"Directional-shadow changed pose {pose.Id} does not match "
                + $"retained caster {_mappedCasterIds[casterIndex]} at "
                + $"slot {casterIndex}.");
        }
    }

    private void RefreshTransform(
        in DirectionalShadowTransformSnapshot pose,
        int casterIndex,
        int transformIndex)
    {
        DirectionalShadowTransformSource source =
            _transformSources[transformIndex];
        if (source.CasterIndex != casterIndex)
        {
            throw new InvalidOperationException(
                "Directional-shadow transform source maps to a different caster.");
        }

        IReadOnlyList<MeshRef> currentMeshes = pose.EntityPayload.MeshRefs;
        if ((uint)source.MeshIndex >= (uint)currentMeshes.Count)
        {
            throw new InvalidOperationException(
                "Directional-shadow changed pose has a stale mesh index.");
        }

        MeshRef currentMesh = currentMeshes[source.MeshIndex];
        _transforms[transformIndex] = source.IsSetupPart
            ? WbDrawDispatcher.ComposePartWorldMatrix(
                pose.Transform.LocalToWorld,
                currentMesh.PartTransform,
                source.SetupPartTransform)
            : currentMesh.PartTransform * pose.Transform.LocalToWorld;
    }

    private void RefreshTransform(
        ReadOnlySpan<DirectionalShadowCaster> currentCasters,
        int transformIndex)
    {
        DirectionalShadowTransformSource source =
            _transformSources[transformIndex];
        if ((uint)source.CasterIndex >= (uint)currentCasters.Length)
        {
            throw new InvalidOperationException(
                "Directional-shadow transform source has a stale caster index.");
        }

        RenderProjectionRecord projection =
            currentCasters[source.CasterIndex].Projection;
        IReadOnlyList<MeshRef> meshes = projection.EntityPayload.MeshRefs;
        if ((uint)source.MeshIndex >= (uint)meshes.Count)
        {
            throw new InvalidOperationException(
                "Directional-shadow transform source has a stale mesh index.");
        }

        MeshRef mesh = meshes[source.MeshIndex];
        _transforms[transformIndex] = source.IsSetupPart
            ? WbDrawDispatcher.ComposePartWorldMatrix(
                projection.Transform.LocalToWorld,
                mesh.PartTransform,
                source.SetupPartTransform)
            : mesh.PartTransform * projection.Transform.LocalToWorld;
    }

    public void Abort()
    {
        _sourceCount = 0;
        _commandCount = 0;
        _runCount = 0;
        _activeCommandCount = 0;
        _activeRunCount = 0;
        _dynamicTransformSlotCount = 0;
        _allDynamicTransformSlotCount = 0;
        if (_mappedCasterCount != 0)
        {
            Array.Clear(
                _mappedCasterIdentityPresent,
                0,
                _mappedCasterCount);
        }
        _mappedCasterCount = 0;
        OpaqueCommandCount = 0;
        OpaqueRunCount = 0;
        ActiveOpaqueCommandCount = 0;
        ActiveOpaqueRunCount = 0;
        Stats = default;
        SourceGeneration = default;
        SourceCasterBuildSequence = 0;
        SourceRenderDataAvailabilityVersion = 0;
        SourceTranslucencyFadeRevision = 0;
        SourceCasterSelectionSequence = 0;
        LastDynamicTransformRefreshCount = 0;
        LastDynamicTransformRefreshWasDense = false;
        _retryClassificationNextFrame = false;
        _building = false;
    }

    internal static bool TryClassifyMaterial(
        TranslucencyKind translucency,
        out DirectionalShadowCasterMaterial material)
    {
        switch (translucency)
        {
            case TranslucencyKind.Opaque:
                material = DirectionalShadowCasterMaterial.Opaque;
                return true;
            case TranslucencyKind.ClipMap:
                material = DirectionalShadowCasterMaterial.AlphaCutout;
                return true;
            default:
                material = default;
                return false;
        }
    }

    internal static bool FadeExcludesCaster(float translucency) =>
        !float.IsFinite(translucency) || translucency > 0f;

    private static void EnsureCapacity<T>(ref T[] values, int required)
    {
        if (values.Length >= required)
            return;
        int capacity = values.Length == 0 ? 16 : values.Length;
        while (capacity < required)
            capacity = checked(capacity * 2);
        Array.Resize(ref values, capacity);
    }

    private readonly struct GroupOrderComparer(
        ulong[] keyHi,
        ulong[] keyLo,
        int[] firstDraw) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            ulong left = keyHi[x];
            ulong right = keyHi[y];
            if (left != right)
                return left < right ? -1 : 1;
            left = keyLo[x];
            right = keyLo[y];
            if (left != right)
                return left < right ? -1 : 1;
            return firstDraw[x].CompareTo(firstDraw[y]);
        }
    }

    private readonly record struct DirectionalShadowDrawKey(
        uint FirstIndex,
        int BaseVertex,
        int IndexCount,
        GpuTextureSlot TextureSlot,
        uint TextureLayer,
        CullMode CullMode,
        DirectionalShadowCasterMaterial Material,
        uint FoliageFlags = 0u);

    private readonly record struct DirectionalShadowSourceDraw(
        DirectionalShadowDrawKey Key,
        Matrix4x4 Transform,
        DirectionalShadowTransformSource TransformSource);

}

public sealed partial class WbDrawDispatcher
{
    private readonly DirectionalShadowPreparedDraws _directionalShadowDraws = new();

    internal DirectionalShadowMeshGeometry GetDirectionalShadowGeometry()
    {
        GlobalMeshBuffer mesh = _meshAdapter.MeshManager?.GlobalBuffer
            ?? throw new InvalidOperationException("The shared mesh arena is not published.");
        return new DirectionalShadowMeshGeometry(
            mesh.VertexStore ?? throw new InvalidOperationException(
                "The shared mesh arena has no vertex store."),
            mesh.IndexStore ?? throw new InvalidOperationException(
                "The shared mesh arena has no index store."));
    }

    internal long DirectionalShadowAvailabilityVersion =>
        _meshAdapter.MeshManager?.RenderDataAvailabilityVersion ?? 0L;

    internal DirectionalShadowPreparedDraws PrepareDirectionalShadowDraws(
        DirectionalShadowCasterFrame casters,
        bool allowTopologyRebuild = true)
    {
        ArgumentNullException.ThrowIfNull(casters);
        ReadOnlySpan<DirectionalShadowCaster> source = casters.Casters;
        long renderDataAvailabilityVersion =
            _meshAdapter.MeshManager?.RenderDataAvailabilityVersion ?? 0L;
        ulong translucencyFadeRevision = _translucencyFades.Revision;
        if (!_directionalShadowDraws.RequiresTopologyBuild(
                casters.Generation,
                casters.BuildSequence,
                renderDataAvailabilityVersion,
                translucencyFadeRevision))
        {
            _directionalShadowDraws.RefreshDynamicTransforms(casters);
            _directionalShadowDraws.ApplySelection(casters);
            return _directionalShadowDraws;
        }
        if (!allowTopologyRebuild
            && _directionalShadowDraws.SourceCasterBuildSequence
                == casters.BuildSequence
            && _directionalShadowDraws.SourceGeneration == casters.Generation)
        {
            _directionalShadowDraws.RefreshDynamicTransforms(casters);
            _directionalShadowDraws.ApplySelection(casters);
            return _directionalShadowDraws;
        }

        int estimatedInstances = 0;
        for (int i = 0; i < source.Length; i++)
        {
            estimatedInstances = checked(
                estimatedInstances
                + source[i].Projection.EntityPayload.MeshRefs.Count);
        }
        if (!_directionalShadowDraws.TryBegin(
                casters.Generation,
                casters.BuildSequence,
                estimatedInstances,
                renderDataAvailabilityVersion,
                translucencyFadeRevision))
        {
            _directionalShadowDraws.ApplySelection(casters);
            return _directionalShadowDraws;
        }

        int meshRefs = 0;
        int parts = 0;
        int batches = 0;
        int rejectedTransparent = 0;
        int rejectedFaded = 0;
        int missingMeshes = 0;
        int unresolvedCutoutTextures = 0;
        try
        {
            for (int casterIndex = 0; casterIndex < source.Length; casterIndex++)
            {
                RenderProjectionRecord projection = source[casterIndex].Projection;
                _directionalShadowDraws.MapCasterIdentity(
                    casterIndex,
                    projection.Id,
                    projection.ProjectionClass);
                IReadOnlyList<MeshRef> projectionMeshes =
                    projection.EntityPayload.MeshRefs;
                var frameCandidate = new RenderFrameEntityCandidate(
                    projection,
                    MeshPartOffset: 0,
                    MeshPartCount: projectionMeshes.Count,
                    Animated: source[casterIndex].UsesCurrentAnimatedTransforms);
                RenderInstanceCandidate candidate =
                    RenderInstanceCandidate.FromFrame(
                        in frameCandidate,
                        projection.Residency.OwnerLandblockId);
                PaletteCompositeIdentity paletteIdentity =
                    projection.EntityPayload.PaletteOverride is null
                        ? default
                        : TextureCache.GetPaletteIdentity(
                            projection.EntityPayload.PaletteOverride);

                for (int meshIndex = 0;
                     meshIndex < projectionMeshes.Count;
                     meshIndex++)
                {
                    meshRefs++;
                    MeshRef meshRef = projectionMeshes[meshIndex];
                    ObjectRenderData? renderData =
                        _meshAdapter.TryGetRenderData(meshRef.GfxObjId);
                    if (renderData is null)
                    {
                        missingMeshes++;
                        _meshAdapter.EnsureLoaded(meshRef.GfxObjId);
                        continue;
                    }

                    if (renderData.IsSetup && renderData.SetupParts.Count > 0)
                    {
                        bool entityHasCutoutSubset = FoliageWindClassification
                            .ComputeEntityHasCutoutSubset(
                                renderData.SetupParts,
                                _meshAdapter,
                                static (adapter, part) => adapter.TryGetRenderData(part.GfxObjId)
                                    is { HasCutoutSubset: true });

                        for (int setupPartIndex = 0;
                             setupPartIndex < renderData.SetupParts.Count;
                             setupPartIndex++)
                        {
                            parts++;
                            if (PartFadeExcludesCaster(
                                    projection.Source.LocalEntityId,
                                    setupPartIndex))
                            {
                                rejectedFaded++;
                                continue;
                            }

                            (ulong partGfxObjId, Matrix4x4 partTransform) =
                                renderData.SetupParts[setupPartIndex];
                            ObjectRenderData? partData =
                                _meshAdapter.TryGetRenderData(partGfxObjId);
                            if (partData is null)
                            {
                                missingMeshes++;
                                _meshAdapter.EnsureLoaded(partGfxObjId);
                                continue;
                            }

                            Matrix4x4 model = ComposePartWorldMatrix(
                                projection.Transform.LocalToWorld,
                                meshRef.PartTransform,
                                partTransform);
                            DirectionalShadowTransformSource transformSource =
                                source[casterIndex].UsesCurrentAnimatedTransforms
                                    ? DirectionalShadowTransformSource.Dynamic(
                                        casterIndex,
                                        meshIndex,
                                        true,
                                        in partTransform)
                                    : DirectionalShadowTransformSource.Static(
                                        casterIndex);
                            AddDirectionalShadowBatches(
                                partData,
                                in candidate,
                                meshRef,
                                paletteIdentity,
                                in model,
                                in transformSource,
                                ref batches,
                                ref rejectedTransparent,
                                ref unresolvedCutoutTextures,
                                entityHasCutoutSubset);
                        }
                    }
                    else
                    {
                        parts++;
                        if (PartFadeExcludesCaster(
                                projection.Source.LocalEntityId,
                                meshIndex))
                        {
                            rejectedFaded++;
                            continue;
                        }

                        Matrix4x4 model = meshRef.PartTransform
                            * projection.Transform.LocalToWorld;
                        Matrix4x4 noSetupPart = default;
                        DirectionalShadowTransformSource transformSource =
                            source[casterIndex].UsesCurrentAnimatedTransforms
                                ? DirectionalShadowTransformSource.Dynamic(
                                    casterIndex,
                                    meshIndex,
                                    false,
                                    in noSetupPart)
                                : DirectionalShadowTransformSource.Static(
                                    casterIndex);
                        AddDirectionalShadowBatches(
                            renderData,
                            in candidate,
                            meshRef,
                            paletteIdentity,
                            in model,
                            in transformSource,
                            ref batches,
                            ref rejectedTransparent,
                            ref unresolvedCutoutTextures);
                    }
                }
            }

            var stats = new DirectionalShadowPreparationStats(
                SourceCasters: source.Length,
                SourceMeshRefs: meshRefs,
                SourceParts: parts,
                SourceBatches: batches,
                PreparedInstances: 0,
                PreparedOpaqueCommands: 0,
                PreparedAlphaCutoutCommands: 0,
                RejectedTransparentBatches: rejectedTransparent,
                RejectedFadedParts: rejectedFaded,
                MissingMeshes: missingMeshes,
                UnresolvedAlphaCutoutTextures: unresolvedCutoutTextures);
            _directionalShadowDraws.Complete(
                casters.Generation,
                casters.BuildSequence,
                in stats,
                renderDataAvailabilityVersion,
                translucencyFadeRevision);
            _directionalShadowDraws.ApplySelection(casters);
            return _directionalShadowDraws;
        }
        catch
        {
            _directionalShadowDraws.Abort();
            throw;
        }
    }

    private bool PartFadeExcludesCaster(uint entityId, int partIndex) =>
        _translucencyFades.TryGetCurrentValue(
            entityId,
            checked((uint)partIndex),
            out float translucency)
        && DirectionalShadowPreparedDraws.FadeExcludesCaster(translucency);

    private void AddDirectionalShadowBatches(
        ObjectRenderData renderData,
        in RenderInstanceCandidate candidate,
        MeshRef meshRef,
        PaletteCompositeIdentity paletteIdentity,
        in Matrix4x4 model,
        in DirectionalShadowTransformSource transformSource,
        ref int sourceBatches,
        ref int rejectedTransparent,
        ref int unresolvedCutoutTextures,
        bool? entityHasCutoutSubsetOverride = null)
    {
        if (_meshAdapter.IsRuntimeHiddenMarker(meshRef.GfxObjId))
            return;

        bool entityHasCutoutSubset = entityHasCutoutSubsetOverride ?? renderData.HasCutoutSubset;
        for (int batchIndex = 0;
             batchIndex < renderData.Batches.Count;
             batchIndex++)
        {
            ObjectRenderBatch batch = renderData.Batches[batchIndex];

            if (!RetailUntexturedSubsetPolicy.Draws(candidate.IsBuildingShell, batch.Key.IsSolid))
                continue;

            sourceBatches++;
            if (!DirectionalShadowPreparedDraws.TryClassifyMaterial(
                    batch.Translucency,
                    out DirectionalShadowCasterMaterial material))
            {
                rejectedTransparent++;
                continue;
            }

            GpuTextureSlot textureSlot = GpuTextureSlot.Unassigned;
            uint textureLayer = 0;
            if (material is DirectionalShadowCasterMaterial.AlphaCutout)
            {
                if (batch.Key.SurfaceId is 0 or uint.MaxValue)
                {
                    unresolvedCutoutTextures++;
                    continue;
                }
                ResolvedTexture texture = ResolveTexture(
                    in candidate,
                    meshRef,
                    batch,
                    paletteIdentity,
                    out bool compositePending);
                if (compositePending || !texture.Slot.IsAssigned)
                {
                    unresolvedCutoutTextures++;
                    continue;
                }
                textureSlot = texture.Slot;
                textureLayer = texture.Layer;
            }

            uint foliageFlags = FoliageWindClassification.Classify(
                candidate.LocalEntityId,
                FoliageWindExclusions.Contains(meshRef.GfxObjId),
                batch.Translucency,
                entityHasCutoutSubset);

            _directionalShadowDraws.Add(
                batch.FirstIndex,
                checked((int)batch.BaseVertex),
                batch.IndexCount,
                textureSlot,
                textureLayer,
                batch.CullMode,
                material,
                in model,
                in transformSource,
                foliageFlags);
        }
    }
}
