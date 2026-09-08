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
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Range,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(20u, surface.LastBeginTarget);
        Assert.Contains("Olthoi Soldier", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetLockKeepsCurrentTargetAcrossRescan()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "First", 4, 20)],
        };
        var settings = new CombatSettings
        {
            TargetLock = true,
            ScanIntervalSeconds = 0.1,
        };
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
        };
        var settings = new CombatSettings
        {
            SelectionMethod = TargetSelectionMethod.Angle,
            TargetLock = false,
            ScanIntervalSeconds = 0.1,
        };
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
        };
        var settings = new CombatSettings
        {
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.1,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
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
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Both,
            TargetSelectAngleRange = 10,
        };
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
        };
        var settings = new CombatSettings { AttackPower = 0.6f };
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
        };
        var controller = new CombatController(
            new FakeHost(surface), new CombatSettings());

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
            new CombatSettings { UseBreakableTurnTo = true });

        controller.Toggle();
        controller.OnTick(0.25);

        PluginMovementIntent turn = Assert.Single(surface.MovementIntents);
        Assert.True(turn.TurnRight);
        Assert.Empty(surface.CastSpellIds);

        surface.NavigationSnapshot = NavigationAt(heading: 90f);
        controller.OnTick(0.25);

        Assert.Equal([100u], surface.CastSpellIds);
        Assert.Equal(1, surface.ClearMovementCount);
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
        };
        var controller = new CombatController(
            new FakeHost(surface),
            new CombatSettings { UseProjectileAwareness = true });

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
        };
        var controller = new CombatController(
            new FakeHost(surface),
            new CombatSettings { UseProjectileAwareness = false });

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
        };
        var controller = new CombatController(
            new FakeHost(surface),
            new CombatSettings
            {
                UseProjectileAwareness = true,
                ShowCollisionDebug = true,
            });

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
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil | MonsterActionFlags.Attack,
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
    public void RingOnlyRuleCastsUntargetedRingWithOneNearbyRingTarget()
    {
        PluginSpellInfo ring = Spell(110, "Flame Ring") with
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
        controller.OnTick(0.25);
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
        controller.OnTick(0.25);
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
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.Equal(701u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
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
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.Equal(801u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
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
            "stuck combat state",
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
        Assert.Contains("Debuffs complete", controller.Status, StringComparison.Ordinal);
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
        Assert.DoesNotContain("Debuffs complete", controller.Status, StringComparison.Ordinal);

        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty,
                "You cast Imperil Other VII on Drudge.",
                string.Empty),
        ];
        controller.OnTick(0.1);
        Assert.Contains("Debuffs complete", controller.Status, StringComparison.Ordinal);
    }


    [Fact]
    public void PreparerWieldedCasterInPeaceRequestsMagicThenReady()
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
        var preparer = new BuffCasterPreparer(
            new FakeHost(surface), new CombatSettings(), new VitalSettings());

        preparer.Tick(0.1);

        Assert.False(preparer.Ready);
        Assert.False(preparer.Stopped);
        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(["EnterMode:Magic"], surface.CallLog);

        surface.ConfirmPendingModeChange();
        preparer.Tick(0.1);

        Assert.True(preparer.Ready);
        Assert.Equal(1, surface.ModeChangeRequests);
    }

    [Fact]
    public void PreparerProfiledCasterNotWieldedWieldsThenEntersMagicInOrder()
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
        var preparer = new BuffCasterPreparer(
            new FakeHost(surface), settings, new VitalSettings());

        preparer.Tick(0.1);
        Assert.Equal(["EnterMode:Peace"], surface.CallLog);
        Assert.False(preparer.Ready);

        surface.ConfirmPendingModeChange();
        preparer.Tick(0.1);
        // Equip only happens after the snapshot reports Peace, never before.
        Assert.Equal(["EnterMode:Peace", "Equip:00000320"], surface.CallLog);
        Assert.False(preparer.Ready);

        surface.ConfirmPendingEquip();
        preparer.Tick(0.1);
        Assert.Equal(
            ["EnterMode:Peace", "Equip:00000320", "EnterMode:Magic"],
            surface.CallLog);
        Assert.False(preparer.Ready);

        surface.ConfirmPendingModeChange();
        preparer.Tick(0.1);
        Assert.True(preparer.Ready);
        Assert.Equal(
            ["EnterMode:Peace", "Equip:00000320", "EnterMode:Magic"],
            surface.CallLog);
    }

    [Fact]
    public void PreparerNoCasterAnywhereStopsAndPostsNoticeOnceUntilReset()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var preparer = new BuffCasterPreparer(
            new FakeHost(surface), new CombatSettings(), new VitalSettings());

        preparer.Tick(0.1);

        Assert.True(preparer.Stopped);
        Assert.False(preparer.Ready);
        Assert.Contains(
            "wand",
            Assert.Single(surface.PostedSystemMessages),
            StringComparison.OrdinalIgnoreCase);

        preparer.Tick(0.1);
        Assert.Single(surface.PostedSystemMessages);

        preparer.Reset();
        preparer.Tick(0.1);
        Assert.Equal(2, surface.PostedSystemMessages.Count);
    }

    [Fact]
    public void PreparerModeRequestNeverConfirmedStopsAfterRetryBudgetNamingStage()
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
        var vitalSettings = new VitalSettings { DropToPeaceModeRetryCount = 2 };
        var preparer = new BuffCasterPreparer(
            new FakeHost(surface), new CombatSettings(), vitalSettings);

        preparer.Tick(0.1);
        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.False(preparer.Stopped);

        preparer.Tick(2.0);
        Assert.Equal(2, surface.ModeChangeRequests);
        Assert.False(preparer.Stopped);

        preparer.Tick(2.0);
        Assert.True(preparer.Stopped);
        Assert.False(preparer.Ready);
        Assert.Equal(2, surface.ModeChangeRequests);
        Assert.Contains(
            "could not enter magic mode",
            preparer.Status,
            StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void ArbiterIdleWithPeaceModeOnRequestsOncePerRetryWindow()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Mode = Melee
            IgnoreModeChanges = true, // isolate the 1 s retry gate itself
        };
        var arbiter = new MacroIdleModeArbiter(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = true });

        arbiter.Tick(0.1, macroRunning: true, idle: true);
        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Contains("peace", arbiter.Status!, StringComparison.OrdinalIgnoreCase);

        arbiter.Tick(0.5, macroRunning: true, idle: true);
        Assert.Equal(1, surface.ModeChangeRequests);

        arbiter.Tick(0.6, macroRunning: true, idle: true);
        Assert.Equal(2, surface.ModeChangeRequests);
    }

    [Fact]
    public void ArbiterAnyOwnedActionTargetOrCastingSuppressesIt()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var arbiter = new MacroIdleModeArbiter(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = true });

        arbiter.Tick(5.0, macroRunning: true, idle: false);

        Assert.Equal(0, surface.ModeChangeRequests);
        Assert.Null(arbiter.Status);
        Assert.Equal(PluginCombatMode.Melee, surface.CombatSnapshot.Mode);
    }

    [Fact]
    public void ArbiterIdlePeaceModeOffNeverRequests()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var arbiter = new MacroIdleModeArbiter(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = false });

        arbiter.Tick(5.0, macroRunning: true, idle: true);

        Assert.Equal(0, surface.ModeChangeRequests);
        Assert.Null(arbiter.Status);
    }

    [Fact]
    public void ArbiterRequestsWhenCombatDisabledButMacroRunning()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var settings = new CombatSettings { IdlePeaceMode = true, Enabled = false };
        var controller = new CombatController(new FakeHost(surface), settings);
        var arbiter = new MacroIdleModeArbiter(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.False(controller.HasTarget);
        Assert.Contains("disabled", controller.Status, StringComparison.OrdinalIgnoreCase);

        arbiter.Tick(1.5, macroRunning: controller.Enabled, idle: !controller.HasTarget);

        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(PluginCombatMode.Peace, surface.CombatSnapshot.Mode);
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
        public IReadOnlyList<PluginSpellInfo> KnownCombatSpells { get; set; } = [];
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
        bool IEquipmentAutomation.IsAvailable => true;
        bool IEquipmentAutomation.IsBusy =>
            SimulateAsyncEquip && _pendingEquipObjectId is not null;
        public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() =>
            EquipmentItems;
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
        public PluginCombatCommandResult EnterDefaultMode() =>
            new(PluginCombatCommandStatus.ModeChangeSent);

        public bool DeferModeConfirmation { get; set; }
        private PluginCombatMode? _pendingMode;
        public PluginCombatCommandResult EnterMode(PluginCombatMode mode)
        {
            ModeChangeRequests++;
            CallLog.Add($"EnterMode:{mode}");
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
