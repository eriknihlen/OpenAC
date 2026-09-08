namespace AcDream.Core.CharGen;

public static class ChargenAttributeMath
{
    public const int AttributeMin = 10;

    public const int AttributeMax = 100;

    public static int RemainingCredits(uint attributeCreditBudget, ChargenAttributeValues values) =>
        checked((int)attributeCreditBudget) - values.Total;

    public static bool IsFullySpent(uint attributeCreditBudget, ChargenAttributeValues values) =>
        RemainingCredits(attributeCreditBudget, values) == 0;

    public static bool IsWithinRange(int value) => value >= AttributeMin && value <= AttributeMax;

    public static bool AreAllWithinRange(ChargenAttributeValues values) =>
        IsWithinRange(values.Strength)
        && IsWithinRange(values.Endurance)
        && IsWithinRange(values.Coordination)
        && IsWithinRange(values.Quickness)
        && IsWithinRange(values.Focus)
        && IsWithinRange(values.Self);
}
