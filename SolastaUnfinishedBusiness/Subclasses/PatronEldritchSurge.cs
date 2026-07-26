using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Infrastructure;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using static RuleDefinitions;
using static ActionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Subclasses.Builders.EldritchVersatilityBuilders;

namespace SolastaUnfinishedBusiness.Subclasses;

[UsedImplicitly]
public class PatronEldritchSurge : AbstractSubclass
{
    public const string Name = "PatronEldritchSurge";
    private static readonly int[] BeamIncreaseDeterminationThresholds = [3, 8, 13, 18];

    // LEVEL 01 Blast Exclusive
    private static readonly FeatureDefinitionBonusCantrips BonusCantripBlastExclusive =
        FeatureDefinitionBonusCantripsBuilder
            .Create($"BonusCantrips{Name}BlastExclusive")
            .SetGuiPresentation(Category.Feature)
            .SetBonusCantrips(EldritchBlast)
            .AddCustomSubFeatures(new ModifyEffectDescriptionEldritchBlast())
            .AddToDB();

    // LEVEL 01 Versatility Switch
    public static readonly FeatureDefinitionPower PowerVersatilitySwitch = BuildVersatilitySwitch();

    // LEVEL 06 Blast Pursuit
    public static readonly FeatureDefinition FeatureBlastPursuit = FeatureDefinitionBuilder
        .Create($"Feature{Name}BlastPursuit")
        .SetGuiPresentation(Category.Feature)
        .AddCustomSubFeatures(new OnReducedToZeroHpByMeBlastPursuit())
        .AddToDB();

