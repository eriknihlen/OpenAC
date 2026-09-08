using AcDream.Core.Chat;

namespace AcDream.UI.Abstractions.Panels.SpewBox;

public readonly record struct SpewBoxLine(string Text, double RemainingLifetimeSeconds);

public sealed class SpewBoxVM
{
    private readonly SpewBoxState _state;

    public SpewBoxVM(SpewBoxState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>Monotonic revision of the underlying visible-line set.</summary>
    public long Revision => _state.Revision;

    public bool HasVisibleLines => _state.Count > 0;

    public IReadOnlyList<SpewBoxLine> Lines(double nowSeconds)
    {
        _state.Tick(nowSeconds);
        SpewBoxEntry[] snapshot = _state.Snapshot();
        if (snapshot.Length == 0)
            return Array.Empty<SpewBoxLine>();

        var lines = new SpewBoxLine[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            double remaining = Math.Max(0d, snapshot[i].ExpiresAtSeconds - nowSeconds);
            lines[i] = new SpewBoxLine(snapshot[i].Text, remaining);
        }
        return lines;
    }
}
