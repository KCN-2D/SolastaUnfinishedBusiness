using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionRestHealingModifiers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPointPools;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly FeatureDefinitionPower PowerBardFontOfInspiration = FeatureDefinitionPowerBuilder
        .Create("PowerBardFontOfInspiration")
        .SetGuiPresentation("Feature/&BardFontOfInspirationTitle", "Feature/&BardFontOfInspiration2024Description")
        .SetUsesFixed(ActivationTime.NoCost)
        .SetEffectDescription(EffectDescriptionBuilder.Create()
            .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
            .Build())
        .AddCustomSubFeatures(new CustomBehaviorBardFontOfInspiration())
        .AddToDB();

    private static readonly ConditionDefinition ConditionBardCounterCharmSavingThrowAdvantage =
        ConditionDefinitionBuilder
            .Create("ConditionBardCounterCharmSavingThrowAdvantage")
            .SetGuiPresentation(PowerBardCountercharm.GuiPresentation)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetFeatures(
                FeatureDefinitionSavingThrowAffinityBuilder
                    .Create(FeatureDefinitionSavingThrowAffinitys.SavingThrowAffinityAdvantageToAll,
                        "SavingThrowAffinityBardCounterCharmAdvantage")
                    .SetGuiPresentation(PowerBardCountercharm.GuiPresentation)
                    .AddToDB())
            .SetSpecialInterruptions(ConditionInterruption.SavingThrow)
            .AddToDB();

    private static readonly FeatureDefinitionFeatureSet FeatureSetBardMagicalSecrets =
        FeatureDefinitionFeatureSetBuilder
            .Create("FeatureSetBardMagicalSecrets")
            .SetGuiPresentation(Category.Feature)
            .SetFeatureSet(
                FeatureDefinitionMagicAffinityBuilder
                    .Create("MagicAffinityBardMagicalSecretsCleric")
                    .SetGuiPresentationNoContent(true)
                    .SetExtendedSpellList(SpellListDefinitions.SpellListCleric)
                    .AddToDB(),
                FeatureDefinitionMagicAffinityBuilder
                    .Create("MagicAffinityBardMagicalSecretsDruid")
                    .SetGuiPresentationNoContent(true)
                    .SetExtendedSpellList(SpellListDefinitions.SpellListDruid)
                    .AddToDB(),
                FeatureDefinitionMagicAffinityBuilder
                    .Create("MagicAffinityBardMagicalSecretsWizard")
                    .SetGuiPresentationNoContent(true)
                    .SetExtendedSpellList(SpellListDefinitions.SpellListWizard)
                    .AddToDB())
            .AddToDB();

    private static void LoadBardCounterCharm()
    {
        PowerBardCountercharm.AddCustomSubFeatures(
            new ModifyPowerVisibility((_, _, _) => !Main.Settings.EnableBardCounterCharm2024),
            new TryAlterOutcomeSavingThrowBardCounterCharm());
    }

    private static void LoadBardBardicInspiration()
    {
        PowerBardGiveBardicInspiration.GuiPresentation.description =
            "Feature/&PowerBardGiveBardicInspirationConciseDescription";
    }

    internal static void SwitchBardCounterCharm()
    {
        var level = Main.Settings.EnableBardCounterCharm2024 ? 7 : 6;

        Bard.FeatureUnlocks.FirstOrDefault(x => x.FeatureDefinition == PowerBardCountercharm)!.level = level;
        if (Main.Settings.EnableBardCounterCharm2024)
        {
            PowerBardCountercharm.GuiPresentation.description = "Feature/&PowerBardCountercharmExtendedDescription";
            PowerBardCountercharm.activationTime = ActivationTime.NoCost;
        }
        else
        {
            PowerBardCountercharm.GuiPresentation.description = "Feature/&PowerBardCountercharmDescription";
            PowerBardCountercharm.activationTime = ActivationTime.Action;
        }

        Bard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchBardExpertiseOneLevelBefore()
    {
        var level = Main.Settings.EnableBardExpertiseOneLevelBefore2024 ? 2 : 3;

        foreach (var featureUnlock in Bard.FeatureUnlocks
                     .Where(x => x.FeatureDefinition == PointPoolBardExpertiseLevel3))
        {
            featureUnlock.level = level;
        }

        level = Main.Settings.EnableBardExpertiseOneLevelBefore2024 ? 9 : 10;

        foreach (var featureUnlock in Bard.FeatureUnlocks
                     .Where(x => x.FeatureDefinition == PointPoolBardExpertiseLevel10))
        {
            featureUnlock.level = level;
        }

        if (Main.Settings.EnableBardExpertiseOneLevelBefore2024)
        {
            PointPoolBardExpertiseLevel3.GuiPresentation.description = "Feature/&BardExpertiseExtendedDescription";
            PointPoolBardExpertiseLevel10.GuiPresentation.description = "Feature/&BardExpertiseExtendedDescription";
        }
        else
        {
            PointPoolBardExpertiseLevel3.GuiPresentation.description = "Feature/&BardExpertiseDescription";
            PointPoolBardExpertiseLevel10.GuiPresentation.description = "Feature/&BardExpertiseDescription";
        }

        Bard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchBardBardicInspiration()
    {
        Bard.FeatureUnlocks.RemoveAll(unlock => unlock.FeatureDefinition == PowerBardFontOfInspiration);

        if (Main.Settings.EnableBardicInspiration2024)
        {
            Bard.FeatureUnlocks.Add(new FeatureUnlockByLevel(PowerBardFontOfInspiration, 5));
        }

        Bard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        if (Main.Settings.EnableBardicInspiration2024)
        {
            ConditionDefinitions.ConditionBardicInspiration.durationType = DurationType.Hour;
            ConditionDefinitions.ConditionBardicInspiration.durationParameter = 1;
        }
        else
        {
            ConditionDefinitions.ConditionBardicInspiration.durationType = DurationType.Minute;
            ConditionDefinitions.ConditionBardicInspiration.durationParameter = 10;
        }
    }

    internal static void SwitchBardSongOfRest()
    {
        Bard.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == RestHealingModifierBardSongOfRest);

        if (!Main.Settings.RemoveBardSongOfRest2024)
        {
            Bard.FeatureUnlocks.Add(new FeatureUnlockByLevel(RestHealingModifierBardSongOfRest, 2));
        }

        Bard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchBardBardMagicalSecrets()
    {
        Bard.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == FeatureSetBardMagicalSecrets ||
            x.FeatureDefinition == PointPoolBardMagicalSecrets10 ||
            x.FeatureDefinition == PointPoolBardMagicalSecrets14 ||
            x.FeatureDefinition == Level20Context.PointPoolBardMagicalSecrets18);

        if (Main.Settings.EnableBardMagicalSecrets2024)
        {
            Bard.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureSetBardMagicalSecrets, 10));
        }
        else
        {
            Bard.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PointPoolBardMagicalSecrets10, 10),
                new FeatureUnlockByLevel(PointPoolBardMagicalSecrets14, 14),
                new FeatureUnlockByLevel(Level20Context.PointPoolBardMagicalSecrets18, 18));
        }

        Bard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchBardSuperiorInspiration()
    {
        Bard.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == Level20Context.FeatureBardSuperiorInspiration ||
            x.FeatureDefinition == Level20Context.FeatureBardSuperiorInspiration2024);

        Bard.FeatureUnlocks.Add(
            Main.Settings.EnableBardSuperiorInspiration2024
                ? new FeatureUnlockByLevel(Level20Context.FeatureBardSuperiorInspiration2024, 18)
                : new FeatureUnlockByLevel(Level20Context.FeatureBardSuperiorInspiration, 20));

        Bard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchBardWordsOfCreation()
    {
        Bard.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == Level20Context.AutoPreparedSpellsBardWordOfCreation);

        if (Main.Settings.EnableBardWordsOfCreation2024)
        {
            Bard.FeatureUnlocks.Add(
                new FeatureUnlockByLevel(Level20Context.AutoPreparedSpellsBardWordOfCreation, 20));
        }

        Bard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private sealed class CustomBehaviorBardFontOfInspiration : IValidatePowerUse, IPowerOrSpellFinishedByMe
    {
        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
        {
            return Main.Settings.EnableBardicInspiration2024 && character.usedBardicInspiration > 0 &&
                   character.GetClassSpellRepertoire(Bard)?.AtLeastOneSpellSlotAvailable() == true;
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            var character = action.ActingCharacter;
            var rulesetCharacter = character.RulesetCharacter;
            var battleManager = ServiceRepository.GetService<IGameLocationBattleService>() as GameLocationBattleManager;
            var actionService = ServiceRepository.GetService<IGameLocationActionService>();

            if (battleManager == null || actionService == null ||
                !CanUsePower(rulesetCharacter, PowerBardFontOfInspiration))
            {
                yield break;
            }

            var count = actionService.PendingReactionRequestGroups.Count;
            var reactionParams = new CharacterActionParams(character, Id.SpendSpellSlot)
            {
                IntParameter = 1,
                StringParameter = "FontOfInspiration",
                SpellRepertoire = rulesetCharacter.GetClassSpellRepertoire(Bard)
            };

            actionService.ReactToSpendSpellSlot(reactionParams);
            yield return battleManager.WaitForReactions(character, actionService, count);

            // The native slot-spending action owns payment, including shared and pact slots.
            // Cancelling the selection spends neither a slot nor an action.
            if (!reactionParams.ReactionValidated)
            {
                yield break;
            }

            rulesetCharacter.usedBardicInspiration--;
            rulesetCharacter.BardicInspirationAltered?.Invoke(rulesetCharacter,
                rulesetCharacter.RemainingBardicInspirations);
            rulesetCharacter.LogCharacterUsedPower(PowerBardFontOfInspiration);
        }
    }

    private sealed class TryAlterOutcomeSavingThrowBardCounterCharm : ITryAlterOutcomeSavingThrow
    {
        public IEnumerator OnTryAlterOutcomeSavingThrow(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper,
            SavingThrowData savingThrowData,
            bool hasHitVisual)
        {
            if (!Main.Settings.EnableBardCounterCharm2024)
            {
                yield break;
            }

            if (savingThrowData.SaveOutcome != RollOutcome.Success &&
                !helper.IsOppositeSide(defender.Side) &&
                helper.CanReact() &&
                helper.IsWithinRange(defender, 6) &&
                HasCharmedOrFrightened(savingThrowData.EffectDescription.EffectForms))
            {
                yield return helper.MyReactToDoNothing(
                    ExtraActionId.DoNothingFree, // cannot use DoNothingReaction here as we reroll in validate
                    defender,
                    "BardCounterCharm",
                    FormatReactionDescription(savingThrowData.Title, attacker, defender, helper),
                    ReactionValidated);
            }

            yield break;

            static bool HasCharmedOrFrightened(List<EffectForm> effectForms)
            {
                return effectForms.GetAppliedConditionDefinitions().Any(condition =>
                    condition.IsSubtypeOf(ConditionDefinitions.ConditionCharmed.Name) ||
                    condition.IsSubtypeOf(ConditionDefinitions.ConditionFrightened.Name));
            }

            void ReactionValidated()
            {
                var rulesetDefender = defender.RulesetCharacter;

                rulesetDefender.InflictCondition(
                    ConditionBardCounterCharmSavingThrowAdvantage.Name,
                    DurationType.Round,
                    1,
                    TurnOccurenceType.StartOfTurn,
                    AttributeDefinitions.TagEffect,
                    rulesetDefender.guid,
                    rulesetDefender.CurrentFaction.Name,
                    1,
                    ConditionBardCounterCharmSavingThrowAdvantage.Name,
                    0,
                    0,
                    0);

                // we need to manually spend the reaction here as rolling the saving again below
                helper.SpendActionType(ActionType.Reaction);
                helper.RulesetCharacter.LogCharacterUsedPower(PowerBardCountercharm);
                EffectHelpers.StartVisualEffect(
                    helper, defender, PowerBardCountercharm, EffectHelpers.EffectType.Caster);
                TryAlterOutcomeSavingThrow.TryRerollSavingThrow(attacker, defender, savingThrowData, hasHitVisual);
            }
        }

        private static string FormatReactionDescription(
            string sourceTitle,
            [CanBeNull] GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper)
        {
            var text = defender == helper ? "Self" : "Ally";

            return $"CustomReactionBardCounterCharmDescription{text}".Formatted(
                Category.Reaction, defender.Name, attacker?.Name ?? ReactionRequestCustom.EnvTitle, sourceTitle);
        }
    }
}
