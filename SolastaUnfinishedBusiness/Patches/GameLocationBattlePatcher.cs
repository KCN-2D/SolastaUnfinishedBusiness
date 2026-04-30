using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationBattlePatcher
{
    private static bool SetInvisibleBaseFeatures(params FeatureDefinition[] features)
    {
        var currentFeatures = DatabaseHelper.ConditionDefinitions.ConditionInvisibleBase.Features;

        if (currentFeatures.Count == features.Length)
        {
            var same = true;

            for (var i = 0; i < features.Length; i++)
            {
                if (currentFeatures[i] == features[i])
                {
                    continue;
                }

                same = false;
                break;
            }

            if (same)
            {
                return false;
            }
        }

        currentFeatures.SetRange(features);

        return true;
    }

    private static void UpdatePerceptionIfInvisibleBaseChanged(params FeatureDefinition[] features)
    {
        if (!SetInvisibleBaseFeatures(features))
        {
            return;
        }

        var service =
            ServiceRepository.GetService<IGameLocationVisibilityService>() as GameLocationVisibilityManager;

        service?.UpdatePerception();
    }

    private static bool TryGetSenseNormalVisionConditionName(out string conditionName)
    {
        var multiplier = Main.Settings.SenseNormalVisionRangeMultiplier;

        if (multiplier <= 0)
        {
            conditionName = null;
            return false;
        }

        conditionName = $"ConditionSenseNormalVision{(multiplier == 1 ? 24 : 48)}";

        return true;
    }

    private static void InflictSenseNormalVisionCondition(GameLocationCharacter character)
    {
        if (!TryGetSenseNormalVisionConditionName(out var conditionName))
        {
            return;
        }

        var rulesetCharacter = character.RulesetCharacter;

        if (rulesetCharacter.HasConditionOfType(conditionName))
        {
            return;
        }

        rulesetCharacter.InflictCondition(
            conditionName,
            RuleDefinitions.DurationType.Irrelevant,
            1,
            RuleDefinitions.TurnOccurenceType.StartOfTurn,
            AttributeDefinitions.TagEffect,
            rulesetCharacter.guid,
            rulesetCharacter.CurrentFaction.Name,
            1,
            conditionName,
            0,
            0,
            0);
    }

    private static void AddSenseNormalVisionConditionWithoutRefresh(GameLocationCharacter character)
    {
        if (!TryGetSenseNormalVisionConditionName(out var conditionName))
        {
            return;
        }

        var rulesetCharacter = character.RulesetCharacter;

        if (rulesetCharacter.HasConditionOfType(conditionName))
        {
            return;
        }

        // don't use InflictCondition here to avoid a too soon character refresh
        var condition = DatabaseRepository.GetDatabase<ConditionDefinition>().GetElement(conditionName);
        var activeCondition = RulesetCondition.CreateActiveCondition(
            character.Guid,
            condition,
            RuleDefinitions.DurationType.Irrelevant,
            1,
            RuleDefinitions.TurnOccurenceType.StartOfTurn,
            character.Guid,
            rulesetCharacter.CurrentFaction.Name,
            effectDefinitionName: conditionName);

        rulesetCharacter.AddConditionOfCategory(AttributeDefinitions.TagEffect, activeCondition, false);
    }

    [HarmonyPatch(typeof(GameLocationBattle), nameof(GameLocationBattle.StartContenders))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartContenders_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            if (!Main.Settings.OfficialObscurementRulesInvisibleCreaturesCanBeTarget)
            {
                return;
            }

            UpdatePerceptionIfInvisibleBaseChanged(
                DatabaseHelper.FeatureDefinitionCombatAffinitys.CombatAffinityInvisible);
        }
    }

    [HarmonyPatch(typeof(GameLocationBattle), nameof(GameLocationBattle.SignalEndToContenders))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SignalEndToContenders_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            if (!Main.Settings.OfficialObscurementRulesInvisibleCreaturesCanBeTarget)
            {
                return;
            }

            UpdatePerceptionIfInvisibleBaseChanged(
                DatabaseHelper.FeatureDefinitionCombatAffinitys.CombatAffinityInvisible,
                DatabaseHelper.FeatureDefinitionPerceptionAffinitys.PerceptionAffinityConditionInvisible);
        }
    }

    //PATCH: EnableEnemiesControlledByPlayer
    [HarmonyPatch(typeof(GameLocationBattle), nameof(GameLocationBattle.GetMyContenders))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetMyContenders_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationBattle __instance, ref List<GameLocationCharacter> __result)
        {
            if (!Main.Settings.EnableEnemiesControlledByPlayer ||
                Global.IsMultiplayer ||
                __instance == null)
            {
                return;
            }

            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

            if (!characterService.PartyCharacters.Contains(__instance.ActiveContender)
                && !characterService.GuestCharacters.Contains(__instance.ActiveContender))
            {
                __result = __instance.EnemyContenders;
            }
        }
    }

    //PATCH: EnableEnemiesControlledByPlayer
    [HarmonyPatch(typeof(GameLocationBattle), nameof(GameLocationBattle.GetOpposingContenders))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetOpposingContenders_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationBattle __instance, ref List<GameLocationCharacter> __result)
        {
            if (!Main.Settings.EnableEnemiesControlledByPlayer ||
                Global.IsMultiplayer ||
                __instance == null)
            {
                return;
            }

            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

            if (!characterService.PartyCharacters.Contains(__instance.ActiveContender)
                && !characterService.GuestCharacters.Contains(__instance.ActiveContender))
            {
                __result = __instance.PlayerContenders;
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationBattle), nameof(GameLocationBattle.RollInitiative))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RollInitiative_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, GameLocationBattle __instance)
        {
            while (values.MoveNext())
            {
                yield return values.Current;
            }

            var contenders = new List<GameLocationCharacter>(__instance.InitiativeSortedContenders);

            foreach (var character in contenders)
            {
                //PATCH: supports `SenseNormalVisionRangeMultiplier`
                InflictSenseNormalVisionCondition(character);

                //PATCH: mainly supports Thief level 17th through ICharacterInitiativeEndListener interface
                var features = character.RulesetCharacter.GetSubFeaturesByType<IInitiativeEndListener>();

                foreach (var feature in features)
                {
                    yield return feature.OnInitiativeEnded(character);
                }
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationBattle), nameof(GameLocationBattle.StartFirstTurnOfRound))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationBattle_StartFirstTurnOfRound
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            Global.RolledPerceptionThisTurn.Clear();
            if (!Main.Settings.EnableInitiativeRollOnEveryRoundStart ||
                Global.IsMultiplayer ||
                Gui.Battle == null ||
                Gui.Battle.CurrentRound < 2)
            {
                return;
            }

            Gui.Battle.initiativeSortedContenders.Clear();
            Gui.Battle.initiativeByName.Clear();
            Gui.Battle.initiativeFirstRollByName.Clear();
            Gui.Battle.initiativeModifierByName.Clear();

            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var playerContender in Gui.Battle.playerContenders)
            {
                if (!playerContender.RulesetActor.IsDead)
                {
                    Gui.Battle.initiativeSortedContenders.Add(playerContender);
                }
            }

            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var enemyContender in Gui.Battle.enemyContenders)
            {
                if (!enemyContender.RulesetActor.IsDead)
                {
                    Gui.Battle.initiativeSortedContenders.Add(enemyContender);
                }
            }

            foreach (var initiativeSortedContender in Gui.Battle.initiativeSortedContenders)
            {
                Gui.Battle.ProcessContenderInitiative(initiativeSortedContender);
            }

            Gui.Battle.initiativeSortedContenders.Sort(Gui.Battle);
            Gui.GuiService.GetScreen<GameLocationScreenBattle>().initiativeTable.Refresh();
        }
    }

    //PATCH: supports `SenseNormalVisionRangeMultiplier`
    [HarmonyPatch(typeof(GameLocationBattle), nameof(GameLocationBattle.IntroduceNewContender))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IntroduceNewContender_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter character)
        {
            AddSenseNormalVisionConditionWithoutRefresh(character);
        }
    }
}
