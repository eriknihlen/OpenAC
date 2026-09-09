using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Properties;
using Xunit;

namespace AcDream.Core.Tests.Properties;

public sealed class PropertyEnumConformanceTests
{
    private static readonly (string Name, uint Value)[] PropertyIntGolden =
    [
        ("Undef", 0),
        ("ItemType", 1),
        ("CreatureType", 2),
        ("PaletteTemplate", 3),
        ("ClothingPriority", 4),
        ("EncumbranceVal", 5),
        ("ItemsCapacity", 6),
        ("ContainersCapacity", 7),
        ("Mass", 8),
        ("ValidLocations", 9),
        ("CurrentWieldedLocation", 10),
        ("MaxStackSize", 11),
        ("StackSize", 12),
        ("StackUnitEncumbrance", 13),
        ("StackUnitMass", 14),
        ("StackUnitValue", 15),
        ("ItemUseable", 16),
        ("RareId", 17),
        ("UiEffects", 18),
        ("Value", 19),
        ("CoinValue", 20),
        ("TotalExperience", 21),
        ("AvailableCharacter", 22),
        ("TotalSkillCredits", 23),
        ("AvailableSkillCredits", 24),
        ("Level", 25),
        ("AccountRequirements", 26),
        ("ArmorType", 27),
        ("ArmorLevel", 28),
        ("AllegianceCpPool", 29),
        ("AllegianceRank", 30),
        ("ChannelsAllowed", 31),
        ("ChannelsActive", 32),
        ("Bonded", 33),
        ("MonarchsRank", 34),
        ("AllegianceFollowers", 35),
        ("ResistMagic", 36),
        ("ResistItemAppraisal", 37),
        ("ResistLockpick", 38),
        ("DeprecatedResistRepair", 39),
        ("CombatMode", 40),
        ("CurrentAttackHeight", 41),
        ("CombatCollisions", 42),
        ("NumDeaths", 43),
        ("Damage", 44),
        ("DamageType", 45),
        ("DefaultCombatStyle", 46),
        ("AttackType", 47),
        ("WeaponSkill", 48),
        ("WeaponTime", 49),
        ("AmmoType", 50),
        ("CombatUse", 51),
        ("ParentLocation", 52),
        ("PlacementPosition", 53),
        ("WeaponEncumbrance", 54),
        ("WeaponMass", 55),
        ("ShieldValue", 56),
        ("ShieldEncumbrance", 57),
        ("MissileInventoryLocation", 58),
        ("FullDamageType", 59),
        ("WeaponRange", 60),
        ("AttackersSkill", 61),
        ("DefendersSkill", 62),
        ("AttackersSkillValue", 63),
        ("AttackersClass", 64),
        ("Placement", 65),
        ("CheckpointStatus", 66),
        ("Tolerance", 67),
        ("TargetingTactic", 68),
        ("CombatTactic", 69),
        ("HomesickTargetingTactic", 70),
        ("NumFollowFailures", 71),
        ("FriendType", 72),
        ("FoeType", 73),
        ("MerchandiseItemTypes", 74),
        ("MerchandiseMinValue", 75),
        ("MerchandiseMaxValue", 76),
        ("NumItemsSold", 77),
        ("NumItemsBought", 78),
        ("MoneyIncome", 79),
        ("MoneyOutflow", 80),
        ("MaxGeneratedObjects", 81),
        ("InitGeneratedObjects", 82),
        ("ActivationResponse", 83),
        ("OriginalValue", 84),
        ("NumMoveFailures", 85),
        ("MinLevel", 86),
        ("MaxLevel", 87),
        ("LockpickMod", 88),
        ("BoosterEnum", 89),
        ("BoostValue", 90),
        ("MaxStructure", 91),
        ("Structure", 92),
        ("PhysicsState", 93),
        ("TargetType", 94),
        ("RadarBlipColor", 95),
        ("EncumbranceCapacity", 96),
        ("LoginTimestamp", 97),
        ("CreationTimestamp", 98),
        ("PkLevelModifier", 99),
        ("GeneratorType", 100),
        ("AiAllowedCombatStyle", 101),
        ("LogoffTimestamp", 102),
        ("GeneratorDestructionType", 103),
        ("ActivationCreateClass", 104),
        ("ItemWorkmanship", 105),
        ("ItemSpellcraft", 106),
        ("ItemCurMana", 107),
        ("ItemMaxMana", 108),
        ("ItemDifficulty", 109),
        ("ItemAllegianceRankLimit", 110),
        ("PortalBitmask", 111),
        ("AdvocateLevel", 112),
        ("Gender", 113),
        ("Attuned", 114),
        ("ItemSkillLevelLimit", 115),
        ("GateLogic", 116),
        ("ItemManaCost", 117),
        ("Logoff", 118),
        ("Active", 119),
        ("AttackHeight", 120),
        ("NumAttackFailures", 121),
        ("AiCpThreshold", 122),
        ("AiAdvancementStrategy", 123),
        ("Version", 124),
        ("Age", 125),
        ("VendorHappyMean", 126),
        ("VendorHappyVariance", 127),
        ("CloakStatus", 128),
        ("VitaeCpPool", 129),
        ("NumServicesSold", 130),
        ("MaterialType", 131),
        ("NumAllegianceBreaks", 132),
        ("ShowableOnRadar", 133),
        ("PlayerKillerStatus", 134),
        ("VendorHappyMaxItems", 135),
        ("ScorePageNum", 136),
        ("ScoreConfigNum", 137),
        ("ScoreNumScores", 138),
        ("DeathLevel", 139),
        ("AiOptions", 140),
        ("OpenToEveryone", 141),
        ("GeneratorTimeType", 142),
        ("GeneratorStartTime", 143),
        ("GeneratorEndTime", 144),
        ("GeneratorEndDestructionType", 145),
        ("XpOverride", 146),
        ("NumCrashAndTurns", 147),
        ("ComponentWarningThreshold", 148),
        ("HouseStatus", 149),
        ("HookPlacement", 150),
        ("HookType", 151),
        ("HookItemType", 152),
        ("AiPpThreshold", 153),
        ("GeneratorVersion", 154),
        ("HouseType", 155),
        ("PickupEmoteOffset", 156),
        ("WeenieIteration", 157),
        ("WieldRequirements", 158),
        ("WieldSkilltype", 159),
        ("WieldDifficulty", 160),
        ("HouseMaxHooksUsable", 161),
        ("HouseCurrentHooksUsable", 162),
        ("AllegianceMinLevel", 163),
        ("AllegianceMaxLevel", 164),
        ("HouseRelinkHookCount", 165),
        ("SlayerCreatureType", 166),
        ("ConfirmationInProgress", 167),
        ("ConfirmationTypeInProgress", 168),
        ("TsysMutationData", 169),
        ("NumItemsInMaterial", 170),
        ("NumTimesTinkered", 171),
        ("AppraisalLongDescDecoration", 172),
        ("AppraisalLockpickSuccessPercent", 173),
        ("AppraisalPages", 174),
        ("AppraisalMaxPages", 175),
        ("AppraisalItemSkill", 176),
        ("GemCount", 177),
        ("GemType", 178),
        ("ImbuedEffect", 179),
        ("AttackersRawSkillValue", 180),
        ("ChessRank", 181),
        ("ChessTotalGames", 182),
        ("ChessGamesWon", 183),
        ("ChessGamesLost", 184),
        ("TypeOfAlteration", 185),
        ("SkillToBeAltered", 186),
        ("SkillAlterationCount", 187),
        ("HeritageGroup", 188),
        ("TransferFromAttribute", 189),
        ("TransferToAttribute", 190),
        ("AttributeTransferCount", 191),
        ("FakeFishingSkill", 192),
        ("NumKeys", 193),
        ("DeathTimestamp", 194),
        ("PkTimestamp", 195),
        ("VictimTimestamp", 196),
        ("HookGroup", 197),
        ("AllegianceSwearTimestamp", 198),
        ("HousePurchaseTimestamp", 199),
        ("RedirectableEquippedArmorCount", 200),
        ("MeleedefenseImbuedEffectTypeCache", 201),
        ("MissileDefenseImbuedEffectTypeCache", 202),
        ("MagicDefenseImbuedEffectTypeCache", 203),
        ("ElementalDamageBonus", 204),
        ("ImbueAttempts", 205),
        ("ImbueSuccesses", 206),
        ("CreatureKills", 207),
        ("PlayerKillsPk", 208),
        ("PlayerKillsPkl", 209),
        ("RaresTierOne", 210),
        ("RaresTierTwo", 211),
        ("RaresTierThree", 212),
        ("RaresTierFour", 213),
        ("RaresTierFive", 214),
        ("AugmentationStat", 215),
        ("AugmentationFamilyStat", 216),
        ("AugmentationInnateFamily", 217),
        ("AugmentationInnateStrength", 218),
        ("AugmentationInnateEndurance", 219),
        ("AugmentationInnateCoordination", 220),
        ("AugmentationInnateQuickness", 221),
        ("AugmentationInnateFocus", 222),
        ("AugmentationInnateSelf", 223),
        ("AugmentationSpecializeSalvaging", 224),
        ("AugmentationSpecializeItemTinkering", 225),
        ("AugmentationSpecializeArmorTinkering", 226),
        ("AugmentationSpecializeMagicItemTinkering", 227),
        ("AugmentationSpecializeWeaponTinkering", 228),
        ("AugmentationExtraPackSlot", 229),
        ("AugmentationIncreasedCarryingCapacity", 230),
        ("AugmentationLessDeathItemLoss", 231),
        ("AugmentationSpellsRemainPastDeath", 232),
        ("AugmentationCriticalDefense", 233),
        ("AugmentationBonusXp", 234),
        ("AugmentationBonusSalvage", 235),
        ("AugmentationBonusImbueChance", 236),
        ("AugmentationFasterRegen", 237),
        ("AugmentationIncreasedSpellDuration", 238),
        ("AugmentationResistanceFamily", 239),
        ("AugmentationResistanceSlash", 240),
        ("AugmentationResistancePierce", 241),
        ("AugmentationResistanceBlunt", 242),
        ("AugmentationResistanceAcid", 243),
        ("AugmentationResistanceFire", 244),
        ("AugmentationResistanceFrost", 245),
        ("AugmentationResistanceLightning", 246),
        ("RaresTierOneLogin", 247),
        ("RaresTierTwoLogin", 248),
        ("RaresTierThreeLogin", 249),
        ("RaresTierFourLogin", 250),
        ("RaresTierFiveLogin", 251),
        ("RaresLoginTimestamp", 252),
        ("RaresTierSix", 253),
        ("RaresTierSeven", 254),
        ("RaresTierSixLogin", 255),
        ("RaresTierSevenLogin", 256),
        ("ItemAttributeLimit", 257),
        ("ItemAttributeLevelLimit", 258),
        ("ItemAttribute2ndLimit", 259),
        ("ItemAttribute2ndLevelLimit", 260),
        ("CharacterTitleId", 261),
        ("NumCharacterTitles", 262),
        ("ResistanceModifierType", 263),
        ("FreeTinkersBitfield", 264),
        ("EquipmentSetId", 265),
        ("PetClass", 266),
        ("Lifespan", 267),
        ("RemainingLifespan", 268),
        ("UseCreateQuantity", 269),
        ("WieldRequirements2", 270),
        ("WieldSkilltype2", 271),
        ("WieldDifficulty2", 272),
        ("WieldRequirements3", 273),
        ("WieldSkilltype3", 274),
        ("WieldDifficulty3", 275),
        ("WieldRequirements4", 276),
        ("WieldSkilltype4", 277),
        ("WieldDifficulty4", 278),
        ("Unique", 279),
        ("SharedCooldown", 280),
        ("Faction1Bits", 281),
        ("Faction2Bits", 282),
        ("Faction3Bits", 283),
        ("Hatred1Bits", 284),
        ("Hatred2Bits", 285),
        ("Hatred3Bits", 286),
        ("SocietyRankCelhan", 287),
        ("SocietyRankEldweb", 288),
        ("SocietyRankRadblo", 289),
        ("HearLocalSignals", 290),
        ("HearLocalSignalsRadius", 291),
        ("Cleaving", 292),
        ("AugmentationSpecializeGearcraft", 293),
        ("AugmentationInfusedCreatureMagic", 294),
        ("AugmentationInfusedItemMagic", 295),
        ("AugmentationInfusedLifeMagic", 296),
        ("AugmentationInfusedWarMagic", 297),
        ("AugmentationCriticalExpertise", 298),
        ("AugmentationCriticalPower", 299),
        ("AugmentationSkilledMelee", 300),
        ("AugmentationSkilledMissile", 301),
        ("AugmentationSkilledMagic", 302),
        ("ImbuedEffect2", 303),
        ("ImbuedEffect3", 304),
        ("ImbuedEffect4", 305),
        ("ImbuedEffect5", 306),
        ("DamageRating", 307),
        ("DamageResistRating", 308),
        ("AugmentationDamageBonus", 309),
        ("AugmentationDamageReduction", 310),
        ("ImbueStackingBits", 311),
        ("HealOverTime", 312),
        ("CritRating", 313),
        ("CritDamageRating", 314),
        ("CritResistRating", 315),
        ("CritDamageResistRating", 316),
        ("HealingResistRating", 317),
        ("DamageOverTime", 318),
        ("ItemMaxLevel", 319),
        ("ItemXpStyle", 320),
        ("EquipmentSetExtra", 321),
        ("AetheriaBitfield", 322),
        ("HealingBoostRating", 323),
        ("HeritageSpecificArmor", 324),
        ("AlternateRacialSkills", 325),
        ("AugmentationJackOfAllTrades", 326),
        ("AugmentationResistanceNether", 327),
        ("AugmentationInfusedVoidMagic", 328),
        ("WeaknessRating", 329),
        ("NetherOverTime", 330),
        ("NetherResistRating", 331),
        ("LuminanceAward", 332),
        ("LumAugDamageRating", 333),
        ("LumAugDamageReductionRating", 334),
        ("LumAugCritDamageRating", 335),
        ("LumAugCritReductionRating", 336),
        ("LumAugSurgeEffectRating", 337),
        ("LumAugSurgeChanceRating", 338),
        ("LumAugItemManaUsage", 339),
        ("LumAugItemManaGain", 340),
        ("LumAugVitality", 341),
        ("LumAugHealingRating", 342),
        ("LumAugSkilledCraft", 343),
        ("LumAugSkilledSpec", 344),
        ("LumAugNoDestroyCraft", 345),
        ("RestrictInteraction", 346),
        ("OlthoiLootTimestamp", 347),
        ("OlthoiLootStep", 348),
        ("UseCreatesContractId", 349),
        ("DotResistRating", 350),
        ("LifeResistRating", 351),
        ("CloakWeaveProc", 352),
        ("WeaponType", 353),
        ("MeleeMastery", 354),
        ("RangedMastery", 355),
        ("SneakAttackRating", 356),
        ("RecklessnessRating", 357),
        ("DeceptionRating", 358),
        ("CombatPetRange", 359),
        ("WeaponAuraDamage", 360),
        ("WeaponAuraSpeed", 361),
        ("SummoningMastery", 362),
        ("HeartbeatLifespan", 363),
        ("UseLevelRequirement", 364),
        ("LumAugAllSkills", 365),
        ("UseRequiresSkill", 366),
        ("UseRequiresSkillLevel", 367),
        ("UseRequiresSkillSpec", 368),
        ("UseRequiresLevel", 369),
        ("GearDamage", 370),
        ("GearDamageResist", 371),
        ("GearCrit", 372),
        ("GearCritResist", 373),
        ("GearCritDamage", 374),
        ("GearCritDamageResist", 375),
        ("GearHealingBoost", 376),
        ("GearNetherResist", 377),
        ("GearLifeResist", 378),
        ("GearMaxHealth", 379),
        ("Unknown380", 380),
        ("PKDamageRating", 381),
        ("PKDamageResistRating", 382),
        ("GearPKDamageRating", 383),
        ("GearPKDamageResistRating", 384),
        ("Unknown385", 385),
        ("Overpower", 386),
        ("OverpowerResist", 387),
        ("GearOverpower", 388),
        ("GearOverpowerResist", 389),
        ("Enlightenment", 390),
    ];

