using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AcDream.App.Tests.Rendering;

public sealed class SkyParamsLayoutTests
{
    private static Type SkyParamsType =>
        typeof(AcDream.App.Rendering.Sky.SkyRenderer)
            .GetNestedType("SkyParams", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SkyRenderer.SkyParams is missing.");

    [Theory]
    // Three transforms first: mat4 is 4 vec4s in std140, so these need no thought.
    [InlineData("Model", 0)]
    [InlineData("SkyView", 64)]
    [InlineData("SkyProjection", 128)]
    [InlineData("AmbientColor", 192)]
    [InlineData("Emissive", 204)]
    [InlineData("SunColor", 208)]
    [InlineData("DiffuseFactor", 220)]
    [InlineData("SunDir", 224)]
    [InlineData("Transparency", 236)]
    // Finally a vec2 (8-byte aligned) and the last two scalars, filling the
    // sixteenth vec4 exactly.
    [InlineData("UvScroll", 240)]
    [InlineData("ApplyFog", 248)]
    [InlineData("SurfOpacity", 252)]
    public void EveryMemberSitsWhereStd140PutsIt(string member, int expectedOffset)
    {
        Assert.Equal(
            new IntPtr(expectedOffset),
            Marshal.OffsetOf(SkyParamsType, member));
    }

    [Fact]
    public void TheBlockIsAWholeNumberOfVec4s()
    {
        int declared = (int)(SkyParamsType
            .GetField("SizeInBytes", BindingFlags.Public | BindingFlags.Static)
            ?.GetRawConstantValue()
            ?? throw new InvalidOperationException("SkyParams.SizeInBytes is missing."));

        Assert.Equal(256, declared);
        Assert.Equal(declared, Marshal.SizeOf(SkyParamsType));
        // std140 rounds a block up to its largest member's alignment (16).
        Assert.Equal(0, declared % 16);
    }

    [Fact]
    public void BothStagesDeclareTheBlockIdentically()
    {
        string shaders = Path.Combine(AppContext.BaseDirectory, "Rendering", "Shaders");
        string vertexBlock = ExtractSkyParamsBlock(File.ReadAllText(Path.Combine(shaders, "sky.vert")));
        string fragmentBlock = ExtractSkyParamsBlock(File.ReadAllText(Path.Combine(shaders, "sky.frag")));

        Assert.Equal(vertexBlock, fragmentBlock);

        // And the binding must be the one the CPU binds the buffer to.
        Assert.Equal(4u, AcDream.App.Rendering.Gpu.GpuBindingModel.UniformSkyParams);
        Assert.Contains("binding = 4) uniform SkyParams {", vertexBlock);
    }

    private static string ExtractSkyParamsBlock(string source)
    {
        int start = source.IndexOf("layout(std140", StringComparison.Ordinal);
        while (start >= 0)
        {
            int end = source.IndexOf("};", start, StringComparison.Ordinal);
            Assert.True(end > start, "A layout(std140 …) block was never closed.");
            string block = source[start..(end + 2)];
            if (block.Contains("uniform SkyParams", StringComparison.Ordinal))
                return Normalise(block);
            start = source.IndexOf("layout(std140", end, StringComparison.Ordinal);
        }

        throw new InvalidOperationException("No SkyParams block found in the shader source.");
    }

    private static string Normalise(string block)
    {
        var text = new System.Text.StringBuilder();
        foreach (string rawLine in block.Replace("\r\n", "\n").Split('\n'))
        {
            int comment = rawLine.IndexOf("//", StringComparison.Ordinal);
            string line = comment >= 0 ? rawLine[..comment] : rawLine;
            string collapsed = string.Join(' ', line.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (collapsed.Length > 0)
                text.Append(collapsed).Append('\n');
        }

        return text.ToString();
    }
}
