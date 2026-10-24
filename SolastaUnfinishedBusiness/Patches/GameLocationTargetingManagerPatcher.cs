using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationTargetingManagerPatcher
{
    [HarmonyPatch]
    [UsedImplicitly]
    public static class ComputeTargetsOfAreaOfEffect_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods() =>
            typeof(GameLocationTargetingManager).GetMethods(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).Where(method =>
                method.Name.EndsWith(".ComputeTargetsOfAreaOfEffect", System.StringComparison.Ordinal) &&
                method.GetParameters().Any(parameter => parameter.ParameterType == typeof(EffectDescription)));

        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter sourceCharacter, EffectDescription effectDescription,
            List<GameLocationCharacter> affectedCharacters, ref int __result)
        {
            __result -= TargetingContext.FilterExpandedTargets(sourceCharacter, effectDescription, affectedCharacters);
        }
    }

    [HarmonyPatch(typeof(GameLocationTargetingManager),
        "IGameLocationTargetingService.CollectPerceivingTargetsWithinDistance")]
    [UsedImplicitly]
    public static class CollectPerceivingTargetsWithinDistance_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter caster, EffectDescription effectDescription,
            List<GameLocationCharacter> targets, List<ActionModifier> actionModifiers, ref int __result)
        {
            __result -= TargetingContext.FilterExpandedTargets(caster, effectDescription, targets, actionModifiers);
        }
    }

    [HarmonyPatch(typeof(GameLocationTargetingManager), "IGameLocationTargetingService.ComputeAndSortSubtargets")]
    [UsedImplicitly]
    public static class ComputeAndSortSubtargets_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            instructions = instructions.ReplaceCalls(
                AccessTools.Method(typeof(RulesetEffect), nameof(RulesetEffect.ComputeTargetParameter)), 1,
                "GameLocationTargetingManager.ComputeAndSortSubtargets.SelectionCandidates",
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TargetingContext),
                    nameof(TargetingContext.GetSubtargetCandidateLimit))));

            instructions = instructions.ReplaceCalls(
                AccessTools.Method(typeof(List<GameLocationCharacter>), nameof(List<GameLocationCharacter>.Add)), 1,
                "GameLocationTargetingManager.ComputeAndSortSubtargets.Candidates",
                new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ComputeAndSortSubtargets_Patch),
                    nameof(AddSubtarget))));

            return instructions.ReplaceCalls(
                AccessTools.Method(typeof(GameLocationCharacter), nameof(GameLocationCharacter.IsOppositeSide)), 1,
                "GameLocationTargetingManager.ComputeAndSortSubtargets.Side",
                new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ComputeAndSortSubtargets_Patch),
                    nameof(IsSubtargetAllowed))));
        }

        private static void AddSubtarget(List<GameLocationCharacter> targets, GameLocationCharacter target,
            GameLocationCharacter caster, RulesetEffect effect)
        {
            if (IsSubtargetAllowed(target, caster.Side, caster, effect))
            {
                targets.Add(target);
            }
        }

        private static bool IsSubtargetAllowed(GameLocationCharacter target, RuleDefinitions.Side casterSide,
            GameLocationCharacter caster, RulesetEffect effect) =>
            TargetingContext.IsValidOffensiveTarget(caster, target, effect.SourceDefinition,
                effect.EffectDescription);
    }

    //PATCH: UseHeightOneCylinderEffect
    [HarmonyPatch(typeof(GameLocationTargetingManager), nameof(GameLocationTargetingManager.BuildAABB))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BuildAABB_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationTargetingManager __instance)
        {
            if (!Main.Settings.UseHeightOneCylinderEffect)
            {
                return;
            }

            if (__instance.shapeType != MetricsDefinitions.GeometricShapeType.Cube)
            {
                return;
            }

            if (__instance.geometricParameter2 <= 0)
            {
                return;
            }

            var edgeSize = __instance.geometricParameter;
            var height = __instance.geometricParameter2;

            Vector3 vector = new();

            if (__instance.hasMagneticTargeting || __instance.rangeType == RuleDefinitions.RangeType.Self)
            {
                if (edgeSize % 2.0 == 0.0)
                {
                    vector = new Vector3(0.5f, 0f, 0.5f);
                }

                if (height % 2.0 == 0.0)
                {
                    vector.y = 0.5f;
                }
            }
            else
            {
                vector = new Vector3(0.0f, (float)((0.5 * height) - 0.5), 0.0f);

                if (edgeSize % 2.0 == 0.0)
                {
                    vector += new Vector3(0.5f, 0.0f, 0.5f);
                }
            }

            __instance.bounds = new Bounds(__instance.origin + vector, new Vector3(edgeSize, height, edgeSize));
        }
    }

    //PATCH: UseHeightOneCylinderEffect
    [HarmonyPatch(typeof(GameLocationTargetingManager), nameof(GameLocationTargetingManager.DoesShapeContainPoint))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DoesShapeContainPoint_Patch
    {
        [UsedImplicitly]
        public static bool MyCubeContainsPoint_Regular(
            Vector3 cubeOrigin,
            float edgeSize,
            int targetSize,
            bool hasMagneticTargeting,
            Vector3 point,
            float height)
        {
            var result =
                GeometryUtils.CubeContainsPoint_Regular(cubeOrigin, edgeSize, targetSize, hasMagneticTargeting, point);

            if (!Main.Settings.UseHeightOneCylinderEffect)
            {
                return result;
            }

            if (height == 0)
            {
                return result;
            }

            // Code from CubeContainsPoint_Regular modified with height
            var vector3 = new Vector3();

            if (hasMagneticTargeting)
            {
                if (edgeSize % 2.0 == 0.0)
                {
                    vector3 = new Vector3(0.5f, 0f, 0.5f);
                }

                if (height % 2.0 == 0.0)
                {
                    vector3.y = 0.5f;
                }
            }
            else
            {
                vector3.y = (float)((0.5 * height) - 0.5);

                if (edgeSize % 2.0 == 0.0)
                {
                    vector3 += new Vector3(0.5f, 0.0f, 0.5f);
                }
            }

            var vector32 = point - cubeOrigin - vector3;

            result =
                Mathf.Abs(vector32.x) <= (double)0.5f * edgeSize
                && Mathf.Abs(vector32.y) <= (double)0.5f * height
                && Mathf.Abs(vector32.z) <= (double)0.5f * edgeSize;

            return result;
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var geometricParameter2Field =
                typeof(GameLocationTargetingManager).GetField("geometricParameter2",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            var cubeContainsPointRegularMethod = typeof(GeometryUtils).GetMethod("CubeContainsPoint_Regular");
            var myCubeContainsPointRegularMethod =
                typeof(DoesShapeContainPoint_Patch).GetMethod("MyCubeContainsPoint_Regular");

            return instructions.ReplaceCalls(cubeContainsPointRegularMethod,
                "CursorLocationGeometricShape.DoesShapeContainPoint",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, geometricParameter2Field),
                new CodeInstruction(OpCodes.Call, myCubeContainsPointRegularMethod));
        }

        [HarmonyPatch(typeof(CursorLocationGeometricShape), nameof(CursorLocationGeometricShape.RefreshHover))]
        [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
        [UsedImplicitly]
        public static class RefreshHover_Patch
        {
            [UsedImplicitly]
            public static void Postfix(CursorLocationGeometricShape __instance)
            {
                __instance.affectedCharacterColor =
                    CampaignsContext.HighContrastColors[Main.Settings.HighContrastTargetingAoeSelectedColor];
            }
        }
    }
}