    private static readonly (string Name, uint Value)[] PropertyInt64Golden =
    [
        ("Undef", 0),
        ("TotalExperience", 1),
        ("AvailableExperience", 2),
        ("AugmentationCost", 3),
        ("ItemTotalXp", 4),
        ("ItemBaseXp", 5),
        ("AvailableLuminance", 6),
        ("MaximumLuminance", 7),
        ("InteractionReqs", 8),
    ];

    private static readonly (string Name, uint Value)[] PropertyBoolGolden =
    [
        ("Undef", 0),
        ("Stuck", 1),
        ("Open", 2),
        ("Locked", 3),
        ("RotProof", 4),
        ("AllegianceUpdateRequest", 5),
        ("AiUsesMana", 6),
        ("AiUseHumanMagicAnimations", 7),
        ("AllowGive", 8),
        ("CurrentlyAttacking", 9),
        ("AttackerAi", 10),
        ("IgnoreCollisions", 11),
        ("ReportCollisions", 12),
        ("Ethereal", 13),
        ("GravityStatus", 14),
        ("LightsStatus", 15),
        ("ScriptedCollision", 16),
        ("Inelastic", 17),
        ("Visibility", 18),
        ("Attackable", 19),
        ("SafeSpellComponents", 20),
        ("AdvocateState", 21),
        ("Inscribable", 22),
        ("DestroyOnSell", 23),
        ("UiHidden", 24),
        ("IgnoreHouseBarriers", 25),
        ("HiddenAdmin", 26),
        ("PkWounder", 27),
        ("PkKiller", 28),
        ("NoCorpse", 29),
        ("UnderLifestoneProtection", 30),
        ("ItemManaUpdatePending", 31),
        ("GeneratorStatus", 32),
        ("ResetMessagePending", 33),
        ("DefaultOpen", 34),
        ("DefaultLocked", 35),
        ("DefaultOn", 36),
        ("OpenForBusiness", 37),
        ("IsFrozen", 38),
        ("DealMagicalItems", 39),
        ("LogoffImDead", 40),
        ("ReportCollisionsAsEnvironment", 41),
        ("AllowEdgeSlide", 42),
        ("AdvocateQuest", 43),
        ("IsAdmin", 44),
        ("IsArch", 45),
        ("IsSentinel", 46),
        ("IsAdvocate", 47),
        ("CurrentlyPoweringUp", 48),
        ("GeneratorEnteredWorld", 49),
        ("NeverFailCasting", 50),
        ("VendorService", 51),
        ("AiImmobile", 52),
        ("DamagedByCollisions", 53),
        ("IsDynamic", 54),
        ("IsHot", 55),
        ("IsAffecting", 56),
        ("AffectsAis", 57),
        ("SpellQueueActive", 58),
        ("GeneratorDisabled", 59),
        ("IsAcceptingTells", 60),
        ("LoggingChannel", 61),
        ("OpensAnyLock", 62),
        ("UnlimitedUse", 63),
        ("GeneratedTreasureItem", 64),
        ("IgnoreMagicResist", 65),
        ("IgnoreMagicArmor", 66),
        ("AiAllowTrade", 67),
        ("SpellComponentsRequired", 68),
        ("IsSellable", 69),
        ("IgnoreShieldsBySkill", 70),
        ("NoDraw", 71),
        ("ActivationUntargeted", 72),
        ("HouseHasGottenPriorityBootPos", 73),
        ("GeneratorAutomaticDestruction", 74),
        ("HouseHooksVisible", 75),
        ("HouseRequiresMonarch", 76),
        ("HouseHooksEnabled", 77),
        ("HouseNotifiedHudOfHookCount", 78),
        ("AiAcceptEverything", 79),
        ("IgnorePortalRestrictions", 80),
        ("RequiresBackpackSlot", 81),
        ("DontTurnOrMoveWhenGiving", 82),
        ("NpcLooksLikeObject", 83),
        ("IgnoreCloIcons", 84),
        ("AppraisalHasAllowedWielder", 85),
        ("ChestRegenOnClose", 86),
        ("LogoffInMinigame", 87),
        ("PortalShowDestination", 88),
        ("PortalIgnoresPkAttackTimer", 89),
        ("NpcInteractsSilently", 90),
        ("Retained", 91),
        ("IgnoreAuthor", 92),
        ("Limbo", 93),
        ("AppraisalHasAllowedActivator", 94),
        ("ExistedBeforeAllegianceXpChanges", 95),
        ("IsDeaf", 96),
        ("IsPsr", 97),
        ("Invincible", 98),
        ("Ivoryable", 99),
        ("Dyable", 100),
        ("CanGenerateRare", 101),
        ("CorpseGeneratedRare", 102),
        ("NonProjectileMagicImmune", 103),
        ("ActdReceivedItems", 104),
        ("Unknown105", 105),
        ("FirstEnterWorldDone", 106),
        ("RecallsDisabled", 107),
        ("RareUsesTimer", 108),
        ("ActdPreorderReceivedItems", 109),
        ("Afk", 110),
        ("IsGagged", 111),
        ("ProcSpellSelfTargeted", 112),
        ("IsAllegianceGagged", 113),
        ("EquipmentSetTriggerPiece", 114),
        ("Uninscribe", 115),
        ("WieldOnUse", 116),
        ("ChestClearedWhenClosed", 117),
        ("NeverAttack", 118),
        ("SuppressGenerateEffect", 119),
        ("TreasureCorpse", 120),
        ("EquipmentSetAddLevel", 121),
        ("BarberActive", 122),
        ("TopLayerPriority", 123),
        ("NoHeldItemShown", 124),
        ("LoginAtLifestone", 125),
        ("OlthoiPk", 126),
        ("Account15Days", 127),
        ("HadNoVitae", 128),
        ("NoOlthoiTalk", 129),
        ("AutowieldLeft", 130),
    ];