    // LEVEL 10 Blast Reload
    public static readonly FeatureDefinitionPower FeatureBlastReload = FeatureDefinitionPowerBuilder
        .Create($"Power{Name}BlastReload")
        .SetGuiPresentation(Category.Feature)
        .SetUsesFixed(ActivationTime.Permanent)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Permanent)
                .SetEffectForms(
                    EffectFormBuilder
                        .Create()
                        .SetConditionForm(
                            BlastReloadSupportRulesetCondition.BindingDefinition,
                            ConditionForm.ConditionOperation.Add)
                        .Build())
                .Build())
        .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
        .AddToDB();

    public PatronEldritchSurge()
    {
        var builder = CharacterSubclassDefinitionBuilder
            .Create(Name)
            .SetGuiPresentation(Category.Subclass, Sprites.GetSprite(Name, Resources.PatronEldritchSurge, 256))
            .AddFeaturesAtLevel(1,
                BonusCantripBlastExclusive,
                Learn2Versatility,
                PowerEldritchVersatilityPointPool,
                PowerVersatilitySwitch)
            .AddFeaturesAtLevel(6,
                FeatureBlastPursuit,
                Learn1Versatility)
            .AddFeaturesAtLevel(10,
                FeatureBlastReload,
                Learn1Versatility)
            .AddFeaturesAtLevel(14,
                PowerBlastOverload,
                Learn1Versatility);

        for (var i = 2; i <= 20; i++)
        {
            builder.AddFeaturesAtLevel(i, UnLearn1Versatility);
        }

        BuildVersatilities();
        Subclass = builder.AddToDB();
    }

    internal override CharacterClassDefinition Klass => CharacterClassDefinitions.Warlock;

    internal override CharacterSubclassDefinition Subclass { get; }

    internal override FeatureDefinitionSubclassChoice SubclassChoice =>
        FeatureDefinitionSubclassChoices.SubclassChoiceWarlockOtherworldlyPatrons;

    // ReSharper disable once UnassignedGetOnlyAutoProperty
    internal override DeityDefinition DeityDefinition { get; }

    private static FeatureDefinitionPower BuildVersatilitySwitch()
    {
        var pool = FeatureDefinitionPowerBuilder
            .Create($"Power{Name}VersatilitySwitchPool")
            .SetGuiPresentation(Category.Feature,
                Sprites.GetSprite("VersatilitySwitch", Resources.VersatilitySwitch, 128))
            .SetUsesFixed(ActivationTime.NoCost, RechargeRate.TurnStart)
            .AddCustomSubFeatures(new RechargePoolWhenBattleEnd())
            .AddToDB();

        var powerVersatilitySwitchStr =
            FeatureDefinitionPowerSharedPoolBuilder
                .Create($"Power{Name}VersatilitySwitchStr")
                .SetGuiPresentation(Category.Feature)
                .SetSharedPool(ActivationTime.NoCost, pool)
                .AddCustomSubFeatures(new VersatilitySwitchCustom(AttributeDefinitions.Strength))
                .AddToDB();

        var powerVersatilitySwitchInt =
            FeatureDefinitionPowerSharedPoolBuilder
                .Create($"Power{Name}VersatilitySwitchInt")
                .SetGuiPresentation(Category.Feature)
                .SetSharedPool(ActivationTime.NoCost, pool)
                .AddCustomSubFeatures(new VersatilitySwitchCustom(AttributeDefinitions.Intelligence))
                .AddToDB();

        var powerVersatilitySwitchWis =
            FeatureDefinitionPowerSharedPoolBuilder
                .Create($"Power{Name}VersatilitySwitchWis")
                .SetGuiPresentation(Category.Feature)
                .SetSharedPool(ActivationTime.NoCost, pool)
                .AddCustomSubFeatures(new VersatilitySwitchCustom(AttributeDefinitions.Wisdom))
                .AddToDB();

        var powerVersatilitySwitchNone =
            FeatureDefinitionPowerSharedPoolBuilder
                .Create($"Power{Name}VersatilitySwitchNone")
                .SetGuiPresentation(Category.Feature)
                .SetSharedPool(ActivationTime.NoCost, pool)
                .AddCustomSubFeatures(new VersatilitySwitchCustom(string.Empty))
                .AddToDB();

        PowerBundle.RegisterPowerBundle(pool, false,
            powerVersatilitySwitchStr,
            powerVersatilitySwitchInt,
            powerVersatilitySwitchWis,
            powerVersatilitySwitchNone);

        return pool;
    }

    public static bool IsEldritchBlast(RulesetEffect rulesetEffect)
    {
        return rulesetEffect is RulesetEffectSpell rulesetEffectSpell
               && rulesetEffectSpell.SpellDefinition == EldritchBlast;
    }

    internal static int ComputeDetermination(int characterLevel, int warlockLevel)
    {
        return warlockLevel - (2 * (characterLevel - warlockLevel));
    }

    internal static int ComputeBeamCount(int characterLevel, int warlockLevel)
    {
        var determination = ComputeDetermination(characterLevel, warlockLevel);

        return 1 + BeamIncreaseDeterminationThresholds.Count(level => determination >= level);
    }

    private static RulesetCharacter GetStateOwner(RulesetCharacter character)
    {
        return character.TryGetShapeChangeOriginalHero(out var shapeChangeHero)
            ? shapeChangeHero
            : character;
    }

    private class VersatilitySwitchCustom(string replacedAbilityScore) : IPowerOrSpellFinishedByMe
    {
        private string ReplacedAbilityScore { get; } = replacedAbilityScore;

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            var rulesetCharacter = action.ActingCharacter.RulesetCharacter;

            if (!rulesetCharacter.GetVersatilitySupportCondition(out var supportCondition))
            {
                yield break;
            }

            supportCondition.ReplacedAbilityScore = ReplacedAbilityScore;
            supportCondition.ModifyAttributeScores(rulesetCharacter, ReplacedAbilityScore);

            // Auto recharge out of combat
            if (Gui.Battle is null)
            {
                GetStateOwner(rulesetCharacter).UsablePowers.DoIf(x =>
                        x.PowerDefinition == PowerVersatilitySwitch,
                    y => y.Recharge());
            }

            yield break;
        }
    }

    public sealed class ModifyEffectDescriptionEldritchBlast : IModifyEffectDescription
    {
        public bool IsValid(
            BaseDefinition definition,
            RulesetCharacter character,
            EffectDescription effectDescription)
        {
            return definition == EldritchBlast
                   && character.GetSubclassLevel(CharacterClassDefinitions.Warlock, Name) > 0;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter rulesetCharacter,
            RulesetEffect rulesetEffect)
        {
            var stateOwner = GetStateOwner(rulesetCharacter);
            var characterLevel = stateOwner.TryGetAttributeValue(AttributeDefinitions.CharacterLevel);
            var warlockLevel = stateOwner.GetClassLevel(CharacterClassDefinitions.Warlock);

            effectDescription.effectAdvancement.Clear();
            effectDescription.targetParameter = ComputeBeamCount(characterLevel, warlockLevel);

            return effectDescription;
        }
    }

    private sealed class OnReducedToZeroHpByMeBlastPursuit : IOnReducedToZeroHpByMe
    {
        public IEnumerator HandleReducedToZeroHpByMe(
            GameLocationCharacter attacker,
            GameLocationCharacter downedCreature,
            RulesetAttackMode attackMode,
            RulesetEffect activeEffect)
        {
            var rulesetAttacker = attacker.RulesetCharacter;

            if (rulesetAttacker is not { IsDeadOrDyingOrUnconscious: false })
            {
                yield break;
            }

            if (!IsEldritchBlast(activeEffect)
                || !rulesetAttacker.GetVersatilitySupportCondition(out var supportCondition))
            {
                yield break;
            }

            supportCondition.TryEarnOrSpendPoints(
                PointAction.Modify, PointUsage.EarnPoints, supportCondition.BeamNumber);
        }
    }

    internal interface IQualifySpellToRepertoireLine
    {
        void QualifySpells(RulesetCharacter character, SpellRepertoireLine line, List<SpellDefinition> spells);
    }

    private sealed class BlastReloadCustom :
        IMagicEffectFinishedByMe, ICharacterTurnStartListener, IQualifySpellToRepertoireLine
    {
        public void OnCharacterTurnStarted(GameLocationCharacter gameLocationCharacter)
        {
            // clean up cantrips list on every turn start
            // combat condition will be removed automatically after combat
            var rulesetCharacter = gameLocationCharacter.RulesetCharacter;

            if (rulesetCharacter is not { IsDeadOrDyingOrUnconscious: false })
            {
                return;
            }

            if (!BlastReloadSupportRulesetCondition.GetCustomConditionFromCharacter(
                    rulesetCharacter, out var supportCondition))
            {
                return;
            }

            supportCondition.CantripAsMain = false;
            supportCondition.SpellAsMain = false;
        }

        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            var actionParams = action.ActionParams;
            var actionType = action.ActionType;
            var rulesetCharacter = attacker.RulesetCharacter;

            if (Gui.Battle == null ||
                action.Countered ||
                action is CharacterActionCastSpell { ExecutionFailed: true } ||
                actionParams.activeEffect is not RulesetEffectSpell rulesetEffectSpell ||
                actionType != ActionType.Main ||
                !BlastReloadSupportRulesetCondition.GetCustomConditionFromCharacter(
                    rulesetCharacter, out var supportCondition)
               )
            {
                yield break;
            }

            switch (RulesetEffectSpellWithOrigin.GetOriginSpell(rulesetEffectSpell).SpellLevel)
            {
                case 0:
                    supportCondition.CantripAsMain = true;
                    break;
                case > 0:
                    supportCondition.SpellAsMain = true;
                    break;
            }
        }

        public void QualifySpells(
            RulesetCharacter rulesetCharacter,
            SpellRepertoireLine spellRepertoireLine,
            List<SpellDefinition> spells)
        {
            if (spellRepertoireLine.actionType != ActionType.Bonus
                || !BlastReloadSupportRulesetCondition.GetCustomConditionFromCharacter(
                    rulesetCharacter, out var supportCondition))
            {
                return;
            }

            if (supportCondition.SpellAsMain)
            {
                spellRepertoireLine.relevantSpells.AddRange(spells.FindAll(x =>
                    x.ActivationTime == ActivationTime.Action && x.SpellLevel == 0));
            }

            if (supportCondition.CantripAsMain)
            {
                spellRepertoireLine.relevantSpells.AddRange(spells.FindAll(x =>
                    x.ActivationTime == ActivationTime.Action && x.SpellLevel > 0));
            }
        }
    }

    private class BlastReloadSupportRulesetCondition :
        RulesetConditionCustom<BlastReloadSupportRulesetCondition>, IBindToRulesetConditionCustom
    {
        public bool CantripAsMain;
        public bool SpellAsMain;

        static BlastReloadSupportRulesetCondition()
        {
            Category = AttributeDefinitions.TagEffect;
            Marker = new BlastReloadSupportRulesetCondition();
            BindingDefinition = ConditionDefinitionBuilder
                .Create($"Condition{PatronEldritchSurge.Name}BlastReloadSupport")
                .SetGuiPresentationNoContent(true)
                .SetSilent(Silent.WhenAddedOrRemoved)
                .AddCustomSubFeatures(Marker, new BlastReloadCustom())
                .AddToDB();
        }

        public void ReplaceRulesetCondition(
            RulesetCondition originalRulesetCondition, out RulesetCondition replacedRulesetCondition)
        {
            replacedRulesetCondition = GetFromPoolAndCopyOriginalRulesetCondition(originalRulesetCondition);
        }

        [UsedImplicitly]
        public override void SerializeAttributes(IAttributesSerializer serializer, IVersionProvider versionProvider)
        {
            base.SerializeAttributes(serializer, versionProvider);

            try
            {
                CantripAsMain = serializer.SerializeAttribute("CantripAsMain", CantripAsMain);
                SpellAsMain = serializer.SerializeAttribute("SpellAsMain", SpellAsMain);
            }
            catch (Exception ex)
            {
                Trace.LogException(new Exception(
                    "[TACTICAL INVISIBLE FOR PLAYERS] error with BlastReloadSupportRulesetCondition serialization (may be caused by mods or bad versioning implementation) " +
                    ex.Message, ex));
            }
        }

        protected override void ClearCustomStates()
        {
            CantripAsMain = false;
            SpellAsMain = false;
        }
    }

    private class RechargePoolWhenBattleEnd : ICharacterBattleEndedListener
    {
        public void OnCharacterBattleEnded(GameLocationCharacter locationCharacter)
        {
            GetStateOwner(locationCharacter.RulesetCharacter).UsablePowers.DoIf(x =>
                    x.PowerDefinition == PowerVersatilitySwitch,
                y => y.Recharge());
        }
    }
}
