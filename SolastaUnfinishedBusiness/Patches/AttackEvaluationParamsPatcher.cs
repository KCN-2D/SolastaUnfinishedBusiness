using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class AttackEvaluationParamsPatcher
{
    [HarmonyPatch(typeof(BattleDefinitions.AttackEvaluationParams), nameof(BattleDefinitions.AttackEvaluationParams.FillForMagicDistance))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillForMagicDistance_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            CombinedMetamagic.ReplaceTypeChecks(instructions, MetamagicType.DistantSpell, "BattleDefinitions.AttackEvaluationParams.FillForMagicDistance");
    }


    [HarmonyPatch(typeof(BattleDefinitions.AttackEvaluationParams), nameof(BattleDefinitions.AttackEvaluationParams.FillForMagicRangeAttack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillForMagicRangeAttack_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            CombinedMetamagic.ReplaceTypeChecks(instructions, MetamagicType.DistantSpell, "BattleDefinitions.AttackEvaluationParams.FillForMagicRangeAttack");
    }


    private static void HandlePhysicalAttackRules(BattleDefinitions.AttackEvaluationParams evaluationParams)
    {
        //PATCH: apply flanking rules
        FlankingAndHigherGround.HandleFlanking(evaluationParams);

        //PATCH: apply higher ground rules
        FlankingAndHigherGround.HandleHigherGround(evaluationParams);

        //PATCH: apply small races rules
        RacesContext.HandleSmallRaces(evaluationParams);

        //PATCH: apply 2024 heavy weapon ability requirement
        Tabletop2024Context.HandleHeavyWeaponAbilityRequirement(evaluationParams);
    }

    [HarmonyPatch(typeof(BattleDefinitions.AttackEvaluationParams),
        nameof(BattleDefinitions.AttackEvaluationParams.FillForMagicTouchAttack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillForMagicTouchAttack_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            CombinedMetamagic.ReplaceTypeChecks(instructions, MetamagicType.DistantSpell, "BattleDefinitions.AttackEvaluationParams.FillForMagicTouchAttack", true);


        [UsedImplicitly]
        public static void Postfix(
            // Since `AttackEvaluationParams` is a struct, we need to use ref to get actual object, instead of a copy
            ref BattleDefinitions.AttackEvaluationParams __instance,
            EffectDescription effectDescription,
            MetamagicOptionDefinition metamagicOption)
        {
            //PATCH: allow for `Touch` effects to have reach changed, unless `Distant Spell` metamagic is used
            if (CombinedMetamagic.HasType(metamagicOption, MetamagicType.DistantSpell))
            {
                return;
            }

            __instance.maxRange = Math.Max(effectDescription.rangeParameter, 1f);
        }
    }

    [HarmonyPatch(typeof(BattleDefinitions.AttackEvaluationParams),
        nameof(BattleDefinitions.AttackEvaluationParams.FillForMagicReachAttack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillForMagicReachAttack_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            CombinedMetamagic.ReplaceTypeChecks(instructions, MetamagicType.DistantSpell, "BattleDefinitions.AttackEvaluationParams.FillForMagicReachAttack", true);


        [UsedImplicitly]
        public static void Postfix(
            // Since `AttackEvaluationParams` is a struct, we need to use ref to get actual object, instead of a copy
            ref BattleDefinitions.AttackEvaluationParams __instance,
            EffectDescription effectDescription,
            MetamagicOptionDefinition metamagicOption)
        {
            //PATCH: apply flanking rules
            FlankingAndHigherGround.HandleFlanking(__instance);

            //PATCH: apply higher ground rules
            FlankingAndHigherGround.HandleHigherGround(__instance);

            //PATCH: apply small races rules
            RacesContext.HandleSmallRaces(__instance);

            //PATCH: allow for `MeleeHit` effects to have reach changed, unless `Distant Spell` metamagic is used
            if (CombinedMetamagic.HasType(metamagicOption, MetamagicType.DistantSpell))
            {
                return;
            }

            __instance.maxRange = Math.Max(effectDescription.rangeParameter, 1f);
        }
    }

    [HarmonyPatch(typeof(BattleDefinitions.AttackEvaluationParams),
        nameof(BattleDefinitions.AttackEvaluationParams.FillForPhysicalReachAttack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillForPhysicalReachAttack_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            // Since `AttackEvaluationParams` is a struct, we need to use ref to get actual object, instead of a copy
            ref BattleDefinitions.AttackEvaluationParams __instance)
        {
            HandlePhysicalAttackRules(__instance);
        }
    }

    [HarmonyPatch(typeof(BattleDefinitions.AttackEvaluationParams),
        nameof(BattleDefinitions.AttackEvaluationParams.FillForPhysicalRangeAttack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillForPhysicalRangeAttack_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            // Since `AttackEvaluationParams` is a struct, we need to use ref to get actual object, instead of a copy
            ref BattleDefinitions.AttackEvaluationParams __instance)
        {
            HandlePhysicalAttackRules(__instance);
        }
    }

    [HarmonyPatch(typeof(BattleDefinitions.AttackEvaluationParams),
        nameof(BattleDefinitions.AttackEvaluationParams.ComputeDistance))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeDistance_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ref BattleDefinitions.AttackEvaluationParams __instance)
        {
            //PATCH: use better distance calculation algorithm
            __instance.distance = DistanceCalculation.GetDistanceFromCharacters(__instance.attacker, __instance.defender);

            return false;
        }
    }
}
