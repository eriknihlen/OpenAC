namespace AcDream.UI.Abstractions.Panels.Vitals;

public sealed class VitalsPanel : IPanel
{
    private const float BarWidth = 200f;

    private readonly VitalsVM _vm;

    public VitalsPanel(VitalsVM vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
    }

    /// <inheritdoc />
    public string Id => "acdream.vitals";

    /// <inheritdoc />
    public string Title => "Vitals";

    /// <inheritdoc />
    public bool IsVisible { get; set; } = true;

    /// <inheritdoc />
    public void Render(PanelContext ctx, IPanelRenderer renderer)
    {
        if (!renderer.Begin(Title))
        {
            renderer.End();
            return;
        }

        float hp = _vm.HealthPercent;
        renderer.Text("HP");
        renderer.SameLine();
        renderer.ProgressBar(hp, BarWidth, overlay: FormatOverlay(
            hp, _vm.HealthCurrent, _vm.HealthMax));

        // Stamina — show only when the VM has a real value.
        if (_vm.StaminaPercent is float stam)
        {
            renderer.Text("Stam");
            renderer.SameLine();
            renderer.ProgressBar(stam, BarWidth, overlay: FormatOverlay(
                stam, _vm.StaminaCurrent, _vm.StaminaMax));
        }

        // Mana — show only when the VM has a real value.
        if (_vm.ManaPercent is float mana)
        {
            renderer.Text("Mana");
            renderer.SameLine();
            renderer.ProgressBar(mana, BarWidth, overlay: FormatOverlay(
                mana, _vm.ManaCurrent, _vm.ManaMax));
        }

        renderer.End();
    }

    private static string FormatOverlay(float percent, uint? current, uint? max)
    {
        if (current is uint c && max is uint m && m > 0)
            return $"{c} / {m} ({percent * 100f:F0}%)";
        return $"{percent * 100f:F0}%";
    }
}