    private static readonly (string Name, uint Value)[] PropertyFloatGolden =
    [
        ("Undef", 0),
        ("HeartbeatInterval", 1),
        ("HeartbeatTimestamp", 2),
        ("HealthRate", 3),
        ("StaminaRate", 4),
        ("ManaRate", 5),
        ("HealthUponResurrection", 6),
        ("StaminaUponResurrection", 7),
        ("ManaUponResurrection", 8),
        ("StartTime", 9),
        ("StopTime", 10),
        ("ResetInterval", 11),
        ("Shade", 12),
        ("ArmorModVsSlash", 13),
        ("ArmorModVsPierce", 14),
        ("ArmorModVsBludgeon", 15),
        ("ArmorModVsCold", 16),
        ("ArmorModVsFire", 17),
        ("ArmorModVsAcid", 18),
        ("ArmorModVsElectric", 19),
        ("CombatSpeed", 20),
        ("WeaponLength", 21),
        ("DamageVariance", 22),
        ("CurrentPowerMod", 23),
        ("AccuracyMod", 24),
        ("StrengthMod", 25),
        ("MaximumVelocity", 26),
        ("RotationSpeed", 27),
        ("MotionTimestamp", 28),
        ("WeaponDefense", 29),
        ("WimpyLevel", 30),
        ("VisualAwarenessRange", 31),
        ("AuralAwarenessRange", 32),
        ("PerceptionLevel", 33),
        ("PowerupTime", 34),
        ("MaxChargeDistance", 35),
        ("ChargeSpeed", 36),
        ("BuyPrice", 37),
        ("SellPrice", 38),
        ("DefaultScale", 39),
        ("LockpickMod", 40),
        ("RegenerationInterval", 41),
        ("RegenerationTimestamp", 42),
        ("GeneratorRadius", 43),
        ("TimeToRot", 44),
        ("DeathTimestamp", 45),
        ("PkTimestamp", 46),
        ("VictimTimestamp", 47),
        ("LoginTimestamp", 48),
        ("CreationTimestamp", 49),
        ("MinimumTimeSincePk", 50),
        ("DeprecatedHousekeepingPriority", 51),
        ("AbuseLoggingTimestamp", 52),
        ("LastPortalTeleportTimestamp", 53),
        ("UseRadius", 54),
        ("HomeRadius", 55),
        ("ReleasedTimestamp", 56),
        ("MinHomeRadius", 57),
        ("Facing", 58),
        ("ResetTimestamp", 59),
        ("LogoffTimestamp", 60),
        ("EconRecoveryInterval", 61),
        ("WeaponOffense", 62),
        ("DamageMod", 63),
        ("ResistSlash", 64),
        ("ResistPierce", 65),
        ("ResistBludgeon", 66),
        ("ResistFire", 67),
        ("ResistCold", 68),
        ("ResistAcid", 69),
        ("ResistElectric", 70),
        ("ResistHealthBoost", 71),
        ("ResistStaminaDrain", 72),
        ("ResistStaminaBoost", 73),
        ("ResistManaDrain", 74),
        ("ResistManaBoost", 75),
        ("Translucency", 76),
        ("PhysicsScriptIntensity", 77),
        ("Friction", 78),
        ("Elasticity", 79),
        ("AiUseMagicDelay", 80),
        ("ItemMinSpellcraftMod", 81),
        ("ItemMaxSpellcraftMod", 82),
        ("ItemRankProbability", 83),
        ("Shade2", 84),
        ("Shade3", 85),
        ("Shade4", 86),
        ("ItemEfficiency", 87),
        ("ItemManaUpdateTimestamp", 88),
        ("SpellGestureSpeedMod", 89),
        ("SpellStanceSpeedMod", 90),
        ("AllegianceAppraisalTimestamp", 91),
        ("PowerLevel", 92),
        ("AccuracyLevel", 93),
        ("AttackAngle", 94),
        ("AttackTimestamp", 95),
        ("CheckpointTimestamp", 96),
        ("SoldTimestamp", 97),
        ("UseTimestamp", 98),
        ("UseLockTimestamp", 99),
        ("HealkitMod", 100),
        ("FrozenTimestamp", 101),
        ("HealthRateMod", 102),
        ("AllegianceSwearTimestamp", 103),
        ("ObviousRadarRange", 104),
        ("HotspotCycleTime", 105),
        ("HotspotCycleTimeVariance", 106),
        ("SpamTimestamp", 107),
        ("SpamRate", 108),
        ("BondWieldedTreasure", 109),
        ("BulkMod", 110),
        ("SizeMod", 111),
        ("GagTimestamp", 112),
        ("GeneratorUpdateTimestamp", 113),
        ("DeathSpamTimestamp", 114),
        ("DeathSpamRate", 115),
        ("WildAttackProbability", 116),
        ("FocusedProbability", 117),
        ("CrashAndTurnProbability", 118),
        ("CrashAndTurnRadius", 119),
        ("CrashAndTurnBias", 120),
        ("GeneratorInitialDelay", 121),
        ("AiAcquireHealth", 122),
        ("AiAcquireStamina", 123),
        ("AiAcquireMana", 124),
        ("ResistHealthDrain", 125),
        ("LifestoneProtectionTimestamp", 126),
        ("AiCounteractEnchantment", 127),
        ("AiDispelEnchantment", 128),
        ("TradeTimestamp", 129),
        ("AiTargetedDetectionRadius", 130),
        ("EmotePriority", 131),
        ("LastTeleportStartTimestamp", 132),
        ("EventSpamTimestamp", 133),
        ("EventSpamRate", 134),
        ("InventoryOffset", 135),
        ("CriticalMultiplier", 136),
        ("ManaStoneDestroyChance", 137),
        ("SlayerDamageBonus", 138),
        ("AllegianceInfoSpamTimestamp", 139),
        ("AllegianceInfoSpamRate", 140),
        ("NextSpellcastTimestamp", 141),
        ("AppraisalRequestedTimestamp", 142),
        ("AppraisalHeartbeatDueTimestamp", 143),
        ("ManaConversionMod", 144),
        ("LastPkAttackTimestamp", 145),
        ("FellowshipUpdateTimestamp", 146),
        ("CriticalFrequency", 147),
        ("LimboStartTimestamp", 148),
        ("WeaponMissileDefense", 149),
        ("WeaponMagicDefense", 150),
        ("IgnoreShield", 151),
        ("ElementalDamageMod", 152),
        ("StartMissileAttackTimestamp", 153),
        ("LastRareUsedTimestamp", 154),
        ("IgnoreArmor", 155),
        ("ProcSpellRate", 156),
        ("ResistanceModifier", 157),
        ("AllegianceGagTimestamp", 158),
        ("AbsorbMagicDamage", 159),
        ("CachedMaxAbsorbMagicDamage", 160),
        ("GagDuration", 161),
        ("AllegianceGagDuration", 162),
        ("GlobalXpMod", 163),
        ("HealingModifier", 164),
        ("ArmorModVsNether", 165),
        ("ResistNether", 166),
        ("CooldownDuration", 167),
        ("WeaponAuraOffense", 168),
        ("WeaponAuraDefense", 169),
        ("WeaponAuraElemental", 170),
        ("WeaponAuraManaConv", 171),
    ];

