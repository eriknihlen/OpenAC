using System.Globalization;
using System.Reflection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum VtankPrismaticAmmoPolicy
{
    Any,
    NoPrismatic,
    ForcePrismatic,
}

internal readonly record struct VtankAmmunitionOption(
    string Name,
    int LauncherType,
    int WieldRequirement,
    int Element,
    int Quality,
    int SpecialMask,
    uint SecondarySkill,
    int SecondaryRequirement);

internal static class VtankAmmunitionDatabase
{
    private const string ResourceSuffix = ".VtankAmmunitionOptions.tsv";
    private static readonly Lazy<VtankAmmunitionOption[]> Loaded = new(Load);

    public static IReadOnlyList<VtankAmmunitionOption> Options => Loaded.Value;

    public static int LauncherType(uint ammoType) => ammoType switch
    {
        0x001u or 0x008u or 0x040u => 5,
        0x002u or 0x010u or 0x080u => 6,
        0x004u or 0x020u or 0x100u => 7,
        _ => 0,
    };

    /// <summary>
    /// The bundled table. Kept as the fallback for a session with no
    /// <c>gameinfodb.ugd</c> in its profile directory.
    /// </summary>
    public static VtankAmmunitionOption? Select(
        int launcherType,
        MonsterDamageType damage,
        VtankPrismaticAmmoPolicy prismatic,
        int enabledSpecialMask,
        ICharacterInfo character,
        Func<string, bool> isAvailable) => Select(
            Loaded.Value,
            launcherType,
            damage,
            prismatic,
            enabledSpecialMask,
            character,
            isAvailable);

    public static VtankAmmunitionOption? Select(
        IReadOnlyList<VtankAmmunitionOption> options,
        int launcherType,
        MonsterDamageType damage,
        VtankPrismaticAmmoPolicy prismatic,
        int enabledSpecialMask,
        ICharacterInfo character,
        Func<string, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(isAvailable);
        int desiredElement = Element(damage);
        if (launcherType == 0 || desiredElement < 0)
            return null;

        VtankAmmunitionOption? best = null;
        int bestQuality = int.MinValue;
        foreach (VtankAmmunitionOption option in options)
        {
            if (option.LauncherType != launcherType)
                continue;
            int quality = option.Quality;
            if (prismatic == VtankPrismaticAmmoPolicy.ForcePrismatic
                && option.Element != 100)
            {
                quality -= 1000;
            }
            if (option.Element != desiredElement)
            {
                if (option.Element != 100)
                    continue;
                if (prismatic == VtankPrismaticAmmoPolicy.NoPrismatic)
                    quality -= 1000;
            }
            if (quality < bestQuality
                || !MeetsRequirements(option, character)
                || (option.SpecialMask != 0
                    && (option.SpecialMask & enabledSpecialMask) == 0)
                || !isAvailable(option.Name))
            {
                continue;
            }
            bestQuality = quality;
            best = option;
        }
        return best;
    }

    private static bool MeetsRequirements(
        in VtankAmmunitionOption option,
        ICharacterInfo character)
    {
        if (option.WieldRequirement > 0)
        {
            if (!character.TryGetSkill(47u, out PluginSkillInfo missile)
                || missile.Training == PluginSkillTraining.Untrained
                || missile.Base < option.WieldRequirement)
            {
                return false;
            }
        }
        if (option.SecondarySkill == 0u || option.SecondaryRequirement == 0)
            return true;
        return character.TryGetSkill(
                option.SecondarySkill,
                out PluginSkillInfo secondary)
            && secondary.Training != PluginSkillTraining.Untrained
            && secondary.Current >= option.SecondaryRequirement;
    }

    private static int Element(MonsterDamageType damage) => damage switch
    {
        MonsterDamageType.Pierce => 0,
        MonsterDamageType.Bludgeon => 1,
        MonsterDamageType.Slash => 2,
        MonsterDamageType.Acid => 3,
        MonsterDamageType.Electric => 4,
        MonsterDamageType.Cold => 5,
        MonsterDamageType.Fire => 6,
        MonsterDamageType.Prismatic => 0,
        _ => -1,
    };

    private static VtankAmmunitionOption[] Load()
    {
        Assembly assembly = typeof(VtankAmmunitionDatabase).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(
            static name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                "The embedded VTank AmmunitionOptions table is missing.");
        using var reader = new StreamReader(stream);
        var all = new List<VtankAmmunitionOption>(120);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] fields = line.Split('\t');
            if (fields.Length != 8)
                throw new InvalidDataException("Malformed VTank ammunition row.");
            all.Add(new VtankAmmunitionOption(
                fields[0],
                Parse(fields[1]),
                Parse(fields[2]),
                Parse(fields[3]),
                Parse(fields[4]),
                Parse(fields[5]),
                (uint)Parse(fields[6]),
                Parse(fields[7])));
        }
        if (all.Count != 120)
        {
            throw new InvalidDataException(
                $"Expected 120 official VTank ammunition rows, found {all.Count}.");
        }
        return [.. all];
    }

    private static int Parse(string value) =>
        int.Parse(value, CultureInfo.InvariantCulture);
}
