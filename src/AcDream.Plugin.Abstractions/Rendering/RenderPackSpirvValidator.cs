using System.Buffers.Binary;

namespace AcDream.Plugin.Abstractions.Rendering;

/// <summary>The shader stage a render-pack declaration assigns to one SPIR-V asset.</summary>
public enum RenderPackShaderStage
{
    Vertex,
    Fragment,
}

/// <summary>Hardware-independent validation result for one declared SPIR-V module.</summary>
public readonly record struct RenderPackSpirvValidationResult(bool Success, string? Reason)
{
    public static RenderPackSpirvValidationResult Valid() => new(true, null);

    public static RenderPackSpirvValidationResult Invalid(string reason) => new(false, reason);
}

public static class RenderPackSpirvValidator
{
    private const uint SpirvMagic = 0x0723_0203;
    private const uint VertexExecutionModel = 0;
    private const uint FragmentExecutionModel = 4;

    public static RenderPackSpirvValidationResult ValidatePassShader(
        ReadOnlySpan<byte> spirv,
        RenderPackShaderStage stage,
        RenderPassDeclaration pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        bool declaresSampledInput = pass.ResourceReads.Count != 0
            || pass.SemanticInputs.Any(static semantic => semantic is
                RenderSemanticInput.WorldColor
                or RenderSemanticInput.SceneDepth
                or RenderSemanticInput.SceneNormals
                or RenderSemanticInput.DirectionalShadowMaps);
        bool directionalDepth = pass.Semantic == RenderPassSemantic.DirectionalShadowDepth;
        var access = new AllowedInterface(
            StorageBindings: directionalDepth ? [0u, 1u] : [],
            UniformBindings: [],
            AllowSampledTable: declaresSampledInput,
            AllowAtmosphericFrame: directionalDepth || UsesAtmosphericFrame(pass.SemanticInputs),
            AllowDirectionalShadow: directionalDepth
                || pass.SemanticInputs.Contains(RenderSemanticInput.DirectionalShadowMaps)
                || pass.SemanticInputs.Contains(
                    RenderSemanticInput.SelectedCelestialDirectionalLight),
            AllowPackPass: !directionalDepth,
            AllowPackSettings: !directionalDepth);
        return Validate(spirv, stage, access);
    }

    public static RenderPackSpirvValidationResult ValidatePipelineVariantShader(
        ReadOnlySpan<byte> spirv,
        RenderPackShaderStage stage,
        PipelineVariantDeclaration variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        AllowedInterface access = variant.Semantic switch
        {
            RenderPipelineVariantSemantic.TerrainDirectionalShadowCaster =>
                new([], [], false, false, true, false, false),
            RenderPipelineVariantSemantic.TerrainMultiviewDirectionalShadowCaster =>
                new([], [], false, false, true, false, false),
            RenderPipelineVariantSemantic.WorldOpaqueDirectionalShadowCaster =>
                new([0u, 1u], [], false, true, true, false, false),
            RenderPipelineVariantSemantic.WorldOpaqueMultiviewDirectionalShadowCaster =>
                new([0u, 1u], [], false, true, true, false, false),
            RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster =>
                new([0u, 1u], [], true, true, true, false, false),
            RenderPipelineVariantSemantic.WorldAlphaCutoutMultiviewDirectionalShadowCaster =>
                new([0u, 1u], [], true, true, true, false, false),
            RenderPipelineVariantSemantic.TerrainDirectionalShadowReceiver =>
                new([], [1u, 2u, 3u], true, false, true, false, true),
            RenderPipelineVariantSemantic.WorldDirectionalShadowReceiver =>
                new([0u, 1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u], [1u],
                    true, true, true, false, true),
            _ => new(
                [],
                [],
                variant.SemanticInputs.Any(static semantic => semantic is
                    RenderSemanticInput.WorldColor
                    or RenderSemanticInput.SceneDepth
                    or RenderSemanticInput.SceneNormals
                    or RenderSemanticInput.DirectionalShadowMaps),
                UsesAtmosphericFrame(variant.SemanticInputs),
                variant.SemanticInputs.Contains(RenderSemanticInput.DirectionalShadowMaps)
                    || variant.SemanticInputs.Contains(
                        RenderSemanticInput.SelectedCelestialDirectionalLight),
                false,
                true),
        };
        return Validate(spirv, stage, access);
    }

