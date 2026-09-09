namespace AcDream.App.Rendering.Residency;

internal sealed class DelegateResidencyDomainSource(
    ResidencyDomain domain,
    Func<ResidencyDomainSnapshot> capture) : IResidencyDomainSource
{
    private readonly Func<ResidencyDomainSnapshot> _capture =
        capture ?? throw new ArgumentNullException(nameof(capture));

    public ResidencyDomain Domain { get; } = domain;

    public ResidencyDomainSnapshot CaptureResidency()
    {
        ResidencyDomainSnapshot snapshot = _capture();
        if (snapshot.Domain != Domain)
        {
            throw new InvalidOperationException(
                $"Residency source {Domain} returned {snapshot.Domain}.");
        }
        snapshot.Validate();
        return snapshot;
    }
}