    private static readonly (string Name, uint Value)[] PropertyStringGolden =
    [
        ("Undef", 0),
        ("Name", 1),
        ("Title", 2),
        ("Sex", 3),
        ("HeritageGroup", 4),
        ("Template", 5),
        ("AttackersName", 6),
        ("Inscription", 7),
        ("ScribeName", 8),
        ("VendorsName", 9),
        ("Fellowship", 10),
        ("MonarchsName", 11),
        ("LockCode", 12),
        ("KeyCode", 13),
        ("Use", 14),
        ("ShortDesc", 15),
        ("LongDesc", 16),
        ("ActivationTalk", 17),
        ("UseMessage", 18),
        ("ItemHeritageGroupRestriction", 19),
        ("PluralName", 20),
        ("MonarchsTitle", 21),
        ("ActivationFailure", 22),
        ("ScribeAccount", 23),
        ("TownName", 24),
        ("CraftsmanName", 25),
        ("UsePkServerError", 26),
        ("ScoreCachedText", 27),
        ("ScoreDefaultEntryFormat", 28),
        ("ScoreFirstEntryFormat", 29),
        ("ScoreLastEntryFormat", 30),
        ("ScoreOnlyEntryFormat", 31),
        ("ScoreNoEntry", 32),
        ("Quest", 33),
        ("GeneratorEvent", 34),
        ("PatronsTitle", 35),
        ("HouseOwnerName", 36),
        ("QuestRestriction", 37),
        ("AppraisalPortalDestination", 38),
        ("TinkerName", 39),
        ("ImbuerName", 40),
        ("HouseOwnerAccount", 41),
        ("DisplayName", 42),
        ("DateOfBirth", 43),
        ("ThirdPartyApi", 44),
        ("KillQuest", 45),
        ("Afk", 46),
        ("AllegianceName", 47),
        ("AugmentationAddQuest", 48),
        ("KillQuest2", 49),
        ("KillQuest3", 50),
        ("UseSendsSignal", 51),
        ("GearPlatingName", 52),
    ];

