using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering;

internal sealed class FixedEntityTextureOwnerLease : IDisposable
{
    private readonly IEntityTextureLifetime _lifetime;
    private readonly uint _ownerLocalId;
    private bool _active;

    public FixedEntityTextureOwnerLease(IEntityTextureLifetime lifetime, uint ownerLocalId)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        if (ownerLocalId == 0)
            throw new ArgumentOutOfRangeException(nameof(ownerLocalId));
        _ownerLocalId = ownerLocalId;
    }

    public void Replace(bool hasReplacement)
    {
        if (_active)
            _lifetime.ReleaseOwner(_ownerLocalId);
        _active = hasReplacement;
    }

    public void Dispose() => Replace(hasReplacement: false);
}
