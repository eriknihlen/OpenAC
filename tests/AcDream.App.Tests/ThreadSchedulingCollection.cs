namespace AcDream.App.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadSchedulingCollection
{
    public const string Name = "Dedicated thread scheduling";
}
