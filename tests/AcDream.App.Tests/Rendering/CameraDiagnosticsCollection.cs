using Xunit;

namespace AcDream.App.Tests.Rendering;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CameraDiagnosticsCollection
{
    public const string Name = "Camera diagnostics globals";
}
