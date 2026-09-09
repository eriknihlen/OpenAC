namespace AcDream.Core.CharGen;

public static class ChargenPalSetMath
{
    public static int GetPaletteIndex(int count, double shade)
    {
        if (count <= 0 || shade < 0.0 || shade > 1.0)
            return -1;

        int index = (int)((count - 0.000001) * shade);
        if (index < 0)
            index = 0;
        if (index > count - 1)
            index = count - 1;
        return index;
    }
}
