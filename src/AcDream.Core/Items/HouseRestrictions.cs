using System.Collections.Frozen;
using System.Collections.Generic;

namespace AcDream.Core.Items;

public sealed record HouseRestrictionRecord
{
    public HouseRestrictionRecord(
        bool OpenToPublic,
        uint AllegianceMonarchId,
        IReadOnlyDictionary<uint, uint> Guests)
    {
        ArgumentNullException.ThrowIfNull(Guests);
        this.OpenToPublic = OpenToPublic;
        this.AllegianceMonarchId = AllegianceMonarchId;
        this.Guests = Guests.ToFrozenDictionary();
    }

    public bool OpenToPublic { get; }
    public uint AllegianceMonarchId { get; }

    public IReadOnlyDictionary<uint, uint> Guests { get; }

    public bool IsAllowedIn(uint moverId, uint moverMonarchId)
    {
        if (OpenToPublic) return true;
        if (AllegianceMonarchId != 0 && moverMonarchId == AllegianceMonarchId) return true;
        return moverId != 0 && Guests.ContainsKey(moverId);
    }
}
