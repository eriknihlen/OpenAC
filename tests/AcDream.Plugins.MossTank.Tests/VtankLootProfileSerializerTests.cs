namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankLootProfileSerializerTests
{
    [Fact]
    public void ReadsAndWritesExactUtlOneRecordsAndUnknownBlocks()
    {
        const string source = "UTL\r\n1\r\n1\r\n"
            + "Epic loot\r\nLegacy editor text\r\n42;10;1;3;9999\r\n7\r\n"
            + "12\r\n^Epic.*\r\n1\r\n11\r\n25000\r\n19\r\n"
            + "7\r\nfalse\r\n"
            + "SalvageCombine\r\n36\r\n1\r\n1-6, 7-8, 9, 10\r\n1\r\n61\r\n1-10\r\n0\r\n"
            + "FutureBlock\r\n7\r\nhello\r\n";

        Assert.True(VtankLootProfileSerializer.TryRead(
            source,
            out VtankLootProfile profile,
            out string error), error);
        LootRule rule = Assert.Single(profile.Rules);
        Assert.Equal("Epic loot", rule.Name);
        Assert.Equal("Legacy editor text", rule.CustomExpression);
        Assert.Equal(LootAction.KeepUpTo, rule.Action);
        Assert.Equal(7, rule.KeepCount);
        Assert.Equal(42, rule.Priority);
        Assert.Equal([1, 3, 9999],
            rule.VtankRequirements.Select(static requirement => requirement.Type));
        Assert.Equal("1-10", profile.SalvageCombine.MaterialCombineStrings[61]);
        Assert.Equal("hello\r\n", Assert.Single(profile.UnknownBlocks).Payload);

        string canonical = VtankLootProfileSerializer.Write(profile);
        Assert.True(VtankLootProfileSerializer.TryRead(
            canonical,
            out VtankLootProfile second,
            out error), error);
        Assert.Equal(
            canonical,
            VtankLootProfileSerializer.Write(second));
    }

    [Fact]
    public void RoundTripsEveryVtankRequirementTypeWithoutLosingPayload()
    {
        int[] types =
        [
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
            14, 15, 16, 17,
            1000, 1001, 1002, 1003, 1004,
            2000, 2001, 2003, 2005, 2006, 2007, 2008,
            9999,
        ];
        var profile = new VtankLootProfile();
        LootRule rule = new()
        {
            Name = "All vocabulary",
            Action = LootAction.Keep,
            Priority = -19,
        };
        foreach (int type in types)
        {
            rule.VtankRequirements.Add(new VtankLootRequirement
            {
                Type = type,
                Payload = $"payload-{type}\r\nsecond-{type}\r\n",
            });
        }
        profile.Rules.Add(rule);

        string source = VtankLootProfileSerializer.Write(profile);
        Assert.True(VtankLootProfileSerializer.TryRead(
            source,
            out VtankLootProfile loaded,
            out string error), error);
        LootRule roundTrip = Assert.Single(loaded.Rules);
        Assert.Equal(types,
            roundTrip.VtankRequirements.Select(static requirement => requirement.Type));
        Assert.Equal(
            rule.VtankRequirements.Select(static requirement => requirement.Payload),
            roundTrip.VtankRequirements.Select(static requirement => requirement.Payload));
        Assert.Equal(source, VtankLootProfileSerializer.Write(loaded));
    }

    [Fact]
    public void NativeExpressionsExportDisabledInsteadOfAccidentalMatchAll()
    {
        var profile = new VtankLootProfile
        {
            Rules =
            [
                new LootRule
                {
                    Name = "Native only",
                    Expression = "name ~= Sword && value > 100",
                    Action = LootAction.Keep,
                },
            ],
        };

        string source = VtankLootProfileSerializer.Write(profile);
        Assert.Contains("0;1;9999\r\n", source, StringComparison.Ordinal);
        Assert.Contains("6\r\ntrue\r\n", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsLegacyVersionZeroKnownRequirements()
    {
        const string source = "1\r\nLegacy\r\n7;1;1;8\r\n^Sword$\r\n1\r\n3\r\n";

        Assert.True(VtankLootProfileSerializer.TryRead(
            source,
            out VtankLootProfile profile,
            out string error), error);
        LootRule rule = Assert.Single(profile.Rules);
        Assert.Equal(0, profile.SourceVersion);
        Assert.Equal(2, rule.VtankRequirements.Count);
        Assert.Equal("^Sword$\r\n1\r\n", rule.VtankRequirements[0].Payload);
        Assert.Equal("3\r\n", rule.VtankRequirements[1].Payload);
    }
}
