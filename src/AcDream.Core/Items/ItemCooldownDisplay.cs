namespace AcDream.Core.Items;

public static class ItemCooldownDisplay
{
    public static int GetOverlayStep(double itemDuration, double remaining)
    {
        if (!(itemDuration > 0d) || !(remaining > 0d))
            return 0;

        double step = Math.Truncate((remaining / itemDuration) * 10d + 1d);
        if (!(step >= 1d))
            return 0;
        if (step >= 10d)
            return 10;
        return (int)step;
    }
}
