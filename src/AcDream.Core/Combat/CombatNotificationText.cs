using System;
using System.Globalization;
using System.Text;

namespace AcDream.Core.Combat;

public static class CombatHitAdjectives
{
    public const double LightThreshold = 0.1;

    public const double MediumThreshold = 0.25;

    public const double HeavyThreshold = 0.5;

    public const uint Slash = 0x1u;
    public const uint Pierce = 0x2u;
    public const uint Bludgeon = 0x4u;
    public const uint Cold = 0x8u;
    public const uint Fire = 0x10u;
    public const uint Acid = 0x20u;
    public const uint Electric = 0x40u;
    public const uint Health = 0x80u;
    public const uint Stamina = 0x100u;
    public const uint Mana = 0x200u;
    public const uint Nether = 0x400u;
    public const uint Base = 0x10000000u;

    public static bool Inq(
        uint damageType, double percent, out string verb, out string verbThirdPerson)
    {
        if (!(percent >= 0.0))
        {
            verb = string.Empty;
            verbThirdPerson = string.Empty;
            return false;
        }

        switch (damageType)
        {
            case Slash:
                Pick(percent, "scratch", "scratches", "cut", "cuts",
                    "slash", "slashes", "mangle", "mangles",
                    out verb, out verbThirdPerson);
                return true;

            case Pierce:
                PickPlusS(percent, "nick", "stab", "impale", "gore",
                    out verb, out verbThirdPerson);
                return true;

            case Bludgeon:
                Pick(percent, "graze", "grazes", "bash", "bashes",
                    "smash", "smashes", "crush", "crushes",
                    out verb, out verbThirdPerson);
                return true;

            case Cold:
                PickPlusS(percent, "numb", "chill", "frost", "freeze",
                    out verb, out verbThirdPerson);
                return true;

            case Fire:
                Pick(percent, "singe", "singes", "scorch", "scorches",
                    "burn", "burns", "incinerate", "incinerates",
                    out verb, out verbThirdPerson);
                return true;

            case Acid:
                PickPlusS(percent, "blister", "sear", "corrode", "dissolve",
                    out verb, out verbThirdPerson);
                return true;

            case Electric:
                Pick(percent, "spark", "sparks", "shock", "shocks",
                    "jolt", "jolts", "blast", "blasts",
                    out verb, out verbThirdPerson);
                return true;

            case Health:
                PickPlusS(percent, "drain", "exhaust", "siphon", "deplete",
                    out verb, out verbThirdPerson);
                return true;

            case Nether:
                PickPlusS(percent, "scar", "twist", "wither", "eradicate",
                    out verb, out verbThirdPerson);
                return true;

            default:
                verb = "hit";
                verbThirdPerson = "hits";
                return false;
        }
    }

    private static void Pick(
        double percent,
        string lightFirst, string lightThird,
        string mediumFirst, string mediumThird,
        string heavyFirst, string heavyThird,
        string severeFirst, string severeThird,
        out string verb, out string verbThirdPerson)
    {
        if (!(percent > LightThreshold)) { verb = lightFirst; verbThirdPerson = lightThird; }
        else if (!(percent > MediumThreshold)) { verb = mediumFirst; verbThirdPerson = mediumThird; }
        else if (!(percent > HeavyThreshold)) { verb = heavyFirst; verbThirdPerson = heavyThird; }
        else { verb = severeFirst; verbThirdPerson = severeThird; }
    }

    private static void PickPlusS(
        double percent, string light, string medium, string heavy, string severe,
        out string verb, out string verbThirdPerson)
    {
        if (!(percent > LightThreshold)) verb = light;
        else if (!(percent > MediumThreshold)) verb = medium;
        else if (!(percent > HeavyThreshold)) verb = heavy;
        else verb = severe;
        verbThirdPerson = verb + "s";
    }
}

public static class DamageTypeText
{
    public static string Describe(uint damageType)
    {
        var sb = new StringBuilder(24);
        Append(sb, damageType, CombatHitAdjectives.Slash, "Slashing");
        Append(sb, damageType, CombatHitAdjectives.Pierce, "Piercing");
        Append(sb, damageType, CombatHitAdjectives.Bludgeon, "Bludgeoning");
        Append(sb, damageType, CombatHitAdjectives.Cold, "Cold");
        Append(sb, damageType, CombatHitAdjectives.Fire, "Fire");
        Append(sb, damageType, CombatHitAdjectives.Acid, "Acid");
        Append(sb, damageType, CombatHitAdjectives.Electric, "Electrical");
        Append(sb, damageType, CombatHitAdjectives.Nether, "Nether");
        Append(sb, damageType, CombatHitAdjectives.Base, "Prismatic");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, uint mask, uint bit, string name)
    {
        if ((mask & bit) == 0) return;
        if (sb.Length != 0) sb.Append('/');
        sb.Append(name);
    }
}

