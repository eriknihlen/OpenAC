using AcDream.Core.Physics;
using AcDream.Headless.Configuration;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;

namespace AcDream.Headless.Tests;

[CollectionDefinition(
    HeadlessStaticStateAuditCollection.Name,
    DisableParallelization = true)]
public sealed class HeadlessStaticStateAuditCollection
{
    public const string Name = "Headless static-state audit";
}

[Collection(HeadlessStaticStateAuditCollection.Name)]
public sealed class HeadlessStaticStateAuditTests : IDisposable
{
    public HeadlessStaticStateAuditTests() => PhysicsDiagnostics.ResetForTest();

    public void Dispose() => PhysicsDiagnostics.ResetForTest();

    [Fact]
    public void SingleSessionWithProbeEnabledIsAllowedAndLoggedLoudly()
    {
        PhysicsDiagnostics.ProbeParkEnabled = true;
        using var captured = new StringWriter();
        var diagnostics = new HeadlessDiagnosticWriter(captured);

        HeadlessStaticStateAudit.ValidateProcessIsolation(
            sessionCount: 1, diagnostics);

        Assert.Contains(
            nameof(PhysicsDiagnostics.ProbeParkEnabled),
            captured.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SingleSessionWithNoProbesEnabledIsSilent()
    {
        using var captured = new StringWriter();
        var diagnostics = new HeadlessDiagnosticWriter(captured);

        HeadlessStaticStateAudit.ValidateProcessIsolation(
            sessionCount: 1, diagnostics);

        Assert.Equal(string.Empty, captured.ToString());
    }

    [Fact]
    public void MultiSessionWithProbeEnabledStillThrowsNamingTheProbe()
    {
        PhysicsDiagnostics.ProbeParkEnabled = true;
        var diagnostics = new HeadlessDiagnosticWriter(TextWriter.Null);

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessStaticStateAudit.ValidateProcessIsolation(
                    sessionCount: 2, diagnostics));

        Assert.Contains(
            nameof(PhysicsDiagnostics.ProbeParkEnabled),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultiSessionWithNoProbesEnabledIsAllowed()
    {
        var diagnostics = new HeadlessDiagnosticWriter(TextWriter.Null);

        Exception? exception = Record.Exception(
            () => HeadlessStaticStateAudit.ValidateProcessIsolation(
                sessionCount: 3, diagnostics));

        Assert.Null(exception);
    }
}
