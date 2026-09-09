using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal readonly record struct RenderPackTextureInput(
    RenderSemanticInput? Semantic,
    string? ResourceId)
{
    internal static RenderPackTextureInput FromSemantic(RenderSemanticInput value) =>
        new(value, null);

    internal static RenderPackTextureInput FromResource(string value) =>
        new(null, value);
}

internal static class RenderPackTextureBindingResolver
{
    internal static IReadOnlyList<RenderPackTextureInput> Resolve(
        RenderPassDeclaration pass,
        IReadOnlyDictionary<string, RenderResourceDeclaration> resources)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(resources);
        var result = new List<RenderPackTextureInput>(4);
        foreach (RenderSemanticInput semantic in pass.SemanticInputs)
        {
            if (semantic is RenderSemanticInput.WorldColor
                or RenderSemanticInput.SceneDepth
                or RenderSemanticInput.SceneNormals)
                result.Add(RenderPackTextureInput.FromSemantic(semantic));
        }
        foreach (string resourceId in pass.ResourceReads)
        {
            if (!resources.TryGetValue(resourceId, out RenderResourceDeclaration? resource))
                throw new InvalidOperationException($"Unknown render-pack resource '{resourceId}'.");
            if (resource.Format == RenderFormatClass.DirectionalDepth
                && pass.SemanticInputs.Contains(RenderSemanticInput.DirectionalShadowMaps))
                continue;
            result.Add(RenderPackTextureInput.FromResource(resourceId));
        }
        if (result.Count > 4)
        {
            throw new InvalidOperationException(
                $"Render-pack pass '{pass.Id}' exceeds the four API-v1 texture slots.");
        }
        return result;
    }
}
