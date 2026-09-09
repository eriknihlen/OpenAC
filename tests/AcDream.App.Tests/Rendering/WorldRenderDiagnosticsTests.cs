using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class WorldRenderDiagnosticsTests
{
    [Fact]
    public void TerrainDiagnostics_RetryFailedPublicationWithoutLosingSamples()
    {
        var log = new ThrowOnceLog();
        var diagnostics = new WorldRenderDiagnostics(log);
        var facts = new TerrainRenderDiagnosticFacts(3, 3, 5, 8);

        diagnostics.BeginTerrainDraw();
        diagnostics.EndTerrainDraw();
        Assert.Throws<InvalidOperationException>(() => diagnostics.PublishTerrainDiagnostics(facts));

        diagnostics.BeginTerrainDraw();
        diagnostics.EndTerrainDraw();
        diagnostics.PublishTerrainDiagnostics(facts);

        string message = Assert.Single(log.Messages);
        Assert.Contains("draws=3/frame", message);
        Assert.Contains("visible=3", message);
        Assert.Contains("loaded=5", message);
        Assert.Contains("capacity=8", message);
    }

    private sealed class ThrowOnceLog : IRenderFrameDiagnosticLog
    {
        private bool _throw = true;
        public List<string> Messages { get; } = [];

        public void WriteLine(string message)
        {
            if (_throw)
            {
                _throw = false;
                throw new InvalidOperationException("diagnostic sink failed");
            }

            Messages.Add(message);
        }
    }
}
