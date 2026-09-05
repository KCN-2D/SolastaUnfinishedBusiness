using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Spells;
using SolastaUnfinishedBusiness.Subclasses;
using UnityEngine.AddressableAssets;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionFeatureSets;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAttributeModifiers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionDamageAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellListDefinitions;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private const int KnownSpellsTableLength = 20;
    private const string RitualCastingFeatureOriginMarker = "Tabletop2024RitualCasting";

    private static string _courtMageCounterspellMasteryDescription;
    private static EffectDescription _counterspellOriginalEffectDescription;

    private static readonly int[] BardPreparedSpells2024 =
        [4, 5, 6, 7, 9, 10, 11, 12, 14, 15, 16, 16, 17, 17, 18, 18, 19, 20, 21, 22];

    private static readonly int[] BardKnownSpells2014 =
        [4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 15, 15, 16, 18, 19, 19, 20, 22, 22, 22];

    private static readonly int[] RangerPreparedSpells2024WithLevel1Casting =
        [2, 3, 4, 5, 6, 6, 7, 7, 9, 9, 10, 10, 11, 11, 12, 12, 14, 14, 15, 15];

    private static readonly int[] RangerPreparedSpells2024Default =
        [0, 3, 4, 5, 6, 6, 7, 7, 9, 9, 10, 10, 11, 11, 12, 12, 14, 14, 15, 15];

    private static readonly int[] RangerKnownSpells2014WithLevel1Casting =
        [2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11];

    private static readonly int[] RangerKnownSpells2014Default =
        [0, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11];

    private static readonly int[] SorcererPreparedSpells2024 =
        [2, 4, 6, 7, 9, 10, 11, 12, 14, 15, 16, 16, 17, 17, 18, 18, 19, 20, 21, 22];

    private static readonly int[] SorcererKnownSpells2014 =
        [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 12, 13, 13, 14, 14, 15, 15, 15, 15];

    internal static readonly IReadOnlyList<int> ArtificerPreparedSpells2024 =
        [2, 3, 4, 5, 6, 6, 7, 7, 9, 9, 10, 10, 11, 11, 12, 12, 14, 14, 15, 15];

    internal static void SwitchSpellLists2024()
    {
        var enabled = Main.Settings.EnableSpellLists2024;

        foreach (var (spellList, spell, included) in EnumerateSpellListChanges2024())
        {
            SpellsContext.ApplySpellList2024Change(spellList, spell, included, enabled);
        }

        SpellsContext.SwitchSpellAvailabilityWithSpellLists2024(SpellsContext.SorcerousBurst, enabled);
        SpellsContext.ApplySpellList2024Restrictions(enabled);
        SpellsContext.RecalculateAllSpells();
        WizardAbjuration.RefreshSpellList();
        WizardEvocation.RefreshSpellList();
        RefreshFeatSpellSelectionLists2024();
    }

    private static IEnumerable<(SpellListDefinition SpellList, SpellDefinition Spell, bool Included)>
        EnumerateSpellListChanges2024()
    {
        // Bard
        yield return (SpellListBard, ColorSpray, true);
        yield return (SpellListBard, Aid, true);
        yield return (SpellListBard, MassHealingWord, true);
        yield return (SpellListBard, Slow, true);
        yield return (SpellListBard, PhantasmalKiller, true);
        yield return (SpellListBard, GetDefinition<SpellDefinition>("HeroesFeast"), true);
        yield return (SpellListBard, PrismaticSpray, true);

        // Cleric
        yield return (SpellListCleric, GetDefinition<SpellDefinition>("CircleOfMagicalNegation"), true);
        yield return (SpellListCleric, Sunbeam, true);
        yield return (SpellListCleric, Sunburst, true);

        // Druid
        yield return (SpellListDruid, SpareTheDying, true);
        yield return (SpellListDruid, Aid, true);
        yield return (SpellListDruid, ProtectionFromEvilGood, true);
        yield return (SpellListDruid, SpellsContext.AuraOfVitality, true);
        yield return (SpellListDruid, Revivify, true);
        yield return (SpellListDruid, FireShield, true);
        yield return (SpellListDruid, ConeOfCold, true);
        yield return (SpellListDruid, GetDefinition<SpellDefinition>("Symbol"), true);
        yield return (SpellListDruid, IncendiaryCloud, true);

        // Paladin
        yield return (SpellListPaladin, GreaterRestoration, true);
        yield return (SpellListPaladin, PrayerOfHealing, true);
        yield return (SpellListPaladin, WardingBond, true);

        // Ranger
        yield return (SpellListRanger, DispelMagic, true);
        yield return (SpellListRanger, Entangle, true);
        yield return (SpellListRanger, Aid, true);
        yield return (SpellListRanger, EnhanceAbility, true);
        yield return (SpellListRanger, MagicWeapon, true);
        yield return (SpellListRanger, Revivify, true);
        yield return (SpellListRanger, DominateBeast, true);
        yield return (SpellListRanger, GreaterRestoration, true);
        yield return (SpellListRanger, SpellsContext.SearingSmite, false);

        // Sorcerer
        yield return (SpellListSorcerer, Grease, true);
        yield return (SpellListSorcerer, FlameBlade, true);
        yield return (SpellListSorcerer, FlamingSphere, true);
        yield return (SpellListSorcerer, MagicWeapon, true);
        yield return (SpellListSorcerer, VampiricTouch, true);
        yield return (SpellListSorcerer, FireShield, true);
        yield return (SpellListSorcerer, FreezingSphere, true);
        yield return (SpellListSorcerer, SpellsContext.SorcerousBurst, true);

        // Warlock
        yield return (SpellListWarlock, Bane, true);
        yield return (SpellListWarlock, DetectMagic, true);
        yield return (SpellListWarlock, HideousLaughter, true);
        yield return (SpellListWarlock, Shatter, false);
        yield return (SpellListWarlock, ConjureFey, false);
        // Wizard
        yield return (SpellListWizard, EnhanceAbility, true);
        yield return (SpellListWizard, GetDefinition<SpellDefinition>("CircleOfMagicalNegation"), true);

        // Revised Artificer
        yield return (InventorClass.SpellList, DancingLights, true);
        yield return (InventorClass.SpellList, Light, true);
        yield return (InventorClass.SpellList, TrueStrike, true);
        yield return (InventorClass.SpellList, GetDefinition<SpellDefinition>("WaterBreathing"), true);
        yield return (InventorClass.SpellList, GetDefinition<SpellDefinition>("WaterWalk"), true);
        yield return (
            InventorClass.SpellList,
            GetDefinition<SpellDefinition>("DragonsBreathSpell"),
            true);
        yield return (
            InventorClass.SpellList,
            GetDefinition<SpellDefinition>("CircleOfMagicalNegation"),
            true);
    }

    internal static void SwitchShineCantrip()
    {
        Shine.EffectDescription.DifficultyClassComputation = Main.Settings.SwapShineCantrip
            ? EffectDifficultyClassComputation.FixedValue
            : EffectDifficultyClassComputation.SpellCastingFeature;
        Shine.EffectDescription.FixedSavingThrowDifficultyClass = Main.Settings.SwapShineCantrip ? 15 : 0;
    }

    private static readonly List<(string, string)> GuidanceProficiencyPairs =
    [
        (AttributeDefinitions.Dexterity, SkillDefinitions.Acrobatics),
        (AttributeDefinitions.Wisdom, SkillDefinitions.AnimalHandling),
        (AttributeDefinitions.Intelligence, SkillDefinitions.Arcana),
        (AttributeDefinitions.Strength, SkillDefinitions.Athletics),
        (AttributeDefinitions.Charisma, SkillDefinitions.Deception),
        (AttributeDefinitions.Intelligence, SkillDefinitions.History),
        (AttributeDefinitions.Wisdom, SkillDefinitions.Insight),
        (AttributeDefinitions.Charisma, SkillDefinitions.Intimidation),
        (AttributeDefinitions.Intelligence, SkillDefinitions.Investigation),
        (AttributeDefinitions.Wisdom, SkillDefinitions.Medecine),
        (AttributeDefinitions.Intelligence, SkillDefinitions.Nature),
        (AttributeDefinitions.Wisdom, SkillDefinitions.Perception),
        (AttributeDefinitions.Charisma, SkillDefinitions.Performance),
        (AttributeDefinitions.Charisma, SkillDefinitions.Persuasion),
        (AttributeDefinitions.Intelligence, SkillDefinitions.Religion),
        (AttributeDefinitions.Dexterity, SkillDefinitions.SleightOfHand),
        (AttributeDefinitions.Dexterity, SkillDefinitions.Stealth),
        (AttributeDefinitions.Wisdom, SkillDefinitions.Survival)
    ];

    private static readonly List<SpellDefinition> GuidanceSubSpells = [];

    private static readonly ConditionDefinition ConditionTrueStrike2024 = ConditionDefinitionBuilder
        .Create("ConditionTrueStrike2024")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .SetSpecialDuration()
        .SetFeatures(
            FeatureDefinitionAdditionalDamageBuilder
                .Create("AdditionalDamageTrueStrike")
                .SetGuiPresentationNoContent(true)
                .SetNotificationTag("TrueStrike")
                .SetRequiredProperty(RestrictedContextRequiredProperty.Weapon)
                .SetDamageDice(DieType.D6, 0)
                .SetSpecificDamageType(DamageTypeRadiant)
                .SetAdvancement(
                    ExtraAdditionalDamageAdvancement.CharacterLevel,
                    DiceByRankBuilder.InterpolateDiceByRankTable(0, 20, (5, 1), (11, 2), (17, 3)))
                .SetImpactParticleReference(SacredFlame
                    .EffectDescription.EffectParticleParameters.effectParticleReference)
                .SetAttackModeOnly()
                .AddToDB())
        .SetSpecialInterruptions(ExtraConditionInterruption.AttacksWithWeaponOrUnarmed)
        .AddCustomSubFeatures(new ModifyAttackActionModifierTrueStrike())
        .AddToDB();

    private static readonly EffectForm EffectFormPowerWordStunStopped = EffectFormBuilder
        .Create()
        .SetFilterId(1)
        .SetConditionForm(
            ConditionDefinitionBuilder
                .Create(CustomConditionsContext.StopMovement, "ConditionPowerWordStunStopped")
                .SetSpecialDuration(DurationType.Round, 0, TurnOccurenceType.StartOfTurn)
                .AddToDB(),
            ConditionForm.ConditionOperation.Add)
        .Build();

    internal static void SwitchOneDndSpellRitualOnAllCasters()
    {
        var subclasses = SharedSpellsContext.SubclassCasterType.Keys.Select(GetDefinition<CharacterSubclassDefinition>);
        var enabled = Main.Settings.EnableRitualOnAllCasters2024;

        SetRitualCastingFeature(
            Paladin.FeatureUnlocks,
            Main.Settings.EnablePaladinSpellCastingAtLevel1 ? 1 : 2,
            enabled);
        SetRitualCastingFeature(
            Ranger.FeatureUnlocks,
            Main.Settings.EnableRangerSpellCastingAtLevel1 ? 1 : 2,
            enabled);
        SetRitualCastingFeature(Sorcerer.FeatureUnlocks, 1, enabled);
        SetRitualCastingFeature(Warlock.FeatureUnlocks, 1, enabled);

        foreach (var subclass in subclasses)
        {
            SetRitualCastingFeature(subclass.FeatureUnlocks, 3, enabled);
        }
    }

    internal static void SynchronizeRitualCastingFeatures(RulesetCharacterHero hero)
    {
        if (hero?.ActiveFeatures == null ||
            hero.ClassesAndLevels == null ||
            hero.ClassesAndSubclasses == null)
        {
            return;
        }

        var managedTags = new HashSet<string>
        {
            AttributeDefinitions.GetClassTag(Paladin, 1),
            AttributeDefinitions.GetClassTag(Paladin, 2),
            AttributeDefinitions.GetClassTag(Ranger, 1),
            AttributeDefinitions.GetClassTag(Ranger, 2),
            AttributeDefinitions.GetClassTag(Sorcerer, 1),
            AttributeDefinitions.GetClassTag(Warlock, 1)
        };
        var activeTags = new HashSet<string>();
        var enabled = Main.Settings.EnableRitualOnAllCasters2024;

        if (enabled)
        {
            AddEligibleClassTag(
                hero,
                Paladin,
                Main.Settings.EnablePaladinSpellCastingAtLevel1 ? 1 : 2,
                activeTags);
            AddEligibleClassTag(
                hero,
                Ranger,
                Main.Settings.EnableRangerSpellCastingAtLevel1 ? 1 : 2,
                activeTags);
            AddEligibleClassTag(hero, Sorcerer, 1, activeTags);
            AddEligibleClassTag(hero, Warlock, 1, activeTags);
        }

        foreach (var classAndSubclass in hero.ClassesAndSubclasses)
        {
            var characterClass = classAndSubclass.Key;
            var subclass = classAndSubclass.Value;

            if (subclass == null ||
                !SharedSpellsContext.SubclassCasterType.ContainsKey(subclass.Name))
            {
                continue;
            }

            var tag = AttributeDefinitions.GetSubclassTag(characterClass, 3, subclass);

            managedTags.Add(tag);

            if (enabled &&
                hero.ClassesAndLevels.TryGetValue(characterClass, out var classLevel) &&
                classLevel >= 3)
            {
                activeTags.Add(tag);
            }
        }

        // Feature origins are rebuilt by native feature browsing and are not a durable save
        // boundary. Restrict stale cleanup to the class/subclass tags exclusively managed by
        // this option so an existing Cleric (or any other native ritual source) is never altered.
        foreach (var tag in managedTags.Where(tag => !activeTags.Contains(tag)))
        {
            SynchronizeRitualCastingTag(hero, tag, false);
        }

        foreach (var tag in activeTags)
        {
            SynchronizeRitualCastingTag(hero, tag, true);
        }
    }

    private static void AddEligibleClassTag(
        RulesetCharacterHero hero,
        CharacterClassDefinition characterClass,
        int featureLevel,
        ISet<string> activeTags)
    {
        if (hero.ClassesAndLevels.TryGetValue(characterClass, out var classLevel) &&
            classLevel >= featureLevel)
        {
            activeTags.Add(AttributeDefinitions.GetClassTag(characterClass, featureLevel));
        }
    }

    private static void SynchronizeRitualCastingTag(
        RulesetCharacterHero hero,
        string tag,
        bool active)
    {
        if (!hero.ActiveFeatures.TryGetValue(tag, out var features))
        {
            if (!active)
            {
                return;
            }

            features = [];
            hero.ActiveFeatures.Add(tag, features);
        }

        if (active)
        {
            SynchronizeRitualCastingFeature(hero, features, FeatureSetClericRitualCasting);

            foreach (var feature in FeatureSetClericRitualCasting.FeatureSet)
            {
                SynchronizeRitualCastingFeature(hero, features, feature);
            }

            return;
        }

        features.RemoveAll(IsRitualCastingFeature);

        foreach (var feature in EnumerateRitualCastingFeatureGraph())
        {
            if (hero.FeaturesOrigin.TryGetValue(feature, out var origin) &&
                IsRitualCastingFeatureOrigin(origin))
            {
                hero.FeaturesOrigin.Remove(feature);
            }
        }
    }

    private static void SynchronizeRitualCastingFeature(
        RulesetCharacterHero hero,
        ICollection<FeatureDefinition> features,
        FeatureDefinition feature)
    {
        if (feature == null)
        {
            return;
        }

        if (!features.Contains(feature))
        {
            features.Add(feature);
        }

        hero.FeaturesOrigin[feature] = new FeatureOrigin(
            FeatureSourceType.ExplicitFeature,
            RitualCastingFeatureOriginMarker,
            FeatureSetClericRitualCasting,
            string.Empty);
    }

    private static IEnumerable<FeatureDefinition> EnumerateRitualCastingFeatureGraph()
    {
        yield return FeatureSetClericRitualCasting;

        foreach (var feature in FeatureSetClericRitualCasting.FeatureSet)
        {
            yield return feature;
        }
    }

    private static bool IsRitualCastingFeature(FeatureDefinition feature)
    {
        return feature == FeatureSetClericRitualCasting ||
               FeatureSetClericRitualCasting.FeatureSet.Contains(feature);
    }

    private static bool IsRitualCastingFeatureOrigin(FeatureOrigin origin)
    {
        return origin.sourceType == FeatureSourceType.ExplicitFeature &&
               origin.sourceName == RitualCastingFeatureOriginMarker &&
               ReferenceEquals(origin.source, FeatureSetClericRitualCasting);
    }

    private static void SetRitualCastingFeature(
        List<FeatureUnlockByLevel> featureUnlocks,
        int level,
        bool enabled)
    {
        featureUnlocks.RemoveAll(
            unlock => unlock.FeatureDefinition == FeatureSetClericRitualCasting);

        if (enabled)
        {
            featureUnlocks.Add(
                new FeatureUnlockByLevel(FeatureSetClericRitualCasting, level));
        }

        featureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchOneDndSpellBarkskin()
    {
        if (Main.Settings.EnableOneDndBarkskinSpell)
        {
            Barkskin.requiresConcentration = false;
            Barkskin.castingTime = ActivationTime.BonusAction;
            AttributeModifierBarkskin.modifierValue = 17;
            Barkskin.GuiPresentation.description = "Spell/&BarkskinOneDndDescription";
            ConditionBarkskin.GuiPresentation.description = "Rules/&ConditionOneDndBarkskinDescription";
        }
        else
        {
            Barkskin.requiresConcentration = true;
            Barkskin.castingTime = ActivationTime.Action;
            AttributeModifierBarkskin.modifierValue = 16;
            Barkskin.GuiPresentation.description = "Spell/&BarkskinDescription";
            ConditionBarkskin.GuiPresentation.description = "Rules/&ConditionBarkskinDescription";
        }
    }

    internal static void SwitchOneDndSpellCounterspell()
    {
        var enabled = Main.Settings.EnableOneDndCounterspellSpell;
        var effectDescription = Counterspell.EffectDescription;
        _counterspellOriginalEffectDescription ??= EffectDescriptionBuilder.Create(effectDescription).Build();

        Counterspell.GuiPresentation.description = enabled
            ? "Spell/&CounterspellOneDndDescription"
            : "Spell/&CounterspellDescription";

        // The rules resolver handles the saving throw. Keep its counter form, but replace
        // the legacy effect summary and remove slot-level improvements from the definition.
        effectDescription.specialFormsDescription = enabled
            ? "Rules/&Counterspell2024EffectDescription"
            : _counterspellOriginalEffectDescription.specialFormsDescription;
        effectDescription.EffectAdvancement.Copy(_counterspellOriginalEffectDescription.EffectAdvancement);

        if (enabled)
        {
            effectDescription.EffectAdvancement.Clear();
        }

        PowerBundle.ClearSpellEffectCacheForDefinition(Counterspell);

        var mastery = GetDefinition<FeatureDefinitionMagicAffinity>("MagicAffinityCourtMageCounterspellMastery");
        _courtMageCounterspellMasteryDescription ??= mastery.GuiPresentation.description;
        mastery.GuiPresentation.description = enabled
            ? "Feature/&TraditionCourtMageCounterspellMastery2024Description"
            : _courtMageCounterspellMasteryDescription;

        // Also runs at startup and keeps dependent subclass rules in sync after every toggle.
        SwitchWizardAbjurerSpellBreaker();
    }

    internal static void SwitchOneDndCantripChillTouch()
    {
        var effectDescription = ChillTouch.EffectDescription;
        if (Main.Settings.EnableOneDndChillTouchCantrip)
        {
            effectDescription.FindFirstDamageForm().dieType = DieType.D10;
            effectDescription.rangeType = RangeType.MeleeHit;
            effectDescription.rangeParameter = 1;
        }
        else
        {
            effectDescription.FindFirstDamageForm().dieType = DieType.D8;
            effectDescription.rangeType = RangeType.RangeHit;
            effectDescription.rangeParameter = 24;
        }
    }
    
    internal static void SwitchOneDndCantripBladeWard()
    {
        var bladeWard = SpellsContext.BladeWard;
        if (Main.Settings.EnableOneDndBladeWardCantrip)
        {
            bladeWard.requiresConcentration = true;
            bladeWard.effectDescription = SpellBuilders.BladeWardEffect2024;
            bladeWard.guiPresentation.description = "Spell/&BladeWard2024Description";
        }
        else
        {
            bladeWard.requiresConcentration = false;
            bladeWard.effectDescription = SpellBuilders.BladeWardEffect2014;
            bladeWard.guiPresentation.description = "Spell/&BladeWardDescription";
        }
    }

    private static void LoadOneDndSpellGuidanceSubspells()
    {
        foreach (var (attribute, skill) in GuidanceProficiencyPairs)
        {
            var proficiencyPair = (attribute, skill);
            var affinity = $"AbilityCheckAffinityGuidance{skill}";
            var condition = $"ConditionGuidance{skill}";

            GuidanceSubSpells.Add(
                SpellDefinitionBuilder
                    .Create($"Guidance{skill}")
                    .SetGuiPresentation(Category.Spell, Guidance.GuiPresentation.SpriteReference)
                    .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolDivination)
                    .SetSpellLevel(0)
                    .SetCastingTime(ActivationTime.Action)
                    .SetMaterialComponent(MaterialComponentType.None)
                    .SetVerboseComponent(true)
                    .SetSomaticComponent(true)
                    .SetRequiresConcentration(true)
                    .SetVocalSpellSameType(VocalSpellSemeType.Buff)
                    .SetEffectDescription(
                        EffectDescriptionBuilder
                            .Create()
                            .SetDurationData(DurationType.Minute, 1)
                            .SetTargetingData(Side.Ally, RangeType.Touch, 0, TargetType.IndividualsUnique)
                            .SetEffectForms(EffectFormBuilder.ConditionForm(
                                ConditionDefinitionBuilder
                                    .Create(ConditionGuided, condition)
                                    .SetGuiPresentation(Category.Condition, ConditionGuided)
                                    .SetSpecialInterruptions(ConditionInterruption.None)
                                    .SetFeatures(
                                        FeatureDefinitionAbilityCheckAffinityBuilder
                                            .Create(affinity)
                                            .SetGuiPresentationNoContent(true)
                                            .BuildAndSetAffinityGroups(CharacterAbilityCheckAffinity.None, DieType.D4,
                                                1, AbilityCheckGroupOperation.AddDie, proficiencyPair)
                                            .AddToDB())
                                    .AddToDB()))
                            .SetParticleEffectParameters(Guidance)
                            .Build())
                    .AddToDB());
        }
    }

    internal static void SwitchOneDndSpellDivineFavor()
    {
        DivineFavor.requiresConcentration = !Main.Settings.EnableOneDndDivineFavorSpell;
    }

    internal static void SwitchOneDndSpellLesserRestoration()
    {
        LesserRestoration.castingTime = Main.Settings.EnableOneDndLesserRestorationSpell
            ? ActivationTime.BonusAction
            : ActivationTime.Action;
    }

    private static void LoadOneDndSpellSpareTheDying()
    {
        SpareTheDying.AddCustomSubFeatures(new ModifyEffectDescriptionSpareTheDying());
    }

    internal static void SwitchOneDndSpellSpareTheDying()
    {
        SpareTheDying.GuiPresentation.description =
            Main.Settings.EnableOneDndSpareTheDyingSpell
                ? "Spell/&SpareTheDyingExtendedDescription"
                : "Spell/&SpareTheDyingDescription";
    }

    internal static void SwitchOneDndSpellSpiderClimb()
    {
        SpiderClimb.EffectDescription.EffectAdvancement.additionalTargetsPerIncrement =
            Main.Settings.EnableOneDndSpiderClimbSpell
                ? 1
                : 0;
        SpiderClimb.EffectDescription.EffectAdvancement.effectIncrementMethod =
            Main.Settings.EnableOneDndSpiderClimbSpell
                ? EffectIncrementMethod.PerAdditionalSlotLevel
                : EffectIncrementMethod.None;
    }

    internal static void SwitchOneDndSpellStoneSkin()
    {
        Stoneskin.GuiPresentation.description = "Spell/&StoneskinExtendedDescription";
        ConditionStoneskin.GuiPresentation.description = "Rules/&ConditionStoneskinExtendedDescription";
        DamageAffinityStoneskinBludgeoning.TagsIgnoringAffinity.Clear();
        DamageAffinityStoneskinPiercing.TagsIgnoringAffinity.Clear();
        DamageAffinityStoneskinSlashing.TagsIgnoringAffinity.Clear();

        if (Main.Settings.EnableOneDndStoneSkinSpell)
        {
            return;
        }

        Stoneskin.GuiPresentation.description = "Spell/&StoneskinDescription";
        ConditionStoneskin.GuiPresentation.description = "Rules/&ConditionStoneskinDescription";
        DamageAffinityStoneskinBludgeoning.TagsIgnoringAffinity.AddRange(
            TagsDefinitions.MagicalWeapon, TagsDefinitions.MagicalEffect);
        DamageAffinityStoneskinPiercing.TagsIgnoringAffinity.AddRange(
            TagsDefinitions.MagicalWeapon, TagsDefinitions.MagicalEffect);
        DamageAffinityStoneskinSlashing.TagsIgnoringAffinity.AddRange(
            TagsDefinitions.MagicalWeapon, TagsDefinitions.MagicalEffect);
    }

    internal static void SwitchOneDndSpellGuidance()
    {
        foreach (var spell in GuidanceSubSpells)
        {
            spell.implemented = false;
        }

        if (Main.Settings.EnableOneDndGuidanceSpell)
        {
            Guidance.spellsBundle = true;
            Guidance.SubspellsList.SetRange(GuidanceSubSpells);
            Guidance.compactSubspellsTooltip = true;
            Guidance.EffectDescription.EffectForms.Clear();
            Guidance.GuiPresentation.description = "Spell/&OneDndGuidanceDescription";
        }
        else
        {
            Guidance.spellsBundle = false;
            Guidance.SubspellsList.Clear();
            Guidance.EffectDescription.EffectForms.SetRange(EffectFormBuilder.ConditionForm(ConditionGuided));
            Guidance.GuiPresentation.description = "Spell/&GuidanceDescription";
        }
    }

    internal static void SwitchOneDndSpellHideousLaughter()
    {
        HideousLaughter.EffectDescription.EffectAdvancement.effectIncrementMethod =
            Main.Settings.EnableOneDndHideousLaughterSpell
                ? EffectIncrementMethod.PerAdditionalSlotLevel
                : EffectIncrementMethod.None;
    }

    internal static void SwitchOneDndSpellHuntersMark()
    {
        FeatureDefinitionAdditionalDamages.AdditionalDamageHuntersMark.specificDamageType = DamageTypeForce;
        FeatureDefinitionAdditionalDamages.AdditionalDamageHuntersMark.additionalDamageType =
            Main.Settings.EnableOneDndHuntersMarkSpell
                ? AdditionalDamageType.Specific
                : AdditionalDamageType.SameAsBaseDamage;
        HuntersMark.GuiPresentation.description =
            Main.Settings.EnableOneDndHuntersMarkSpell
                ? "Spell/&HuntersMarkExtendedDescription"
                : "Spell/&HuntersMarkDescription";
        ConditionMarkedByHunter.GuiPresentation.description =
            Main.Settings.EnableOneDndHuntersMarkSpell
                ? "Rules/&ConditionMarkedByHunterExtendedDescription"
                : "Rules/&ConditionMarkedByHunterDescription";
    }

    internal static void SwitchOneDndSpellMagicWeapon()
    {
        if (Main.Settings.EnableOneDndMagicWeaponSpell)
        {
            MagicWeapon.requiresConcentration = false;
            MagicWeapon.castingTime = ActivationTime.BonusAction;
            MagicWeapon.EffectDescription.EffectForms[0].ItemPropertyForm.FeatureBySlotLevel[1].level = 3;
        }
        else
        {
            MagicWeapon.requiresConcentration = true;
            MagicWeapon.castingTime = ActivationTime.Action;
            MagicWeapon.EffectDescription.EffectForms[0].ItemPropertyForm.FeatureBySlotLevel[1].level = 4;
        }
    }

    internal static void SwitchOneDndSpellPowerWordStun()
    {
        var effectForms = PowerWordStun.EffectDescription.EffectForms;

        if (effectForms.Count > 1)
        {
            effectForms.RemoveAt(1);
            PowerWordStun.EffectDescription.EffectFormFilters.RemoveAt(1);
        }

        PowerWordStun.GuiPresentation.description = "Spell/&PowerWordStunDescription";

        if (!Main.Settings.EnableOneDndPowerWordStunSpell)
        {
            return;
        }

        PowerWordStun.GuiPresentation.description = "Spell/&PowerWordStunExtendedDescription";
        PowerWordStun.EffectDescription.EffectFormFilters.Add(
            new EffectFormFilter { effectFormId = 1, minHitPoints = 151, maxHitPoints = 10000 });
        effectForms.Add(EffectFormPowerWordStunStopped);
    }

    internal static void SwitchOneDndPreparedSpellsTables()
    {
        var enable2024 = Main.Settings.EnablePreparedSpellsTables2024;

        ApplyKnownSpellsTable(
            FeatureDefinitionCastSpells.CastSpellBard,
            enable2024 ? BardPreparedSpells2024 : BardKnownSpells2014);
        ApplyKnownSpellsTable(FeatureDefinitionCastSpells.CastSpellRanger, GetRangerKnownSpellsTable(enable2024));
        ApplyKnownSpellsTable(
            FeatureDefinitionCastSpells.CastSpellSorcerer,
            enable2024 ? SorcererPreparedSpells2024 : SorcererKnownSpells2014);
        ValidateSpellsTable(ArtificerPreparedSpells2024);
    }

    private static void ApplyKnownSpellsTable(FeatureDefinitionCastSpell castSpell, IReadOnlyList<int> table)
    {
        ValidateSpellsTable(table);

        castSpell.knownSpells = new List<int>(table);
    }

    private static IReadOnlyList<int> GetRangerKnownSpellsTable(bool enable2024)
    {
        if (enable2024)
        {
            return Main.Settings.EnableRangerSpellCastingAtLevel1
                ? RangerPreparedSpells2024WithLevel1Casting
                : RangerPreparedSpells2024Default;
        }

        return Main.Settings.EnableRangerSpellCastingAtLevel1
            ? RangerKnownSpells2014WithLevel1Casting
            : RangerKnownSpells2014Default;
    }

    private static void ValidateSpellsTable(IReadOnlyList<int> table)
    {
#if DEBUG
        if (table.Count != KnownSpellsTableLength)
        {
            throw new InvalidOperationException($"{nameof(table)} must contain {KnownSpellsTableLength} entries.");
        }
#endif
    }

    private static void LoadOneDndSpellTrueStrike()
    {
        if (!Main.Settings.EnableOneDndTrueStrikeCantrip)
        {
            return;
        }

        TrueStrike.AddCustomSubFeatures(
            FixesContext.NoDistanced.Mark,
            FixesContext.NoTwinned.Mark,
            AttackAfterMagicEffect.MarkerAnyWeaponAttack);
        TrueStrike.GuiPresentation.description = "Spell/&TrueStrike2024Description";
        TrueStrike.requiresConcentration = false;
        TrueStrike.effectDescription = EffectDescriptionBuilder
            .Create()
            .SetDurationData(DurationType.Round)
            // 24 seems to be the max range on Solasta ranged weapons
            .SetTargetingData(Side.Enemy, RangeType.Distance, 24, TargetType.IndividualsUnique)
            .SetEffectAdvancement(EffectIncrementMethod.CasterLevelTable, additionalDicePerIncrement: 1)
            .SetEffectForms(
                EffectFormBuilder.ConditionForm(ConditionTrueStrike2024, ConditionForm.ConditionOperation.Add, true))
            .SetParticleEffectParameters(SacredFlame)
            .SetImpactEffectParameters(new AssetReference())
            .Build();
    }

    internal static void SwitchOneDndSpellWitchBolt()
    {
        SpellBuilders.WitchBoltPower.activationTime = Main.Settings.EnableOneDndWitchBoltSpell
            ? ActivationTime.BonusAction
            : ActivationTime.Action;
    }

    internal static void SwitchOneDndHealingSpellsUpgrade()
    {
        var dice = Main.Settings.EnableOneDndHealingSpellsUpgrade ? 2 : 1;

        // Cure Wounds, Healing Word got buf on base damage and add dice
        CureWounds.EffectDescription.EffectForms[0].healingForm.diceNumber = dice;
        CureWounds.EffectDescription.effectAdvancement.additionalDicePerIncrement = dice;
        FalseLife.EffectDescription.EffectForms[0].temporaryHitPointsForm.diceNumber = dice;
        HealingWord.EffectDescription.EffectForms[0].healingForm.diceNumber = dice;
        HealingWord.EffectDescription.effectAdvancement.additionalDicePerIncrement = dice;

        // Mass Cure Wounds and Mass Healing Word only got buf on base damage
        MassHealingWord.EffectDescription.EffectForms[0].healingForm.diceNumber = dice;

        dice = Main.Settings.EnableOneDndHealingSpellsUpgrade ? 5 : 3;

        MassCureWounds.EffectDescription.EffectForms[0].healingForm.diceNumber = dice;

        var school = Main.Settings.EnableOneDndHealingSpellsUpgrade ? SchoolAbjuration : SchoolEvocation;
        SpellsContext.AuraOfVitality.schoolOfMagic = school;
        CureWounds.schoolOfMagic = school;
        Heal.schoolOfMagic = school;
        HealingWord.schoolOfMagic = school;
        MassCureWounds.schoolOfMagic = school;
        MassHealingWord.schoolOfMagic = school;
        GetDefinition<SpellDefinition>("MassHeal").schoolOfMagic = school;
        PrayerOfHealing.schoolOfMagic = school;
    }

    internal static void SwitchOneDndDamagingSpellsUpgrade()
    {
        EffectProxyDefinitions.ProxyArcaneSword.AdditionalFeatures.Clear();

        if (Main.Settings.EnableOneDndDamagingSpellsUpgrade)
        {
            EffectProxyDefinitions.ProxyArcaneSword.damageDie = DieType.D12;
            EffectProxyDefinitions.ProxyArcaneSword.damageDieNum = 4;
            EffectProxyDefinitions.ProxyArcaneSword.addAbilityToDamage = true;
            EffectProxyDefinitions.ProxyArcaneSword.AdditionalFeatures.AddRange(
                FeatureDefinitionMoveModes.MoveModeFly2,
                FeatureDefinitionMoveModes.MoveModeMove6);
            CircleOfDeath.EffectDescription.EffectForms[0].DamageForm.dieType = DieType.D8;
            FlameStrike.EffectDescription.EffectForms[0].DamageForm.diceNumber = 5;
            FlameStrike.EffectDescription.EffectForms[1].DamageForm.diceNumber = 5;
            PrismaticSpray.EffectDescription.EffectForms
                .Where(x => x.FormType == EffectForm.EffectFormType.Damage)
                .Do(y => y.DamageForm.DiceNumber = 12);
            IceStorm.EffectDescription.EffectForms[0].DamageForm.dieType = DieType.D10;
            ViciousMockery.EffectDescription.EffectForms[0].DamageForm.dieType = DieType.D6;
        }
        else
        {
            EffectProxyDefinitions.ProxyArcaneSword.damageDie = DieType.D10;
            EffectProxyDefinitions.ProxyArcaneSword.damageDieNum = 3;
            EffectProxyDefinitions.ProxyArcaneSword.addAbilityToDamage = false;
            EffectProxyDefinitions.ProxyArcaneSword.AdditionalFeatures.AddRange(
                FeatureDefinitionMoveModes.MoveModeFly2,
                FeatureDefinitionMoveModes.MoveModeMove4);
            CircleOfDeath.EffectDescription.EffectForms[0].DamageForm.dieType = DieType.D6;
            FlameStrike.EffectDescription.EffectForms[0].DamageForm.diceNumber = 4;
            FlameStrike.EffectDescription.EffectForms[1].DamageForm.diceNumber = 4;
            PrismaticSpray.EffectDescription.EffectForms
                .Where(x => x.FormType == EffectForm.EffectFormType.Damage)
                .Do(y => y.DamageForm.DiceNumber = 10);
            IceStorm.EffectDescription.EffectForms[0].DamageForm.dieType = DieType.D8;
            ViciousMockery.EffectDescription.EffectForms[0].DamageForm.dieType = DieType.D4;
        }
    }

    private sealed class ModifyEffectDescriptionSpareTheDying : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return Main.Settings.EnableOneDndSpareTheDyingSpell && definition == SpareTheDying;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            if (!Main.Settings.EnableOneDndSpareTheDyingSpell)
            {
                return effectDescription;
            }

            effectDescription.RangeType = RangeType.Distance;

            var level = character.TryGetAttributeValue(AttributeDefinitions.CharacterLevel);
            var power = level switch
            {
                >= 17 => 3,
                >= 11 => 2,
                >= 5 => 1,
                _ => 0
            };

            effectDescription.rangeParameter = 3 * (int)Math.Pow(2, power);

            return effectDescription;
        }
    }

    private sealed class ModifyAttackActionModifierTrueStrike : IModifyAttackActionModifier
    {
        public void OnAttackComputeModifier(
            RulesetCharacter attacker,
            RulesetCharacter defender,
            BattleDefinitions.AttackProximity attackProximity,
            RulesetAttackMode attackMode,
            string effectName,
            ref ActionModifier attackModifier)
        {
            if (attackMode == null)
            {
                return;
            }

            var repertoire = attacker.SpellRepertoires.FirstOrDefault(x => x.HasKnowledgeOfSpell(TrueStrike));

            if (repertoire == null)
            {
                return;
            }

            var damageForm = attackMode.EffectDescription.FindFirstDamageForm();

            if (damageForm != null)
            {
                damageForm.damageType = DamageTypeRadiant;
            }

            var oldAttribute = attackMode.AbilityScore;
            var newAttribute = repertoire.SpellCastingAbility;

            CanUseAttribute.ChangeAttackModeAttributeIfBetter(
                attacker, attackMode, oldAttribute, newAttribute, true);
        }
    }
}