    private static readonly (string Name, uint Value)[] PropertyDataIdGolden =
    [
        ("Undef", 0),
        ("Setup", 1),
        ("MotionTable", 2),
        ("SoundTable", 3),
        ("CombatTable", 4),
        ("QualityFilter", 5),
        ("PaletteBase", 6),
        ("ClothingBase", 7),
        ("Icon", 8),
        ("EyesTexture", 9),
        ("NoseTexture", 10),
        ("MouthTexture", 11),
        ("DefaultEyesTexture", 12),
        ("DefaultNoseTexture", 13),
        ("DefaultMouthTexture", 14),
        ("HairPalette", 15),
        ("EyesPalette", 16),
        ("SkinPalette", 17),
        ("HeadObject", 18),
        ("ActivationAnimation", 19),
        ("InitMotion", 20),
        ("ActivationSound", 21),
        ("PhysicsEffectTable", 22),
        ("UseSound", 23),
        ("UseTargetAnimation", 24),
        ("UseTargetSuccessAnimation", 25),
        ("UseTargetFailureAnimation", 26),
        ("UseUserAnimation", 27),
        ("Spell", 28),
        ("SpellComponent", 29),
        ("PhysicsScript", 30),
        ("LinkedPortalOne", 31),
        ("WieldedTreasureType", 32),
        ("UnknownGuessedname", 33),
        ("UnknownGuessedname2", 34),
        ("DeathTreasureType", 35),
        ("MutateFilter", 36),
        ("ItemSkillLimit", 37),
        ("UseCreateItem", 38),
        ("DeathSpell", 39),
        ("VendorsClassId", 40),
        ("ItemSpecializedOnly", 41),
        ("HouseId", 42),
        ("AccountHouseId", 43),
        ("RestrictionEffect", 44),
        ("CreationMutationFilter", 45),
        ("TsysMutationFilter", 46),
        ("LastPortal", 47),
        ("LinkedPortalTwo", 48),
        ("OriginalPortal", 49),
        ("IconOverlay", 50),
        ("IconOverlaySecondary", 51),
        ("IconUnderlay", 52),
        ("AugmentationMutationFilter", 53),
        ("AugmentationEffect", 54),
        ("ProcSpell", 55),
        ("AugmentationCreateItem", 56),
        ("AlternateCurrency", 57),
        ("BlueSurgeSpell", 58),
        ("YellowSurgeSpell", 59),
        ("RedSurgeSpell", 60),
        ("OlthoiDeathTreasureType", 61),
    ];

    private static readonly (string Name, uint Value)[] PropertyInstanceIdGolden =
    [
        ("Undef", 0),
        ("Owner", 1),
        ("Container", 2),
        ("Wielder", 3),
        ("Freezer", 4),
        ("Viewer", 5),
        ("Generator", 6),
        ("Scribe", 7),
        ("CurrentCombatTarget", 8),
        ("CurrentEnemy", 9),
        ("ProjectileLauncher", 10),
        ("CurrentAttacker", 11),
        ("CurrentDamager", 12),
        ("CurrentFollowTarget", 13),
        ("CurrentAppraisalTarget", 14),
        ("CurrentFellowshipAppraisalTarget", 15),
        ("ActivationTarget", 16),
        ("Creator", 17),
        ("Victim", 18),
        ("Killer", 19),
        ("Vendor", 20),
        ("Customer", 21),
        ("Bonded", 22),
        ("Wounder", 23),
        ("Allegiance", 24),
        ("Patron", 25),
        ("Monarch", 26),
        ("CombatTarget", 27),
        ("HealthQueryTarget", 28),
        ("LastUnlocker", 29),
        ("CrashAndTurnTarget", 30),
        ("AllowedActivator", 31),
        ("HouseOwner", 32),
        ("House", 33),
        ("Slumlord", 34),
        ("ManaQueryTarget", 35),
        ("CurrentGame", 36),
        ("RequestedAppraisalTarget", 37),
        ("AllowedWielder", 38),
        ("AssignedTarget", 39),
        ("LimboSource", 40),
        ("Snooper", 41),
        ("TeleportedCharacter", 42),
        ("Pet", 43),
        ("PetOwner", 44),
        ("PetDevice", 45),
    ];

    public static TheoryData<string> Families =>
        new("PropertyInt", "PropertyInt64", "PropertyBool", "PropertyFloat", "PropertyString", "PropertyDataId", "PropertyInstanceId");

