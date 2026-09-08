using Xunit;

namespace AcDream.Core.Tests.World;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DerethDateTimeCollection
{
    public const string Name = "DerethDateTime global offset";
}
