using AcDream.App.Rendering;

namespace AcDream.App.Rendering.Wb;

internal interface IMeshPipelineDevice : IDisposable
{
    /// <summary>Frame-flight-gated release for everything the pipeline allocates.</summary>
    IGpuResourceRetirementQueue ResourceRetirement { get; }

    /// <summary>The shared per-instance attribute buffer the legacy draw path binds.</summary>
    uint InstanceVBO { get; }

    /// <summary><c>GL_ARB_bindless_texture</c>. Half of the modern-path gate.</summary>
    bool HasBindless { get; }

    /// <summary>GL 4.3 or better. The other half of the modern-path gate.</summary>
    bool HasOpenGL43 { get; }

    /// <summary>True while deferred device work is still queued.</summary>
    bool HasPendingWork { get; }

    void ProcessQueue();
}
