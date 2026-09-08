using AcDream.App.Streaming;

namespace AcDream.App.Rendering;

internal sealed class WorldRevealRenderResourceScheduler
    : IWorldRevealRenderResourceScheduler
{
    private readonly Action<bool>[] _participants;
    private long _activeGeneration;

    public WorldRevealRenderResourceScheduler(
        params Action<bool>[] participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Length == 0)
            throw new ArgumentException(
                "At least one reveal upload participant is required.",
                nameof(participants));
        if (participants.Any(static participant => participant is null))
            throw new ArgumentException(
                "Reveal upload participants cannot contain null.",
                nameof(participants));

        _participants = [.. participants];
    }

    public void BeginDestinationReveal(long revealGeneration)
    {
        if (revealGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(revealGeneration));

        _activeGeneration = revealGeneration;
        for (int i = 0; i < _participants.Length; i++)
            _participants[i](true);
    }

    public void EndDestinationReveal(long revealGeneration)
    {
        if (revealGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(revealGeneration));
        if (_activeGeneration != revealGeneration)
            return;

        for (int i = 0; i < _participants.Length; i++)
            _participants[i](false);
        _activeGeneration = 0;
    }
}
