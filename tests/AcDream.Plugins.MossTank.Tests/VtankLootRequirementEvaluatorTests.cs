using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankLootRequirementEvaluatorTests
{
    // Spell 2604 grants +20 to IntValueKey 28 (VtankLootRequirementEvaluator's
    // IntSpellBonuses table) — key 28 is not one of the named
    // PluginInventoryItem fields, so it only "exists" via the raw property
    // bag.
    private const uint BonusIntKey = 28u;
    private const uint BonusIntSpellId = 2604u;

    [Fact]
    public void BuffedIntRequirementDoesNotApplyBonusWhenBaseKeyIsAbsent()
    {
        PluginInventoryItem item = Item() with
        {
            AppraisedSpellIds = [BonusIntSpellId],
        };
        var properties = new PluginItemProperties(
            Ints: new Dictionary<uint, int>(), // key 28 absent entirely
            Int64s: new Dictionary<uint, long>(),
            Bools: new Dictionary<uint, bool>(),
            Floats: new Dictionary<uint, double>(),
            Strings: new Dictionary<uint, string>(),
            DataIds: new Dictionary<uint, uint>(),
            InstanceIds: new Dictionary<uint, uint>());

        // Type 2003 = BuffedLongValKeyGE: values[0]=threshold, values[1]=key.
        // Pre-fix this would read base=0, add the +20 bonus unconditionally
        // (20 >= 10 => true); the gate must keep it at the un-buffed base
        // value (0 >= 10 => false) since the key never existed at all.
        var requirement = new VtankLootRequirement
        {
            Type = 2003,
            Payload = $"10\r\n{BonusIntKey}\r\n",
        };
        bool matched = VtankLootRequirementEvaluator.IsMatch(
            [requirement], item, properties, host: null, out string? error);
        Assert.Null(error);
        Assert.False(matched);
    }

    [Fact]
    public void BuffedIntRequirementAppliesBonusWhenBaseKeyExists()
    {
        PluginInventoryItem item = Item() with
        {
            AppraisedSpellIds = [BonusIntSpellId],
        };
        var properties = new PluginItemProperties(
            Ints: new Dictionary<uint, int> { [BonusIntKey] = 0 }, // key exists, base 0
            Int64s: new Dictionary<uint, long>(),
            Bools: new Dictionary<uint, bool>(),
            Floats: new Dictionary<uint, double>(),
            Strings: new Dictionary<uint, string>(),
            DataIds: new Dictionary<uint, uint>(),
            InstanceIds: new Dictionary<uint, uint>());

        var requirement = new VtankLootRequirement
        {
            Type = 2003,
            Payload = $"10\r\n{BonusIntKey}\r\n",
        };
        // Base key exists (value 0) so the +20 bonus applies: 20 >= 10.
        Assert.True(VtankLootRequirementEvaluator.IsMatch(
            [requirement], item, properties, host: null, out string? error));
        Assert.Null(error);
    }

    private const uint BonusDoubleKey = 29u;
    private const uint BonusDoubleAdditiveSpellId = 2600u;

    [Fact]
    public void BuffedDoubleRequirementDoesNotApplyBonusWhenBaseKeyIsAbsent()
    {
        PluginInventoryItem item = Item() with
        {
            AppraisedSpellIds = [BonusDoubleAdditiveSpellId],
        };
        var properties = new PluginItemProperties(
            Ints: new Dictionary<uint, int>(),
            Int64s: new Dictionary<uint, long>(),
            Bools: new Dictionary<uint, bool>(),
            Floats: new Dictionary<uint, double>(), // key 29 absent entirely
            Strings: new Dictionary<uint, string>(),
            DataIds: new Dictionary<uint, uint>(),
            InstanceIds: new Dictionary<uint, uint>());

        // Type 2005 = BuffedDoubleValKeyGE: values[0]=threshold, values[1]=key.
        // Pre-fix (no double-side gate) this would read base=0, add the
        // +.03 bonus unconditionally (0.03 >= 0.02 => true); the gate must
        // keep it at the un-buffed base value (0 >= 0.02 => false) since the
        // key never existed at all.
        var requirement = new VtankLootRequirement
        {
            Type = 2005,
            Payload = $"0.02\r\n{BonusDoubleKey}\r\n",
        };
        bool matched = VtankLootRequirementEvaluator.IsMatch(
            [requirement], item, properties, host: null, out string? error);
        Assert.Null(error);
        Assert.False(matched);
    }

    [Fact]
    public void BuffedDoubleRequirementAppliesAdditiveBonusWhenBaseKeyExists()
    {
        PluginInventoryItem item = Item() with
        {
            AppraisedSpellIds = [BonusDoubleAdditiveSpellId],
        };
        var properties = new PluginItemProperties(
            Ints: new Dictionary<uint, int>(),
            Int64s: new Dictionary<uint, long>(),
            Bools: new Dictionary<uint, bool>(),
            Floats: new Dictionary<uint, double> { [BonusDoubleKey] = 0d }, // key exists, base 0
            Strings: new Dictionary<uint, string>(),
            DataIds: new Dictionary<uint, uint>(),
            InstanceIds: new Dictionary<uint, uint>());

        var requirement = new VtankLootRequirement
        {
            Type = 2005,
            Payload = $"0.02\r\n{BonusDoubleKey}\r\n",
        };
        // Base key exists (value 0) so the +.03 additive bonus applies:
        // 0.03 >= 0.02.
        Assert.True(VtankLootRequirementEvaluator.IsMatch(
            [requirement], item, properties, host: null, out string? error));
        Assert.Null(error);
    }

    [Fact]
    public void BuffedDoubleRequirementAppliesMultiplicativeBonusWhenChangeIsSet()
    {
        const uint MultiplicativeKey = 144u;
        const uint MultiplicativeSpellId = 3201u;
        PluginInventoryItem item = Item() with
        {
            AppraisedSpellIds = [MultiplicativeSpellId],
        };
        var properties = new PluginItemProperties(
            Ints: new Dictionary<uint, int>(),
            Int64s: new Dictionary<uint, long>(),
            Bools: new Dictionary<uint, bool>(),
            Floats: new Dictionary<uint, double> { [MultiplicativeKey] = 10d }, // base 10
            Strings: new Dictionary<uint, string>(),
            DataIds: new Dictionary<uint, uint>(),
            InstanceIds: new Dictionary<uint, uint>());

        var additiveWouldPass = new VtankLootRequirement
        {
            Type = 2005,
            Payload = $"10.6\r\n{MultiplicativeKey}\r\n",
        };
        // Additive would give 11.05 (>= 10.6 => true); multiplicative gives
        // 10.5 (>= 10.6 => false). Observing "false" here proves the branch
        // took the multiplicative path.
        Assert.False(VtankLootRequirementEvaluator.IsMatch(
            [additiveWouldPass], item, properties, host: null, out string? error));
        Assert.Null(error);
    }

    private static PluginInventoryItem Item() => new(
        0u, 0u, "Test Item", 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
        1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);
}
