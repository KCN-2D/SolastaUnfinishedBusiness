using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class TooltipFeatureItemProficiencyPatcher
{
    [HarmonyPatch(typeof(TooltipFeatureCompatibleProficiencies), nameof(TooltipFeatureCompatibleProficiencies.Bind),
        [typeof(ICompatibleProficienciesProvider), typeof(RulesetCharacterHero)])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CompatibleProficienciesBind_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var getChild = AccessTools.Method(typeof(UnityEngine.Transform), nameof(UnityEngine.Transform.GetChild));
            var getGameObject = AccessTools.PropertyGetter(typeof(UnityEngine.Component),
                nameof(UnityEngine.Component.gameObject));
            var setActive = AccessTools.Method(typeof(UnityEngine.GameObject), nameof(UnityEngine.GameObject.SetActive));
            var matches = Enumerable.Range(1, Math.Max(0, codes.Count - 8))
                .Where(i => codes[i].Calls(getChild) && codes[i + 1].Calls(getGameObject) &&
                            codes[i + 2].opcode == OpCodes.Ldc_I4_0 && codes[i + 3].Calls(setActive) &&
                            codes[i + 4].opcode == OpCodes.Ldloc_S && codes[i + 5].opcode == OpCodes.Ldc_I4_1 &&
                            codes[i + 6].opcode == OpCodes.Add && codes[i + 7].opcode == OpCodes.Stloc_S &&
                            Equals(codes[i + 4].operand, codes[i + 7].operand))
                .ToArray();

            if (matches.Length != 1)
            {
                Main.Error("Couldn't patch TooltipFeatureCompatibleProficiencies.Bind surplus rows");

                return codes;
            }

            // The native cleanup repeatedly hides the first unused row. Use the loop index instead,
            // so all surplus rows are hidden before the native layout rebuild.
            var index = matches[0];
            codes[index - 1].opcode = codes[index + 4].opcode;
            codes[index - 1].operand = codes[index + 4].operand;

            return codes;
        }
    }

    [HarmonyPatch(typeof(TooltipFeatureItemProficiency), nameof(TooltipFeatureItemProficiency.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureItemProficiency __instance, ITooltip tooltip)
        {
            var obj = __instance.gameObject;
            if (obj.activeSelf)
            {
                return;
            }

            if (tooltip.DataProvider is not IItemDefinitionProvider data)
            {
                return;
            }

            if (!RecipeHelper.RecipeIsKnown(data.ItemDefinition))
            {
                return;
            }

            __instance.notProficientLabel.Text = "Failure/&FailureFlagRecipeAlreadyKnown";
            obj.SetActive(true);
        }
    }
}
