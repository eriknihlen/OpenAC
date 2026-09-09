using System.Text;

namespace AcDream.Tools.ShaderCompiler;

internal static class VulkanGlslPreamble
{
    internal static IReadOnlyList<string> PushConstantFields { get; } =
    [
        "uViewProjection",
        "uDrawIDOffset",
        "uLightingMode",
        "uRenderPass",
        "uLightDebug",
        "uTextureIndexA",
        "uTextureIndexB",
        "uParamA",
        "uParamB",
        "uTextureIndexC",
        "uTextureIndexD",
    ];

    /// <summary>
    /// Builds the text inserted after the <c>#version</c> directive.
    /// <paramref name="stage"/> only affects which stage-specific rewrites are
    /// emitted.
    /// </summary>
    internal static string Build(string stage) =>
        Build(stage, includePackUniformSet: false, includePackTextureIndices: false);

    private static string Build(
        string stage,
        bool includePackUniformSet,
        bool includePackTextureIndices)
    {
        var text = new StringBuilder();
        text.AppendLine("// ---- injected by tools/compile-shaders.ps1 ----");
        text.AppendLine("// Vulkan descriptor bindings and shader input compatibility.");
        text.AppendLine("#extension GL_EXT_nonuniform_qualifier : require");
        if (stage == "vert")
        {
            // gl_DrawID is Vulkan's shaderDrawParameters feature, and glslang
            // still gates the identifier behind the ARB extension name even when
            // targeting Vulkan. Declared here so a source that does not name it
            // still gets it.
            text.AppendLine("#extension GL_ARB_shader_draw_parameters : require");
        }

        text.AppendLine();
        text.AppendLine("// Set 1: uniform buffers.");
        text.AppendLine("#undef ACDREAM_UBO_SET");
        text.AppendLine("#define ACDREAM_UBO_SET set = 1,");
        if (includePackUniformSet)
        {
            text.AppendLine("#undef ACDREAM_PACK_UBO_SET");
            text.AppendLine("#define ACDREAM_PACK_UBO_SET set = 3,");
        }
        text.AppendLine();
        text.AppendLine("// Set 2: the global sampled-texture table that replaces");
        text.AppendLine("// GL_ARB_bindless_texture. Variable count, partially bound,");
        text.AppendLine("// update-after-bind; the CPU never writes it per frame.");
        text.AppendLine("layout(set = 2, binding = 0) uniform sampler2DArray uTextures[];");
        text.AppendLine("#undef ACDREAM_TEXTURE_HANDLE");
        text.AppendLine("#define ACDREAM_TEXTURE_HANDLE(idx) (idx)");
        text.AppendLine("#define ACDREAM_TEXTURE(idx) uTextures[nonuniformEXT(uint(idx))]");
        text.AppendLine(
            "#define ACDREAM_SAMPLE_2D(idx, uv) texture(ACDREAM_TEXTURE(idx), vec3((uv), 0.0))");
        text.AppendLine(
            "#define ACDREAM_SAMPLE_ARRAY(idx, uvw) texture(ACDREAM_TEXTURE(idx), uvw)");
        text.AppendLine("#define ACDREAM_TEXTURE_NONE 0xFFFFFFFFu");
        text.AppendLine();
        text.AppendLine("// Push constants: one shared 96-byte block, so switching pipelines");
        text.AppendLine("// mid-pass invalidates neither descriptors nor constants.");
        text.AppendLine("layout(push_constant) uniform AcdreamPushBlock {");
        text.AppendLine("    mat4 viewProjection;");
        text.AppendLine("    int drawIdOffset;");
        text.AppendLine("    int lightingMode;");
        text.AppendLine("    int renderPass;");
        text.AppendLine("    int lightDebug;");
        text.AppendLine("    uint textureIndexA;");
        text.AppendLine("    uint textureIndexB;");
        text.AppendLine("    float paramA;");
        text.AppendLine("    float paramB;");
        text.AppendLine("} acdreamPush;");
        text.AppendLine();
        text.AppendLine("#define uViewProjection acdreamPush.viewProjection");
        text.AppendLine("#define uDrawIDOffset   acdreamPush.drawIdOffset");
        text.AppendLine("#define uLightingMode   acdreamPush.lightingMode");
        text.AppendLine("#define uRenderPass     acdreamPush.renderPass");
        text.AppendLine("#define uLightDebug     acdreamPush.lightDebug");
        text.AppendLine("#define uTextureIndexA  acdreamPush.textureIndexA");
        text.AppendLine("#define uTextureIndexB  acdreamPush.textureIndexB");
        text.AppendLine("#define uParamA         acdreamPush.paramA");
        text.AppendLine("#define uParamB         acdreamPush.paramB");
        if (includePackTextureIndices)
        {
            text.AppendLine("#define uTextureIndexC  floatBitsToUint(acdreamPush.paramA)");
            text.AppendLine("#define uTextureIndexD  floatBitsToUint(acdreamPush.paramB)");
        }
        text.AppendLine();
        text.AppendLine("// gl_DrawIDARB stays as written — glslang exposes it for Vulkan");
        text.AppendLine("// under the same ARB extension name. gl_InstanceIndex already includes");
        text.AppendLine("// firstInstance, so the GL idiom gl_BaseInstanceARB + gl_InstanceID");
        text.AppendLine("// collapses to it exactly.");
        text.AppendLine(stage == "vert"
            ? "#define gl_BaseInstanceARB 0\n"
                + "#define gl_InstanceID gl_InstanceIndex\n"
                + "#define gl_VertexID gl_VertexIndex"
            : "// (the vertex/instance-index rewrites apply to the vertex stage only)");
        text.AppendLine("// ---- end injected preamble ----");
        return text.ToString();
    }