    private static bool UsesAtmosphericFrame(IReadOnlyList<RenderSemanticInput> inputs) =>
        inputs.Any(static semantic => semantic is
            RenderSemanticInput.SunDirection
            or RenderSemanticInput.SunScreenPosition
            or RenderSemanticInput.ActiveDayGroup
            or RenderSemanticInput.Weather
            or RenderSemanticInput.CameraMatrices
            or RenderSemanticInput.FrameTime);

    private static RenderPackSpirvValidationResult Validate(
        ReadOnlySpan<byte> bytes,
        RenderPackShaderStage stage,
        AllowedInterface access)
    {
        if (bytes.Length < 20 || (bytes.Length & 3) != 0)
            return Invalid("the module is not a word-aligned SPIR-V binary");
        uint[] words = new uint[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
        if (words[0] != SpirvMagic)
            return Invalid("the module does not have the SPIR-V magic word");

        Module module;
        try
        {
            module = Module.Parse(words);
        }
        catch (InvalidDataException error)
        {
            return Invalid(error.Message);
        }

        uint expectedModel = stage == RenderPackShaderStage.Vertex
            ? VertexExecutionModel
            : FragmentExecutionModel;
        if (module.EntryPoints.Count != 1
            || module.EntryPoints[0].ExecutionModel != expectedModel
            || !string.Equals(module.EntryPoints[0].Name, "main", StringComparison.Ordinal))
        {
            return Invalid(
                $"the declared {stage.ToString().ToLowerInvariant()} asset must expose exactly "
                + "entry point 'main' for that stage");
        }

        var descriptors = new HashSet<(uint Set, uint Binding)>();
        int pushBlockCount = 0;
        foreach (Variable variable in module.Variables)
        {
            if (variable.StorageClass == StorageClass.PushConstant)
            {
                if (++pushBlockCount > 1)
                    return Invalid("the module declares more than one push-constant block");
                string? pushFailure = ValidatePushBlock(module, variable);
                if (pushFailure is not null)
                    return Invalid(pushFailure);
                continue;
            }
            if (variable.StorageClass is not StorageClass.UniformConstant
                and not StorageClass.Uniform
                and not StorageClass.StorageBuffer)
                continue;

            if (!module.DescriptorSets.TryGetValue(variable.Id, out uint set)
                || !module.Bindings.TryGetValue(variable.Id, out uint binding))
                return Invalid($"descriptor %{variable.Id} does not declare both set and binding");
            if (!descriptors.Add((set, binding)))
                return Invalid($"descriptor set {set} binding {binding} is declared more than once");

            if (set == RenderPackShaderAbi.SampledTextureDescriptorSet
                && binding == RenderPackShaderAbi.SampledTextureBinding)
            {
                if (!access.AllowSampledTable)
                    return Invalid("set 2 binding 0 is sampled without a declared semantic/resource input");
                if (!module.IsGlobalSampledTextureTable(variable))
                    return Invalid("set 2 binding 0 must be one runtime array of combined 2-D-array samplers");
                continue;
            }

            if (set == RenderPackShaderAbi.UniformDescriptorSet)
            {
                if (variable.StorageClass != StorageClass.Uniform)
                    return Invalid($"set 3 binding {binding} must be a uniform buffer");
                bool allowed = binding switch
                {
                    RenderPackShaderAbi.AtmosphericFrameBinding => access.AllowAtmosphericFrame,
                    RenderPackShaderAbi.DirectionalShadowBinding => access.AllowDirectionalShadow,
                    RenderPackShaderAbi.PackPassBinding => access.AllowPackPass,
                    RenderPackShaderAbi.PackSettingsBinding => access.AllowPackSettings,
                    _ => false,
                };
                if (!allowed)
                    return Invalid($"set 3 binding {binding} is not declared for this shader role");
                string? blockFailure = ValidatePackBlock(module, variable, binding);
                if (blockFailure is not null)
                    return Invalid(blockFailure);
                continue;
            }

            if (set == 0 && variable.StorageClass == StorageClass.StorageBuffer)
            {
                if (!access.StorageBindings.Contains(binding))
                    return Invalid($"set 0 binding {binding} storage access is not allowed for this shader role");
                if (!module.IsReadOnlyStorage(variable))
                    return Invalid($"set 0 binding {binding} is writable; render-pack storage writes are forbidden");
                if (!module.IsSingleBlockDescriptor(variable))
                    return Invalid($"set 0 binding {binding} must be one storage-buffer descriptor");
                continue;
            }

            if (set == 1 && variable.StorageClass == StorageClass.Uniform)
            {
                if (!access.UniformBindings.Contains(binding))
                    return Invalid($"set 1 binding {binding} aliases renderer state not allowed for this shader role");
                if (!module.IsSingleBlockDescriptor(variable))
                    return Invalid($"set 1 binding {binding} must be one uniform-buffer descriptor");
                continue;
            }

            return Invalid(
                $"descriptor set {set} binding {binding} has no render-pack API v1 binding");
        }

        if (module.ContainsImageWrite)
            return Invalid("storage image writes are forbidden by render-pack API v1");
        return RenderPackSpirvValidationResult.Valid();
    }

    private static string? ValidatePackBlock(Module module, Variable variable, uint binding)
    {
        if (!module.TryPointeeStruct(variable, out uint structId, out uint[] members)
            || !module.Blocks.Contains(structId))
            return $"set 3 binding {binding} must point to one std140 Block struct";

        return binding switch
        {
            RenderPackShaderAbi.AtmosphericFrameBinding =>
                ValidateAtmosphericFrame(module, structId, members),
            RenderPackShaderAbi.DirectionalShadowBinding =>
                ValidateDirectionalShadow(module, structId, members),
            RenderPackShaderAbi.PackPassBinding =>
                ValidateVec4Block(module, structId, members, 4, "PackPass"),
            RenderPackShaderAbi.PackSettingsBinding =>
                ValidatePackSettings(module, structId, members),
            _ => $"set 3 binding {binding} is reserved",
        };
    }

    private static string? ValidateAtmosphericFrame(Module module, uint id, uint[] members)
    {
        if (members.Length != 7 && members.Length != 9)
        {
            return "AtmosphericFrame must match ABI v1 (seven members, 160 bytes) or "
                + "ABI v2 (nine members, 192 bytes)";
        }
        for (int i = 0; i < 6; i++)
        {
            if (!module.IsFloatVector(members[i], 4) || module.MemberOffset(id, i) != (uint)(i * 16))
                return "AtmosphericFrame member types/offsets do not match ABI v1";
        }
        if (!module.IsFloatMatrix(members[6], 4, 4)
            || module.MemberOffset(id, 6) != 96
            || module.MemberDecoration(id, 6, Decoration.ColMajor) is null
            || module.MemberDecoration(id, 6, Decoration.MatrixStride) != 16)
            return "AtmosphericFrame inverse-view-projection layout does not match ABI v1";
        if (members.Length == 7)
            return null;

        if (!module.IsFloatVector(members[7], 4) || module.MemberOffset(id, 7) != 160)
            return "AtmosphericFrame clock/wind member does not match ABI v2";
        if (!module.IsFloatVector(members[8], 4) || module.MemberOffset(id, 8) != 176)
            return "AtmosphericFrame wind-amplitude member does not match ABI v2";
        return null;
    }

    private static string? ValidateDirectionalShadow(Module module, uint id, uint[] members)
    {
        if (members.Length != 6
            || !module.IsArray(members[0], 4, 64, static (m, t) => m.IsFloatMatrix(t, 4, 4))
            || module.MemberOffset(id, 0) != 0
            || module.MemberDecoration(id, 0, Decoration.ColMajor) is null
            || module.MemberDecoration(id, 0, Decoration.MatrixStride) != 16)
            return "DirectionalShadow matrix array does not match the 336-byte ABI v1 layout";
        for (int i = 1; i <= 3; i++)
        {
            if (!module.IsFloatVector(members[i], 4)
                || module.MemberOffset(id, i) != (uint)(240 + i * 16))
                return "DirectionalShadow vec4 member types/offsets do not match ABI v1";
        }
        if (!module.IsUIntVector(members[4], 4) || module.MemberOffset(id, 4) != 304)
            return "DirectionalShadow flags member does not match ABI v1";
        if (!module.IsFloatVector(members[5], 4) || module.MemberOffset(id, 5) != 320)
        {
            return "DirectionalShadow selected-light direction/source member does not "
                + "match the 336-byte ABI v1 layout";
        }
        return null;
    }

    private static string? ValidateVec4Block(
        Module module,
        uint id,
        uint[] members,
        int count,
        string name)
    {
        if (members.Length != count)
            return $"{name} must contain {count} vec4 members";
        for (int i = 0; i < count; i++)
        {
            if (!module.IsFloatVector(members[i], 4) || module.MemberOffset(id, i) != (uint)(i * 16))
                return $"{name} member types/offsets do not match ABI v1";
        }
        return null;
    }

    private static string? ValidatePackSettings(Module module, uint id, uint[] members)
    {
        if (members.Length != 1
            || module.MemberOffset(id, 0) != 0
            || !module.IsArray(members[0], 16, 16, static (m, t) => m.IsFloatVector(t, 4)))
            return "PackSettings must be one std140 vec4[16] block occupying 256 bytes";
        return null;
    }

    private static string? ValidatePushBlock(Module module, Variable variable)
    {
        if (!module.TryPointeeStruct(variable, out uint id, out uint[] members)
            || !module.Blocks.Contains(id)
            || members.Length != 9)
            return "the push-constant block must match the exact 96-byte retail layout";
        uint[] offsets = [0, 64, 68, 72, 76, 80, 84, 88, 92];
        for (int i = 0; i < offsets.Length; i++)
        {
            if (module.MemberOffset(id, i) != offsets[i])
                return "the push-constant member offsets do not match the exact 96-byte retail layout";
        }
        if (!module.IsFloatMatrix(members[0], 4, 4)
            || module.MemberDecoration(id, 0, Decoration.ColMajor) is null
            || module.MemberDecoration(id, 0, Decoration.MatrixStride) != 16
            || !module.IsInt(members[1], signed: true)
            || !module.IsInt(members[2], signed: true)
            || !module.IsInt(members[3], signed: true)
            || !module.IsInt(members[4], signed: true)
            || !module.IsInt(members[5], signed: false)
            || !module.IsInt(members[6], signed: false)
            || !module.IsFloat(members[7])
            || !module.IsFloat(members[8]))
            return "the push-constant member types do not match the exact 96-byte retail layout";
        return null;
    }

    private static RenderPackSpirvValidationResult Invalid(string reason) =>
        RenderPackSpirvValidationResult.Invalid(reason);

    private sealed record AllowedInterface(
        IReadOnlyList<uint> StorageBindings,
        IReadOnlyList<uint> UniformBindings,
        bool AllowSampledTable,
        bool AllowAtmosphericFrame,
        bool AllowDirectionalShadow,
        bool AllowPackPass,
        bool AllowPackSettings);

    private enum StorageClass : uint
    {
        UniformConstant = 0,
        Uniform = 2,
        PushConstant = 9,
        StorageBuffer = 12,
    }

    private enum Decoration : uint
    {
        Block = 2,
        ColMajor = 5,
        ArrayStride = 6,
        MatrixStride = 7,
        NonWritable = 24,
        Binding = 33,
        DescriptorSet = 34,
        Offset = 35,
    }

    private readonly record struct EntryPoint(uint ExecutionModel, string Name);
    private readonly record struct Variable(uint ResultType, uint Id, StorageClass StorageClass);
    private sealed record TypeInstruction(uint Opcode, uint[] Operands);

    private sealed class Module
    {
        private const uint OpEntryPoint = 15;
        private const uint OpTypeInt = 21;
        private const uint OpTypeFloat = 22;
        private const uint OpTypeVector = 23;
        private const uint OpTypeMatrix = 24;
        private const uint OpTypeImage = 25;
        private const uint OpTypeSampledImage = 27;
        private const uint OpTypeArray = 28;
        private const uint OpTypeRuntimeArray = 29;
        private const uint OpTypeStruct = 30;
        private const uint OpTypePointer = 32;
        private const uint OpConstant = 43;
        private const uint OpVariable = 59;
        private const uint OpDecorate = 71;
        private const uint OpMemberDecorate = 72;
        private const uint OpImageWrite = 99;

        internal List<EntryPoint> EntryPoints { get; } = [];
        internal List<Variable> Variables { get; } = [];
        internal Dictionary<uint, uint> DescriptorSets { get; } = [];
        internal Dictionary<uint, uint> Bindings { get; } = [];
        internal HashSet<uint> NonWritable { get; } = [];
        internal HashSet<uint> Blocks { get; } = [];
        internal bool ContainsImageWrite { get; private set; }

        private Dictionary<uint, TypeInstruction> Types { get; } = [];
        private Dictionary<uint, uint> Constants { get; } = [];
        private Dictionary<(uint Id, Decoration Decoration), uint> Decorations { get; } = [];
        private Dictionary<(uint Id, int Member, Decoration Decoration), uint> MemberDecorations { get; } = [];

        internal static Module Parse(uint[] words)
        {
            var module = new Module();
            int index = 5;
            while (index < words.Length)
            {
                uint header = words[index];
                int count = (int)(header >> 16);
                uint opcode = header & 0xffff;
                if (count <= 0 || index + count > words.Length)
                    throw new InvalidDataException($"malformed SPIR-V instruction at word {index}");
                ReadOnlySpan<uint> instruction = words.AsSpan(index, count);
                module.ReadInstruction(opcode, instruction);
                index += count;
            }
            return module;
        }

        private void ReadInstruction(uint opcode, ReadOnlySpan<uint> words)
        {
            if (opcode == OpEntryPoint)
            {
                if (words.Length < 4)
                    throw new InvalidDataException("malformed SPIR-V OpEntryPoint");
                EntryPoints.Add(new EntryPoint(words[1], ReadString(words[3..])));
            }
            else if (opcode is >= OpTypeInt and <= OpTypePointer)
            {
                if (words.Length < 2)
                    throw new InvalidDataException("malformed SPIR-V type instruction");
                Types[words[1]] = new TypeInstruction(opcode, words[1..].ToArray());
            }
            else if (opcode == OpConstant && words.Length >= 4)
            {
                Constants[words[2]] = words[3];
            }
            else if (opcode == OpVariable)
            {
                if (words.Length < 4)
                    throw new InvalidDataException("malformed SPIR-V OpVariable");
                Variables.Add(new Variable(words[1], words[2], (StorageClass)words[3]));
            }
            else if (opcode == OpDecorate)
            {
                if (words.Length < 3)
                    throw new InvalidDataException("malformed SPIR-V OpDecorate");
                Decoration decoration = (Decoration)words[2];
                uint value = words.Length >= 4 ? words[3] : 1;
                Decorations[(words[1], decoration)] = value;
                if (decoration == Decoration.DescriptorSet) DescriptorSets[words[1]] = value;
                if (decoration == Decoration.Binding) Bindings[words[1]] = value;
                if (decoration == Decoration.NonWritable) NonWritable.Add(words[1]);
                if (decoration == Decoration.Block) Blocks.Add(words[1]);
            }
            else if (opcode == OpMemberDecorate)
            {
                if (words.Length < 4)
                    throw new InvalidDataException("malformed SPIR-V OpMemberDecorate");
                Decoration decoration = (Decoration)words[3];
                MemberDecorations[(words[1], checked((int)words[2]), decoration)] =
                    words.Length >= 5 ? words[4] : 1;
            }
            else if (opcode == OpImageWrite)
            {
                ContainsImageWrite = true;
            }
        }

        private static string ReadString(ReadOnlySpan<uint> words)
        {
            var bytes = new List<byte>(words.Length * 4);
            foreach (uint word in words)
            {
                for (int shift = 0; shift < 32; shift += 8)
                {
                    byte value = (byte)(word >> shift);
                    if (value == 0)
                        return System.Text.Encoding.UTF8.GetString([.. bytes]);
                    bytes.Add(value);
                }
            }
            throw new InvalidDataException("unterminated SPIR-V string");
        }

        internal bool IsSingleBlockDescriptor(Variable variable) =>
            TryPointeeStruct(variable, out uint id, out _) && Blocks.Contains(id);

        internal bool IsReadOnlyStorage(Variable variable)
        {
            if (NonWritable.Contains(variable.Id))
                return true;
            return TryPointeeStruct(variable, out uint id, out uint[] members)
                && members.Length != 0
                && Enumerable.Range(0, members.Length).All(member =>
                    MemberDecoration(id, member, Decoration.NonWritable) is not null);
        }

        internal bool TryPointeeStruct(Variable variable, out uint id, out uint[] members)
        {
            id = 0;
            members = [];
            if (!Types.TryGetValue(variable.ResultType, out TypeInstruction? pointer)
                || pointer.Opcode != OpTypePointer
                || pointer.Operands.Length < 3)
                return false;
            id = pointer.Operands[2];
            if (!Types.TryGetValue(id, out TypeInstruction? structure)
                || structure.Opcode != OpTypeStruct)
                return false;
            members = structure.Operands[1..];
            return true;
        }

        internal bool IsGlobalSampledTextureTable(Variable variable)
        {
            if (!TryPointee(variable.ResultType, out uint arrayId)
                || !Types.TryGetValue(arrayId, out TypeInstruction? array)
                || array.Opcode != OpTypeRuntimeArray
                || array.Operands.Length != 2
                || !Types.TryGetValue(array.Operands[1], out TypeInstruction? sampled)
                || sampled.Opcode != OpTypeSampledImage
                || sampled.Operands.Length != 2
                || !Types.TryGetValue(sampled.Operands[1], out TypeInstruction? image)
                || image.Opcode != OpTypeImage
                || image.Operands.Length < 8)
                return false;
            // Dim=2D (1), Arrayed=true, Sampled=image used with a sampler (1).
            return image.Operands[2] == 1 && image.Operands[4] == 1 && image.Operands[6] == 1;
        }

        private bool TryPointee(uint pointerId, out uint pointee)
        {
            pointee = 0;
            if (!Types.TryGetValue(pointerId, out TypeInstruction? pointer)
                || pointer.Opcode != OpTypePointer
                || pointer.Operands.Length < 3)
                return false;
            pointee = pointer.Operands[2];
            return true;
        }

        internal uint? MemberOffset(uint id, int member) =>
            MemberDecoration(id, member, Decoration.Offset);

        internal uint? MemberDecoration(uint id, int member, Decoration decoration) =>
            MemberDecorations.TryGetValue((id, member, decoration), out uint value) ? value : null;

        internal bool IsFloat(uint id) => IsScalar(id, OpTypeFloat, 32, signed: null);
        internal bool IsInt(uint id, bool signed) => IsScalar(id, OpTypeInt, 32, signed);

        private bool IsScalar(uint id, uint opcode, uint width, bool? signed)
        {
            if (!Types.TryGetValue(id, out TypeInstruction? type)
                || type.Opcode != opcode
                || type.Operands.Length < 2
                || type.Operands[1] != width)
                return false;
            return signed is null
                || type.Operands.Length >= 3 && type.Operands[2] == (signed.Value ? 1u : 0u);
        }

        internal bool IsFloatVector(uint id, uint count) =>
            IsVector(id, count, static (m, t) => m.IsFloat(t));

        internal bool IsUIntVector(uint id, uint count) =>
            IsVector(id, count, static (m, t) => m.IsInt(t, signed: false));

        private bool IsVector(uint id, uint count, Func<Module, uint, bool> element)
        {
            return Types.TryGetValue(id, out TypeInstruction? vector)
                && vector.Opcode == OpTypeVector
                && vector.Operands.Length == 3
                && vector.Operands[2] == count
                && element(this, vector.Operands[1]);
        }

        internal bool IsFloatMatrix(uint id, uint rows, uint columns)
        {
            return Types.TryGetValue(id, out TypeInstruction? matrix)
                && matrix.Opcode == OpTypeMatrix
                && matrix.Operands.Length == 3
                && matrix.Operands[2] == columns
                && IsFloatVector(matrix.Operands[1], rows);
        }

        internal bool IsArray(
            uint id,
            uint length,
            uint stride,
            Func<Module, uint, bool> element)
        {
            return Types.TryGetValue(id, out TypeInstruction? array)
                && array.Opcode == OpTypeArray
                && array.Operands.Length == 3
                && Constants.TryGetValue(array.Operands[2], out uint actualLength)
                && actualLength == length
                && Decorations.TryGetValue((id, Decoration.ArrayStride), out uint actualStride)
                && actualStride == stride
                && element(this, array.Operands[1]);
        }
    }
}