public static class BodyPartText
{
    public static string Describe(int bodyPart) => bodyPart switch
    {
        -1 => "UNDEFINED",
        0 => "HEAD",
        1 => "CHEST",
        2 => "ABDOMEN",
        3 => "UPPER_ARM",
        4 => "LOWER_ARM",
        5 => "HAND",
        6 => "UPPER_LEG",
        7 => "LOWER_LEG",
        8 => "FOOT",
        9 => "HORN",
        10 => "FRONT_LEG",
        12 => "FRONT_FOOT",
        13 => "REAR_LEG",
        15 => "REAR_FOOT",
        16 => "TORSO",
        17 => "TAIL",
        18 => "ARM",
        19 => "LEG",
        20 => "CLAW",
        21 => "WINGS",
        22 => "BREATH",
        23 => "TENTACLE",
        24 => "UPPER_TENTACLE",
        25 => "LOWER_TENTACLE",
        26 => "CLOAK",
        27 => "NUM",
        _ => "Unknown",
    };

    public static string ToDisplay(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Replace('_', ' ').ToLowerInvariant();
    }

    public static string DescribeForDisplay(int bodyPart) => ToDisplay(Describe(bodyPart));
}

public static class CombatNotificationText
{
    public const ulong CriticalProtectionAugmentation = 0x1ul;
    public const ulong Recklessness = 0x2ul;
    public const ulong SneakAttack = 0x4ul;

    public static string AttackerLine(
        string defenderName, uint damageType, double percent,
        uint damage, bool critical, ulong attackConditions)
    {
        CombatHitAdjectives.Inq(damageType, percent, out string verb, out _);

        var sb = new StringBuilder(96);
        if (critical) sb.Append("Critical hit!  ");
        if ((attackConditions & SneakAttack) != 0) sb.Append("Sneak Attack! ");
        if ((attackConditions & Recklessness) != 0) sb.Append("Recklessness! ");

        sb.Append("You ").Append(verb).Append(' ').Append(defenderName)
          .Append(" for ").Append(damage.ToString(CultureInfo.InvariantCulture))
          .Append(" point").Append(Plural(damage))
          .Append(" of ").Append(TypeTextWithTrailingSpace(damageType))
          .Append("damage!");

        if ((attackConditions & CriticalProtectionAugmentation) != 0)
            sb.Append(" Your target's Critical Protection augmentation "
                + "allows them to avoid your critical hit!");

        return sb.ToString();
    }

    public static string DefenderLine(
        string attackerName, uint damageType, double percent, uint damage,
        int bodyPart, bool critical, ulong attackConditions)
        => DefenderLineForBodyPartText(
            attackerName, damageType, percent, damage,
            BodyPartText.DescribeForDisplay(bodyPart), critical, attackConditions);

    public static string DefenderLineForBodyPartText(
        string attackerName, uint damageType, double percent, uint damage,
        string bodyPartText, bool critical, ulong attackConditions)
    {
        CombatHitAdjectives.Inq(damageType, percent, out _, out string verbThirdPerson);
        string part = bodyPartText ?? string.Empty;

        var sb = new StringBuilder(112);
        if (critical) sb.Append("Critical hit! ");
        if ((attackConditions & SneakAttack) != 0) sb.Append("Sneak Attack! ");
        if ((attackConditions & Recklessness) != 0) sb.Append("Reckless! ");

        sb.Append(attackerName).Append(' ').Append(verbThirdPerson);
        if (part.Length != 0) sb.Append(" your ").Append(part);
        else sb.Append(" you");
        sb.Append(" for ").Append(damage.ToString(CultureInfo.InvariantCulture))
          .Append(" point").Append(Plural(damage))
          .Append(" of ").Append(TypeTextWithTrailingSpace(damageType))
          .Append("damage!");

        if ((attackConditions & CriticalProtectionAugmentation) != 0)
            sb.Append(" Your Critical Protection augmentation allows you "
                + "to avoid a critical hit!");

        return sb.ToString();
    }

    public static string EvasionAttackerLine(string defenderName)
        => defenderName + " evaded your attack.";

    public static string EvasionDefenderLine(string attackerName)
        => "You evaded " + attackerName + "!";

    private static string Plural(uint damage) => damage == 1 ? string.Empty : "s";

    private static string TypeTextWithTrailingSpace(uint damageType)
    {
        string text = DamageTypeText.Describe(damageType);
        // Invariant: see BodyPartText.ToDisplay.
        return text.Length == 0 ? string.Empty : text.ToLowerInvariant() + " ";
    }
}
