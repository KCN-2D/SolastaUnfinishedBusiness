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
    private static readonly ConditionalWeakTable<RulesetCondition, SpellEffectOrigin>
        SpellDerivedConditionOrigins = new();

    private static readonly ConditionalWeakTable<RulesetEffectPower, SpellEffectOrigin>
        SpellDerivedPowerOrigins = new();

    private sealed class SpellEffectOrigin(
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spellDefinition,
        ulong casterGuid,
        int baseSaveDc,
        bool useSpellListClassification)
    {
        internal RulesetSpellRepertoire SpellRepertoire { get; } = spellRepertoire;
        internal SpellDefinition SpellDefinition { get; } = spellDefinition;
        internal ulong CasterGuid { get; } = casterGuid;
        internal int BaseSaveDc { get; } = baseSaveDc;
        internal bool UseSpellListClassification { get; } = useSpellListClassification;
    }

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
        if (!TryGetSpellDerivedPowerOrigin(powerEffect, out var spellOrigin) ||
            !IsSorcererSpell(
                spellOrigin.SpellRepertoire,
                spellOrigin.SpellDefinition,
                EffectHelpers.GetCharacterByGuid(spellOrigin.CasterGuid),
                spellOrigin.UseSpellListClassification))
        {
            return;
        }

        var spellRepertoire = spellOrigin.SpellRepertoire;

        if (powerEffect.EffectDescription.DifficultyClassComputation ==
            EffectDifficultyClassComputation.SpellCastingFeature)
        {
            saveDc = spellRepertoire?.SaveDC ?? spellOrigin.BaseSaveDc;
        }

        if (powerEffect.EffectDescription.HasSavingThrow &&
            IsInnateSorceryValid(spellOrigin))
        {
            saveDc++;
        }
    }

    internal static void ModifyInnateSorcerySaveDc(
        RulesetCondition condition,
        ref int saveDc)
    {
        if (condition != null &&
            IsInnateSorceryValid(GetSpellDerivedConditionOrigin(condition)))
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

        SpellDerivedPowerOrigins.Remove(powerEffect);

        if (TryResolveSpellDerivedPowerOrigin(powerEffect, out var spellOrigin))
        {
            SpellDerivedPowerOrigins.Add(powerEffect, spellOrigin);
        }
    }

    internal static void BindSpellDerivedConditionOrigin(
        RulesetCondition condition,
        RulesetEffectSpell spellEffect)
    {
        if (condition == null)
        {
            return;
        }

        SpellDerivedConditionOrigins.Remove(condition);

        var spellOrigin = CreateSpellEffectOrigin(spellEffect, condition.SourceGuid);

        if (spellOrigin != null)
        {
            SpellDerivedConditionOrigins.Add(condition, spellOrigin);
        }
    }

    internal static void UnbindSpellDerivedConditionOrigin(RulesetCondition condition)
    {
        if (condition != null)
        {
            SpellDerivedConditionOrigins.Remove(condition);
        }
    }

    private static bool IsInnateSorceryValid(SpellEffectOrigin spellOrigin)
    {
        return spellOrigin != null &&
               IsInnateSorceryValid(
                   spellOrigin.SpellRepertoire,
                   spellOrigin.SpellDefinition,
                   EffectHelpers.GetCharacterByGuid(spellOrigin.CasterGuid),
                   spellOrigin.UseSpellListClassification);
    }

    private static bool IsInnateSorceryValid(
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spellDefinition,
        RulesetCharacter effectCaster,
        bool useSpellListClassification)
    {
        return IsSorcererSpell(
                   spellRepertoire,
                   spellDefinition,
                   effectCaster,
                   useSpellListClassification) &&
               (HasInnateSorceryCondition(effectCaster) ||
                HasInnateSorceryCondition(spellRepertoire?.GetCaster()));
    }

    private static bool IsInnateSorceryValid(RulesetEffect rulesetEffect)
    {
        if (rulesetEffect is RulesetEffectSpell spellEffect)
        {
            var effectCaster = spellEffect.Caster;

            return effectCaster?.IsSpellCastAsClassOrSubclassSpell(spellEffect, Sorcerer) == true &&
                   (HasInnateSorceryCondition(effectCaster) ||
                    HasInnateSorceryCondition(spellEffect.SpellRepertoire?.GetCaster()));
        }

        return rulesetEffect is RulesetEffectPower powerEffect &&
               TryGetSpellDerivedPowerOrigin(powerEffect, out var spellOrigin) &&
               IsInnateSorceryValid(spellOrigin);
    }

    private static bool HasInnateSorceryCondition(RulesetCharacter character)
    {
        if (character == null)
        {
            return false;
        }

        if (character.HasConditionOfCategoryAndType(
                AttributeDefinitions.TagEffect,
                ConditionSorcererInnateSorcery.Name))
        {
            return true;
        }

        var featureOwner = character.GetFeatureOwnerOrSelf();

        return featureOwner != character &&
               featureOwner?.HasConditionOfCategoryAndType(
                   AttributeDefinitions.TagEffect,
                   ConditionSorcererInnateSorcery.Name) == true;
    }

    private static bool IsSorcererSpell(
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spellDefinition,
        RulesetCharacter effectCaster,
        bool useSpellListClassification)
    {
        return (effectCaster ?? spellRepertoire?.GetCaster())
            ?.IsSpellCastAsClassOrSubclassSpell(
                spellRepertoire,
                spellDefinition,
                Sorcerer,
                useSpellListClassification) == true;
    }

    private static bool TryGetSpellDerivedPowerOrigin(
        RulesetEffectPower powerEffect,
        out SpellEffectOrigin spellOrigin)
    {
        spellOrigin = null;

        if (powerEffect == null || powerEffect.OriginItem != null)
        {
            return false;
        }

        if (SpellDerivedPowerOrigins.TryGetValue(powerEffect, out spellOrigin))
        {
            return true;
        }

        if (!TryResolveSpellDerivedPowerOrigin(powerEffect, out spellOrigin))
        {
            return false;
        }

        SpellDerivedPowerOrigins.Add(powerEffect, spellOrigin);

        return true;
    }

    private static bool TryResolveSpellDerivedPowerOrigin(
        RulesetEffectPower powerEffect,
        out SpellEffectOrigin spellOrigin)
    {
        spellOrigin = null;

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
            var candidate = GetSpellDerivedConditionOrigin(condition);

            if (candidate == null)
            {
                spellOrigin = null;

                return false;
            }

            // A usable power does not identify which of two equal condition features granted it.
            // Refuse an ambiguous origin instead of applying the feature to the wrong spell.
            if (spellOrigin != null &&
                (spellOrigin.SpellRepertoire != candidate.SpellRepertoire ||
                 spellOrigin.SpellDefinition != candidate.SpellDefinition ||
                 spellOrigin.CasterGuid != candidate.CasterGuid ||
                 spellOrigin.BaseSaveDc != candidate.BaseSaveDc ||
                 spellOrigin.UseSpellListClassification != candidate.UseSpellListClassification))
            {
                spellOrigin = null;

                return false;
            }

            spellOrigin = candidate;
        }

        return spellOrigin != null;
    }

    private static SpellEffectOrigin GetSpellDerivedConditionOrigin(RulesetCondition condition)
    {
        if (condition == null)
        {
            return null;
        }

        if (SpellDerivedConditionOrigins.TryGetValue(condition, out var spellOrigin))
        {
            return spellOrigin;
        }

        var sourceCharacter = EffectHelpers.GetCharacterByGuid(condition.SourceGuid);
        var spellEffect = sourceCharacter?.SpellsCastByMe
            .FirstOrDefault(x => x.TrackedConditionGuids.Contains(condition.Guid));

        spellOrigin = CreateSpellEffectOrigin(spellEffect, condition.SourceGuid);

        if (spellOrigin != null)
        {
            SpellDerivedConditionOrigins.Add(condition, spellOrigin);
        }

        return spellOrigin;
    }

    private static SpellEffectOrigin CreateSpellEffectOrigin(
        RulesetEffectSpell spellEffect,
        ulong fallbackCasterGuid = 0)
    {
        if (spellEffect == null || !spellEffect.SpellDefinition)
        {
            return null;
        }

        var spellDefinition = spellEffect.SpellDefinition;

        if (SpellsContext.SpellsChildMaster.TryGetValue(spellDefinition, out var masterSpell))
        {
            spellDefinition = masterSpell;
        }

        var casterGuid = spellEffect.Caster?.Guid ??
                         (fallbackCasterGuid != 0
                             ? fallbackCasterGuid
                             : spellEffect.SpellRepertoire?.GetCaster()?.Guid ?? 0);

        if (casterGuid == 0)
        {
            return null;
        }

        return new SpellEffectOrigin(
            spellEffect.SpellRepertoire,
            spellDefinition,
            casterGuid,
            GetSpellBaseSaveDc(spellEffect),
            spellEffect.UsesSpellListClassification());
    }

    internal static int GetSpellBaseSaveDc(RulesetEffectSpell spellEffect)
    {
        var saveDc = spellEffect.SaveDC;

        if (spellEffect.EffectDescription?.HasSavingThrow == true &&
            IsInnateSorceryValid(spellEffect))
        {
            saveDc--;
        }

        return saveDc;
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
