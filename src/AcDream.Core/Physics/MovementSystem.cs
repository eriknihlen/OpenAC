using System;

namespace AcDream.Core.Physics;

public static class MovementSystem
{
    public static float GetRunRate(float burden, int runSkill, float scaling = 1f)
    {
        if (runSkill == 800)
            return 18f / 4f;

        float loadMod = EncumbranceSystem.LoadMod(burden);
        return ((loadMod * ((float)runSkill / (runSkill + 200) * 11f) + 4f) / scaling) / 4f;
    }

    public static float GetJumpHeight(
        float burden,
        int jumpSkill,
        float power,
        float scaling = 1f)
    {
        power = Math.Clamp(power, 0f, 1f);
        float loadMod = EncumbranceSystem.LoadMod(burden);
        float result = loadMod
            * ((float)jumpSkill / (jumpSkill + 1300f) * 22.2f + 0.05f)
            * power
            / scaling;
        return result < 0.35f ? 0.35f : result;
    }

    public static int JumpStaminaCost(float power, float burden, bool pk)
    {
        if (pk)
            return (int)((power + 1.0f) * 100.0f);

        return (int)Math.Ceiling((burden + 0.5f) * power * 8f + 2f);
    }

    public static float GetJumpPower(uint stamina, float burden, bool pk)
    {
        if (pk)
            return stamina / 100.0f - 1.0f;

        return (stamina - 2.0f) / (burden * 8.0f + 4.0f);
    }
}
