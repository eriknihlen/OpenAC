using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class PluginLootClassifierRegistryTests
{
    [Fact]
    public void RegistrationClassifiesAndDisposalRemovesEntry()
    {
        var registry = new PluginLootClassifierRegistry();
        var classifier = new Classifier();
        IDisposable registration = registry.Register(
            "test/rules",
            "Test Rules",
            classifier);
        var context = new PluginLootClassificationContext(
            default,
            default,
            []);

        Assert.Equal("test/rules", Assert.Single(registry.Available).Id);
        Assert.True(registry.TryClassify(
            "TEST/RULES",
            context,
            out PluginLootClassification classification));
        Assert.True(classification.Matched);
        Assert.Equal(PluginLootAction.Keep, classification.Action);
        Assert.True(registry.TryNotifyLooted(
            "test/rules",
            new PluginLootedItem(default, PluginLootAction.User1)));
        Assert.True(registry.TryNotifyItemRemoved("test/rules", 42u));
        Assert.Equal(PluginLootAction.User1, Assert.Single(classifier.Looted).Action);
        Assert.Equal(new[] { 42u }, classifier.Removed);

        registration.Dispose();

        Assert.Empty(registry.Available);
        Assert.False(registry.TryClassify(
            "test/rules",
            context,
            out _));
    }

    [Fact]
    public void ClassifierFailureIsIsolatedAsUnavailableDecision()
    {
        var registry = new PluginLootClassifierRegistry();
        using IDisposable registration = registry.Register(
            "bad/rules",
            "Bad Rules",
            new ThrowingClassifier());

        Assert.False(registry.TryClassify(
            "bad/rules",
            new PluginLootClassificationContext(default, default, []),
            out _));
    }

    private sealed class Classifier : IPluginLootClassifier
    {
        public List<PluginLootedItem> Looted { get; } = [];
        public List<uint> Removed { get; } = [];

        public PluginLootClassification Classify(
            in PluginLootClassificationContext context) =>
            new(true, PluginLootAction.Keep, "External");

        public void OnLooted(in PluginLootedItem item) => Looted.Add(item);

        public void OnItemRemoved(uint objectId) => Removed.Add(objectId);
    }

    private sealed class ThrowingClassifier : IPluginLootClassifier
    {
        public PluginLootClassification Classify(
            in PluginLootClassificationContext context) =>
            throw new InvalidOperationException("classifier failed");
    }
}
