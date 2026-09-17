using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Items;

/// <summary>
/// The one place a retained <see cref="ClientWeaponProfile"/>/
/// <see cref="ClientArmorProfile"/> is projected onto the public
/// <see cref="PluginWeaponProfile"/>/<see cref="PluginArmorProfile"/>
/// contract. Lives in Core (rather than the App or Runtime automation
/// surfaces) so both the graphical host's <c>RuntimeAutomationSurface</c> and
/// the vendor automation adapter in <c>AcDream.Runtime</c> -- neither of
/// which may depend on the other -- can share the exact same mapping
/// instead of drifting apart.
/// </summary>
public static class ClientAppraisalProfileMapper
{
    // The wire's Damage field uses uint.MaxValue (0xFFFFFFFF) as its
    // "unknown/not applicable" sentinel. Casting straight to int would
    // silently produce -1 as an implementation accident of two's-complement
    // wraparound; this makes that intentional and documents it for callers.
    public static int NormalizeDamage(uint damage) =>
        damage == uint.MaxValue ? -1 : (int)damage;

    public static PluginWeaponProfile? ToPluginWeaponProfile(
        ClientWeaponProfile? source) =>
        source is { } w
            ? new PluginWeaponProfile(
                (int)w.DamageType,
                (int)w.WeaponTime,
                w.WeaponSkill,
                NormalizeDamage(w.Damage),
                w.DamageVariance,
                w.DamageMod,
                w.WeaponLength,
                w.MaxVelocity,
                w.WeaponOffense,
                (int)w.MaxVelocityEstimated)
            : null;

    public static PluginArmorProfile? ToPluginArmorProfile(
        ClientArmorProfile? source,
        int armorLevel) =>
        source is { } a
            ? new PluginArmorProfile(
                armorLevel,
                a.SlashingProtection,
                a.PiercingProtection,
                a.BludgeoningProtection,
                a.ColdProtection,
                a.FireProtection,
                a.AcidProtection,
                a.NetherProtection,
                a.LightningProtection)
            : null;
}
