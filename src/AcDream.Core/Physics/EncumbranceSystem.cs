namespace AcDream.Core.Physics;

public static class EncumbranceSystem
{
    public static int EncumbranceCapacity(int strength, int aug) =>
        AcDream.Core.Items.BurdenMath.EncumbranceCapacity(strength, aug);

    public static float Load(int capacity, int burden) =>
        AcDream.Core.Items.BurdenMath.LoadRatio(capacity, burden);

    public static float LoadMod(float load) =>
        AcDream.Core.Items.BurdenMath.LoadModifier(load);
}
