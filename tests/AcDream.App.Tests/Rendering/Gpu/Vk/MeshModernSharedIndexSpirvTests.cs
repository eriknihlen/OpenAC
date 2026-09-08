namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class MeshModernSharedIndexSpirvTests
{
    private const ushort OpConstant = 43;
    private const ushort OpVariable = 59;
    private const ushort OpLoad = 61;
    private const ushort OpAccessChain = 65;
    private const ushort OpDecorate = 71;
    private const ushort OpBitcast = 124;
    private const ushort OpIAdd = 128;
    private const ushort OpISub = 130;
    private const ushort OpIMul = 132;
    private const ushort OpPhi = 245;

    private const uint StorageClassInput = 1;
    private const uint StorageClassPushConstant = 9;
    private const uint DecorationBuiltIn = 11;
    private const uint DecorationBinding = 33;
    private const uint DecorationDescriptorSet = 34;
    private const uint BuiltInInstanceIndex = 43;

    [Fact]
    public void ProductionModule_UsesAbsoluteTransformAndLocalOrdinarySidecars()
    {
        string path = Path.Combine(
            RepositoryRoot(),
            "src", "AcDream.App", "Rendering", "Shaders", "spv", "mesh_modern.vert.spv");
        Spirv module = Spirv.Read(path);

        uint instanceInput = Assert.Single(
            module.Variables,
            variable => variable.Value == StorageClassInput
                && module.Decoration(variable.Key, DecorationBuiltIn) == BuiltInInstanceIndex).Key;
        uint absoluteIndex = Assert.Single(
            module.Instructions,
            instruction => instruction.OpCode == OpLoad && instruction.Operands[0] == instanceInput).ResultId;

        uint pushBlock = Assert.Single(
            module.Variables,
            variable => variable.Value == StorageClassPushConstant).Key;
        uint prefixPointer = Assert.Single(
            module.Instructions,
            instruction => instruction.OpCode == OpAccessChain
                && instruction.Operands[0] == pushBlock
                && instruction.Operands.Skip(1).Any(id => module.Constant(id) == 6u)).ResultId;
        uint prefixUnsigned = Assert.Single(
            module.Instructions,
            instruction => instruction.OpCode == OpLoad
                && instruction.Operands[0] == prefixPointer).ResultId;
        uint prefixSigned = Assert.Single(
            module.Instructions,
            instruction => instruction.OpCode == OpBitcast
                && instruction.Operands[0] == prefixUnsigned).ResultId;
        Instruction subtraction = Assert.Single(
            module.Instructions,
            instruction => instruction.OpCode == OpISub
                && instruction.Operands.SequenceEqual([absoluteIndex, prefixSigned]));
        uint localIndex = subtraction.ResultId;

        Instruction transformLookup = Assert.Single(module.RootAccessChains(set: 0, binding: 0));
        Assert.Contains(absoluteIndex, transformLookup.Operands.Skip(1));
        Assert.DoesNotContain(localIndex, transformLookup.Operands.Skip(1));

        foreach (uint sidecarBinding in new uint[] { 5, 6, 7, 8, 9 })
        {
            Instruction[] lookups = module.RootAccessChains(set: 0, binding: sidecarBinding).ToArray();
            Assert.NotEmpty(lookups);
            Assert.All(
                lookups,
                lookup => Assert.Contains(
                    lookup.Operands.Skip(1),
                    index => module.DependsOn(index, localIndex)));
        }
    }

    private readonly record struct Instruction(ushort OpCode, uint ResultId, uint[] Operands);

    private sealed class Spirv
    {
        private readonly Dictionary<uint, Dictionary<uint, uint>> _decorations = [];
        private readonly Dictionary<uint, uint> _constants = [];
        private readonly Dictionary<uint, Instruction> _results = [];

        private Spirv()
        {
        }

        internal Dictionary<uint, uint> Variables { get; } = [];

        internal List<Instruction> Instructions { get; } = [];

        internal static Spirv Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 20 && bytes.Length % sizeof(uint) == 0, $"{path} is not SPIR-V.");
            uint[] words = new uint[bytes.Length / sizeof(uint)];
            Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
            Assert.Equal(0x07230203u, words[0]);

            var module = new Spirv();
            for (int index = 5; index < words.Length;)
            {
                int wordCount = checked((int)(words[index] >> 16));
                ushort opCode = checked((ushort)(words[index] & 0xFFFFu));
                Assert.True(wordCount > 0 && index + wordCount <= words.Length);

                if (opCode == OpDecorate)
                {
                    uint target = words[index + 1];
                    uint decoration = words[index + 2];
                    if (wordCount >= 4)
                    {
                        if (!module._decorations.TryGetValue(target, out Dictionary<uint, uint>? values))
                            module._decorations[target] = values = [];
                        values[decoration] = words[index + 3];
                    }
                }
                else if (opCode == OpConstant && wordCount >= 4)
                {
                    module._constants[words[index + 2]] = words[index + 3];
                }
                else if (opCode == OpVariable && wordCount >= 4)
                {
                    module.Variables[words[index + 2]] = words[index + 3];
                }

                if (TryReadResult(opCode, words.AsSpan(index, wordCount), out Instruction instruction))
                {
                    module.Instructions.Add(instruction);
                    module._results[instruction.ResultId] = instruction;
                }

                index += wordCount;
            }
            return module;
        }

        internal uint? Decoration(uint id, uint decoration) =>
            _decorations.TryGetValue(id, out Dictionary<uint, uint>? values)
            && values.TryGetValue(decoration, out uint value)
                ? value
                : null;

        internal uint? Constant(uint id) => _constants.TryGetValue(id, out uint value) ? value : null;

        internal IEnumerable<Instruction> RootAccessChains(uint set, uint binding)
        {
            HashSet<uint> roots = Variables.Keys
                .Where(id => Decoration(id, DecorationDescriptorSet) == set)
                .Where(id => Decoration(id, DecorationBinding) == binding)
                .ToHashSet();
            Assert.Single(roots);
            return Instructions.Where(instruction =>
                instruction.OpCode == OpAccessChain && roots.Contains(instruction.Operands[0]));
        }

        internal bool DependsOn(uint value, uint dependency)
        {
            if (value == dependency)
                return true;
            var visited = new HashSet<uint>();
            return Visit(value);

            bool Visit(uint current)
            {
                if (!visited.Add(current) || !_results.TryGetValue(current, out Instruction instruction))
                    return false;
                foreach (uint operand in instruction.Operands)
                {
                    if (operand == dependency || Visit(operand))
                        return true;
                }
                return false;
            }
        }

        private static bool TryReadResult(
            ushort opCode,
            ReadOnlySpan<uint> words,
            out Instruction instruction)
        {
            switch (opCode)
            {
                case OpLoad:
                case OpAccessChain:
                case OpBitcast:
                case OpIAdd:
                case OpISub:
                case OpIMul:
                case OpPhi:
                    instruction = new Instruction(opCode, words[2], words[3..].ToArray());
                    return true;
                default:
                    instruction = default;
                    return false;
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
