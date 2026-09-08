namespace AcDream.Core.CharGen;

public readonly record struct ChargenAttributeValues(
    int Strength,
    int Endurance,
    int Coordination,
    int Quickness,
    int Focus,
    int Self)
{
    public int Total => Strength + Endurance + Coordination + Quickness + Focus + Self;
}
