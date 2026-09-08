using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AcDream.Core.Spells;

public static class RetailSpellFormula
{
    private const uint LowestTaperId = 63u;
    private const uint PrismaticTaperId = 0xBCu;

    public static uint DeterminePowerLevelOfComponent(uint componentId) => componentId switch
    {
        >= 1u and <= 6u => componentId,
        0x6Eu => 7u,
        0x70u => 8u,
        0xC0u => 9u,
        0xC1u => 10u,
        _ => 0u,
    };

    public static int InqSpellLevelByRoughHeuristic(IReadOnlyList<uint> components)
    {
        uint power = components.Count == 0
            ? 0u
            : DeterminePowerLevelOfComponent(components[0]);
        return power switch
        {
            < 7u => (int)power,
            >= 9u => (int)power - 2,
            _ => (int)power - 1,
        };
    }

    public static uint GetTargetingType(IReadOnlyList<uint> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count <= 4)
            return 0u;

        int last = 4;
        while (last < 7 && last + 1 < components.Count && components[last + 1] != 0u)
            last++;
        return GetTargetTypeFromComponentId(components[last]);
    }

    public static uint GetTargetTypeFromComponentId(uint componentId) => componentId switch
    {
        0x31u or 0x32u or 0x33u or 0x34u or 0x35u or 0x36u
            or 0x37u or 0x38u or 0x3Cu or 0x3Du or 0x3Eu or 0xBEu => 0x10u,
        0x39u => 0x00088B8Fu,
        0x3Bu => 0x10010000u,
        _ => 0u,
    };

    public static IReadOnlyList<uint> InqScarabOnlyFormula(
        IReadOnlyList<uint> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        var result = new List<uint>(8);
        uint strongestPower = 0u;
        foreach (uint component in components.Take(8))
        {
            if (component == 0u)
                break;
            if (!IsPowerComponent(component))
                continue;

            result.Add(component);
            strongestPower = Math.Max(
                strongestPower,
                DeterminePowerLevelOfComponent(component));
        }

        int taperCount = strongestPower switch
        {
            1u => 1,
            2u => 2,
            3u or 4u or 7u => 3,
            5u or 6u or 8u or 9u or 10u => 4,
            _ => 0,
        };
        while (taperCount-- > 0 && result.Count < 8)
            result.Add(PrismaticTaperId);
        return result;
    }

    private static bool IsPowerComponent(uint component) => component is
        >= 1u and <= 6u or 0x6Eu or 0x6Fu or 0x70u or 0xC0u or 0xC1u;

    public static uint ComputeNameHash(string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        long result = 0;
        foreach (byte raw in Encoding.GetEncoding(1252).GetBytes(value ?? string.Empty))
        {
            sbyte character = unchecked((sbyte)raw);
            result = character + (result << 4);
            if ((result & 0xF0000000L) != 0)
                result = (result ^ ((result & 0xF0000000L) >> 24)) & 0x0FFFFFFF;
        }
        return unchecked((uint)result);
    }

    public static IReadOnlyList<uint> CustomizeForAccount(
        IReadOnlyList<uint> components,
        uint formulaVersion,
        string accountName)
    {
        uint[] result = components.Take(8).ToArray();
        return formulaVersion switch
        {
            1u => RandomizeVersion1(result, accountName),
            2u => RandomizeVersion2(result, accountName),
            3u => RandomizeVersion3(result, accountName),
            _ => result,
        };
    }

    private static IReadOnlyList<uint> RandomizeVersion1(uint[] c, string accountName)
    {
        int count = c.Count(value => value != 0);
        if (count < 5) return c;

        uint seed = ComputeNameHash(accountName) % 0x13D573u;
        uint scarab = c[0];
        int herbIndex = count > 5 ? 2 : 1;
        uint herb = c[herbIndex];
        int powderIndex = herbIndex + 1 + (count > 6 ? 1 : 0);
        if (powderIndex + 1 >= c.Length) return c;
        uint powder = c[powderIndex];
        uint potion = c[powderIndex + 1];
        int talismanIndex = powderIndex + 2 + (count > 7 ? 1 : 0);
        if (talismanIndex >= c.Length) return c;
        uint talisman = c[talismanIndex];

        if (count > 5)
            c[1] = unchecked(powder + 2u * herb + potion + talisman + scarab) % 12u + LowestTaperId;
        if (count > 6)
        {
            uint denominator = unchecked(scarab + powder + potion);
            if (denominator != 0)
                c[3] = unchecked((scarab + herb + talisman + 2u * (powder + potion))
                    * (seed / denominator)) % 12u + LowestTaperId;
        }
        if (count > 7)
        {
            uint denominator = unchecked(talisman + scarab);
            if (denominator != 0)
                c[6] = unchecked((powder + 2u * talisman + potion + herb + scarab)
                    * (seed / denominator)) % 12u + LowestTaperId;
        }
        return c;
    }

    private static IReadOnlyList<uint> RandomizeVersion2(uint[] c, string accountName)
    {
        if (c.Length < 8) return c;
        uint seed = ComputeNameHash(accountName) % 0x13D573u;
        uint p1 = c[0], c4 = c[4], x = c[5], a = c[7];
        c[3] = unchecked(a + 3u * p1 + 2u * c4 * x + c[2] + c[1]) % 12u + LowestTaperId;
        uint denominator = unchecked(c[1] * a + 2u * c4);
        if (denominator != 0)
            c[6] = unchecked((a + 3u * p1 * c[2] + 2u * x + c4)
                * (seed / denominator)) % 12u + LowestTaperId;
        return c;
    }

    private static IReadOnlyList<uint> RandomizeVersion3(uint[] c, string accountName)
    {
        if (c.Length < 7) return c;
        uint hash = ComputeNameHash(accountName);
        uint h0 = unchecked(hash % 0x13D573u + c[0]) % 12u;
        uint h1 = unchecked(hash % 0x4AEFDu + c[1]) % 12u;
        uint h2 = unchecked(hash % 0x96A7Fu + c[2]) % 12u;
        uint h4 = unchecked(hash % 0x100A03u + c[4]) % 12u;
        uint h5 = unchecked(hash % 0xEB2EFu + c[5]) % 12u;
        uint h7 = unchecked(hash % 0x121E7Du + (c.Length > 7 ? c[7] : 0u)) % 12u;
        c[3] = unchecked(h0 + h1 + h2 + h4 + h5 + h2 * h5 + h0 * h1 + h7 * (h4 + 1u))
            % 12u + LowestTaperId;
        c[6] = unchecked(h0 + h1 + h2 + h4 + hash % 0x65039u % 12u
            + h7 * (h4 * (h0 * h1 * h2 * h5 + 7u) + 1u)
            + h5 + 5u * h0 * h1 + 11u * h2 * h5) % 12u + LowestTaperId;
        return c;
    }
}
