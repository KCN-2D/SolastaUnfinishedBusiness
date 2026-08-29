using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;
using static FeatureDefinitionAttributeModifier;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionActionAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPointPools;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSubclassChoices;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly ConditionalWeakTable<RulesetCondition, RulesetSpellRepertoire>
        SpellDerivedConditionRepertoires = new();

    private static readonly ConditionalWeakTable<RulesetEffectPower, RulesetSpellRepertoire>
        SpellDerivedPowerRepertoires = new();

    private static readonly ConditionDefinition ConditionSorcererInnateSorcery = ConditionDefinitionBuilder
        .Create("ConditionSorcererInnateSorcery")
        .SetGuiPresentation(Category.Condition, ConditionAuraOfCourage)
        .AddCustomSubFeatures(new ModifyMagicEffectAttackModifierInnateSorcery())
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerSorcererInnateSorcery = FeatureDefinitionPowerBuilder
        .Create("PowerSorcererInnateSorcery")
        .SetGuiPresentation(Category.Feature, PowerTraditionShockArcanistGreaterArcaneShock)
        .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.LongRest, 1, 2)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Minute, 1)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetEffectForms(EffectFormBuilder.ConditionForm(ConditionSorcererInnateSorcery))
                .SetCasterEffectParameters(PowerSorcererDraconicElementalResistance)
                .Build())
        .AddCustomSubFeatures(new ValidatorsValidatePowerUse(c =>
            c.GetClassLevel(Sorcerer) < 7 || c.GetRemainingPowerUses(PowerSorcererInnateSorcery) > 0))
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerSorcererSorceryIncarnate = FeatureDefinitionPowerBuilder
        .Create(PowerSorcererInnateSorcery, "PowerSorcererSorceryIncarnate")
        .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.SorceryPoints, 2, 0)
        .AddCustomSubFeatures(new ValidatorsValidatePowerUse(c =>
            c.GetClassLevel(Sorcerer) >= 7 && c.GetRemainingPowerUses(PowerSorcererInnateSorcery) == 0))
        .AddToDB();

    private static readonly FeatureDefinitionFeatureSet FeatureSetSorcererSorceryIncarnate =
        FeatureDefinitionFeatureSetBuilder
            .Create("FeatureSetSorcererSorceryIncarnate")
            .SetGuiPresentation(Category.Feature)
            .SetFeatureSet(PowerSorcererSorceryIncarnate)
            .AddToDB();

    private static readonly ConditionDefinition ConditionArcaneApotheosis = ConditionDefinitionBuilder
        .Create("ConditionArcaneApotheosis")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .SetFixedAmount(0)
        .AddToDB();

    private static readonly FeatureDefinition FeatureSorcererArcaneApotheosis =
        FeatureDefinitionBuilder
            .Create("FeatureSorcererArcaneApotheosis")
            .SetGuiPresentation(Category.Feature)
            .AddCustomSubFeatures(new CustomBehaviorArcaneApotheosis())
            .AddToDB();

    private static readonly FeatureDefinitionPower PowerSorcerousRestoration = FeatureDefinitionPowerBuilder
        .Create(PowerSorcererManaPainterTap, "PowerSorcerousRestoration")
        .SetOrUpdateGuiPresentation(Category.Feature)
        .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
        .AddToDB();


    internal static void SwitchSorcererArcaneApotheosis()
    {
        Sorcerer.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == FeatureSorcererArcaneApotheosis ||
            x.FeatureDefinition == Level20Context.PowerSorcerousRestoration);

        Sorcerer.FeatureUnlocks.Add(
            Main.Settings.EnableSorcererArcaneApotheosis2024
                ? new FeatureUnlockByLevel(FeatureSorcererArcaneApotheosis, 20)
                : new FeatureUnlockByLevel(Level20Context.PowerSorcerousRestoration, 20));

        Sorcerer.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchSorcererMetamagic()
    {
        MetamagicContext.SwitchSorcererMetamagicRules2024();

        Sorcerer.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == PointPoolSorcererMetamagic ||
            x.FeatureDefinition == PointPoolSorcererAdditionalMetamagic ||
            x.FeatureDefinition == ActionAffinitySorcererMetamagicToggle);

        if (Main.Settings.EnableSorcererMetamagic2024)
        {
            Sorcerer.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PointPoolSorcererMetamagic, 2),
                new FeatureUnlockByLevel(ActionAffinitySorcererMetamagicToggle, 2),
                new FeatureUnlockByLevel(PointPoolSorcererMetamagic, 10),
                new FeatureUnlockByLevel(PointPoolSorcererMetamagic, 17));
        }
        else
        {
            Sorcerer.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PointPoolSorcererMetamagic, 3),
                new FeatureUnlockByLevel(ActionAffinitySorcererMetamagicToggle, 3),
                new FeatureUnlockByLevel(PointPoolSorcererAdditionalMetamagic, 17));
        }

        Sorcerer.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchSorcererOriginLearningLevel()
    {
        var origins = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>()
            .Where(x => x.Name.StartsWith("Sorcerous"))
            .ToList();

        var fromLevel = 3;
        var toLevel = 1;

        if (Main.Settings.EnableSorcererOrigin2024)
        {
            fromLevel = 1;
            toLevel = 3;
        }

        // handle level 2 grants
        var featuresGrantedAt2 = new[]
        {
            ("SorcerousManaPainter", "PowerSorcererManaPainterDrain"),
            ("SorcerousChildRift", "PowerSorcererChildRiftDeflection"),
            ("SorcerousSpellBlade", "FeatureSetSorcerousSpellBladeManaShield")
        };

        var level = Main.Settings.EnableSorcererOrigin2024 ? 3 : 2;

        foreach (var (subClassName, featureName) in featuresGrantedAt2)
        {
            var subClass = GetDefinition<CharacterSubclassDefinition>(subClassName);
            var feature = GetDefinition<FeatureDefinition>(featureName);

            subClass.FeatureUnlocks.FirstOrDefault(x => x.FeatureDefinition == feature)!.level = level;
        }

        SwitchSubclassLearningLevel(origins, Sorcerer, SubclassChoiceSorcerousOrigin, fromLevel, toLevel);
    }

    internal static bool IsArcaneApotheosisValid(RulesetCharacter rulesetCharacter, RulesetEffect rulesetEffect)
    {
        var character = GameLocationCharacter.GetFromActor(rulesetCharacter);

        return IsArcaneApotheosisValid(character, rulesetEffect, false);
    }

    private static bool IsArcaneApotheosisValid(
        GameLocationCharacter character,
        RulesetEffect rulesetEffect,
        bool validateMetamagicOption = true)
    {
        if (!Main.Settings.EnableSorcererArcaneApotheosis2024 ||
            rulesetEffect is not RulesetEffectSpell rulesetEffectSpell ||
            (validateMetamagicOption && !rulesetEffectSpell.MetamagicOption))
        {
            return false;
        }

        var rulesetCharacter = character.RulesetCharacter;
        var sorcererLevel = rulesetCharacter.GetClassLevel(Sorcerer);

        if (sorcererLevel < 20)
        {
            return false;
        }

        if (Gui.Battle != null &&
            !character.OnceInMyTurnIsValid(FeatureSorcererArcaneApotheosis.Name))
        {
            return false;
        }

        return rulesetCharacter.HasConditionOfCategoryAndType(
            AttributeDefinitions.TagEffect, ConditionSorcererInnateSorcery.Name);
    }

    internal static void ModifyInnateSorcerySaveDc(
        RulesetEffectSpell spellEffect,
        ref int saveDc)
    {
        if (spellEffect?.EffectDescription?.HasSavingThrow == true &&
            IsInnateSorceryValid(spellEffect))
        {
            saveDc++;
        }
    }

    internal static void ModifyInnateSorcerySaveDc(
        RulesetEffectPower powerEffect,
        ref int saveDc)
    {
        if (!TryGetSpellDerivedPowerRepertoire(powerEffect, out var spellRepertoire) ||
            !IsSorcererSpellRepertoire(spellRepertoire))
        {
            return;
        }

        if (powerEffect.EffectDescription.DifficultyClassComputation ==
            EffectDifficultyClassComputation.SpellCastingFeature)
        {
            saveDc = spellRepertoire.SaveDC;
        }

        if (powerEffect.EffectDescription.HasSavingThrow && IsInnateSorceryValid(spellRepertoire))
        {
            saveDc++;
        }
    }

    internal static void ModifyInnateSorcerySaveDc(
        RulesetCondition condition,
        ref int saveDc)
    {
        if (condition != null &&
            IsInnateSorceryValid(GetSpellDerivedConditionRepertoire(condition)))
        {
            saveDc++;
        }
    }

    internal static void BindSpellDerivedPowerOrigin(RulesetEffectPower powerEffect)
    {
        if (powerEffect == null)
        {
            return;
        }

        SpellDerivedPowerRepertoires.Remove(powerEffect);

        if (TryResolveSpellDerivedPowerRepertoire(powerEffect, out var spellRepertoire))
        {
            SpellDerivedPowerRepertoires.Add(powerEffect, spellRepertoire);
        }
    }

    internal static void BindSpellDerivedConditionOrigin(
        RulesetCondition condition,
        RulesetSpellRepertoire spellRepertoire)
    {
        if (condition == null)
        {
            return;
        }

        SpellDerivedConditionRepertoires.Remove(condition);

        if (spellRepertoire != null)
        {
            SpellDerivedConditionRepertoires.Add(condition, spellRepertoire);
        }
    }

    internal static void UnbindSpellDerivedConditionOrigin(RulesetCondition condition)
    {
        if (condition != null)
        {
            SpellDerivedConditionRepertoires.Remove(condition);
        }
    }

    private static bool IsInnateSorceryValid(RulesetSpellRepertoire spellRepertoire)
    {
        return IsSorcererSpellRepertoire(spellRepertoire) &&
               spellRepertoire.GetCaster()?.HasConditionOfCategoryAndType(
                   AttributeDefinitions.TagEffect, ConditionSorcererInnateSorcery.Name) == true;
    }

    private static bool IsInnateSorceryValid(RulesetEffect rulesetEffect)
    {
        if (rulesetEffect is RulesetEffectSpell spellEffect)
        {
            return spellEffect.OriginItem == null &&
                   IsInnateSorceryValid(spellEffect.SpellRepertoire);
        }

        return rulesetEffect is RulesetEffectPower powerEffect &&
               TryGetSpellDerivedPowerRepertoire(powerEffect, out var spellRepertoire) &&
               IsInnateSorceryValid(spellRepertoire);
    }

    private static bool IsSorcererSpellRepertoire(RulesetSpellRepertoire spellRepertoire)
    {
        return spellRepertoire?.SpellCastingFeature?.SpellCastingOrigin ==
                   FeatureDefinitionCastSpell.CastingOrigin.Class &&
               spellRepertoire.SpellCastingClass == Sorcerer;
    }

    private static bool TryGetSpellDerivedPowerRepertoire(
        RulesetEffectPower powerEffect,
        out RulesetSpellRepertoire spellRepertoire)
    {
        spellRepertoire = null;

        if (powerEffect == null || powerEffect.OriginItem != null)
        {
            return false;
        }

        if (SpellDerivedPowerRepertoires.TryGetValue(powerEffect, out spellRepertoire))
        {
            return true;
        }

        if (!TryResolveSpellDerivedPowerRepertoire(powerEffect, out spellRepertoire))
        {
            return false;
        }

        SpellDerivedPowerRepertoires.Add(powerEffect, spellRepertoire);

        return true;
    }

    private static bool TryResolveSpellDerivedPowerRepertoire(
        RulesetEffectPower powerEffect,
        out RulesetSpellRepertoire spellRepertoire)
    {
        spellRepertoire = null;

        if (powerEffect == null ||
            powerEffect.OriginItem != null ||
            powerEffect.User == null ||
            powerEffect.PowerDefinition == null)
        {
            return false;
        }

        foreach (var condition in powerEffect.User.AllConditions.Where(x =>
                     x.ConditionDefinition.Features.Contains(powerEffect.PowerDefinition)))
        {
            var candidate = GetSpellDerivedConditionRepertoire(condition);

            if (candidate == null)
            {
                continue;
            }

            // A usable power does not identify which of two equal condition features granted it.
            // Refuse an ambiguous cross-repertoire origin instead of applying the feature to the wrong spell.
            if (spellRepertoire != null && spellRepertoire != candidate)
            {
                spellRepertoire = null;

                return false;
            }

            spellRepertoire = candidate;
        }

        return spellRepertoire != null;
    }

    private static RulesetSpellRepertoire GetSpellDerivedConditionRepertoire(RulesetCondition condition)
    {
        if (SpellDerivedConditionRepertoires.TryGetValue(condition, out var spellRepertoire))
        {
            return spellRepertoire;
        }

        var sourceCharacter = EffectHelpers.GetCharacterByGuid(condition.SourceGuid);

        return sourceCharacter?.SpellsCastByMe
            .FirstOrDefault(x => x.TrackedConditionGuids.Contains(condition.Guid))
            ?.SpellRepertoire;
    }

    internal static void SwitchSorcererInnateSorcery()
    {
        Sorcerer.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == PowerSorcererInnateSorcery ||
            x.FeatureDefinition == FeatureSetSorcererSorceryIncarnate);

        if (Main.Settings.EnableSorcererInnateSorcery2024)
        {
            Sorcerer.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PowerSorcererInnateSorcery, 1),
                new FeatureUnlockByLevel(FeatureSetSorcererSorceryIncarnate, 7));
        }

        Sorcerer.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private static void LoadSorcererSorcerousRestoration()
    {
        RestActivityDefinitionBuilder
            .Create("RestActivitySorcerousRestoration")
            .SetGuiPresentation(
                "Feature/&PowerSorcerousRestorationShortTitle", "Feature/&PowerSorcerousRestorationDescription")
            .SetRestData(RestDefinitions.RestStage.AfterRest, RestType.ShortRest,
                RestActivityDefinition.ActivityCondition.CanUsePower, "UsePower", PowerSorcerousRestoration.Name)
            .AddToDB();

        PowerSorcerousRestoration.EffectDescription.EffectForms[0].SpellSlotsForm.type =
            (SpellSlotsForm.EffectType)ExtraEffectType.RecoverSorceryHalfLevelDown;
    }

    internal static void SwitchSorcererSorcerousRestorationAtLevel5()
    {
        Sorcerer.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == PowerSorcerousRestoration);

        if (Main.Settings.EnableSorcererSorcerousRestoration2024)
        {
            Sorcerer.FeatureUnlocks.Add(new FeatureUnlockByLevel(PowerSorcerousRestoration, 5));
        }

        Sorcerer.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchSorcererDraconicBloodlineAC()
    {
        var feature = FeatureDefinitionAttributeModifiers.AttributeModifierSorcererDraconicResilienceAC;
        var featureSet = FeatureDefinitionFeatureSets.FeatureSetSorcererDraconicResilience;
        if (Main.Settings.EnableSorcererDraconicBloodlineAC2024)
        {
            feature.modifierOperation = AttributeModifierOperation.SetWithDexPlusOtherAbilityScoreBonusIfBetter;
            feature.modifierValue = 10;
            feature.modifierAbilityScore = AttributeDefinitions.Charisma;

            featureSet.GuiPresentation.Description = "Feature/&FeatureSetSorcererDraconicResilience2024Description";
        }
        else
        {
            feature.modifierOperation = AttributeModifierOperation.Set;
            feature.modifierValue = 13;
            //not really needed, just returning to default
            feature.modifierAbilityScore = AttributeDefinitions.Constitution;

            featureSet.GuiPresentation.Description = "Feature/&FeatureSetSorcererDraconicResilienceDescription";
        }
    }

    private sealed class CustomBehaviorArcaneApotheosis : IMagicEffectInitiatedByMe, IMagicEffectFinishedByMe
    {
        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (!IsArcaneApotheosisValid(attacker, action.ActionParams.RulesetEffect))
            {
                yield break;
            }

            attacker.SetSpecialFeatureUses(FeatureSorcererArcaneApotheosis.Name, 0);

            var rulesetCharacter = attacker.RulesetCharacter;

            if (!TryGetArcaneApotheosisSnapshot(rulesetCharacter, out var previousUsedSorceryPoints))
            {
                yield break;
            }

            RefundArcaneApotheosisMetamagicOnly(
                rulesetCharacter,
                previousUsedSorceryPoints,
                GetArcaneApotheosisMetamagicRefund(action.ActionParams.RulesetEffect));
        }

        public IEnumerator OnMagicEffectInitiatedByMe(
            CharacterAction action,
            RulesetEffect activeEffect,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (!IsArcaneApotheosisValid(attacker, action.ActionParams.RulesetEffect))
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;

            rulesetAttacker.InflictCondition(
                ConditionArcaneApotheosis.Name,
                DurationType.Round,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                rulesetAttacker.Guid,
                rulesetAttacker.CurrentFaction.Name,
                1,
                ConditionArcaneApotheosis.Name,
                rulesetAttacker.UsedSorceryPoints,
                0,
                0);
        }

        private static bool TryGetArcaneApotheosisSnapshot(
            RulesetCharacter rulesetCharacter,
            out int usedSorceryPoints)
        {
            usedSorceryPoints = 0;

            if (!rulesetCharacter.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, ConditionArcaneApotheosis.Name, out var activeCondition))
            {
                return false;
            }

            usedSorceryPoints = activeCondition.Amount;

            return true;
        }

        private static int GetArcaneApotheosisMetamagicRefund(RulesetEffect rulesetEffect)
        {
            if (rulesetEffect is not RulesetEffectSpell rulesetEffectSpell)
            {
                return 0;
            }

            var metamagicOption = rulesetEffectSpell.MetamagicOption;

            if (!metamagicOption)
            {
                return 0;
            }

            return metamagicOption.CostMethod == MetamagicCostMethod.SpellLevel
                ? System.Math.Max(1, rulesetEffectSpell.EffectLevel)
                : System.Math.Max(0, metamagicOption.SorceryPointsCost);
        }

        private static void RefundArcaneApotheosisMetamagicOnly(
            RulesetCharacter rulesetCharacter,
            int previousUsedSorceryPoints,
            int metamagicRefund)
        {
            if (metamagicRefund <= 0)
            {
                return;
            }

            var currentUsedSorceryPoints = rulesetCharacter.UsedSorceryPoints;
            var spentSinceSnapshot = System.Math.Max(0, currentUsedSorceryPoints - previousUsedSorceryPoints);
            var actualRefund = System.Math.Min(metamagicRefund, spentSinceSnapshot);

            if (actualRefund <= 0)
            {
                return;
            }

            var adjustedUsedSorceryPoints = System.Math.Max(
                previousUsedSorceryPoints,
                currentUsedSorceryPoints - actualRefund);

            if (adjustedUsedSorceryPoints == currentUsedSorceryPoints)
            {
                return;
            }

            rulesetCharacter.usedSorceryPoints = adjustedUsedSorceryPoints;
            rulesetCharacter.SorceryPointsAltered?.Invoke(rulesetCharacter, adjustedUsedSorceryPoints);
        }
    }

    private sealed class ModifyMagicEffectAttackModifierInnateSorcery : IModifyMagicEffectAttackModifier
    {
        private readonly TrendInfo _trendInfo =
            new(1, FeatureSourceType.CharacterFeature, "PowerSorcererInnateSorcery", null);

        public void ModifyMagicEffectAttackModifier(
            RulesetCharacter attacker,
            RulesetActor defender,
            RulesetAttackMode attackMode,
            RulesetEffect rulesetEffect,
            ActionModifier actionModifier)
        {
            if (IsInnateSorceryValid(rulesetEffect))
            {
                actionModifier.AttackAdvantageTrends.Add(_trendInfo);
            }
        }
    }
}
