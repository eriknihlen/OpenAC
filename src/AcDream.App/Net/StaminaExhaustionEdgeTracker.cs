namespace AcDream.App.Net;

internal sealed class StaminaExhaustionEdgeTracker
{
    private bool? _wasExhausted;

    public bool Observe(int currentStamina)
    {
        bool exhausted = currentStamina == 0;
        if (_wasExhausted == exhausted)
            return false;

        bool isEdge = _wasExhausted.HasValue;
        _wasExhausted = exhausted;
        return isEdge;
    }

    public void Reset() => _wasExhausted = null;
}
