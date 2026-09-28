using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Validators;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionActionAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAttributeModifiers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSubclassChoices;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    internal const string BlessedStrikes = "BlessedStrikes";
    internal const string ClericThaumaturgeExtraSpellsTag = "Thaumaturge";

    private static readonly FeatureDefinitionPointPool PointPoolClericThaumaturgeCantrip =
        FeatureDefinitionPointPoolBuilder
            .Create("PointPoolClericThaumaturgeCantrip")
            .SetGuiPresentation(Category.Feature)
            .SetSpellOrCantripPool
                (HeroDefinitions.PointsPoolType.Cantrip, 1, SpellListDefinitions.SpellListCleric,
                    ClericThaumaturgeExtraSpellsTag)
            .AddCustomSubFeatures(new ModifyAbilityCheckThaumaturge())
            .AddToDB();

    private static readonly FeatureDefinitionFeatureSet FeatureSetClericDivineOrder = FeatureDefinitionFeatureSetBuilder
        .Create("FeatureSetClericDivineOrder")
        .SetGuiPresentation(Category.Feature)
        .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
        .SetFeatureSet(
            PointPoolClericThaumaturgeCantrip,
            FeatureDefinitionFeatureSetBuilder
                .Create("FeatureSetClericProtector")
                .SetGuiPresentation(Category.Feature)
                .SetFeatureSet(
                    FeatureDefinitionProficiencyBuilder
                        .Create("ProficiencyClericProtectorArmor")
                        .SetGuiPresentationNoContent(true)
                        .SetProficiencies(ProficiencyType.Armor, EquipmentDefinitions.HeavyArmorCategory)
                        .AddToDB(),
                    FeatureDefinitionProficiencyBuilder
                        .Create("ProficiencyClericProtectorWeapons")
                        .SetGuiPresentationNoContent(true)
                        .SetProficiencies(ProficiencyType.Weapon, EquipmentDefinitions.MartialWeaponCategory)
                        .AddToDB())
                .AddToDB())
        .AddToDB();

    private static readonly FeatureDefinition FeatureClericSearUndead = FeatureDefinitionBuilder
        .Create("FeatureClericSearUndead")
        .SetGuiPresentation(Category.Feature)
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerClericDivineSpark = FeatureDefinitionPowerBuilder
        .Create("PowerClericDivineSpark")
        .SetGuiPresentation(Category.Feature,
            Sprites.GetSprite("PowerDivineSpark", Resources.PowerDivineSpark, 256, 128))
        .SetUsesFixed(ActivationTime.Action, RechargeRate.ChannelDivinity)
        .AddToDB();

    private static readonly EffectForm SearUndeadDamageForm = EffectFormBuilder
        .Create()
        .HasSavingThrow(EffectSavingThrowType.Negates)
        .SetDamageForm(DamageTypeRadiant, 0, DieType.D8)
        .Build();

    private static readonly FeatureDefinitionFeatureSet FeatureSetClericBlessedStrikes =
        FeatureDefinitionFeatureSetBuilder
            .Create("FeatureSetClericBlessedStrikes")
            .SetGuiPresentation(Category.Feature)
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
            .AddToDB();

    private static readonly FeatureDefinitionPower PowerClericImprovedBlessedStrikes = FeatureDefinitionPowerBuilder
        .Create("PowerClericImprovedBlessedStrikes")
        .SetGuiPresentation(Category.Feature, PowerPaladinLayOnHands)
        .SetUsesFixed(ActivationTime.NoCost, RechargeRate.None)
        .SetShowCasting(false)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.UntilAnyRest)
                .SetTargetingData(Side.Ally, RangeType.Distance, 12, TargetType.IndividualsUnique)
                .SetEffectForms(
                    EffectFormBuilder
                        .Create()
                        .SetTempHpForm()
                        .Build())
                .SetParticleEffectParameters(PowerPaladinLayOnHands)
                .Build())
        .AddCustomSubFeatures(new ModifyEffectDescriptionPowerClericImprovedBlessedStrikes())
        .AddToDB();

    private static readonly ConditionDefinition ConditionClericImprovedBlessedStrikes = ConditionDefinitionBuilder
        .Create("ConditionClericImprovedBlessedStrikes")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .SetFeatures(PowerClericImprovedBlessedStrikes)
        .AddCustomSubFeatures(new AddUsablePowersFromCondition())
        .AddToDB();

    private static readonly FeatureDefinition FeatureClericImprovedBlessedStrikes =
        FeatureDefinitionBuilder
            .Create("FeatureClericImprovedBlessedStrikes")
            .SetGuiPresentation(Category.Feature)
            .AddCustomSubFeatures(new CustomBehaviorFeatureClericImprovedBlessedStrikes())
            .AddToDB();

    private sealed class CustomBehaviorFeatureClericImprovedBlessedStrikes : IMagicEffectFinishedByMe
    {
        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            var rulesetAttacker = attacker.RulesetCharacter;

            if (Main.Settings.EnableClericBlessedStrikes2024 &&
                action is CharacterActionMagicEffect { Countered: false, ExecutionFailed: false } magicAction &&
                magicAction.damagePerTargetIndexCache.Values.Any(damage => damage > 0) &&
                rulesetAttacker.HasAnyFeature(FeaturePotentSpellcasting) &&
                rulesetAttacker.GetClassLevel(Cleric) >= 14 &&
                action.ActionParams.RulesetEffect is RulesetEffectSpell
                {
                    SpellDefinition: { SpellLevel: 0 } spellDefinition
                } spellEffect &&
                rulesetAttacker.IsSpellCastAsClassOrSubclassSpell(spellEffect, Cleric) &&
                !rulesetAttacker.HasConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, ConditionClericImprovedBlessedStrikes.Name))
            {
                rulesetAttacker.InflictCondition(
                    ConditionClericImprovedBlessedStrikes.Name,
                    DurationType.Round,
                    0,
                    TurnOccurenceType.EndOfTurn,
                    AttributeDefinitions.TagEffect,
                    rulesetAttacker.guid,
                    FactionDefinitions.Party.Name,
                    1,
                    ConditionClericImprovedBlessedStrikes.Name,
                    0,
                    0,
                    0);
            }

            yield break;
        }
    }

    private static List<(CharacterSubclassDefinition Subclass, FeatureUnlockByLevel Unlock)> _legacyDivineStrikeUnlocks;
    private static readonly Dictionary<FeatureDefinition, FeatureDefinition> SavedBlessedStrikesChoices = [];

    private static readonly FeatureDefinition FeaturePotentSpellcasting = FeatureDefinitionBuilder
        .Create("FeatureClericBlessedStrikesPotentSpellcasting")
        .SetGuiPresentation(Category.Feature)
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerBlessedStrikes = FeatureDefinitionPowerBuilder
        .Create("PowerClericBlessedStrikes")
        .SetGuiPresentation(Category.Feature, hidden: true)
        .SetShowCasting(false)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Round)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .Build())
        .AddToDB();

    private static void LoadClericBlessedStrikes()
    {
        FeaturePotentSpellcasting.AddCustomSubFeatures(
            new ClassFeats.CustomBehaviorFeatPotentSpellcaster(FeaturePotentSpellcasting, Cleric));

        var damageTypes = new (string, IMagicEffect)[]
        {
            (DamageTypeRadiant, Sunburst), (DamageTypeNecrotic, VampiricTouch),
            // these are specific to some domains and not added on feature set but on demand before reaction
            (DamageTypeCold, ConeOfCold), // DomainNature
            (DamageTypeLightning, LightningBolt), // DomainNature
            (DamageTypeFire, FireBolt), // DomainNature and DomainSmith
            (DamageTypeThunder, Shatter), // DomainTempest
            (DamageTypePsychic, PowerMagebaneWarcry) // DomainMischief, DomainOrder
        };

        var powers = new List<FeatureDefinitionPower>();

        PowerBlessedStrikes.AddCustomSubFeatures(new CustomBehaviorBlessedStrikes(PowerBlessedStrikes));

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var (damageType, effect) in damageTypes)
        {
            var additionalDamageBlessedStrikes = FeatureDefinitionAdditionalDamageBuilder
                .Create($"AdditionalDamageClericBlessedStrikes{damageType}")
                .SetGuiPresentationNoContent(true)
                .SetNotificationTag("DivineStrike")
                .SetDamageDice(DieType.D8, 1)
                .SetSpecificDamageType(damageType)
                .SetAdvancement(AdditionalDamageAdvancement.ClassLevel, 1, 1, 7, 7)
                .SetFrequencyLimit(FeatureLimitedUsage.OnceInMyTurn)
                .SetAttackModeOnly()
                .SetRequiredProperty(RestrictedContextRequiredProperty.Weapon)
                .AddCustomSubFeatures(ClassHolder.Cleric)
                .SetImpactParticleReference(effect)
                .AddToDB();

            var conditionBlessedStrikes = ConditionDefinitionBuilder
                .Create($"ConditionClericBlessedStrikes{damageType}")
                .SetGuiPresentationNoContent(true)
                .SetSilent(Silent.WhenAddedOrRemoved)
                .SetFeatures(additionalDamageBlessedStrikes)
                .AddToDB();

            var powerDivineStrike = FeatureDefinitionPowerSharedPoolBuilder
                .Create($"PowerClericBlessedStrikes{damageType}")
                .SetGuiPresentation(
                    $"Tooltip/&Tag{damageType}Title",
                    "Feature/&PowerClericBlessedStrikesSubPowerDescription")
                .AddCustomSubFeatures(new FormattedDefinitionText(description: () => Gui.Format(
                    "Feature/&PowerClericBlessedStrikesSubPowerDescription",
                    Gui.Localize($"Tooltip/&Tag{damageType}Title"))))
                .SetShowCasting(false)
                .SetSharedPool(ActivationTime.NoCost, PowerBlessedStrikes)
                .SetEffectDescription(
                    EffectDescriptionBuilder
                        .Create()
                        .SetDurationData(DurationType.Round)
                        .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                        .SetEffectForms(EffectFormBuilder.ConditionForm(conditionBlessedStrikes))
                        .Build())
                .AddToDB();

            powerDivineStrike.GuiPresentation.hidden = true;

            powers.Add(powerDivineStrike);

            FeatureDefinitionBuilder
                .Create($"FeatureClericAdditionalDamageGenericBlessed{damageType}")
                .SetGuiPresentation(
                    "Feature/&FeatureClericAdditionalDamageGenericBlessedTitle",
                    "Feature/&FeatureClericAdditionalDamageGenericBlessedDescription")
                .AddCustomSubFeatures(new FormattedDefinitionText(
                    () => Gui.Format("Feature/&FeatureClericAdditionalDamageGenericBlessedTitle",
                        Gui.Localize($"Tooltip/&Tag{damageType}Title")),
                    () => Gui.Format("Feature/&FeatureClericAdditionalDamageGenericBlessedDescription",
                        Gui.Localize($"Tooltip/&Tag{damageType}Title"))))
                .AddToDB();
        }

        var actionAffinityToggle = FeatureDefinitionActionAffinityBuilder
            .Create(ActionAffinitySorcererMetamagicToggle, "ActionAffinityBlessedStrikesToggle")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((Id)ExtraActionId.BlessedStrikesToggle)
            .AddToDB();

        var featureSetPrimalStrike = FeatureDefinitionFeatureSetBuilder
            .Create("FeatureSetClericBlessedStrikesPrimalStrike")
            .SetGuiPresentation(PowerBlessedStrikes.GuiPresentation)
            .SetFeatureSet(PowerBlessedStrikes, actionAffinityToggle, powers[0], powers[1])
            .AddToDB();

        PowerBundle.RegisterPowerBundle(PowerBlessedStrikes, false, powers);
        FeatureSetClericBlessedStrikes.FeatureSet.SetRange(FeaturePotentSpellcasting, featureSetPrimalStrike);

        foreach (var choice in FeatureSetClericBlessedStrikes.FeatureSet)
        {
            SavedBlessedStrikesChoices.Add(choice, FeatureDefinitionBuilder
                .Create("FeatureSavedBlessedStrikes" + choice.Name)
                .SetGuiPresentationNoContent(true)
                .AddToDB());
        }
    }

    private static void LoadClericChannelDivinity()
    {
        var powerDivineSparkHeal = FeatureDefinitionPowerSharedPoolBuilder
            .Create("PowerClericDivineSparkHeal")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.Action, PowerClericDivineSpark)
            .SetExplicitAbilityScore(AttributeDefinitions.Wisdom)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetBonusMode(AddBonusMode.AbilityBonus)
                            .SetHealingForm(
                                HealingComputation.Dice, 0, DieType.D8, 1, false, HealingCap.MaximumHitPoints)
                            .Build())
                    .SetParticleEffectParameters(CureWounds)
                    .Build())
            .AddCustomSubFeatures(ClassHolder.Cleric)
            .AddToDB();

        powerDivineSparkHeal.AddCustomSubFeatures(
            new ModifyEffectDescriptionPowerDivineSparkHeal(powerDivineSparkHeal));

        var powerDivineSparkDamageNecrotic = FeatureDefinitionPowerSharedPoolBuilder
            .Create("PowerClericDivineSparkDamageNecrotic")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.Action, PowerClericDivineSpark)
            .SetExplicitAbilityScore(AttributeDefinitions.Wisdom)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Constitution, true,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetBonusMode(AddBonusMode.AbilityBonus)
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDiceAdvancement(LevelSourceType.ClassLevel, 0, 20, (7, 1), (13, 2), (18, 3))
                            .SetDamageForm(DamageTypeNecrotic, 1, DieType.D8)
                            .Build())
                    .SetCasterEffectParameters(FalseLife)
                    .SetImpactEffectParameters(PowerWightLordRetaliate)
                    .Build())
            .AddCustomSubFeatures(ClassHolder.Cleric)
            .AddToDB();

        var powerDivineSparkDamageRadiant = FeatureDefinitionPowerSharedPoolBuilder
            .Create("PowerClericDivineSparkDamageRadiant")
            .SetGuiPresentation(Category.Feature)
            .SetSharedPool(ActivationTime.Action, PowerClericDivineSpark)
            .SetExplicitAbilityScore(AttributeDefinitions.Wisdom)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Constitution, true,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetBonusMode(AddBonusMode.AbilityBonus)
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDiceAdvancement(LevelSourceType.ClassLevel, 0, 20, (7, 1), (13, 2), (18, 3))
                            .SetDamageForm(DamageTypeRadiant, 1, DieType.D8)
                            .Build())
                    .SetCasterEffectParameters(ShadowArmor)
                    .SetImpactEffectParameters(PowerDomainBattleDecisiveStrike)
                    .Build())
            .AddCustomSubFeatures(ClassHolder.Cleric)
            .AddToDB();

        PowerBundle.RegisterPowerBundle(PowerClericDivineSpark, false,
            powerDivineSparkHeal, powerDivineSparkDamageNecrotic, powerDivineSparkDamageRadiant);
    }

    private static void LoadClericSearUndead()
    {
        PowerClericTurnUndead.EffectDescription.EffectForms.Insert(0, SearUndeadDamageForm);
        PowerClericTurnUndead.AddCustomSubFeatures(new ModifyEffectDescriptionPowerTurnUndead());
    }

    internal static void SwitchClericBlessedStrikes()
    {
        _legacyDivineStrikeUnlocks ??= ClericDomains.SelectMany(domain => domain.FeatureUnlocks
                .Where(unlock => HasLegacyDivineStrike(unlock.FeatureDefinition, []))
                .Select(unlock => (domain, unlock)))
            .ToList();

        Cleric.FeatureUnlocks.RemoveAll(unlock => unlock.FeatureDefinition == FeatureSetClericBlessedStrikes ||
                                                  unlock.FeatureDefinition == FeatureClericImprovedBlessedStrikes);

        if (Main.Settings.EnableClericBlessedStrikes2024)
        {
            Cleric.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureSetClericBlessedStrikes, 7));
            Cleric.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureClericImprovedBlessedStrikes, 14));
        }

        foreach (var (domain, unlock) in _legacyDivineStrikeUnlocks)
        {
            domain.FeatureUnlocks.Remove(unlock);

            if (!Main.Settings.EnableClericBlessedStrikes2024)
            {
                domain.FeatureUnlocks.Add(unlock);
            }

            domain.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }

        Cleric.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);

        var characters = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters;

        if (characters != null)
        {
            foreach (var character in characters)
            {
                if (character.RulesetCharacter is RulesetCharacterHero hero)
                {
                    UpdateClericBlessedStrikes(hero);
                }
            }
        }
    }

    internal static void UpdateClericBlessedStrikes(RulesetCharacterHero hero)
    {
        if (hero.GetClassLevel(Cleric) < 7 || !hero.ClassesAndSubclasses.TryGetValue(Cleric, out var subclass))
        {
            return;
        }

        var classTag = AttributeDefinitions.GetClassTag(Cleric, 7);
        var choiceTags = new[] { classTag, AttributeDefinitions.GetSubclassTag(Cleric, 7, subclass),
            AttributeDefinitions.GetSubclassTag(Cleric, 8, subclass) };
        var selected = SavedBlessedStrikesChoices.Keys.FirstOrDefault(choice => choiceTags.Any(tag =>
            hero.ActiveFeatures.TryGetValue(tag, out var features) &&
            (features.Contains(choice) || features.Contains(SavedBlessedStrikesChoices[choice]))));

        // Only migrate an explicit saved choice. Characters without one retain their existing features.
        if (selected == null)
        {
            return;
        }

        var saved = SavedBlessedStrikesChoices[selected];

        foreach (var tag in choiceTags)
        {
            if (hero.ActiveFeatures.TryGetValue(tag, out var features))
            {
                features.Remove(selected);
                features.Remove(saved);
            }
        }

        if (!hero.ActiveFeatures.TryGetValue(classTag, out var choices))
        {
            choices = [];
            hero.ActiveFeatures.Add(classTag, choices);
        }

        var enabled = Main.Settings.EnableClericBlessedStrikes2024;
        choices.Add(enabled ? selected : saved);
        UpdateBlessedStrikesPowers(hero, selected, enabled);

        foreach (var (domain, unlock) in _legacyDivineStrikeUnlocks ?? [])
        {
            if (domain != subclass || unlock.Level > hero.GetClassLevel(Cleric) ||
                EnumerateFeatureTree(unlock.FeatureDefinition).OfType<FeatureDefinitionFeatureSet>()
                    .Any(set => set.Mode == FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion))
            {
                continue;
            }

            // These are automatic features from the hero's actual domain and attained level.
            // Exclusion choices are never reconstructed from the modern selection.
            var legacyTag = AttributeDefinitions.GetSubclassTag(Cleric, unlock.Level, subclass);
            if (!hero.ActiveFeatures.TryGetValue(legacyTag, out var legacyFeatures))
            {
                if (enabled)
                {
                    continue;
                }

                legacyFeatures = [];
                hero.ActiveFeatures.Add(legacyTag, legacyFeatures);
            }

            legacyFeatures.Remove(unlock.FeatureDefinition);
            if (!enabled)
            {
                legacyFeatures.Add(unlock.FeatureDefinition);
            }

            UpdateBlessedStrikesPowers(hero, unlock.FeatureDefinition, !enabled);
        }

        var improvedTag = AttributeDefinitions.GetClassTag(Cleric, 14);

        foreach (var tag in new[] { improvedTag, AttributeDefinitions.GetSubclassTag(Cleric, 14, subclass) })
        {
            if (hero.ActiveFeatures.TryGetValue(tag, out var features))
            {
                features.Remove(FeatureClericImprovedBlessedStrikes);
            }
        }

        if (enabled && hero.GetClassLevel(Cleric) >= 14)
        {
            if (!hero.ActiveFeatures.TryGetValue(improvedTag, out var features))
            {
                features = [];
                hero.ActiveFeatures.Add(improvedTag, features);
            }

            features.Add(FeatureClericImprovedBlessedStrikes);
        }
    }

    private static void UpdateBlessedStrikesPowers(RulesetCharacterHero hero, FeatureDefinition feature, bool enabled)
    {
        foreach (var power in EnumerateFeatureTree(feature).OfType<FeatureDefinitionPower>())
        {
            var usable = hero.UsablePowers.FirstOrDefault(candidate => candidate.PowerDefinition == power);
            if (enabled && usable == null)
            {
                hero.UsablePowers.Add(PowerProvider.Get(power, hero));
            }
            else if (!enabled && usable != null)
            {
                hero.UsablePowers.Remove(usable);
            }
        }
    }

    private static bool HasLegacyDivineStrike(FeatureDefinition feature, HashSet<BaseDefinition> visited)
    {
        if (feature == null || !visited.Add(feature))
        {
            return false;
        }

        return feature switch
        {
            FeatureDefinitionAdditionalDamage damage => damage.NotificationTag == "DivineStrike",
            FeatureDefinitionFeatureSet set => set.FeatureSet.Any(child => HasLegacyDivineStrike(child, visited)),
            FeatureDefinitionPower power => power.EffectDescription.EffectForms
                .GetAppliedConditionDefinitions()
                .Where(condition => visited.Add(condition))
                .Any(condition => condition.Features.Any(child => HasLegacyDivineStrike(child, visited))),
            _ => false
        };
    }

    internal static void SwitchClericChannelDivinity()
    {
        Cleric.FeatureUnlocks
            .RemoveAll(x => x.FeatureDefinition == PowerClericDivineSpark);

        if (Main.Settings.EnableClericChannelDivinity2024)
        {
            Cleric.FeatureUnlocks.Add(new FeatureUnlockByLevel(PowerClericDivineSpark, 2));
            AttributeModifierClericChannelDivinity.modifierValue = 2;
            AttributeModifierClericChannelDivinity.GuiPresentation.description =
                "Feature/&ClericChannelDivinityExtendedDescription";
        }
        else
        {
            AttributeModifierClericChannelDivinity.modifierValue = 1;
            AttributeModifierClericChannelDivinity.GuiPresentation.description =
                "Feature/&ClericChannelDivinityDescription";
        }

        Cleric.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchClericDivineOrder()
    {
        Cleric.FeatureUnlocks
            .RemoveAll(x => x.FeatureDefinition == FeatureSetClericDivineOrder);

        if (Main.Settings.EnableClericDivineOrder2024)
        {
            Cleric.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureSetClericDivineOrder, 1));
        }

        SwitchClericDomainProficiencies();

        Cleric.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private static CharacterSubclassDefinition[] ClericDomains => DatabaseRepository
        .GetDatabase<DeityDefinition>()
        .SelectMany(deity => deity.Subclasses)
        .Concat(SubclassChoiceClericDivineDomains.Subclasses)
        .Select(GetDefinition<CharacterSubclassDefinition>)
        .Concat(SubclassesContext.KlassListContextTab[Cleric].AllSubClasses)
        .Distinct()
        .ToArray();

    private static (CharacterSubclassDefinition, FeatureDefinition)[] _clericFeaturesGrantedAt2;

    private static (CharacterSubclassDefinition, FeatureDefinition)[] ClericFeaturesGrantedAt2 =>
        _clericFeaturesGrantedAt2 ??= ClericDomains
            .SelectMany(y =>
                    y.FeatureUnlocks.Where(z => z.Level == 2),
                (subclass, feature) => (subclass, feature.FeatureDefinition))
            .ToArray();

    internal static void SwitchClericDomainLearningLevel()
    {
        var fromLevel = 3;
        var toLevel = 1;

        if (Main.Settings.EnableClericToLearnDomainAtLevel3)
        {
            fromLevel = 1;
            toLevel = 3;
        }

        var level = Main.Settings.EnableClericToLearnDomainAtLevel3 ? 3 : 2;

        foreach (var (subClass, feature) in ClericFeaturesGrantedAt2)
        {
            var unlock = subClass.FeatureUnlocks.FirstOrDefault(x => x.FeatureDefinition == feature);

            if (unlock != null)
            {
                unlock.level = level;
            }
        }

        SwitchSubclassLearningLevel(ClericDomains, Cleric, SubclassChoiceClericDivineDomains, fromLevel, toLevel);
        SwitchClericDomainProficiencies();
    }

    internal static void SwitchClericSearUndead()
    {
        Cleric.FeatureUnlocks
            .RemoveAll(x =>
                x.FeatureDefinition == FeatureClericSearUndead ||
                x.FeatureDefinition == PowerClericTurnUndead5 ||
                x.FeatureDefinition == PowerClericTurnUndead11 ||
                x.FeatureDefinition == PowerClericTurnUndead14 ||
                x.FeatureDefinition == Level20Context.PowerClericTurnUndead17);

        if (Main.Settings.EnableClericSearUndead2024)
        {
            Cleric.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureClericSearUndead, 5));
        }
        else
        {
            Cleric.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PowerClericTurnUndead5, 5),
                new FeatureUnlockByLevel(PowerClericTurnUndead11, 11),
                new FeatureUnlockByLevel(PowerClericTurnUndead14, 14),
                new FeatureUnlockByLevel(Level20Context.PowerClericTurnUndead17, 17));
        }

        Cleric.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private static int GetWisdomModifierMinimumOne(RulesetCharacter character)
    {
        var wisdom = character.TryGetAttributeValue(AttributeDefinitions.Wisdom);
        var wisMod = AttributeDefinitions.ComputeAbilityScoreModifier(wisdom);

        return Math.Max(wisMod, 1);
    }

    private sealed class ModifyEffectDescriptionPowerDivineSparkHeal(FeatureDefinitionPower powerDivineSparkHeal)
        : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return definition == powerDivineSparkHeal;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            var levels = character.GetClassLevel(Cleric);
            var diceNumber = levels switch
            {
                >= 18 => 4,
                >= 13 => 3,
                >= 7 => 2,
                _ => 1
            };

            effectDescription.EffectForms[0].HealingForm.diceNumber = diceNumber;

            return effectDescription;
        }
    }

    private sealed class ModifyEffectDescriptionPowerTurnUndead : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return definition == PowerClericTurnUndead;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            if (!Main.Settings.EnableClericSearUndead2024 || character.GetClassLevel(Cleric) < 5)
            {
                effectDescription.EffectForms.Remove(SearUndeadDamageForm);

                return effectDescription;
            }

            var diceNumber = GetWisdomModifierMinimumOne(character);

            effectDescription.EffectForms[0].DamageForm.diceNumber = diceNumber;

            return effectDescription;
        }
    }

    private sealed class ModifyAbilityCheckThaumaturge : IModifyAbilityCheck
    {
        public void MinRoll(
            RulesetCharacter character,
            int baseBonus,
            string abilityScoreName,
            string proficiencyName,
            List<TrendInfo> advantageTrends,
            List<TrendInfo> modifierTrends,
            ref int rollModifier, ref int minRoll)
        {
            if (abilityScoreName is not AttributeDefinitions.Intelligence ||
                proficiencyName is not (SkillDefinitions.Arcana or SkillDefinitions.Religion))
            {
                return;
            }

            var modifier = GetWisdomModifierMinimumOne(character);

            rollModifier += modifier;

            modifierTrends.Add(new TrendInfo(modifier, FeatureSourceType.CharacterFeature,
                PointPoolClericThaumaturgeCantrip.Name, PointPoolClericThaumaturgeCantrip));
        }
    }

    private sealed class CustomBehaviorBlessedStrikes(FeatureDefinitionPower powerBlessedStrikes)
        : IPhysicalAttackBeforeHitConfirmedOnEnemy
    {
        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnEnemy(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier actionModifier,
            RulesetAttackMode attackMode,
            bool rangedAttack,
            AdvantageType advantageType,
            List<EffectForm> actualEffectForms,
            bool firstTarget,
            bool criticalHit)
        {
            if (attackMode?.SourceDefinition is ItemDefinition { IsWeapon: true } item &&
                item.WeaponDescription.WeaponTypeDefinition != WeaponTypeDefinitions.UnarmedStrikeType)
            {
                yield return HandleReaction(attacker, battleManager);
            }
        }

        private IEnumerator HandleReaction(GameLocationCharacter attacker, GameLocationBattleManager battleManager)
        {
            var rulesetAttacker = attacker.RulesetCharacter;

            if (!Main.Settings.EnableClericBlessedStrikes2024 ||
                !rulesetAttacker.IsToggleEnabled((Id)ExtraActionId.BlessedStrikesToggle) ||
                !attacker.OnceInMyTurnIsValid(BlessedStrikes))
            {
                yield break;
            }

            var usablePowerPool = PowerProvider.Get(powerBlessedStrikes, rulesetAttacker);

            yield return attacker.MyReactToSpendPowerBundle(
                usablePowerPool,
                [attacker],
                attacker,
                powerBlessedStrikes.Name,
                reactionValidated: ReactionValidated,
                battleManager: battleManager);

            yield break;

            void ReactionValidated(ReactionRequestSpendBundlePower reactionRequest)
            {
                attacker.SetSpecialFeatureUses(BlessedStrikes, 1);
            }
        }
    }

    private sealed class ModifyEffectDescriptionPowerClericImprovedBlessedStrikes : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return definition == PowerClericImprovedBlessedStrikes;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            var tempHp = Math.Max(0, AttributeDefinitions.ComputeAbilityScoreModifier(
                character.TryGetAttributeValue(AttributeDefinitions.Wisdom))) * 2;

            effectDescription.EffectForms[0].TemporaryHitPointsForm.BonusHitPoints = tempHp;

            return effectDescription;
        }
    }
}
