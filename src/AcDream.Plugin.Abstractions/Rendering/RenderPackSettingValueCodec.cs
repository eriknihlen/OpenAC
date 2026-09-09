using System.Globalization;

namespace AcDream.Plugin.Abstractions.Rendering;

public static class RenderPackSettingValueCodec
{
    private const long ExactFloatIntegerLimit = 16_777_216L;

    /// <summary>
    /// Validate and encode one string value for set-1/binding-8. The encoded
    /// value is zero on failure, matching the host block's fail-safe fill.
    /// </summary>
    public static bool TryEncode(
        RenderSettingDeclaration setting,
        string? value,
        out float encoded)
    {
        ArgumentNullException.ThrowIfNull(setting);
        encoded = 0f;
        if (value is null)
            return false;

        switch (setting.Kind)
        {
            case RenderSettingKind.Boolean:
                if (!bool.TryParse(value, out bool boolean))
                    return false;
                encoded = boolean ? 1f : 0f;
                return true;

            case RenderSettingKind.Integer:
                if (!long.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long integer)
                    || integer is < -ExactFloatIntegerLimit or > ExactFloatIntegerLimit
                    || !WithinBounds(integer, setting)
                    || !AlignedToStep(integer, setting))
                {
                    return false;
                }
                encoded = integer;
                return true;

            case RenderSettingKind.Float:
                if (!double.TryParse(
                        value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double floating)
                    || !double.IsFinite(floating)
                    || floating < -float.MaxValue
                    || floating > float.MaxValue
                    || !WithinBounds(floating, setting)
                    || !AlignedToStep(floating, setting))
                {
                    return false;
                }
                encoded = (float)floating;
                return float.IsFinite(encoded);

            case RenderSettingKind.Choice:
                int choice = IndexOf(setting.Choices, value);
                if (choice < 0)
                    return false;
                encoded = choice;
                return true;

            default:
                return false;
        }
    }

    private static bool WithinBounds(
        double value,
        RenderSettingDeclaration setting) =>
        (setting.Minimum is null || value >= setting.Minimum.Value)
        && (setting.Maximum is null || value <= setting.Maximum.Value);

    private static bool AlignedToStep(
        double value,
        RenderSettingDeclaration setting)
    {
        if (setting.Step is not { } step)
            return true;
        if (!double.IsFinite(step) || step <= 0)
            return false;
        double origin = setting.Minimum ?? 0d;
        double quotient = (value - origin) / step;
        if (!double.IsFinite(quotient))
            return false;
        double tolerance = Math.Max(1e-7, Math.Abs(quotient) * 1e-7);
        return Math.Abs(quotient - Math.Round(quotient)) <= tolerance;
    }

    private static int IndexOf(IReadOnlyList<string>? choices, string value)
    {
        if (choices is null)
            return -1;
        for (int i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i], value, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
