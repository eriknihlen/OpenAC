using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class CombatControllerTests
{
    [Fact]
    public void ApproachClosesFromConfiguredRangeBeforeAttacking()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 12, angle: 0)],
            NavigationSnapshot = NavigationAt(heading: 0f),
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(0x7F7F0001, 0d, 0.1d, 0d, 0f, true));
        var settings = new CombatSettings
        {
            MaximumRange = 5f,
            ApproachDistance = 20f,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.05d, navigationEnabled: true);

        PluginMovementIntent intent = Assert.Single(surface.MovementIntents);
        Assert.True(intent.Forward);
        Assert.Equal(0, surface.BeginCount);
        Assert.Contains("Approaching", controller.Status, StringComparison.Ordinal);

        surface.Targets = [Target(10, "Drudge", distance: 4, angle: 0)];
        controller.OnTick(0.05d, navigationEnabled: true);

        Assert.Equal(1, surface.ClearMovementCount);
        Assert.Equal(10u, surface.LastBeginTarget);
    }

    private static (FakeAutomation Surface, CombatController Controller)
        ApproachRig(float selfHeading, double targetEastWest, double targetNorthSouth)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 12, angle: 0)],
            NavigationSnapshot = NavigationAt(selfHeading),
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(
                0x7F7F0001,
                targetEastWest,
                targetNorthSouth,
                0d,
                0f,
                true));
        var settings = new CombatSettings
        {
            MaximumRange = 5f,
            ApproachDistance = 20f,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        return (surface, controller);
    }

    [Fact]
    public void ApproachStopsAndFacesTheTargetOutsideTheFourDegreeBand()
    {
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.0839d, targetNorthSouth: 0.1d);

        controller.OnTick(0.05d, navigationEnabled: true);

        Assert.Empty(surface.MovementIntents);
        Assert.Equal(1, surface.ClearMovementCount);
        float faced = Assert.Single(surface.FacedHeadings);
        Assert.InRange(faced, 39f, 41f);
        Assert.Contains("Turning to", controller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ApproachDoesNotReissueFaceHeadingInsideSevenTenthsOfASecond()
    {
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.0839d, targetNorthSouth: 0.1d);

        // fd.cs:336 — two 293 ms passes fall inside the 0.7 s `p` stamp.
        controller.OnTick(0.293d, navigationEnabled: true);
        controller.OnTick(0.293d, navigationEnabled: true);

        Assert.Single(surface.FacedHeadings);

        controller.OnTick(0.293d, navigationEnabled: true);
        Assert.Single(surface.FacedHeadings);
        controller.OnTick(0.293d, navigationEnabled: true);
        Assert.Equal(2, surface.FacedHeadings.Count);
    }

    [Fact]
    public void ApproachRunsForwardInsideTheFourDegreeBandWithoutFacingAgain()
    {
        // Target ~1.7° east of north.
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.003d, targetNorthSouth: 0.1d);

        controller.OnTick(0.05d, navigationEnabled: true);

        PluginMovementIntent intent = Assert.Single(surface.MovementIntents);
        Assert.True(intent.Forward);
        Assert.True(intent.Run);
        Assert.Empty(surface.FacedHeadings);
        Assert.Contains("Approaching", controller.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(40f)]
    [InlineData(-40f)]
    [InlineData(140f)]
    public void ApproachNeverIssuesHeldTurnKeyIntents(float selfHeading)
    {
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading, targetEastWest: 0d, targetNorthSouth: 0.1d);

        for (int pass = 0; pass < 6; pass++)
            controller.OnTick(0.293d, navigationEnabled: true);

        Assert.DoesNotContain(
            surface.MovementIntents,
            static intent => intent.TurnLeft || intent.TurnRight);
    }


    [Fact]
    public void HigherPriorityRuleWinsEvenWhenTargetIsFarther()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "Drudge", distance: 2, angle: 0),
                Target(20, "Olthoi Soldier", distance: 12, angle: 30),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Range,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(20u, surface.LastBeginTarget);
        Assert.Contains("Olthoi Soldier", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void SamePriorityTargetsDoNotFlipFlopBetweenScans()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "Drudge", distance: 3, angle: 0),
                Target(20, "Drudge", distance: 9, angle: 0),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "Drudge", distance: 9, angle: 0),
            Target(20, "Drudge", distance: 2, angle: 0),
        ];
        controller.OnTick(0.25);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Contains("9.0m", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetTextDistanceIsInvariantUnderASwedishCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            CultureInfo.CurrentUICulture = new CultureInfo("sv-SE");
            var surface = new FakeAutomation
            {
                CombatSnapshot = Physical(),
                Targets = [Target(10, "Drudge", distance: 9, angle: 0)],
                EquipmentItems = [WieldedPlannedWeapon()],
            };
            var settings = new CombatSettings
            {
                MaximumRange = 20f,
                SelectionMethod = TargetSelectionMethod.Range,
                ScanIntervalSeconds = 0.05d,
            };
            ProfileFixtureWeapon(settings);
            var controller = new CombatController(new FakeHost(surface), settings);

            controller.Toggle();
            controller.OnTick(0.25);

            Assert.Contains("9.0m", controller.TargetText, StringComparison.Ordinal);
            Assert.DoesNotContain("9,0m", controller.TargetText, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Fact]
    public void StickyTargetStillYieldsToAHigherPriorityRule()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 3, angle: 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "Drudge", distance: 3, angle: 0),
            Target(20, "Olthoi Soldier", distance: 15, angle: 60),
        ];
        controller.OnTick(0.25);

        Assert.Equal(20u, surface.LastBeginTarget);
    }

    [Fact]
    public void TargetLockKeepsCurrentTargetAcrossRescan()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "First", 4, 20)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            TargetLock = true,
            ScanIntervalSeconds = 0.1,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.1);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "First", 4, 20),
            Target(11, "Closer", 1, 0),
        ];
        controller.OnTick(0.1);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Contains("First", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousValidTargetWinsAngleTieBreakWithoutTargetLock()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "First", 4, 1),
                Target(11, "Second", 4, 20),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            SelectionMethod = TargetSelectionMethod.Angle,
            TargetLock = false,
            ScanIntervalSeconds = 0.1,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.1);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "First", 4, 20),
            Target(11, "Second", 4, 1),
        ];
        controller.OnTick(0.1);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Contains("First", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousTargetDoesNotBeatNewHigherPriorityRule()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.1,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.1);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "Drudge", 2, 0),
            Target(20, "Olthoi Soldier", 4, 20),
        ];
        controller.OnTick(0.1);

        Assert.Equal(20u, surface.LastBeginTarget);
        Assert.Contains("Olthoi Soldier", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSelectionUsesAngleForNearTargetsAndRangeWhenNoneAreNear()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "Near side", 3, 80),
                Target(11, "Near ahead", 8, 5),
                Target(12, "Far", 20, 0),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Both,
            TargetSelectAngleRange = 10,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(11u, surface.LastBeginTarget);
    }

    [Fact]
    public void PhysicalAttackWaitsForConfiguredPowerBeforeRelease()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings { AttackPower = 0.6f };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(1, surface.BeginCount);

        surface.CombatSnapshot = Physical(
            request: true, build: true, bar: 0.59f) with
        {
            DesiredPower = 0.6f,
        };
        controller.OnTick(0.01);
        Assert.Equal(0, surface.ReleaseCount);

        surface.CombatSnapshot = Physical(
            request: true, build: true, bar: 0.60f) with
        {
            DesiredPower = 0.6f,
        };
        controller.OnTick(0.01);
        Assert.Equal(1, surface.ReleaseCount);
    }

    [Fact]
    public void CombatController_SummonsConfiguredPetBeforeStartingAttack()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            ItemEntries = [PetDevice(88, 49387)],
        };
        var settings = new CombatSettings { SummonPets = true };
        settings.CombatItemObjectIds.Add(88u);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(88u, surface.LastUsedItem);
        Assert.Equal(0, surface.BeginCount);
        Assert.Contains("Summoning", controller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void MagicModeCastsBestProjectedAttackOnExplicitTarget()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells = [Spell(100, "Incantation of Flame Bolt")],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface), FireAttackRule(new CombatSettings()));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        Assert.Contains("Flame Bolt", controller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void BreakableTurnFacesTargetBeforeDispatchingTargetedSpell()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    RequiresTurnTo = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
            NavigationSnapshot = NavigationAt(heading: 0f),
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(
                0x7F7F0001u,
                0.1d,
                0d,
                0d,
                0f,
                true));
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseBreakableTurnTo = true }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Empty(surface.MovementIntents);
        Assert.Equal(90f, Assert.Single(surface.FacedHeadings));
        Assert.Empty(surface.CastSpellIds);

        surface.NavigationSnapshot = NavigationAt(heading: 90f);
        controller.OnTick(0.25);

        Assert.Equal([100u], surface.CastSpellIds);
        Assert.Equal(2, surface.ClearMovementCount);
    }

    [Fact]
    public void BreakableTurnDoesNotReissueFaceHeadingInsideSevenTenthsOfASecond()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    RequiresTurnTo = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
            NavigationSnapshot = NavigationAt(heading: 0f),
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(
                0x7F7F0001u,
                0.1d,
                0d,
                0d,
                0f,
                true));
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseBreakableTurnTo = true }));

        controller.Toggle();
        controller.OnTick(0.293);
        controller.OnTick(0.293);

        Assert.Equal(90f, Assert.Single(surface.FacedHeadings));
        Assert.Empty(surface.CastSpellIds);

        controller.OnTick(0.293);
        Assert.Single(surface.FacedHeadings);
        controller.OnTick(0.293);
        Assert.Equal(2, surface.FacedHeadings.Count);
    }

    [Fact]
    public void ProjectileAwarenessBlocksSpellBeforeCastDispatch()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 3,
                BlockingObjectId: 0x50000001u),
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseProjectileAwareness = true }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Empty(surface.CastSpellIds);
        Assert.Contains("blocked", controller.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10u, surface.LastProjectileTarget);
    }

    [Fact]
    public void ProjectileAwarenessCanBeExplicitlyDisabled()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            ProjectilePath = new(PluginProjectilePathStatus.Blocked),
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseProjectileAwareness = false }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Contains(100u, surface.CastSpellIds);
        Assert.Equal(0u, surface.LastProjectileTarget);
    }

    [Fact]
    public void CollisionDebugPublishesDiagnosticSamplesToTheGraphicalHost()
    {
        PluginProjectileDebugSample[] samples =
        [new(new System.Numerics.Vector3(1f, 2f, 3f), false, 0.4f)];
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 1)
            {
                DebugSamples = samples,
            },
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings
            {
                UseProjectileAwareness = true,
                ShowCollisionDebug = true,
            }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(samples, surface.ShownProjectileDebugSamples);
    }

    [Fact]
    public void DoJiggleUsesRetailSelectionCycleInsteadOfMovingTheCharacter()
    {
        PluginSpellInfo attack = Spell(100, "Incantation of Flame Bolt") with
        {
            IsProjectile = false,
            School = 34,
            Difficulty = 300,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells = [attack],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            new CombatSettings
            {
                UseProjectileAwareness = false,
                DoJiggle = true,
            });
        controller.Toggle();
        controller.OnTick(0.25);
        surface.LastCastCompletion = new PluginCastCompletion(1, 100, 10, 0);

        controller.OnTick(0.01);
        controller.OnTick(0.131);

        Assert.Equal(
            [
                PluginSelectionAction.PreviousSelection,
                PluginSelectionAction.NextPlayer,
                PluginSelectionAction.PreviousPlayer,
            ],
            surface.SelectionActions);
        Assert.Empty(surface.MovementIntents);
    }

    [Fact]
    public void MagicRuleDebuffsAndWaitsForServerReceiptBeforeAttack()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [imperil],
            KnownAttackSpells = [Spell(100, "Incantation of Flame Bolt")],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil | MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((90u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(100u, surface.CastSpellIds);

        surface.LastCastCompletion = new PluginCastCompletion(1, 90, 10, 0);
        controller.OnTick(0.25);
        Assert.Contains(100u, surface.CastSpellIds);
    }

    [Fact]
    public void RingArmAlsoRequiresNoStreakColumnAndANonZeroTally()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(110, "Cassius' Ring of Fire", difficulty: 300) with
            {
                TargetMask = 0u,
                IsUntargeted = true,
            },
            MagicSpell(102, "Flame Streak VII", difficulty: 350),
            MagicSpell(100, "Flame Bolt VII", difficulty: 300),
        ];

        (uint untargeted, (uint, uint) targeted) = CastRingScenario(
            known,
            MonsterActionFlags.Ring | MonsterActionFlags.Streak,
            distance: 3f,
            ringDistance: 5d);
        Assert.Equal(0u, untargeted);
        Assert.Equal(102u, targeted.Item1);

        // Ring only, but the monster is beyond RingDistance so the tally is
        // zero (dz.cs:736-739): the ring arm fails and the pass bolts.
        (untargeted, targeted) = CastRingScenario(
            known,
            MonsterActionFlags.Ring,
            distance: 12f,
            ringDistance: 5d);
        Assert.Equal(0u, untargeted);
        Assert.Equal(100u, targeted.Item1);
    }

    private static (uint Untargeted, (uint, uint) Targeted) CastRingScenario(
        IReadOnlyList<PluginSpellInfo> known,
        MonsterActionFlags flags,
        float distance,
        double ringDistance)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", distance, 0)],
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            RingDistance = ringDistance,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = flags,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        return (surface.LastUntargetedCast, surface.LastTargetedCast);
    }

    [Fact]
    public void StrongerBoltBeatsTheArcEvenWithUseArcsYes()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 20, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                MagicSpell(101, "Flame Arc IV", difficulty: 150),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            UseArcs = UseArcsMode.Yes,
            ArcRange = 1d,
            MaximumRange = 40d,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
    }

    [Fact]
    public void UseArcsDecidesOnlyAnExactQualityTie()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(100, "Flame Bolt VII", difficulty: 300),
            MagicSpell(101, "Flame Arc VII", difficulty: 300),
        ];

        Assert.Equal((101u, 10u), CastWithUseArcs(known, UseArcsMode.Yes, distance: 20));
        Assert.Equal((100u, 10u), CastWithUseArcs(known, UseArcsMode.No, distance: 20));
        // hi.cs:521-531 — AtRange arcs only once f7.e >= ArcRange.
        Assert.Equal(
            (101u, 10u),
            CastWithUseArcs(known, UseArcsMode.AtRange, distance: 20, arcRange: 10));
        Assert.Equal(
            (100u, 10u),
            CastWithUseArcs(known, UseArcsMode.AtRange, distance: 5, arcRange: 10));
    }

    private static (uint, uint) CastWithUseArcs(
        IReadOnlyList<PluginSpellInfo> known,
        UseArcsMode mode,
        double distance,
        double arcRange = 1d)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", (float)distance, 0)],
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings
            {
                UseArcs = mode,
                ArcRange = arcRange,
                MaximumRange = 40d,
            }));
        controller.Toggle();
        controller.OnTick(0.25);
        return surface.LastTargetedCast;
    }

    [Fact]
    public void AttackPlusStreakUsesTheStreakOnlyAsAFinishingBlow()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(100, "Flame Bolt VII", difficulty: 300),
            MagicSpell(102, "Flame Streak VII", difficulty: 350),
        ];

        Assert.Equal(100u, CastAgainstHealth(known, healthFraction: 0.9f).Item1);
        Assert.Equal(102u, CastAgainstHealth(known, healthFraction: 0.02f).Item1);
    }

    private static (uint, uint) CastAgainstHealth(
        IReadOnlyList<PluginSpellInfo> known,
        float healthFraction)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                new PluginCombatTarget(
                    10u, "Drudge", 1010u, 5f, 0f, true, healthFraction)
                {
                    MaximumHealth = 1000,
                },
            ],
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack | MonsterActionFlags.Streak,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        return surface.LastTargetedCast;
    }

    private static PluginSpellInfo MagicSpell(
        uint id,
        string name,
        int difficulty) => new(
            id,
            name,
            Family: id,
            Tier: 7,
            Difficulty: difficulty,
            ManaCost: 30,
            DurationSeconds: 0,
            School: 34,
            Description: string.Empty,
            IsSelfTargeted: false,
            IsBeneficial: false)
        {
            IsOffensive = true,
            TargetMask = 0x10,
        };

    [Fact]
    public void DebuffsGoOutInRetailsTwelveStepOrder()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                Debuff(80, "Fester Other VII"),
                Debuff(81, "Broadside of a Barn"),
                Debuff(82, "Fire Vulnerability Other VII"),
                Debuff(83, "Imperil Other VII"),
                Debuff(84, "Magic Yield Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil
                    | MonsterActionFlags.Vulnerability
                    | MonsterActionFlags.Broadside
                    | MonsterActionFlags.Fester,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();

        long revision = 0;
        foreach (uint expected in new uint[] { 84u, 83u, 82u, 81u, 80u })
        {
            controller.OnTick(0.25);
            Assert.Equal((expected, 10u), surface.LastTargetedCast);
            surface.LastCastCompletion = new PluginCastCompletion(
                ++revision, expected, 10u, 0);
            controller.OnTick(0.25);
        }
    }

    [Fact]
    public void OnlyOneDebuffKindIsDispatchedPerPass()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                Debuff(83, "Imperil Other VII"),
                Debuff(84, "Magic Yield Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal([84u], surface.CastSpellIds);
    }

    [Fact]
    public void BlockedDebuffPathDropsThatColumnAndTheChainMovesOn()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                // Step 1 of hi.cs:123-206, and the only projectile here.
                Debuff(84, "Magic Yield Other VII") with { IsProjectile = true },
                // Step 7.
                Debuff(83, "Imperil Other VII"),
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 3,
                BlockingObjectId: 0x50000001u),
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
        };
        Assert.False(settings.AllowDebuffFallback);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((83u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(84u, surface.CastSpellIds);
    }

    [Fact]
    public void BlockedDebuffPathStillLetsTheAttackGoOutOnStockSettings()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                Debuff(84, "Magic Yield Other VII") with { IsProjectile = true },
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
            ],
            ProjectilePath = new(PluginProjectilePathStatus.Blocked),
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack | MonsterActionFlags.Yield,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
    }

    [Fact]
    public void AnUnknownVulnScoresNoUrgencyForTheTargetItCannotDebuff()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                Target(10, "Drudge", distance: 9, angle: 0),
                Target(20, "Rat", distance: 2, angle: 0),
            ],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                MagicSpell(101, "Acid Stream VII", difficulty: 300),
                // The Fire Vuln line is deliberately absent.
                Debuff(85, "Acid Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            SelectionMethod = TargetSelectionMethod.Range,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "name#^Drudge",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        settings.Rules.Add(new MonsterRule(
            "name#^Rat",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Acid,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((101u, 20u), surface.LastTargetedCast);
    }

    [Fact]
    public void ThePlannedWeaponsOwnElementPreemptsTheDamageTable()
    {
        PluginEquipmentItem fireWand = Equipment(
            990u,
            "Flame Wand",
            damageType: 0x0010,
            itemType: CombatModeGate.CasterItemType,
            equippedLocation: 0x00100000u);
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Magma Golem", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                MagicSpell(102, "Frost Bolt VII", difficulty: 300),
            ],
            EquipmentItems = [fireWand],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
                WeaponName = "Flame Wand",
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            vitalSettings: null,
            GameInfo);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
    }

    [Fact]
    public void AnUndeliverablePreferenceListFallsThroughToTheUnlistedWalk()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Magma Golem", 5, 0)],
            KnownCombatSpells = [MagicSpell(101, "Acid Stream VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            vitalSettings: null,
            GameInfo);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((101u, 10u), surface.LastTargetedCast);
        Assert.Contains(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Using unlisted damage type: Acid",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TheLoadedGameInfoDatabaseDrivesTheAutoAttackElement()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Magma Golem", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(103, "Force Bolt VII", difficulty: 300),
                MagicSpell(105, "Shock Wave VII", difficulty: 300),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            vitalSettings: null,
            GameInfo);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((105u, 10u), surface.LastTargetedCast);
    }

    [Fact]
    public void AFistsRowCastsTuskerFistsBeforeTheEnchantmentIsUp()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(0x0B76u, "Tusker Fists", difficulty: 300),
                MagicSpell(105, "Shock Wave VII", difficulty: 300),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fists,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((0x0B76u, 10u), surface.LastTargetedCast);
    }

    [Fact]
    public void AFistsRowWithoutTuskerFistsAttacksWithBludgeon()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(105, "Shock Wave VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fists,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((105u, 10u), surface.LastTargetedCast);
    }

    private static PluginSpellInfo Debuff(uint id, string name) => new(
        id,
        name,
        Family: id,
        Tier: 7,
        Difficulty: 250,
        ManaCost: 30,
        DurationSeconds: 60,
        School: 31,
        Description: string.Empty,
        IsSelfTargeted: false,
        IsBeneficial: false)
    {
        IsDebuff = true,
        IsOffensive = true,
        TargetMask = 0x10,
    };

    [Fact]
    public void UnknownMonsterFallsToRetailsUnlistedElementAndCastsNoExtraVuln()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "New Server Creature", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Force Bolt VII", difficulty: 300),
                Debuff(85, "Piercing Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
                // Ex. Vuln = Auto resolves through the damage table, which has
                // nothing for this monster, so step 9 never runs.
                ExtraVulnerability = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        // Pierce is eDamageElement 0, the first entry of ga.cs:772-774's walk.
        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(85u, surface.CastSpellIds);
        Assert.Contains(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Using unlisted damage type: Pierce",
                StringComparison.Ordinal));
    }

    [Fact]
    public void PermanentFailResultTextForceBlacklistsTheTarget()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);

        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty, "Drudge is an invalid target.", string.Empty),
        ];
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.Contains(
            "Waiting for a target",
            controller.Status,
            StringComparison.Ordinal);
        Assert.False(controller.HasTarget);
    }

    [Fact]
    public void PermanentFailIsIgnoredWhileAMultiTargetSpellIsInFlight()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300) with
                {
                    Family = 638u,
                },
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);

        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty, "Drudge is an invalid target.", string.Empty),
        ];
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.True(controller.HasTarget);
    }

    [Fact]
    public void AKillLineEndsTheTargetBeforeTheWorldRemovesIt()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        int castsBeforeTheKill = surface.CastSpellIds.Count;

        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty, "You killed Drudge!", string.Empty),
        ];
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.False(controller.HasTarget);
        Assert.Equal(castsBeforeTheKill, surface.CastSpellIds.Count);
        Assert.Contains(
            "Waiting for a target",
            controller.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultWaitTimeoutBumpsTheBlacklistAttemptCount()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            BlacklistMonsterAttemptCount = 1,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);

        controller.CastTracker.ObserveCompletion(
            new PluginCastCompletion(1, 100u, 10u, 0u));
        Assert.True(controller.HasTarget);

        // 4 x 907 ms of the result timer (gj.cs:254-255).
        controller.CastTracker.Advance(3.7d);
        controller.OnTick(0.25);

        Assert.False(controller.HasTarget);
    }

    [Fact]
    public void AServerRefusedCastDoesNotCountAgainstTheTarget()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            BlacklistMonsterAttemptCount = 1,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);

        surface.LastCastCompletion = new PluginCastCompletion(1, 100u, 10u, 0x1Du);
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.True(controller.HasTarget);
    }

    [Fact]
    public void ATwentyCandidateScanDoesNotRebuildThePlanPerCandidate()
    {
        var targets = new List<PluginCombatTarget>();
        for (uint i = 0; i < 20u; i++)
            targets.Add(Target(10u + i, "Drudge", distance: 3f + i, angle: 0));
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = targets,
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(80, "Fester Other VII"),
                Debuff(81, "Broadside of a Barn"),
                Debuff(82, "Fire Vulnerability Other VII"),
                Debuff(83, "Imperil Other VII"),
                Debuff(84, "Magic Yield Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil
                    | MonsterActionFlags.Vulnerability
                    | MonsterActionFlags.Broadside
                    | MonsterActionFlags.Fester,
                DamageType = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        // Measured: 2 and 145 with the memos, 169 and 211 without.
        Assert.InRange(surface.CaptureOwnedEquipmentCount, 1, 20);
        Assert.InRange(surface.KnownCombatSpellReads, 1, 180);
    }

    [Fact]
    public void KillAndSuccessResultTextClearTheBlacklist()
    {
        var settings = new CombatSettings { BlacklistMonsterTimeoutSeconds = 300 };
        var tracker = new CombatFailureTracker();

        tracker.ForceBlacklist(10u, now: 0d, settings);
        Assert.Equal(
            CombatSuppressionReason.Blacklisted,
            tracker.Reason(10u, now: 1d));

        tracker.ClearBlacklist(10u);
        Assert.Equal(CombatSuppressionReason.None, tracker.Reason(10u, now: 1d));
    }

    [Fact]
    public void RingOnlyRuleCastsUntargetedRingWithOneNearbyRingTarget()
    {
        PluginSpellInfo ring = Spell(110, "Cassius' Ring of Fire") with
        {
            IsOffensive = true,
            TargetMask = 0,
            Description = "Shoots waves of fire outward from the caster.",
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 3, 0)],
            KnownCombatSpells = [ring],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Ring,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(110u, surface.LastUntargetedCast);
        Assert.Equal(default, surface.LastTargetedCast);
    }

    [Fact]
    public void ExplicitMonsterWeaponUsesCanonicalEquipmentCommandBeforeAttack()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(700, "Fire Sword", damageType: 0x10),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                WeaponObjectId = 700,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(700u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void ExplicitMonsterWeaponResolvesDurableNameAfterRelogChangesObjectId()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(900, "Fire Sword", damageType: 0x10),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                WeaponObjectId = 700,
                WeaponName = "Fire Sword",
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(900u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AutomaticDamageSelectionChoosesStrongestMatchingWeapon()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(700, "Weak fire", damageType: 0x10, damage: 20),
                Equipment(701, "Strong fire", damageType: 0x10, damage: 35),
                Equipment(702, "Acid", damageType: 0x20, damage: 99),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Strong fire");
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(701u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AttackRoutesTheCombatModeThroughTheSharedGateNotEnterDefaultMode()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "War Wand",
                    damageType: 0,
                    equippedLocation: 0x00100000u,
                    itemType: CombatModeGate.CasterItemType),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        // The wand is already wielded; only the mode is wrong. The gate
        // recomputes the mode the wielded item implies and asks for it
        // (ga.cs:1556-1565).
        Assert.Contains("EnterMode:Magic", surface.CallLog);
        Assert.Equal(PluginCombatMode.Magic, surface.CombatSnapshot.Mode);
    }

    [Fact]
    public void AttackWithNothingWieldedAndAnEmptyItemsProfileStopsWithTheWandNotice()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems = [],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(
            "[MossTank] " + CombatModeGate.NoWandNotice,
            Assert.Single(
                surface.PostedSystemMessages,
                message => message.Contains(
                    CombatModeGate.NoWandNotice,
                    StringComparison.Ordinal)));
        Assert.False(controller.Enabled);
        Assert.DoesNotContain("EnterDefaultMode", surface.CallLog);
        Assert.DoesNotContain("EnterMode:Melee", surface.CallLog);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AttackWithNothingWieldedWieldsTheProfiledWandAndRequestsMagic()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "War Wand",
                    damageType: 0,
                    itemType: CombatModeGate.CasterItemType),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("War Wand");
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        int equip = surface.CallLog.IndexOf("Equip:000002BC");
        int magic = surface.CallLog.IndexOf("EnterMode:Magic");
        Assert.True(equip >= 0, "the profiled wand was never wielded");
        Assert.True(magic > equip, "Magic was requested before the wand: "
            + string.Join(" | ", surface.CallLog));
        Assert.True(controller.Enabled);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                CombatModeGate.NoWandNotice,
                StringComparison.Ordinal));
        Assert.DoesNotContain("EnterDefaultMode", surface.CallLog);
    }

    [Fact]
    public void AttackWithAWieldedSwordButNoProfiledWeaponStillTakesTheMagicArm()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems =
            [
                // Wielded, but in NO profile — SelectAutomaticWeapon skips
                // every unprofiled item, so the plan is 0.
                Equipment(
                    900,
                    "Unprofiled Sword",
                    damageType: 0x0001,
                    equippedLocation: 0x00100000u),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(
            "[MossTank] " + CombatModeGate.NoWandNotice,
            Assert.Single(
                surface.PostedSystemMessages,
                message => message.Contains(
                    CombatModeGate.NoWandNotice,
                    StringComparison.Ordinal)));
        Assert.False(controller.Enabled);
        Assert.DoesNotContain("EnterDefaultMode", surface.CallLog);
        Assert.DoesNotContain("EnterMode:Melee", surface.CallLog);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AttackWithAProfiledMeleeWeaponTakesThePhysicalArmThroughTheGate()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems = [Equipment(900, "Fire Sword", damageType: 0x10)],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Fire Sword");
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 6; tick++)
            controller.OnTick(0.25);

        int equip = surface.CallLog.IndexOf("Equip:00000384");
        int melee = surface.CallLog.IndexOf("EnterMode:Melee");
        Assert.True(equip >= 0, "the planned weapon was never wielded");
        Assert.True(melee > equip, "Melee was requested before the weapon: "
            + string.Join(" | ", surface.CallLog));
        Assert.True(controller.Enabled);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                CombatModeGate.NoWandNotice,
                StringComparison.Ordinal));
        Assert.DoesNotContain("EnterDefaultMode", surface.CallLog);
    }

    [Fact]
    public void MissileLauncherSelectsOfficialBestAvailableAmmunition()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            CharacterSkills =
            [
                new PluginSkillInfo(
                    47u,
                    "Missile Weapons",
                    PluginSkillTraining.Trained,
                    300u)
                {
                    Base = 300u,
                },
            ],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "Fire Bow",
                    damageType: 0x10,
                    equippedLocation: 0x00100000u,
                    ammoType: 0x001u),
                Equipment(
                    801,
                    "Deadly Fire Arrow",
                    damageType: 0x10,
                    combatUse: 3,
                    ammoType: 0x001u,
                    stackSize: 20),
            ],
            ItemEntries =
            [
                InventoryItem(
                    801,
                    "Deadly Fire Arrow",
                    itemType: 0x100,
                    spellId: 0,
                    equipped: false),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponObjectId = 700,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(801u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AmmunitionWieldGoesThroughRetailsDropToPeacePrologue()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Missile },
            Targets = [Target(10, "Drudge", 2, 0)],
            CharacterSkills =
            [
                new PluginSkillInfo(
                    47u, "Missile Weapons", PluginSkillTraining.Trained, 300u)
                {
                    Base = 300u,
                },
            ],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "Fire Bow",
                    damageType: 0x10,
                    equippedLocation: 0x00100000u,
                    ammoType: 0x001u),
                Equipment(
                    801,
                    "Deadly Fire Arrow",
                    damageType: 0x10,
                    combatUse: 3,
                    ammoType: 0x001u,
                    stackSize: 20),
            ],
            ItemEntries =
            [
                InventoryItem(801, "Deadly Fire Arrow", 0x100, 0, equipped: false),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponObjectId = 700,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 6; tick++)
            controller.OnTick(0.25);

        // bv.cs:194-195 then :204 — Peace is asked for BEFORE the arrow is
        // wielded, never after.
        int peace = surface.CallLog.IndexOf("EnterMode:Peace");
        int equip = surface.CallLog.IndexOf("Equip:00000321");
        Assert.True(peace >= 0, "Peace was never requested: "
            + string.Join(" | ", surface.CallLog));
        Assert.True(equip > peace, "The arrow was wielded before Peace: "
            + string.Join(" | ", surface.CallLog));
        Assert.Equal(801u, surface.LastEquipObjectId);
    }

    [Fact]
    public void AmmunitionStillWieldsThroughThePanelsExternallyBoundGate()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            CharacterSkills =
            [
                new PluginSkillInfo(
                    47u, "Missile Weapons", PluginSkillTraining.Trained, 300u)
                {
                    Base = 300u,
                },
            ],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "Fire Bow",
                    damageType: 0x10,
                    equippedLocation: 0x00100000u,
                    ammoType: 0x001u),
                Equipment(
                    801,
                    "Deadly Fire Arrow",
                    damageType: 0x10,
                    combatUse: 3,
                    ammoType: 0x001u,
                    stackSize: 20),
            ],
            ItemEntries =
            [
                InventoryItem(801, "Deadly Fire Arrow", 0x100, 0, equipped: false),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponObjectId = 700,
            }));
        var vitals = new VitalSettings();
        var host = new FakeHost(surface);
        var controller = new CombatController(host, settings, vitals);

        // Exactly MossTankPanel.cs:420-425 — the one shared gate, injected.
        var gate = new CombatModeGate(host, settings, vitals, _ => { });
        controller.BindCombatModeGate(gate);

        controller.Toggle();
        for (int tick = 0; tick < 6; tick++)
            controller.OnTick(0.25);

        Assert.Equal(801u, surface.LastEquipObjectId);
        Assert.NotNull(gate.AmmunitionStale);
        Assert.NotNull(gate.WieldAmmunition);
    }

    [Fact]
    public void StuckCombatModeUsesProfiledCasterAfterRetailRetryCount()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            IgnoreModeChanges = true,
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(700, "Fire Sword", damageType: 0x10),
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                WeaponObjectId = 700,
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            new VitalSettings { DropToPeaceModeRetryCount = 2 });

        controller.Toggle();
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(800u, surface.LastUsedItem);
        Assert.Equal(0u, surface.LastEquipObjectId);
        Assert.Contains(
            "bugged combat state",
            controller.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionLossDisablesAndAborts()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
        };
        var controller = new CombatController(
            new FakeHost(surface), new CombatSettings());
        controller.Toggle();
        surface.IsAvailable = false;

        controller.OnTick(0.1);

        Assert.False(controller.Enabled);
        Assert.Equal(1, surface.AbortCount);
        Assert.Equal("Session ended", controller.Status);
    }

    [Fact]
    public void CasterItemDebuffWaitsForUseDoneAndConfirmedCastChat()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        PluginInventoryItem lens = InventoryItem(
            800, "Imperil Lens", 0x8000, 90, equipped: true) with
        {
            ItemSpellcraft = 400,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries = [lens],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.CombatItemObjectIds.Add(lens.ObjectId);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((800u, 10u), surface.LastAppliedItem);
        Assert.Equal(0, surface.BeginCount);

        Assert.True(controller.HasPendingItemDebuff);

        surface.LastItemCompletion = new PluginItemUseCompletion(
            1, 800, 10, 0);
        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty,
                "You cast Imperil Other VII on Drudge.",
                string.Empty),
        ];
        controller.OnTick(0.25);

        Assert.Equal(1, surface.ApplyCount);
        Assert.Contains("Waiting for a target", controller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcWeaponChargesAtZeroAndRequiresCastChatNotAttackDone()
    {
        PluginSpellInfo imperil = Spell(91, "Imperil Other VII") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
            TargetMask = 0x10,
        };
        PluginInventoryItem sword = InventoryItem(
            801, "Imperil Sword", 1, 0, equipped: true) with
        {
            ItemSpellcraft = 400,
            AppraisedSpellIds = [91u],
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries = [sword],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.CombatItemObjectIds.Add(sword.ObjectId);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Equal(0f, surface.LastBeginPower);

        surface.CombatSnapshot = Physical(request: true, build: true, bar: 0);
        controller.OnTick(0.1);
        Assert.Equal(1, surface.ReleaseCount);

        surface.CombatSnapshot = Physical() with
        {
            CompletionRevision = 1,
            CompletionWeenieError = 0,
        };
        controller.OnTick(0.1);
        Assert.DoesNotContain("Waiting for a target", controller.Status, StringComparison.Ordinal);

        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty,
                "You cast Imperil Other VII on Drudge.",
                string.Empty),
        ];
        controller.OnTick(0.1);
        Assert.Contains("Waiting for a target", controller.Status, StringComparison.Ordinal);
    }


    private static CombatModeGate Gate(
        FakeAutomation surface,
        CombatSettings? settings = null,
        VitalSettings? vitals = null,
        List<string>? stops = null) =>
        new(
            new FakeHost(surface),
            settings ?? new CombatSettings(),
            vitals ?? new VitalSettings(),
            notice => (stops ?? []).Add(notice));

    [Fact]
    public void GateWieldedCasterInPeaceReAsksForMagicUntilTheClientAgrees()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            DeferModeConfirmation = true,
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(["EnterMode:Magic"], surface.CallLog);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.ModeChangeRequests);

        surface.ConfirmPendingModeChange();
        gate.AdvancePass(0.1);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.ModeChangeRequests);
    }

    [Fact]
    public void GateProfiledCasterNotWieldedWieldsThenEntersMagicInOrder()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Mode = Melee
            DeferModeConfirmation = true,
            SimulateAsyncEquip = true,
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(["EnterMode:Peace"], surface.CallLog);

        surface.ConfirmPendingModeChange();
        // R2-15: the ack RESTARTS the 600 ms window (f9.cs:322-328 stamps
        // m_h again), so f9.e() still reports the PRE-request mode for one
        // more pass and the drop-to-peace branch re-asks. That second request
        // re-stamps m_g from the now-Peace live mode, which is what lets the
        // pass after it proceed.
        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(
            ["EnterMode:Peace", "EnterMode:Peace"],
            surface.CallLog);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        // Equip only happens after the snapshot reports Peace, never before.
        Assert.Equal(
            ["EnterMode:Peace", "EnterMode:Peace", "Equip:00000320"],
            surface.CallLog);

        surface.ConfirmPendingEquip();
        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(
            [
                "EnterMode:Peace", "EnterMode:Peace", "Equip:00000320",
                "EnterMode:Magic",
            ],
            surface.CallLog);

        surface.ConfirmPendingModeChange();
        gate.AdvancePass(1.0);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(
            [
                "EnterMode:Peace", "EnterMode:Peace", "Equip:00000320",
                "EnterMode:Magic",
            ],
            surface.CallLog);
    }

    [Fact]
    public void GateLogsRequestingPeaceThenEquipThenRequestingMagicAndNeverReDropsAfterEquip()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Mode = Melee, empty hands
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        CombatModeGate gate = Gate(surface, settings);
        var log = new List<(MacroLogChannel Channel, string Message)>();
        gate.Log = (channel, message) => log.Add((channel, message));

        bool ready = false;
        for (int pass = 0; pass < 8 && !ready; pass++)
        {
            gate.AdvancePass(1.0);
            ready = gate.TryPrepare(PluginCombatMode.Magic);
        }
        Assert.True(ready, "gate never converged");

        Assert.Equal(
            [
                (MacroLogChannel.BusyState, "(FCM) requesting Peace"),
                (MacroLogChannel.BusyState, "(FCM) requesting Peace"),
                (MacroLogChannel.BusyState, "(FCM) equip Recovery Wand"),
                (MacroLogChannel.BusyState, "(FCM) requesting Magic"),
            ],
            log);

        int equipIndex = log.FindIndex(entry => entry.Message.Contains("equip"));
        Assert.DoesNotContain(
            log.Skip(equipIndex + 1),
            entry => entry.Message.Contains("requesting Peace"));
    }

    [Fact]
    public void GateNoWandAnywherePostsTheNoticeOnceAndStopsTheMacro()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var stops = new List<string>();
        CombatModeGate gate = Gate(surface, stops: stops);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(
            "You must add at least one wand to your profile.",
            Assert.Single(surface.PostedSystemMessages).Replace(
                "[MossTank] ",
                string.Empty,
                StringComparison.Ordinal));
        Assert.Equal(
            "You must add at least one wand to your profile.",
            Assert.Single(stops));

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Single(surface.PostedSystemMessages);
        Assert.Equal(2, stops.Count);

        gate.Reset();
        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.PostedSystemMessages.Count);
    }

    [Fact]
    public void GateModeRequestNeverConfirmedKeepsAskingWithoutGivingUp()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            DeferModeConfirmation = true,
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        var stops = new List<string>();
        CombatModeGate gate = Gate(
            surface,
            vitals: new VitalSettings { DropToPeaceModeRetryCount = 2 },
            stops: stops);

        for (int pass = 0; pass < 6; pass++)
        {
            gate.AdvancePass(1.0);
            Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        }

        Assert.Equal(6, surface.ModeChangeRequests);
        Assert.Empty(stops);
        Assert.Empty(surface.PostedSystemMessages);
    }

    [Fact]
    public void GateDropToPeaceBudgetIsCountedInPassesAndEndsInWandUseRecovery()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            DeferModeConfirmation = true,
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        var stops = new List<string>();
        CombatModeGate gate = Gate(
            surface,
            settings,
            new VitalSettings { DropToPeaceModeRetryCount = 3 },
            stops);

        // Three passes, each well past the 600 ms window so the Peace request
        // is genuinely re-issued; the third exhausts the budget.
        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.ModeChangeRequests);
        Assert.Equal(0u, surface.LastUsedItem);

        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(800u, surface.LastUsedItem);
        Assert.Equal(2, surface.ModeChangeRequests);
        Assert.Empty(stops);
        Assert.Contains(
            "bugged combat state",
            Assert.Single(surface.PostedSystemMessages),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GateFallbackWandIsItemsPageInsertionOrderNotNameOrder()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            EquipmentItems =
            [
                Equipment(801, "Zephyr Wand", damageType: 0, itemType: 0x00008000u),
                Equipment(802, "Adamant Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Zephyr Wand");
        settings.CombatItemNames.Add("Adamant Wand");
        settings.CombatItemOrder.Add("Zephyr Wand");
        settings.CombatItemOrder.Add("Adamant Wand");
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));

        Assert.Equal(["Equip:00000321"], surface.CallLog);
    }

    [Fact]
    public void GateKeepsAnAlreadyWieldedCasterRatherThanReEquipping()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            EquipmentItems =
            [
                Equipment(
                    801,
                    "Zephyr Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
                Equipment(802, "Adamant Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Adamant Wand");
        settings.CombatItemOrder.Add("Adamant Wand");
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Empty(surface.CallLog);
    }

    [Fact]
    public void GateIsHandsOffWhileTheCombatModeIsUnknown()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = new PluginCombatSnapshot
            {
                Mode = PluginCombatMode.Unknown,
            },
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Empty(surface.CallLog);
    }

    [Fact]
    public void GateWithNoEquipmentProjectionStillRequiresTheMode()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            EquipmentAvailable = false,
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(["EnterMode:Magic"], surface.CallLog);

        gate.AdvancePass(1d);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Ready", gate.Status);
    }

    [Fact]
    public void GateTreatsAnUnavailableModeCommandAsReadyRatherThanDeadlock()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            ModeCommandUnavailable = true,
            EquipmentItems =
            [
                Equipment(800, "War Wand", damageType: 0, itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("War Wand");
        settings.CombatItemOrder.Add("War Wand");
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1d);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
    }

    [Fact]
    public void OncePerRunWarningLatchClearsAtTheMacroStartEdge()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Single(surface.PostedSystemMessages);

        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Single(surface.PostedSystemMessages);

        gate.ResetOncePerRunWarnings();
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.PostedSystemMessages.Count);
    }

    [Fact]
    public void FcmIgnoringItemWarningReachesChatOncePerRun()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            EquipmentItems =
            [
                Equipment(900, "Bread", damageType: 0, itemType: 1) with
                {
                    ValidLocations = 0u,
                },
                Equipment(800, "War Wand", damageType: 0, itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1d);
        gate.TryPrepare(PluginCombatMode.Magic, overrideItemId: 900u);
        string warning = Assert.Single(surface.PostedSystemMessages);
        Assert.Contains(
            "Warning: FCM ignoring item Bread because it cannot currently be "
            + "wielded.",
            warning,
            StringComparison.Ordinal);

        gate.AdvancePass(0.1d);
        gate.TryPrepare(PluginCombatMode.Magic, overrideItemId: 900u);
        Assert.Single(surface.PostedSystemMessages);

        gate.ResetOncePerRunWarnings();
        gate.AdvancePass(0.1d);
        gate.TryPrepare(PluginCombatMode.Magic, overrideItemId: 900u);
        Assert.Equal(2, surface.PostedSystemMessages.Count);
    }

    [Fact]
    public void TheModeWindowIsTimeBasedAndTheAckRestartsIt()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Melee
            DeferModeConfirmation = true,
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        CombatModeGate gate = Gate(surface, settings);

        // Ask for Peace so the wand can be wielded.
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryDropToPeace(surface.EquipmentItems, "Recovery Wand"));
        Assert.Equal(["EnterMode:Peace"], surface.CallLog);

        // The ack arrives. The window RESTARTS, so f9.e() still reports the
        // pre-request Melee and the branch re-asks — which re-stamps the
        // saved mode from the now-Peace live one.
        surface.ConfirmPendingModeChange();
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryDropToPeace(surface.EquipmentItems, "Recovery Wand"));
        Assert.Equal(["EnterMode:Peace", "EnterMode:Peace"], surface.CallLog);

        gate.AdvancePass(0.1d);
        Assert.True(gate.TryDropToPeace(surface.EquipmentItems, "Recovery Wand"));
        Assert.Equal(["EnterMode:Peace", "EnterMode:Peace"], surface.CallLog);
    }

    [Fact]
    public void ThreeGateCallsWithinOnePassAdvanceTheClockExactlyOnce()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        CombatModeGate gate = Gate(surface, settings);

        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Empty(surface.CallLog);

        gate.AdvancePass(0.25);
        double afterOneAdvance = gate.SinceModeRequestSecondsForTests;

        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Empty(surface.CallLog);

        Assert.Equal(afterOneAdvance, gate.SinceModeRequestSecondsForTests);
    }


    private static MacroPassContext IdleTurn(bool canAct = true) =>
        new(0.3d, canAct);

    [Fact]
    public void IdlePeaceRequestsOnEveryPassItWins()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Mode = Melee
            IgnoreModeChanges = true, // keep the rule valid across passes
        };
        var rule = new IdlePeaceRule(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = true });

        Assert.True(rule.ValidNow(IdleTurn()));
        rule.Running = true;
        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Contains("peace", rule.Status!, StringComparison.OrdinalIgnoreCase);

        Assert.True(rule.ValidNow(IdleTurn()));
        rule.Running = true;
        Assert.Equal(2, surface.ModeChangeRequests);
    }

    [Fact]
    public void IdlePeaceIsLastInTheListAndIsInertWhenAskedWithCanActFalse()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var rule = new IdlePeaceRule(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = true });

        IReadOnlyList<MacroRuleEntry> entries = MacroRuleTable.Entries;
        Assert.Equal(MacroRuleSlot.IdlePeace, entries[^1].Slot);
        Assert.DoesNotContain(
            entries.Take(entries.Count - 1),
            static entry => entry.Slot == MacroRuleSlot.IdlePeace);

        Assert.False(rule.ValidNow(IdleTurn(canAct: false)));

        rule.Running = false;
        Assert.Equal(0, surface.ModeChangeRequests);
        Assert.Null(rule.Status);
        Assert.Equal(PluginCombatMode.Melee, surface.CombatSnapshot.Mode);
    }

    [Fact]
    public void IdlePeaceModeOffNeverRequests()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var rule = new IdlePeaceRule(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = false });

        Assert.False(rule.ValidNow(IdleTurn()));
        Assert.Equal(0, surface.ModeChangeRequests);
        Assert.Null(rule.Status);
    }

    /// <summary>
    /// Already in Peace: cm.cs:70-73's whole second half.
    /// </summary>
    [Fact]
    public void IdlePeaceIsInvalidWhenAlreadyInPeace()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = new PluginCombatSnapshot { Mode = PluginCombatMode.Peace },
        };
        var rule = new IdlePeaceRule(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = true });

        Assert.Equal(PluginCombatMode.Peace, surface.CombatSnapshot.Mode);
        Assert.False(rule.ValidNow(IdleTurn()));
    }

    [Fact]
    public void IdlePeaceStillFiresWhenCombatPolicyIsDisabled()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var settings = new CombatSettings { IdlePeaceMode = true, Enabled = false };
        var controller = new CombatController(new FakeHost(surface), settings);
        var rule = new IdlePeaceRule(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.False(controller.HasTarget);
        Assert.Contains("disabled", controller.Status, StringComparison.OrdinalIgnoreCase);

        Assert.True(rule.ValidNow(IdleTurn()));
        rule.Running = true;

        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(PluginCombatMode.Peace, surface.CombatSnapshot.Mode);
    }

    private static CombatSettings FireAttackRule(CombatSettings settings)
    {
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        return settings;
    }

    private static CombatSettings DebuffOnly(MonsterActionFlags flag)
    {
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = flag }));
        return settings;
    }

    private static PluginCombatSnapshot Peaceful() => Physical() with
    {
        Mode = PluginCombatMode.Peace,
    };

    private static PluginCombatSnapshot Physical(
        bool request = false,
        bool build = false,
        float bar = 0f) => new(
            SelectedObjectId: 0,
            PluginCombatMode.Melee,
            PluginAttackHeight.Medium,
            DesiredPower: 0.5f,
            PowerBarLevel: bar,
            BuildInProgress: build,
            RequestInProgress: request,
            ServerResponsePending: false,
            RepeatAttackInProgress: false);

    /// <summary>
    /// A real excerpt of the owner's own <c>gameinfodb.ugd</c> — VTank's
    /// official GameInfoDB, which <c>e0</c> loads from the profile directory
    /// (<c>e0.cs:53-79</c>). Any pin whose subject is a monster's damage
    /// preferences needs one, because acdream ships no embedded default.
    /// </summary>
    private static readonly VtankGameInfoDatabase GameInfo =
        VtankGameInfoDatabase.Parse(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "vtank",
                    "gameinfodb-excerpt.ugd")));

    private static PluginCombatTarget Target(
        uint id, string name, float distance, float angle) => new(
            id, name, id + 1000, distance, angle, true, 1f);

    private static PluginSpellInfo Spell(uint id, string name) => new(
        id, name, Family: 1, Tier: 8, Difficulty: 350, ManaCost: 30,
        DurationSeconds: 0, School: 34, Description: string.Empty,
        IsSelfTargeted: false, IsBeneficial: false);

    private static PluginEquipmentItem Equipment(
        uint id,
        string name,
        int damageType,
        int damage = 20,
        uint equippedLocation = 0,
        uint itemType = 1,
        byte combatUse = 1,
        uint ammoType = 0,
        int stackSize = 1) => new(
            id,
            name,
            ItemType: itemType,
            ValidLocations: 0x00100000,
            EquippedLocation: equippedLocation,
            ContainerObjectId: 1,
            WielderObjectId: 0,
            CombatUse: combatUse,
            DamageType: damageType,
            WeaponSkill: 44,
            Damage: damage,
            DamageVariance: 0.25)
        {
            AmmoType = ammoType,
            StackSize = stackSize,
        };

    private static PluginEquipmentItem WieldedCaster(uint id = 990u) =>
        Equipment(
            id,
            "Fixture Wand",
            damageType: 0,
            itemType: CombatModeGate.CasterItemType,
            equippedLocation: 0x00100000u);

    private static PluginEquipmentItem WieldedPlannedWeapon(uint id = 991u) =>
        Equipment(
            id,
            "Fixture Weapon",
            damageType: 0x007F,
            equippedLocation: 0x00100000u);

    private static CombatSettings ProfileFixtureWeapon(CombatSettings settings)
    {
        settings.CombatItemNames.Add("Fixture Weapon");
        return settings;
    }

    private static PluginInventoryItem PetDevice(uint id, uint wcid) => new(
        id, wcid, "Frost Pet", 0, 1, 0, 0, 0, 0, 0, 0, 1, 50, 50,
        0, 49000, 3, 0, false, 0, 0, 0, 0, 0, 54, 100, 0);

    private static PluginInventoryItem InventoryItem(
        uint id,
        string name,
        uint itemType,
        uint spellId,
        bool equipped) => new(
            id, 0, name, itemType, 1, 0, 0,
            equipped ? 0x00100000u : 0u,
            0, 0, 0, 1, 0, 0, spellId, 0, 0, 0, false, 0,
            0, 0, 0, 0, 0, 0, 0);

    private sealed class FakeHost(FakeAutomation automation) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new FakeLogger();
        public IGameState State { get; } = new FakeState();
        public IEvents Events { get; } = new FakeEvents();
        public ISelectionService Selection { get; } = new FakeSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => automation;
    }

    private sealed class FakeAutomation :
        IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
        IPluginChat, ICombatAutomation
        , IEquipmentAutomation, IItemAutomation, INavigationAutomation,
        IProjectileAutomation, ISelectionAutomation
    {
        public bool IsAvailable { get; set; } = true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => this;
        public ICombatAutomation Combat => this;
        public IEquipmentAutomation Equipment => this;
        public IItemAutomation Items => this;
        public INavigationAutomation Navigation => this;
        public IProjectileAutomation Projectiles => this;
        public ISelectionAutomation Selection => this;
        public PluginCombatSnapshot CombatSnapshot { get; set; }
        public PluginCombatSnapshot Snapshot => CombatSnapshot;
        public IReadOnlyList<PluginCombatTarget> Targets { get; set; } = [];
        public IReadOnlyList<PluginSpellInfo> KnownAttackSpells { get; set; } = [];
        private IReadOnlyList<PluginSpellInfo> _knownCombatSpells = [];
        public int KnownCombatSpellReads { get; private set; }
        public IReadOnlyList<PluginSpellInfo> KnownCombatSpells
        {
            get
            {
                KnownCombatSpellReads++;
                return _knownCombatSpells;
            }
            set => _knownCombatSpells = value;
        }
        public IReadOnlyList<PluginSpellInfo> SpellLookup { get; set; } = [];
        public PluginCastCompletion LastCastCompletion { get; set; }
        public PluginCastCompletion LastCompletion => LastCastCompletion;
        public uint LastBeginTarget { get; private set; }
        public int BeginCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public int AbortCount { get; private set; }
        public (uint Spell, uint Target) LastTargetedCast { get; private set; }
        public uint LastUntargetedCast { get; private set; }
        public List<uint> CastSpellIds { get; } = [];
        public IReadOnlyList<PluginEquipmentItem> EquipmentItems { get; set; } = [];
        public uint LastEquipObjectId { get; private set; }
        public IReadOnlyList<PluginInventoryItem> ItemEntries { get; set; } = [];
        public uint LastUsedItem { get; private set; }
        public (uint Item, uint Target) LastAppliedItem { get; private set; }
        public int ApplyCount { get; private set; }
        public PluginItemUseCompletion LastItemCompletion { get; set; }
        public IReadOnlyList<PluginChatMessage> ChatMessages { get; set; } = [];
        public float LastBeginPower { get; private set; }
        public PluginNavigationSnapshot NavigationSnapshot { get; set; }
        public bool IgnoreModeChanges { get; set; }
        public int ModeChangeRequests { get; private set; }
        PluginNavigationSnapshot INavigationAutomation.Snapshot =>
            NavigationSnapshot;
        public Dictionary<uint, PluginNavigationObject> NavigationObjects { get; } = [];
        public List<PluginMovementIntent> MovementIntents { get; } = [];
        public int ClearMovementCount { get; private set; }
        public PluginProjectilePathResult ProjectilePath { get; set; } =
            new(PluginProjectilePathStatus.Clear);
        public uint LastProjectileTarget { get; private set; }
        public IReadOnlyList<PluginProjectileDebugSample>
            ShownProjectileDebugSamples { get; private set; } = [];
        public List<PluginSelectionAction> SelectionActions { get; } = [];

        public List<string> CallLog { get; } = [];

        public bool SimulateAsyncEquip { get; set; }
        private uint? _pendingEquipObjectId;
        public bool EquipmentAvailable { get; set; } = true;
        bool IEquipmentAutomation.IsAvailable => EquipmentAvailable;
        bool IEquipmentAutomation.IsBusy =>
            SimulateAsyncEquip && _pendingEquipObjectId is not null;
        public int CaptureOwnedEquipmentCount { get; private set; }
        public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment()
        {
            CaptureOwnedEquipmentCount++;
            return EquipmentItems;
        }
        public PluginEquipmentCommandResult Equip(
            uint objectId,
            uint requestedLocation = 0u)
        {
            LastEquipObjectId = objectId;
            CallLog.Add($"Equip:{objectId:X8}");
            if (SimulateAsyncEquip)
                _pendingEquipObjectId = objectId;
            else
                EquipmentItems = MarkEquipped(EquipmentItems, objectId);
            return new(PluginEquipmentCommandStatus.Started);
        }

        public void ConfirmPendingEquip()
        {
            if (_pendingEquipObjectId is not { } objectId)
                return;
            EquipmentItems = MarkEquipped(EquipmentItems, objectId);
            _pendingEquipObjectId = null;
        }

        private static IReadOnlyList<PluginEquipmentItem> MarkEquipped(
            IReadOnlyList<PluginEquipmentItem> items,
            uint objectId) => items
                .Select(item => item.ObjectId == objectId
                    ? item with { EquippedLocation = 0x00100000u }
                    : item)
                .ToArray();
        bool IItemAutomation.IsAvailable => true;
        bool IItemAutomation.IsBusy => false;
        int IItemAutomation.ActiveOwnedPetCount => 0;
        PluginItemUseCompletion IItemAutomation.LastCompletion => LastItemCompletion;
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => ItemEntries;
        public PluginItemCommandResult Use(uint objectId)
        {
            LastUsedItem = objectId;
            return new(PluginItemCommandStatus.Started);
        }
        public PluginItemCommandResult Apply(uint objectId, uint targetObjectId)
        {
            LastAppliedItem = (objectId, targetObjectId);
            ApplyCount++;
            return new(PluginItemCommandStatus.Started);
        }

        public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
            float maximumDistance) => Targets;
        public PluginCombatCommandResult EnterDefaultMode()
        {
            CallLog.Add("EnterDefaultMode");
            return new(PluginCombatCommandStatus.ModeChangeSent);
        }

        public bool DeferModeConfirmation { get; set; }
        private PluginCombatMode? _pendingMode;
        public bool ModeCommandUnavailable { get; set; }

        public PluginCombatCommandResult EnterMode(PluginCombatMode mode)
        {
            ModeChangeRequests++;
            CallLog.Add($"EnterMode:{mode}");
            if (ModeCommandUnavailable)
                return new(PluginCombatCommandStatus.Unavailable);
            if (IgnoreModeChanges)
                return new(PluginCombatCommandStatus.ModeChangeSent);
            if (DeferModeConfirmation)
            {
                _pendingMode = mode;
                return new(PluginCombatCommandStatus.ModeChangeSent);
            }
            CombatSnapshot = CombatSnapshot with { Mode = mode };
            return new(PluginCombatCommandStatus.ModeChangeSent);
        }

        public void ConfirmPendingModeChange()
        {
            if (_pendingMode is not { } mode)
                return;
            CombatSnapshot = CombatSnapshot with { Mode = mode };
            _pendingMode = null;
        }
        public PluginCombatCommandResult BeginPhysicalAttack(
            uint targetObjectId, PluginAttackHeight height, float power)
        {
            LastBeginTarget = targetObjectId;
            LastBeginPower = power;
            BeginCount++;
            return new(PluginCombatCommandStatus.Started);
        }
        public PluginCombatCommandResult ReleasePhysicalAttack()
        {
            ReleaseCount++;
            return new(PluginCombatCommandStatus.Released);
        }
        public PluginCombatCommandResult AbortPhysicalAttack()
        {
            AbortCount++;
            return new(PluginCombatCommandStatus.Stopped);
        }

        public bool TryGetObject(
            uint objectId,
            out PluginNavigationObject value) =>
            NavigationObjects.TryGetValue(objectId, out value);
        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent)
        {
            MovementIntents.Add(intent);
            return PluginNavigationCommandStatus.Accepted;
        }
        public PluginNavigationCommandStatus ClearMovementIntent()
        {
            ClearMovementCount++;
            return PluginNavigationCommandStatus.Accepted;
        }

        public List<float> FacedHeadings { get; } = [];

        public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
        {
            FacedHeadings.Add(headingDegrees);
            return PluginNavigationCommandStatus.Accepted;
        }

        bool IProjectileAutomation.IsAvailable => true;
        public PluginProjectilePathResult EvaluatePath(
            uint targetObjectId,
            PluginProjectilePathKind kind,
            PluginAttackHeight targetHeight,
            float projectileRadius,
            float stepDistance,
            int maximumCollisionChecks)
        {
            LastProjectileTarget = targetObjectId;
            return ProjectilePath;
        }

        public PluginProjectilePathResult EvaluatePathWithDiagnostics(
            uint targetObjectId,
            PluginProjectilePathKind kind,
            PluginAttackHeight targetHeight,
            float projectileRadius,
            float stepDistance,
            int maximumCollisionChecks) => EvaluatePath(
                targetObjectId,
                kind,
                targetHeight,
                projectileRadius,
                stepDistance,
                maximumCollisionChecks);

        public void ShowDebugSamples(
            IReadOnlyList<PluginProjectileDebugSample> samples) =>
            ShownProjectileDebugSamples = samples.ToArray();

        public bool Execute(PluginSelectionAction action)
        {
            SelectionActions.Add(action);
            return true;
        }

        public bool IsCasting { get; set; }
        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Ready;
        public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
            PluginCastGate.Ready;
        public bool Cast(uint spellId)
        {
            LastUntargetedCast = spellId;
            CastSpellIds.Add(spellId);
            return true;
        }
        public bool Cast(uint spellId, uint targetObjectId)
        {
            LastTargetedCast = (spellId, targetObjectId);
            CastSpellIds.Add(spellId);
            return true;
        }
        public List<string> PostedSystemMessages { get; } = [];
        public void PostSystemMessage(string text) =>
            PostedSystemMessages.Add(text);
        public IReadOnlyList<PluginChatMessage> CaptureMessages(
            ulong afterSequence) => ChatMessages
                .Where(message => message.Sequence > afterSequence)
                .ToArray();

        public bool IsInWorld => IsAvailable;
        public uint ObjectId => 1;
        public uint CurrentHealth => 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public int SummoningMastery => 3;
        public IReadOnlyList<PluginSkillInfo> CharacterSkills { get; set; } =
            [new(54, "Summoning", PluginSkillTraining.Trained, 300)];
        public IReadOnlyList<PluginSkillInfo> Skills => CharacterSkills;
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach (PluginSkillInfo candidate in CharacterSkills)
            {
                if (candidate.SkillId == skillId)
                {
                    skill = candidate;
                    return true;
                }
            }
            skill = default;
            return false;
        }
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            foreach (PluginSpellInfo spell in KnownAttackSpells)
            {
                if (spell.SpellId == spellId)
                {
                    info = spell;
                    return true;
                }
            }
            foreach (PluginSpellInfo spell in KnownCombatSpells.Concat(SpellLookup))
            {
                if (spell.SpellId == spellId)
                {
                    info = spell;
                    return true;
                }
            }
            info = default;
            return false;
        }
    }

    private static PluginNavigationSnapshot NavigationAt(float heading) => new(
        IsAvailable: true,
        IsPortalSpace: false,
        LocalObjectId: 1u,
        Position: new PluginNavigationPosition(
            0x7F7F0001,
            0d,
            0d,
            0d,
            heading,
            true),
        IsMoving: false,
        IsAirborne: false);

    private sealed class FakeLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
    private sealed class FakeState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }
    private sealed class FakeEvents : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }
        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }
    private sealed class FakeSelection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }
        public bool Select(uint objectId) => true;
        public bool Clear() => true;
    }
}
