using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public class VitalsBindingTests
{
    // ── Test 1: Health meter Fill + Label providers are bound ─────────────────

    [Fact]
    public void Bind_SetsHealthMeterFillFromProvider()
    {
        var health = new UiMeter();
        var healthText = new UiText();
        var layout = FakeLayout(
            (VitalsController.Health, health),
            (VitalsController.HealthText, healthText));
        float hp = 0.42f;

        VitalsController.Bind(layout,
            healthPct:    () => hp,
            staminaPct:   () => 1f,
            manaPct:      () => 1f,
            healthText:   () => "42/100",
            staminaText:  () => "",
            manaText:     () => "");

        Assert.Equal(0.42f, health.Fill()!.Value);
        // The meter no longer draws its own label; the authored text node is bound in place.
        Assert.Null(health.Label());
        Assert.Equal("42/100", NumberText(healthText));
    }

    // ── Test 2: All three meters wired to distinct providers ──────────────────

    [Fact]
    public void Bind_AllThreeMeters_EachBoundToOwnProvider()
    {
        var health  = new UiMeter();
        var stamina = new UiMeter();
        var mana    = new UiMeter();
        var healthText = new UiText();
        var staminaText = new UiText();
        var manaText = new UiText();
        var layout  = FakeLayout(
            (VitalsController.Health,  health),
            (VitalsController.Stamina, stamina),
            (VitalsController.Mana,    mana),
            (VitalsController.HealthText, healthText),
            (VitalsController.StaminaText, staminaText),
            (VitalsController.ManaText, manaText));

        VitalsController.Bind(layout,
            healthPct:   () => 0.25f,
            staminaPct:  () => 0.50f,
            manaPct:     () => 0.75f,
            healthText:  () => "25/100",
            staminaText: () => "50/100",
            manaText:    () => "75/100");

        // Each meter should reflect its own provider, not another's.
        Assert.Equal(0.25f, health.Fill()!.Value);
        Assert.Equal("25/100", NumberText(healthText));

        Assert.Equal(0.50f, stamina.Fill()!.Value);
        Assert.Equal("50/100", NumberText(staminaText));

        Assert.Equal(0.75f, mana.Fill()!.Value);
        Assert.Equal("75/100", NumberText(manaText));
    }

    // ── Test 3: Missing meter ids are silently skipped (no throw) ─────────────

    [Fact]
    public void Bind_MissingMeterIds_DoesNotThrow()
    {
        // Only Health is present; Stamina and Mana are absent from the layout.
        var health = new UiMeter();
        var layout = FakeLayout((VitalsController.Health, health));

        // Should not throw even though Stamina/Mana are missing.
        VitalsController.Bind(layout,
            healthPct:   () => 1f,
            staminaPct:  () => 1f,
            manaPct:     () => 1f,
            healthText:  () => "100/100",
            staminaText: () => "100/100",
            manaText:    () => "100/100");

        // Health was present — it should be wired.
        Assert.Equal(1f, health.Fill()!.Value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NumberText(UiText num)
    {
        Assert.True(num.Centered);
        Assert.True(num.OneLine);
        Assert.False(num.Selectable);
        var lines = num.LinesProvider();
        return lines.Count > 0 ? lines[0].Text : "";
    }

    private static ImportedLayout FakeLayout(params (uint id, UiElement e)[] items)
    {
        var dict = new Dictionary<uint, UiElement>();
        var root = new UiPanel();
        foreach (var (id, e) in items)
        {
            root.AddChild(e);
            dict[id] = e;
        }
        return new ImportedLayout(root, dict);
    }
}
