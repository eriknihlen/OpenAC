using System;
using System.Numerics;
using AcDream.App.UI;

namespace AcDream.App.UI.Layout;

public static class VitalsController
{
    public const uint Health  = 0x100000E6;
    public const uint Stamina = 0x100000EC;
    public const uint Mana    = 0x100000EE;
    public const uint HealthText  = 0x100000EB;
    public const uint StaminaText = 0x100000ED;
    public const uint ManaText    = 0x100000EF;

    public static void Bind(
        ImportedLayout layout,
        Func<float>  healthPct,
        Func<float>  staminaPct,
        Func<float>  manaPct,
        Func<string> healthText,
        Func<string> staminaText,
        Func<string> manaText)
    {
        BindMeter(layout, Health,  HealthText,  healthPct,  healthText);
        BindMeter(layout, Stamina, StaminaText, staminaPct, staminaText);
        BindMeter(layout, Mana,    ManaText,    manaPct,    manaText);
    }

    /// <summary>White cur/max numbers — matches the former <c>UiMeter.LabelColor</c> default.</summary>
    private static readonly Vector4 NumberColor = new(1f, 1f, 1f, 1f);

    private static void BindMeter(
        ImportedLayout layout, uint id, uint textId,
        Func<float>  pct,
        Func<string> text)
    {
        // Silently skip if the id is absent — missing meters are not an error (partial layouts).
        if (layout.FindElement(id) is not UiMeter m) return;

        m.Fill = () => pct();

        m.Label = () => null;
        if (layout.FindElement(textId) is not UiText number) return;

        number.Centered = true;
        number.RightAligned = false;
        number.OneLine = true;
        number.Selectable = false;
        number.DatFont ??= m.DatFont;
        number.LinesProvider = () =>
        {
            var s = text();
            return string.IsNullOrEmpty(s)
                ? Array.Empty<UiText.Line>()
                : new[] { new UiText.Line(s, NumberColor) };
        };
    }
}