    /// <summary>
    /// Returns <paramref name="source"/> with the preamble inserted after its
    /// <c>#version</c> line and the version raised to 450, which is the floor for
    /// Vulkan GLSL. Everything else is untouched: this never edits a shader body.
    /// </summary>
    internal static string Apply(string source, string stage)
    {
        ArgumentNullException.ThrowIfNull(source);
        bool includePackUniformSet = source.Contains("ACDREAM_PACK_UBO_SET", StringComparison.Ordinal);
        bool includePackTextureIndices =
            source.Contains("uTextureIndexC", StringComparison.Ordinal)
            || source.Contains("uTextureIndexD", StringComparison.Ordinal);
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder();
        bool injected = false;

        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (!injected && trimmed.StartsWith("#version", StringComparison.Ordinal))
            {
                output.AppendLine(HighestVersion(trimmed) >= 460 ? "#version 460 core" : "#version 450 core");
                output.Append(Build(stage, includePackUniformSet, includePackTextureIndices));
                injected = true;
                continue;
            }

            // Bindless textures are what the set-2 descriptor array replaces;
            // requiring the extension under Vulkan is an error rather than a
            // no-op. (GL_ARB_shader_draw_parameters is kept — glslang still gates
            // gl_DrawID behind that name when targeting Vulkan.)
            if (trimmed.StartsWith("#extension GL_ARB_bindless_texture", StringComparison.Ordinal))
            {
                output.AppendLine($"// (dropped for Vulkan: {trimmed})");
                continue;
            }

            if (IsDefaultBlockUniformDeclaration(trimmed))
            {
                output.AppendLine($"// (declaration dropped for Vulkan: {trimmed})");
                continue;
            }

            output.AppendLine(line);
        }

        if (!injected)
        {
            throw new InvalidOperationException(
                "The shader has no #version directive, so there is nowhere to inject the Vulkan preamble.");
        }

        return output.ToString();
    }

    internal static int HighestVersion(string versionDirective)
    {
        string[] parts = versionDirective.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out int version) ? version : 450;
    }

    /// <summary>
    /// True for a loose (default-block) uniform declaration —
    /// <c>uniform mat4 uViewProjection;</c> or <c>uniform float uTexTiling[36];</c> —
    /// and false for a uniform BLOCK, which opens a brace and is legal in both
    /// dialects.
    /// </summary>
    internal static bool IsDefaultBlockUniformDeclaration(string trimmedLine)
    {
        ArgumentNullException.ThrowIfNull(trimmedLine);
        if (!trimmedLine.StartsWith("uniform ", StringComparison.Ordinal))
            return false;

        int comment = trimmedLine.IndexOf("//", StringComparison.Ordinal);
        string code = comment >= 0 ? trimmedLine[..comment] : trimmedLine;
        return !code.Contains('{') && code.TrimEnd().EndsWith(';');
    }
}