    private static (Type Enum, (string Name, uint Value)[] Golden) Resolve(string family) => family switch
    {
        "PropertyInt" => (typeof(PropertyInt), PropertyIntGolden),
        "PropertyInt64" => (typeof(PropertyInt64), PropertyInt64Golden),
        "PropertyBool" => (typeof(PropertyBool), PropertyBoolGolden),
        "PropertyFloat" => (typeof(PropertyFloat), PropertyFloatGolden),
        "PropertyString" => (typeof(PropertyString), PropertyStringGolden),
        "PropertyDataId" => (typeof(PropertyDataId), PropertyDataIdGolden),
        "PropertyInstanceId" => (typeof(PropertyInstanceId), PropertyInstanceIdGolden),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown property family"),
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void EnumMatchesGoldenTableExactly(string family)
    {
        var (type, golden) = Resolve(family);
        var actual = Enum.GetNames(type)
            .Select(n => (Name: n, Value: Convert.ToUInt32(Enum.Parse(type, n))))
            .OrderBy(e => e.Value).ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
        var expected = golden
            .OrderBy(e => e.Value).ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void UnderlyingTypeIsUInt32(string family)
    {
        var (type, _) = Resolve(family);
        Assert.Equal(typeof(uint), Enum.GetUnderlyingType(type));
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void NoTwoMembersShareAValue(string family)
    {
        var (_, golden) = Resolve(family);
        var dupes = golden.GroupBy(e => e.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key + ": " + string.Join(", ", g.Select(e => e.Name)))
            .ToArray();
        Assert.Empty(dupes);
    }

    public static TheoryData<string, string, uint> WeenieAttested
    {
        get
        {
            var d = new TheoryData<string, string, uint>();
            d.Add("PropertyInt", "ItemType", 1);
            d.Add("PropertyInt", "CreatureType", 2);
            d.Add("PropertyInt", "PaletteTemplate", 3);
            d.Add("PropertyInt", "ClothingPriority", 4);
            d.Add("PropertyInt", "EncumbranceVal", 5);
            d.Add("PropertyInt", "ItemsCapacity", 6);
            d.Add("PropertyInt", "ContainersCapacity", 7);
            d.Add("PropertyInt", "Mass", 8);
            d.Add("PropertyInt", "ValidLocations", 9);
            d.Add("PropertyInt", "MaxStackSize", 11);
            d.Add("PropertyInt", "StackSize", 12);
            d.Add("PropertyInt", "StackUnitEncumbrance", 13);
            d.Add("PropertyInt", "StackUnitMass", 14);
            d.Add("PropertyInt", "StackUnitValue", 15);
            d.Add("PropertyInt", "ItemUseable", 16);
            d.Add("PropertyInt", "RareId", 17);
            d.Add("PropertyInt", "UiEffects", 18);
            d.Add("PropertyInt", "Value", 19);
            d.Add("PropertyInt", "CoinValue", 20);
            d.Add("PropertyInt", "Level", 25);
            d.Add("PropertyInt", "AccountRequirements", 26);
            d.Add("PropertyInt", "ArmorType", 27);
            d.Add("PropertyInt", "ArmorLevel", 28);
            d.Add("PropertyInt", "AllegianceRank", 30);
            d.Add("PropertyInt", "Bonded", 33);
            d.Add("PropertyInt", "ResistMagic", 36);
            d.Add("PropertyInt", "ResistItemAppraisal", 37);
            d.Add("PropertyInt", "ResistLockpick", 38);
            d.Add("PropertyInt", "CombatMode", 40);
            d.Add("PropertyInt", "NumDeaths", 43);
            d.Add("PropertyInt", "Damage", 44);
            d.Add("PropertyInt", "DamageType", 45);
            d.Add("PropertyInt", "DefaultCombatStyle", 46);
            d.Add("PropertyInt", "AttackType", 47);
            d.Add("PropertyInt", "WeaponSkill", 48);
            d.Add("PropertyInt", "WeaponTime", 49);
            d.Add("PropertyInt", "AmmoType", 50);
            d.Add("PropertyInt", "CombatUse", 51);
            d.Add("PropertyInt", "ParentLocation", 52);
            d.Add("PropertyInt", "PlacementPosition", 53);
            d.Add("PropertyInt", "WeaponRange", 60);
            d.Add("PropertyInt", "CheckpointStatus", 66);
            d.Add("PropertyInt", "Tolerance", 67);
            d.Add("PropertyInt", "TargetingTactic", 68);
            d.Add("PropertyInt", "CombatTactic", 69);
            d.Add("PropertyInt", "FriendType", 72);
            d.Add("PropertyInt", "MerchandiseItemTypes", 74);
            d.Add("PropertyInt", "MerchandiseMinValue", 75);
            d.Add("PropertyInt", "MerchandiseMaxValue", 76);
            d.Add("PropertyInt", "MaxGeneratedObjects", 81);
            d.Add("PropertyInt", "InitGeneratedObjects", 82);
            d.Add("PropertyInt", "ActivationResponse", 83);
            d.Add("PropertyInt", "MinLevel", 86);
            d.Add("PropertyInt", "MaxLevel", 87);
            d.Add("PropertyInt", "LockpickMod", 88);
            d.Add("PropertyInt", "BoosterEnum", 89);
            d.Add("PropertyInt", "BoostValue", 90);
            d.Add("PropertyInt", "MaxStructure", 91);
            d.Add("PropertyInt", "Structure", 92);
            d.Add("PropertyInt", "PhysicsState", 93);
            d.Add("PropertyInt", "TargetType", 94);
            d.Add("PropertyInt", "RadarBlipColor", 95);
            d.Add("PropertyInt", "EncumbranceCapacity", 96);
            d.Add("PropertyInt", "CreationTimestamp", 98);
            d.Add("PropertyInt", "PkLevelModifier", 99);
            d.Add("PropertyInt", "GeneratorType", 100);
            d.Add("PropertyInt", "AiAllowedCombatStyle", 101);
            d.Add("PropertyInt", "GeneratorDestructionType", 103);
            d.Add("PropertyInt", "ItemWorkmanship", 105);
            d.Add("PropertyInt", "ItemSpellcraft", 106);
            d.Add("PropertyInt", "ItemCurMana", 107);
            d.Add("PropertyInt", "ItemMaxMana", 108);
            d.Add("PropertyInt", "ItemDifficulty", 109);
            d.Add("PropertyInt", "ItemAllegianceRankLimit", 110);
            d.Add("PropertyInt", "PortalBitmask", 111);
            d.Add("PropertyInt", "Gender", 113);
            d.Add("PropertyInt", "Attuned", 114);
            d.Add("PropertyInt", "ItemSkillLevelLimit", 115);
            d.Add("PropertyInt", "ItemManaCost", 117);
            d.Add("PropertyInt", "Active", 119);
            d.Add("PropertyInt", "Age", 125);
            d.Add("PropertyInt", "VendorHappyMean", 126);
            d.Add("PropertyInt", "VendorHappyVariance", 127);
            d.Add("PropertyInt", "MaterialType", 131);
            d.Add("PropertyInt", "ShowableOnRadar", 133);
            d.Add("PropertyInt", "PlayerKillerStatus", 134);
            d.Add("PropertyInt", "ScorePageNum", 136);
            d.Add("PropertyInt", "ScoreConfigNum", 137);
            d.Add("PropertyInt", "ScoreNumScores", 138);
            d.Add("PropertyInt", "AiOptions", 140);
            d.Add("PropertyInt", "GeneratorTimeType", 142);
            d.Add("PropertyInt", "GeneratorStartTime", 143);
            d.Add("PropertyInt", "GeneratorEndTime", 144);
            d.Add("PropertyInt", "GeneratorEndDestructionType", 145);
            d.Add("PropertyInt", "XpOverride", 146);
            d.Add("PropertyInt", "HouseStatus", 149);
            d.Add("PropertyInt", "HookPlacement", 150);
            d.Add("PropertyInt", "HookType", 151);
            d.Add("PropertyInt", "HookItemType", 152);
            d.Add("PropertyInt", "HouseType", 155);
            d.Add("PropertyInt", "PickupEmoteOffset", 156);
            d.Add("PropertyInt", "WeenieIteration", 157);
            d.Add("PropertyInt", "WieldRequirements", 158);
            d.Add("PropertyInt", "WieldSkilltype", 159);
            d.Add("PropertyInt", "WieldDifficulty", 160);
            d.Add("PropertyInt", "HouseMaxHooksUsable", 161);
            d.Add("PropertyInt", "AllegianceMinLevel", 163);
            d.Add("PropertyInt", "SlayerCreatureType", 166);
            d.Add("PropertyInt", "TsysMutationData", 169);
            d.Add("PropertyInt", "NumItemsInMaterial", 170);
            d.Add("PropertyInt", "NumTimesTinkered", 171);
            d.Add("PropertyInt", "AppraisalLongDescDecoration", 172);
            d.Add("PropertyInt", "AppraisalLockpickSuccessPercent", 173);
            d.Add("PropertyInt", "AppraisalPages", 174);
            d.Add("PropertyInt", "AppraisalMaxPages", 175);
            d.Add("PropertyInt", "AppraisalItemSkill", 176);
            d.Add("PropertyInt", "GemCount", 177);
            d.Add("PropertyInt", "GemType", 178);
            d.Add("PropertyInt", "ImbuedEffect", 179);
            d.Add("PropertyInt", "TypeOfAlteration", 185);
            d.Add("PropertyInt", "SkillToBeAltered", 186);
            d.Add("PropertyInt", "HeritageGroup", 188);
            d.Add("PropertyInt", "TransferFromAttribute", 189);
            d.Add("PropertyInt", "TransferToAttribute", 190);
            d.Add("PropertyInt", "NumKeys", 193);
            d.Add("PropertyInt", "HookGroup", 197);
            d.Add("PropertyInt", "ElementalDamageBonus", 204);
            d.Add("PropertyInt", "ItemAttributeLimit", 257);
            d.Add("PropertyInt", "ItemAttributeLevelLimit", 258);
            d.Add("PropertyInt", "CharacterTitleId", 261);
            d.Add("PropertyInt", "NumCharacterTitles", 262);
            d.Add("PropertyInt", "ResistanceModifierType", 263);
            d.Add("PropertyInt", "EquipmentSetId", 265);
            d.Add("PropertyInt", "Lifespan", 267);
            d.Add("PropertyInt", "RemainingLifespan", 268);
            d.Add("PropertyInt", "WieldRequirements2", 270);
            d.Add("PropertyInt", "WieldSkilltype2", 271);
            d.Add("PropertyInt", "WieldDifficulty2", 272);
            d.Add("PropertyInt", "WieldRequirements3", 273);
            d.Add("PropertyInt", "WieldSkilltype3", 274);
            d.Add("PropertyInt", "WieldDifficulty3", 275);
            d.Add("PropertyInt", "WieldRequirements4", 276);
            d.Add("PropertyInt", "WieldSkilltype4", 277);
            d.Add("PropertyInt", "WieldDifficulty4", 278);
            d.Add("PropertyInt", "Unique", 279);
            d.Add("PropertyInt", "SharedCooldown", 280);
            d.Add("PropertyInt", "Faction1Bits", 281);
            d.Add("PropertyInt", "SocietyRankCelhan", 287);
            d.Add("PropertyInt", "SocietyRankEldweb", 288);
            d.Add("PropertyInt", "SocietyRankRadblo", 289);
            d.Add("PropertyInt", "Cleaving", 292);
            d.Add("PropertyInt", "ImbuedEffect2", 303);
            d.Add("PropertyInt", "ImbuedEffect3", 304);
            d.Add("PropertyInt", "ImbuedEffect4", 305);
            d.Add("PropertyInt", "ImbuedEffect5", 306);
            d.Add("PropertyInt", "DamageRating", 307);
            d.Add("PropertyInt", "DamageResistRating", 308);
            d.Add("PropertyInt", "CritRating", 313);
            d.Add("PropertyInt", "CritDamageRating", 314);
            d.Add("PropertyInt", "CritResistRating", 315);
            d.Add("PropertyInt", "CritDamageResistRating", 316);
            d.Add("PropertyInt", "ItemMaxLevel", 319);
            d.Add("PropertyInt", "ItemXpStyle", 320);
            d.Add("PropertyInt", "HeritageSpecificArmor", 324);
            d.Add("PropertyInt", "CloakWeaveProc", 352);
            d.Add("PropertyInt", "WeaponType", 353);
            d.Add("PropertyInt", "UseRequiresSkill", 366);
            d.Add("PropertyInt", "UseRequiresSkillLevel", 367);
            d.Add("PropertyInt", "UseRequiresSkillSpec", 368);
            d.Add("PropertyInt", "UseRequiresLevel", 369);
            d.Add("PropertyInt", "GearDamage", 370);
            d.Add("PropertyInt", "GearDamageResist", 371);
            d.Add("PropertyInt", "GearCrit", 372);
            d.Add("PropertyInt", "GearCritResist", 373);
            d.Add("PropertyInt", "GearCritDamage", 374);
            d.Add("PropertyInt", "GearCritDamageResist", 375);
            d.Add("PropertyInt", "GearHealingBoost", 376);
            d.Add("PropertyInt", "GearNetherResist", 377);
            d.Add("PropertyInt", "GearLifeResist", 378);
            d.Add("PropertyInt", "GearMaxHealth", 379);
            d.Add("PropertyInt", "PKDamageRating", 381);
            d.Add("PropertyInt", "PKDamageResistRating", 382);
            d.Add("PropertyInt", "GearPKDamageRating", 383);
            d.Add("PropertyInt", "GearPKDamageResistRating", 384);
            d.Add("PropertyInt", "Overpower", 386);
            d.Add("PropertyInt", "OverpowerResist", 387);
            d.Add("PropertyInt", "GearOverpower", 388);
            d.Add("PropertyInt", "GearOverpowerResist", 389);
            d.Add("PropertyInt", "Enlightenment", 390);
            d.Add("PropertyInt64", "AugmentationCost", 3);
            d.Add("PropertyInt64", "ItemTotalXp", 4);
            d.Add("PropertyInt64", "ItemBaseXp", 5);
            d.Add("PropertyBool", "Stuck", 1);
            d.Add("PropertyBool", "Open", 2);
            d.Add("PropertyBool", "Locked", 3);
            d.Add("PropertyBool", "AiUsesMana", 6);
            d.Add("PropertyBool", "AiUseHumanMagicAnimations", 7);
            d.Add("PropertyBool", "AllowGive", 8);
            d.Add("PropertyBool", "IgnoreCollisions", 11);
            d.Add("PropertyBool", "ReportCollisions", 12);
            d.Add("PropertyBool", "Ethereal", 13);
            d.Add("PropertyBool", "GravityStatus", 14);
            d.Add("PropertyBool", "LightsStatus", 15);
            d.Add("PropertyBool", "ScriptedCollision", 16);
            d.Add("PropertyBool", "Inelastic", 17);
            d.Add("PropertyBool", "Visibility", 18);
            d.Add("PropertyBool", "Attackable", 19);
            d.Add("PropertyBool", "Inscribable", 22);
            d.Add("PropertyBool", "DestroyOnSell", 23);
            d.Add("PropertyBool", "UiHidden", 24);
            d.Add("PropertyBool", "NoCorpse", 29);
            d.Add("PropertyBool", "ResetMessagePending", 33);
            d.Add("PropertyBool", "DefaultOpen", 34);
            d.Add("PropertyBool", "DefaultLocked", 35);
            d.Add("PropertyBool", "DealMagicalItems", 39);
            d.Add("PropertyBool", "ReportCollisionsAsEnvironment", 41);
            d.Add("PropertyBool", "AllowEdgeSlide", 42);
            d.Add("PropertyBool", "NeverFailCasting", 50);
            d.Add("PropertyBool", "VendorService", 51);
            d.Add("PropertyBool", "AiImmobile", 52);
            d.Add("PropertyBool", "DamagedByCollisions", 53);
            d.Add("PropertyBool", "IsDynamic", 54);
            d.Add("PropertyBool", "IsHot", 55);
            d.Add("PropertyBool", "AffectsAis", 57);
            d.Add("PropertyBool", "LoggingChannel", 61);
            d.Add("PropertyBool", "OpensAnyLock", 62);
            d.Add("PropertyBool", "UnlimitedUse", 63);
            d.Add("PropertyBool", "IgnoreMagicResist", 65);
            d.Add("PropertyBool", "IgnoreMagicArmor", 66);
            d.Add("PropertyBool", "SpellComponentsRequired", 68);
            d.Add("PropertyBool", "IsSellable", 69);
            d.Add("PropertyBool", "IgnoreShieldsBySkill", 70);
            d.Add("PropertyBool", "NoDraw", 71);
            d.Add("PropertyBool", "GeneratorAutomaticDestruction", 74);
            d.Add("PropertyBool", "HouseRequiresMonarch", 76);
            d.Add("PropertyBool", "AiAcceptEverything", 79);
            d.Add("PropertyBool", "RequiresBackpackSlot", 81);
            d.Add("PropertyBool", "DontTurnOrMoveWhenGiving", 82);
            d.Add("PropertyBool", "NpcLooksLikeObject", 83);
            d.Add("PropertyBool", "IgnoreCloIcons", 84);
            d.Add("PropertyBool", "AppraisalHasAllowedWielder", 85);
            d.Add("PropertyBool", "PortalShowDestination", 88);
            d.Add("PropertyBool", "PortalIgnoresPkAttackTimer", 89);
            d.Add("PropertyBool", "NpcInteractsSilently", 90);
            d.Add("PropertyBool", "Retained", 91);
            d.Add("PropertyBool", "IgnoreAuthor", 92);
            d.Add("PropertyBool", "Ivoryable", 99);
            d.Add("PropertyBool", "Dyable", 100);
            d.Add("PropertyBool", "NonProjectileMagicImmune", 103);
            d.Add("PropertyBool", "RareUsesTimer", 108);
            d.Add("PropertyBool", "AutowieldLeft", 130);
            d.Add("PropertyFloat", "HeartbeatInterval", 1);
            d.Add("PropertyFloat", "HeartbeatTimestamp", 2);
            d.Add("PropertyFloat", "HealthRate", 3);
            d.Add("PropertyFloat", "StaminaRate", 4);
            d.Add("PropertyFloat", "ManaRate", 5);
            d.Add("PropertyFloat", "HealthUponResurrection", 6);
            d.Add("PropertyFloat", "StaminaUponResurrection", 7);
            d.Add("PropertyFloat", "ManaUponResurrection", 8);
            d.Add("PropertyFloat", "ResetInterval", 11);
            d.Add("PropertyFloat", "Shade", 12);
            d.Add("PropertyFloat", "ArmorModVsSlash", 13);
            d.Add("PropertyFloat", "ArmorModVsPierce", 14);
            d.Add("PropertyFloat", "ArmorModVsBludgeon", 15);
            d.Add("PropertyFloat", "ArmorModVsCold", 16);
            d.Add("PropertyFloat", "ArmorModVsFire", 17);
            d.Add("PropertyFloat", "ArmorModVsAcid", 18);
            d.Add("PropertyFloat", "ArmorModVsElectric", 19);
            d.Add("PropertyFloat", "WeaponLength", 21);
            d.Add("PropertyFloat", "DamageVariance", 22);
            d.Add("PropertyFloat", "MaximumVelocity", 26);
            d.Add("PropertyFloat", "RotationSpeed", 27);
            d.Add("PropertyFloat", "WeaponDefense", 29);
            d.Add("PropertyFloat", "VisualAwarenessRange", 31);
            d.Add("PropertyFloat", "PowerupTime", 34);
            d.Add("PropertyFloat", "ChargeSpeed", 36);
            d.Add("PropertyFloat", "BuyPrice", 37);
            d.Add("PropertyFloat", "SellPrice", 38);
            d.Add("PropertyFloat", "DefaultScale", 39);
            d.Add("PropertyFloat", "LockpickMod", 40);
            d.Add("PropertyFloat", "RegenerationInterval", 41);
            d.Add("PropertyFloat", "GeneratorRadius", 43);
            d.Add("PropertyFloat", "TimeToRot", 44);
            d.Add("PropertyFloat", "MinimumTimeSincePk", 50);
            d.Add("PropertyFloat", "UseRadius", 54);
            d.Add("PropertyFloat", "HomeRadius", 55);
            d.Add("PropertyFloat", "WeaponOffense", 62);
            d.Add("PropertyFloat", "DamageMod", 63);
            d.Add("PropertyFloat", "ResistSlash", 64);
            d.Add("PropertyFloat", "ResistPierce", 65);
            d.Add("PropertyFloat", "ResistBludgeon", 66);
            d.Add("PropertyFloat", "ResistFire", 67);
            d.Add("PropertyFloat", "ResistCold", 68);
            d.Add("PropertyFloat", "ResistAcid", 69);
            d.Add("PropertyFloat", "ResistElectric", 70);
            d.Add("PropertyFloat", "ResistHealthBoost", 71);
            d.Add("PropertyFloat", "ResistStaminaDrain", 72);
            d.Add("PropertyFloat", "ResistStaminaBoost", 73);
            d.Add("PropertyFloat", "ResistManaDrain", 74);
            d.Add("PropertyFloat", "ResistManaBoost", 75);
            d.Add("PropertyFloat", "Translucency", 76);
            d.Add("PropertyFloat", "PhysicsScriptIntensity", 77);
            d.Add("PropertyFloat", "Friction", 78);
            d.Add("PropertyFloat", "Elasticity", 79);
            d.Add("PropertyFloat", "AiUseMagicDelay", 80);
            d.Add("PropertyFloat", "ItemEfficiency", 87);
            d.Add("PropertyFloat", "HealkitMod", 100);
            d.Add("PropertyFloat", "ObviousRadarRange", 104);
            d.Add("PropertyFloat", "HotspotCycleTime", 105);
            d.Add("PropertyFloat", "HotspotCycleTimeVariance", 106);
            d.Add("PropertyFloat", "BondWieldedTreasure", 109);
            d.Add("PropertyFloat", "BulkMod", 110);
            d.Add("PropertyFloat", "SizeMod", 111);
            d.Add("PropertyFloat", "FocusedProbability", 117);
            d.Add("PropertyFloat", "GeneratorInitialDelay", 121);
            d.Add("PropertyFloat", "AiAcquireHealth", 122);
            d.Add("PropertyFloat", "AiAcquireStamina", 123);
            d.Add("PropertyFloat", "AiAcquireMana", 124);
            d.Add("PropertyFloat", "ResistHealthDrain", 125);
            d.Add("PropertyFloat", "AiCounteractEnchantment", 127);
            d.Add("PropertyFloat", "AiDispelEnchantment", 128);
            d.Add("PropertyFloat", "EmotePriority", 131);
            d.Add("PropertyFloat", "InventoryOffset", 135);
            d.Add("PropertyFloat", "CriticalMultiplier", 136);
            d.Add("PropertyFloat", "ManaStoneDestroyChance", 137);
            d.Add("PropertyFloat", "SlayerDamageBonus", 138);
            d.Add("PropertyFloat", "ManaConversionMod", 144);
            d.Add("PropertyFloat", "CriticalFrequency", 147);
            d.Add("PropertyFloat", "WeaponMissileDefense", 149);
            d.Add("PropertyFloat", "WeaponMagicDefense", 150);
            d.Add("PropertyFloat", "IgnoreShield", 151);
            d.Add("PropertyFloat", "ElementalDamageMod", 152);
            d.Add("PropertyFloat", "IgnoreArmor", 155);
            d.Add("PropertyFloat", "ResistanceModifier", 157);
            d.Add("PropertyFloat", "AbsorbMagicDamage", 159);
            d.Add("PropertyFloat", "ArmorModVsNether", 165);
            d.Add("PropertyFloat", "CooldownDuration", 167);
            d.Add("PropertyString", "Name", 1);
            d.Add("PropertyString", "Sex", 3);
            d.Add("PropertyString", "HeritageGroup", 4);
            d.Add("PropertyString", "Template", 5);
            d.Add("PropertyString", "Inscription", 7);
            d.Add("PropertyString", "ScribeName", 8);
            d.Add("PropertyString", "Fellowship", 10);
            d.Add("PropertyString", "LockCode", 12);
            d.Add("PropertyString", "KeyCode", 13);
            d.Add("PropertyString", "Use", 14);
            d.Add("PropertyString", "ShortDesc", 15);
            d.Add("PropertyString", "LongDesc", 16);
            d.Add("PropertyString", "ActivationTalk", 17);
            d.Add("PropertyString", "UseMessage", 18);
            d.Add("PropertyString", "ItemHeritageGroupRestriction", 19);
            d.Add("PropertyString", "PluralName", 20);
            d.Add("PropertyString", "ActivationFailure", 22);
            d.Add("PropertyString", "TownName", 24);
            d.Add("PropertyString", "CraftsmanName", 25);
            d.Add("PropertyString", "UsePkServerError", 26);
            d.Add("PropertyString", "ScoreDefaultEntryFormat", 28);
            d.Add("PropertyString", "ScoreFirstEntryFormat", 29);
            d.Add("PropertyString", "ScoreLastEntryFormat", 30);
            d.Add("PropertyString", "ScoreOnlyEntryFormat", 31);
            d.Add("PropertyString", "ScoreNoEntry", 32);
            d.Add("PropertyString", "Quest", 33);
            d.Add("PropertyString", "GeneratorEvent", 34);
            d.Add("PropertyString", "QuestRestriction", 37);
            d.Add("PropertyDataId", "Setup", 1);
            d.Add("PropertyDataId", "MotionTable", 2);
            d.Add("PropertyDataId", "SoundTable", 3);
            d.Add("PropertyDataId", "CombatTable", 4);
            d.Add("PropertyDataId", "QualityFilter", 5);
            d.Add("PropertyDataId", "PaletteBase", 6);
            d.Add("PropertyDataId", "ClothingBase", 7);
            d.Add("PropertyDataId", "Icon", 8);
            d.Add("PropertyDataId", "EyesTexture", 9);
            d.Add("PropertyDataId", "NoseTexture", 10);
            d.Add("PropertyDataId", "MouthTexture", 11);
            d.Add("PropertyDataId", "HairPalette", 15);
            d.Add("PropertyDataId", "EyesPalette", 16);
            d.Add("PropertyDataId", "SkinPalette", 17);
            d.Add("PropertyDataId", "ActivationAnimation", 19);
            d.Add("PropertyDataId", "InitMotion", 20);
            d.Add("PropertyDataId", "PhysicsEffectTable", 22);
            d.Add("PropertyDataId", "UseSound", 23);
            d.Add("PropertyDataId", "UseTargetAnimation", 24);
            d.Add("PropertyDataId", "UseTargetSuccessAnimation", 25);
            d.Add("PropertyDataId", "UseTargetFailureAnimation", 26);
            d.Add("PropertyDataId", "UseUserAnimation", 27);
            d.Add("PropertyDataId", "Spell", 28);
            d.Add("PropertyDataId", "SpellComponent", 29);
            d.Add("PropertyDataId", "PhysicsScript", 30);
            d.Add("PropertyDataId", "LinkedPortalOne", 31);
            d.Add("PropertyDataId", "WieldedTreasureType", 32);
            d.Add("PropertyDataId", "UnknownGuessedname", 33);
            d.Add("PropertyDataId", "DeathTreasureType", 35);
            d.Add("PropertyDataId", "MutateFilter", 36);
            d.Add("PropertyDataId", "ItemSkillLimit", 37);
            d.Add("PropertyDataId", "UseCreateItem", 38);
            d.Add("PropertyDataId", "ItemSpecializedOnly", 41);
            d.Add("PropertyDataId", "HouseId", 42);
            d.Add("PropertyDataId", "RestrictionEffect", 44);
            d.Add("PropertyDataId", "TsysMutationFilter", 46);
            d.Add("PropertyDataId", "LinkedPortalTwo", 48);
            d.Add("PropertyDataId", "IconOverlay", 50);
            d.Add("PropertyDataId", "IconOverlaySecondary", 51);
            d.Add("PropertyDataId", "IconUnderlay", 52);
            d.Add("PropertyDataId", "ProcSpell", 55);
            d.Add("PropertyInstanceId", "ActivationTarget", 16);
            d.Add("PropertyInstanceId", "AllowedWielder", 38);
            return d;
        }
    }

    [Theory]
    [MemberData(nameof(WeenieAttested))]
    public void WeenieAttestedMemberHasExactWireValue(string family, string name, uint value)
    {
        var (type, _) = Resolve(family);
        Assert.True(Enum.IsDefined(type, value), $"{family}.{name} ({value}) is not defined");
        Assert.Equal(name, Enum.GetName(type, value));
    }
}
