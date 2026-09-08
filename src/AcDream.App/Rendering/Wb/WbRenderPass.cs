namespace AcDream.App.Rendering.Wb;

public enum WbRenderPass
{
    /// <summary>
    /// The opaque pass. Only non-transparent objects are rendered.
    /// </summary>
    Opaque = 0,

    /// <summary>
    /// The transparent pass. Only transparent objects are rendered,
    /// usually after the opaque pass.
    /// </summary>
    Transparent = 1,

    SinglePass = 2,
}
