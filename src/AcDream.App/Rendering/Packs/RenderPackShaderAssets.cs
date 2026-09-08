using System.Collections.Immutable;
using AcDream.App.Rendering.Gpu;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal static class RenderPackShaderAssets
{
    internal static ValidatedRenderPackShaderAssets Validate(
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets)
    {
        RenderPackValidationResult result = RenderPackValidator.ValidateSelectedAssets(
            descriptor,
            assets,
            out ValidatedRenderPackShaderAssets? validated);
        if (!result.Success)
            throw new InvalidDataException(result.Reason);
        return validated!;
    }

    internal static GpuShaderSet LoadPass(
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        RenderPassDeclaration pass) => new(
        $"{descriptor.Id}:{pass.Id}",
        assets.Copy(pass.VertexShaderAsset),
        assets.Copy(pass.FragmentShaderAsset));

    internal static GpuShaderSet LoadVariant(
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        PipelineVariantDeclaration variant) => new(
        $"{descriptor.Id}:{variant.Id}",
        assets.Copy(variant.VertexShaderAsset),
        assets.Copy(variant.FragmentShaderAsset));
}

internal sealed class ValidatedRenderPackShaderAssets
{
    private readonly IReadOnlyDictionary<string, ImmutableArray<byte>> _assets;

    internal ValidatedRenderPackShaderAssets(
        IReadOnlyDictionary<string, byte[]> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var owned = new Dictionary<string, ImmutableArray<byte>>(
            assets.Count,
            StringComparer.Ordinal);
        foreach ((string key, byte[] bytes) in assets)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(bytes);
            owned.Add(key, [.. bytes]);
        }
        _assets = owned;
    }

    internal byte[] Copy(string key)
    {
        if (!_assets.TryGetValue(key, out ImmutableArray<byte> bytes))
            throw new InvalidDataException($"Validated render-pack shader '{key}' is missing.");
        return [.. bytes];
    }
}
