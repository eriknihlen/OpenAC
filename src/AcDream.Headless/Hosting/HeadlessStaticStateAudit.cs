using System.Reflection;
using AcDream.Core.Physics;
using AcDream.Headless.Configuration;
using AcDream.Headless.Diagnostics;

namespace AcDream.Headless.Hosting;

internal static class HeadlessStaticStateAudit
{
    internal static void ValidateProcessIsolation(
        int sessionCount, HeadlessDiagnosticWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var enabled = new List<string>();
        foreach (PropertyInfo property in typeof(PhysicsDiagnostics)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (property.PropertyType != typeof(bool)
                || property.GetMethod is null
                || (!property.Name.StartsWith(
                        "Probe",
                        StringComparison.Ordinal)
                    && !property.Name.StartsWith(
                        "Dump",
                        StringComparison.Ordinal)))
            {
                continue;
            }
            if (property.GetValue(null) is true)
                enabled.Add(property.Name);
        }
        if (PhysicsDiagnostics.CollisionShadowSampleEvery > 0)
            enabled.Add(nameof(PhysicsDiagnostics.CollisionShadowSampleEvery));
        if (PhysicsResolveCapture.IsEnabled)
            enabled.Add(nameof(PhysicsResolveCapture));

        if (enabled.Count == 0)
            return;

        if (sessionCount == 1)
        {
            diagnostics.Message(
                sessionId: "process",
                eventName: FormattableString.Invariant(
                    $"headless-audit: single-session process — process-global physics probes enabled: {string.Join(", ", enabled)}"));
            return;
        }

        throw new HeadlessConfigurationException(
            "Multi-session headless mode cannot use process-global "
            + "physics probes. Disable: "
            + string.Join(", ", enabled));
    }
}
