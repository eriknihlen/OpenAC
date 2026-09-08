using AcDream.Core.Items;
using AcDream.Core.Spells;

namespace AcDream.Core.Tests.Spells;

public sealed class SpellComponentRequirementServiceTests
{
    [Fact]
    public void RequiresEveryFormulaScidMappedToOwnedWcid()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 2u,
            WeenieClassId = 100u,
            ContainerId = 1u,
            IsComponentPack = true,
        });
        var service = Service(objects, [10u, 11u], new Dictionary<uint, uint>
        {
            [10u] = 100u,
            [11u] = 101u,
        });

        Assert.False(service.HasRequiredComponents(50u));

        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 3u,
            WeenieClassId = 101u,
            ContainerId = 1u,
            IsComponentPack = true,
        });
        Assert.True(service.HasRequiredComponents(50u));
    }

    [Fact]
    public void ServerDisabledComponents_BypassesPreflightAndUsesModernFormula()
    {
        var objects = new ClientObjectTable();
        var player = new ClientObject { ObjectId = 1u, Name = "Player" };
        player.Properties.Bools[SpellComponentRequirementService.SpellComponentsRequiredProperty] = false;
        objects.AddOrUpdate(player);

        SpellComponentRequirementService service =
            Service(objects, [1u, 63u, 10u], new Dictionary<uint, uint>());

        Assert.True(service.HasRequiredComponents(50u));
        Assert.Equal(new uint[] { 1u, 0xBCu }, service.GetAppropriateFormula(50u));
    }

    [Fact]
    public void PersonalizedFormula_UsesExactCanonicalAccountSpelling()
    {
        const string canonical = "CanonicalAccount";
        const string loginSpelling = "canonicalaccount";
        uint[] formula = [6u, 63u, 10u, 64u, 20u, 30u, 65u, 40u];
        IReadOnlyList<uint> canonicalFormula = RetailSpellFormula.CustomizeForAccount(
            formula, 3u, canonical);
        IReadOnlyList<uint> loginFormula = RetailSpellFormula.CustomizeForAccount(
            formula, 3u, loginSpelling);
        Assert.NotEqual(canonicalFormula, loginFormula);

        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        var map = canonicalFormula.Where(scid => scid != 0u)
            .Distinct()
            .ToDictionary(scid => scid, scid => scid + 1000u);
        foreach (uint wcid in map.Values)
            objects.AddOrUpdate(new ClientObject
            {
                ObjectId = wcid + 10000u,
                WeenieClassId = wcid,
                ContainerId = 1u,
            });
        string account = canonical;
        var service = new SpellComponentRequirementService(
            objects, () => 1u, () => account,
            new Dictionary<uint, SpellFormulaDefinition> { [50u] = new(3u, formula) },
            map);

        Assert.True(service.HasRequiredComponents(50u));
        account = loginSpelling;
        Assert.False(service.HasRequiredComponents(50u));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InfusedSchoolOrDirectlyCarriedFocus_UsesScarabOnlyFormula(
        bool infused,
        bool carriesFocus)
    {
        var objects = new ClientObjectTable();
        var player = new ClientObject { ObjectId = 1u, Name = "Player" };
        if (infused)
            player.Properties.Ints[0x129u] = 1;
        objects.AddOrUpdate(player);
        if (carriesFocus)
        {
            objects.AddOrUpdate(new ClientObject
            {
                ObjectId = 2u,
                WeenieClassId = 900u,
                ContainerId = 1u,
                Type = ItemType.Container,
            });
        }
        // War I scarab-only formula is SCID 1 + one prismatic taper (0xBC).
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 3u,
            WeenieClassId = 101u,
            ContainerId = 1u,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 4u,
            WeenieClassId = 188u,
            ContainerId = 1u,
        });
        var service = new SpellComponentRequirementService(
            objects, () => 1u, () => "testaccount",
            new Dictionary<uint, SpellFormulaDefinition>
            {
                [50u] = new(0u, [1u, 63u, 10u], School: 1u),
            },
            new Dictionary<uint, uint>
            {
                [1u] = 101u,
                [0xBCu] = 188u,
            },
            new Dictionary<uint, uint> { [1u] = 900u });

        Assert.True(service.HasRequiredComponents(50u));
    }

    [Fact]
    public void FocusNestedInsidePack_DoesNotCountAsRetailMagicPackOwnership()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 2u,
            WeenieClassId = 700u,
            ContainerId = 1u,
            Type = ItemType.Container,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 3u,
            WeenieClassId = 900u,
            ContainerId = 2u,
            Type = ItemType.Container,
        });
        var service = new SpellComponentRequirementService(
            objects, () => 1u, () => "testaccount",
            new Dictionary<uint, SpellFormulaDefinition>
            {
                [50u] = new(0u, [1u, 63u], School: 1u),
            },
            new Dictionary<uint, uint>(),
            new Dictionary<uint, uint> { [1u] = 900u });

        Assert.False(service.HasRequiredComponents(50u));
    }

    [Fact]
    public void ExaminationFormulaPreservesOrderAndReportsNestedOwnership()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 2u,
            ContainerId = 1u,
            Type = ItemType.Container,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 3u,
            WeenieClassId = 101u,
            ContainerId = 2u,
        });
        var service = Service(
            objects,
            [10u, 11u, 10u],
            new Dictionary<uint, uint>
            {
                [10u] = 101u,
                [11u] = 102u,
            });

        Assert.Equal(
            new uint[] { 10u, 11u, 10u },
            service.GetAppropriateFormula(50u));
        Assert.True(service.IsComponentOwned(10u));
        Assert.False(service.IsComponentOwned(11u));
    }

    private static SpellComponentRequirementService Service(
        ClientObjectTable objects,
        IReadOnlyList<uint> components,
        IReadOnlyDictionary<uint, uint> map) => new(
            objects,
            () => 1u,
            () => "testaccount",
            new Dictionary<uint, SpellFormulaDefinition>
            {
                [50u] = new(0u, components),
            },
            map);
}
