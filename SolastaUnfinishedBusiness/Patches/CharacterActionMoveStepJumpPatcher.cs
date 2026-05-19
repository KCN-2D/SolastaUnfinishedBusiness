using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using TA;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionMoveStepJumpPatcher
{
    //PATCH: support for reach-entered AoO after jump movement
    [HarmonyPatch(typeof(CharacterActionMoveStepJump), nameof(CharacterActionMoveStepJump.ExecuteImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteImpl_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, CharacterActionMoveStepJump __instance)
        {
            var mover = __instance.ActingCharacter;
            var source = __instance.jumpPosition;
            var landing = __instance.landingPosition;

            while (values.MoveNext())
            {
                yield return values.Current;
            }

            if (!TryGetCompletedJumpMovement(mover, source, landing, out var movement))
            {
                yield break;
            }

            var extraAoOEvents = AttacksOfOpportunity.ProcessOnCharacterMoveEnd(mover, movement);

            while (extraAoOEvents.MoveNext())
            {
                yield return extraAoOEvents.Current;
            }
        }

        private static bool TryGetCompletedJumpMovement(
            GameLocationCharacter mover,
            int3 source,
            int3 landing,
            out (int3 from, int3 to) movement)
        {
            if (mover == null)
            {
                movement = (int3.invalid, int3.invalid);

                return false;
            }

            var destination = mover.LocationPosition;

            if (destination == int3.invalid)
            {
                destination = landing;
            }

            movement = (source, destination);

            return source != int3.invalid &&
                   destination != int3.invalid &&
                   source != destination;
        }
    }

    //PATCH: allow check reactions on jump checks regardless of success / failure
    [HarmonyPatch(typeof(CharacterActionMoveStepJump), nameof(CharacterActionMoveStepJump.RollChecksIfNecessary))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RollChecksIfNecessary_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ref IEnumerator __result, CharacterActionMoveStepJump __instance)
        {
            __result = Process(__instance);

            return false;
        }

        private static IEnumerator Process(CharacterActionMoveStepJump action)
        {
            var actingCharacter = action.ActingCharacter;
            var actionModifier = action.ActionParams.ActionModifiers[0];
            RuleDefinitions.AdvantageType BASE_AFFINITY = RuleDefinitions.AdvantageType.None;

            bool isWearingHeavy = actingCharacter.RulesetCharacter.IsWearingHeavyArmor() && Main.Settings.ModifyJumpRulesForArmorAndEncumberance;
            var isActiveFreeJump = FreeJumpContext.IsActiveFreeJumpMove(
                actingCharacter,
                action.jumpPosition,
                action.landingPosition);

            //adjust for wearing heavy armor                
            if (isWearingHeavy)
                BASE_AFFINITY = RuleDefinitions.AdvantageType.Disadvantage;

            if (CharacterActionMoveStepJump.NeedsAcrobaticsCheck(action.landingPosition))
            {
                const int CHECK_DC = 10;

                yield return RollJumpAbilityCheck(
                    action,
                    actingCharacter,
                    actionModifier,
                    AttributeDefinitions.Dexterity,
                    SkillDefinitions.Acrobatics,
                    CHECK_DC,
                    BASE_AFFINITY);
            }

            if (action.AbilityCheckRollOutcome != RuleDefinitions.RollOutcome.Failure &&
                FreeJumpContext.TryComputeFreeJumpPreview(
                    actingCharacter,
                    action.jumpPosition,
                    action.landingPosition,
                    isActiveFreeJump,
                    out var jumpPreview))
            {
                if (jumpPreview.RequiresAthleticsCheck)
                {
                    yield return RollJumpAbilityCheck(
                        action,
                        actingCharacter,
                        actionModifier,
                        AttributeDefinitions.Strength,
                        SkillDefinitions.Athletics,
                        jumpPreview.CheckDc,
                        jumpPreview.Affinity);
                }
                else if (jumpPreview.IsAutomaticFailure)
                {
                    action.AbilityCheckRollOutcome = RuleDefinitions.RollOutcome.Failure;
                }
            }
        }

        private static IEnumerator RollJumpAbilityCheck(
            CharacterActionMoveStepJump action,
            GameLocationCharacter actingCharacter,
            ActionModifier actionModifier,
            string abilityScoreName,
            string proficiencyName,
            int checkDc,
            RuleDefinitions.AdvantageType baseAffinity)
        {
            var abilityCheckRoll = actingCharacter.RollAbilityCheckEx(
                abilityScoreName,
                proficiencyName,
                checkDc,
                baseAffinity,
                actionModifier,
                false,
                -1,
                out var outcome,
                out var successDelta,
                out var rawRoll,
                true);

            var abilityCheckData = new AbilityCheckData
            {
                AbilityCheckRoll = abilityCheckRoll,
                AbilityCheckRollOutcome = outcome,
                AbilityCheckSuccessDelta = successDelta,
                AbilityCheckActionModifier = actionModifier,
                Action = action
            };

            yield return TryAlterOutcomeAttributeCheck
                .HandleITryAlterOutcomeAttributeCheck(actingCharacter, abilityCheckData, rawRoll);

            action.AbilityCheckRoll = abilityCheckData.AbilityCheckRoll;
            action.AbilityCheckRollOutcome = abilityCheckData.AbilityCheckRollOutcome;
            action.AbilityCheckSuccessDelta = abilityCheckData.AbilityCheckSuccessDelta;
        }
    }
}
