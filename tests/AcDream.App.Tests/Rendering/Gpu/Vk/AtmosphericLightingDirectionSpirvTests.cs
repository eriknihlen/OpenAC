using System.Security.Cryptography;
using System.Text;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class AtmosphericLightingDirectionSpirvTests
{
    private const ushort OpExtInst = 12;
    private const ushort OpConstant = 43;
    private const ushort OpVariable = 59;
    private const ushort OpLoad = 61;
    private const ushort OpAccessChain = 65;
    private const ushort OpVectorShuffle = 79;
    private const ushort OpDecorate = 71;
    private const ushort OpFNegate = 127;
    private const ushort OpDot = 148;

    private const uint DecorationBinding = 33;
    private const uint DecorationDescriptorSet = 34;
    private const uint SceneLightingSet = 1;
    private const uint SceneLightingBinding = 1;
    private const uint PackUniformSet = 3;
    private const uint DirectionalShadowBinding = 6;

    [Theory]
    [InlineData("mesh_atmospheric.vert.spv", false)]
    [InlineData("terrain_atmospheric.vert.spv", true)]
    public void ProductionVertexModule_DotsTheNegatedAuthoredDirectionWithoutNormalization(
        string fileName,
        bool terrain)
    {
        Spirv module = Spirv.Read(Shader(fileName));

        Assert.Empty(module.RootVariables(PackUniformSet, DirectionalShadowBinding));
        uint sceneLighting = Assert.Single(module.RootVariables(SceneLightingSet, SceneLightingBinding));
        Instruction direction = Assert.Single(module.Instructions, instruction =>
            instruction.OpCode == OpAccessChain
            && instruction.Operands[0] == sceneLighting
            && IsAuthoredDirectionPath(module, instruction, terrain));
        Instruction loaded = Assert.Single(module.WithOperand(OpLoad, direction.ResultId));
        Instruction xyz = Assert.Single(module.WithOperand(OpVectorShuffle, loaded.ResultId));
        Instruction negated = Assert.Single(module.WithOperand(OpFNegate, xyz.ResultId));
        Instruction dot = Assert.Single(module.WithOperand(OpDot, negated.ResultId));

        Assert.Equal(negated.ResultId, dot.Operands[1]);
    }

    [Theory]
    [InlineData("mesh_atmospheric.frag.spv")]
    [InlineData("terrain_atmospheric.frag.spv")]
    [InlineData("atmospheric_volumetric.frag.spv")]
    public void ProductionShadowAndVolumetricModules_StillNormalizeTheCelestialProjectionDirection(
        string fileName)
    {
        Spirv module = Spirv.Read(Shader(fileName));
        uint shadowBlock = Assert.Single(module.RootVariables(PackUniformSet, DirectionalShadowBinding));
        Instruction direction = Assert.Single(module.Instructions, instruction =>
            instruction.OpCode == OpAccessChain
            && instruction.Operands[0] == shadowBlock
            && instruction.Operands.Length == 2
            && module.Constant(instruction.Operands[1]) == 5u);
        Instruction loaded = Assert.Single(module.WithOperand(OpLoad, direction.ResultId));
        Instruction xyz = Assert.Single(module.WithOperand(OpVectorShuffle, loaded.ResultId));

        Assert.Contains(
            module.Instructions,
            instruction => instruction.OpCode == OpExtInst
                && instruction.Operands.Contains(xyz.ResultId));
    }

    [Fact]
    public void ParentPlainMeshVertexCarriesReviewed478StrideAmendmentAndCompiledModulesRemainByteExact()
    {
        AssertBinaryHash(
            "9909ca4729dbe4fc7fbffb11c73481977d8f593d37f130fcdd44a7803b47944f",
            "src", "AcDream.App", "Rendering", "Shaders", "spv", "mesh_modern.vert.spv");
        AssertBinaryHash(
            "bd47fe8a33e0f1d025fe48fd95b034ba6fe591a6d20e88e636c86238d8773db1",
            "src", "AcDream.App", "Rendering", "Shaders", "spv", "mesh_modern.frag.spv");
        AssertBinaryHash(
            "8a73d89ef0e51e550327b9ff8c24857e309103b1d491030cf0d4d8594b45068c",
            "src", "AcDream.App", "Rendering", "Shaders", "spv", "terrain_modern.vert.spv");
        AssertBinaryHash(
            "7b3cdb01b837ed77ee20559a81c1ce5c9d5395300efcc072560ab0be3c5a1af9",
            "src", "AcDream.App", "Rendering", "Shaders", "spv", "terrain_modern.frag.spv");
    }

    private static bool IsAuthoredDirectionPath(
        Spirv module,
        in Instruction instruction,
        bool terrain)
    {
        if (instruction.Operands.Length != 4
            || module.Constant(instruction.Operands[1]) != 0u
            || module.Constant(instruction.Operands[3]) != 1u)
            return false;

        return !terrain || module.Constant(instruction.Operands[2]) == 0u;
    }

    private static void AssertBinaryHash(string expected, params string[] relativePath) =>
        Assert.Equal(
            expected,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine([RepositoryRoot(), .. relativePath])))).ToLowerInvariant());

    private static string Shader(string fileName) => Path.Combine(
        RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", "spv", fileName);

    private readonly record struct Instruction(ushort OpCode, uint ResultId, uint[] Operands);

    private sealed class Spirv
    {
        private readonly Dictionary<uint, Dictionary<uint, uint>> _decorations = [];
        private readonly Dictionary<uint, uint> _constants = [];

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

                if (opCode == OpDecorate && wordCount >= 4)
                {
                    uint target = words[index + 1];
                    if (!module._decorations.TryGetValue(target, out Dictionary<uint, uint>? values))
                        module._decorations[target] = values = [];
                    values[words[index + 2]] = words[index + 3];
                }
                else if (opCode == OpConstant && wordCount >= 4)
                {
                    module._constants[words[index + 2]] = words[index + 3];
                }
                else if (opCode == OpVariable && wordCount >= 4)
                {
                    module.Variables[words[index + 2]] = words[index + 3];
                }

                if (HasResult(opCode))
                {
                    module.Instructions.Add(new Instruction(
                        opCode,
                        words[index + 2],
                        words.AsSpan(index + 3, wordCount - 3).ToArray()));
                }
                index += wordCount;
            }
            return module;
        }

        internal uint? Constant(uint id) => _constants.TryGetValue(id, out uint value) ? value : null;

        internal IEnumerable<uint> RootVariables(uint set, uint binding) => Variables.Keys
            .Where(id => Decoration(id, DecorationDescriptorSet) == set)
            .Where(id => Decoration(id, DecorationBinding) == binding);

        internal IEnumerable<Instruction> WithOperand(ushort opCode, uint operand) =>
            Instructions.Where(instruction => instruction.OpCode == opCode
                && instruction.Operands.Contains(operand));

        private uint? Decoration(uint id, uint decoration) =>
            _decorations.TryGetValue(id, out Dictionary<uint, uint>? values)
            && values.TryGetValue(decoration, out uint value)
                ? value
                : null;

        private static bool HasResult(ushort opCode) => opCode is
            OpExtInst or OpLoad or OpAccessChain or OpVectorShuffle or OpFNegate or OpDot;
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
