using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ProficienciesPanelPatcher
{
    [HarmonyPatch(typeof(ProficienciesPanel), nameof(ProficienciesPanel.Bind))]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ProficienciesPanel __instance, RulesetCharacterHero __0)
        {
            CharacterInspectionScreenEnhancement.InvalidateProficiencyBindRefresh(__instance);
            // Display settings can change after the screen's initial RuntimeLoaded.
            CustomInvocationSubPanel.AddCustomSubPanels(__instance, __0);
            PermanentDefensesSubPanel.AddSubPanel(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(ProficienciesPanel __instance)
        {
            CharacterInspectionScreenEnhancement.CompleteProficiencyBindRefresh(__instance);
        }
    }

    [HarmonyPatch(typeof(ProficienciesPanel), nameof(ProficienciesPanel.Unbind))]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ProficienciesPanel __instance)
        {
            CharacterInspectionScreenEnhancement.InvalidateProficiencyBindRefresh(__instance);
        }
    }

    [HarmonyPatch(typeof(ProficienciesPanel), nameof(ProficienciesPanel.OnBeginShow))]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var refresh = new System.Action<GuiPanel>(CharacterInspectionScreenEnhancement.RefreshProficienciesOnShow)
                .Method;
            return instructions.ReplaceCalls(typeof(GuiPanel).GetMethod(nameof(GuiPanel.RefreshNow)),
                "ProficienciesPanel.OnBeginShow", new CodeInstruction(OpCodes.Call, refresh));
        }

        [UsedImplicitly]
        public static void Finalizer(ProficienciesPanel __instance)
        {
            // An exception before the replaced call must not leave a pending token.
            CharacterInspectionScreenEnhancement.InvalidateProficiencyBindRefresh(__instance);
        }
    }

    [HarmonyPatch(typeof(ProficienciesPanel), nameof(ProficienciesPanel.Refresh))]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ProficienciesPanel __instance)
        {
            CharacterInspectionScreenEnhancement.InvalidateProficiencyBindRefresh(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(ProficienciesPanel __instance)
        {
            // Native Refresh filters before binding. A changed hero or display option
            // can change a section's visibility during Bind, so refresh its toggle too.
            __instance.FilterToggles();
        }
    }

    [HarmonyPatch(typeof(ProficienciesPanel), nameof(ProficienciesPanel.RuntimeLoaded))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RuntimeLoaded_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ProficienciesPanel __instance)
        {
            //PATCH: support for custom invocations
            //adds separate sub-panels for each custom invocation type
            CustomInvocationSubPanel.AddCustomSubPanels(__instance);
            PermanentDefensesSubPanel.AddSubPanel(__instance);
        }
    }

    [HarmonyPatch]
    [UsedImplicitly]
    public static class UnloadMoveNext_Patch
    {
        [UsedImplicitly]
        private static MethodBase TargetMethod()
        {
            var iterator = typeof(ProficienciesPanel).GetNestedTypes(BindingFlags.NonPublic)
                .Single(type => type.Name.StartsWith($"<{nameof(ProficienciesPanel.Unload)}>d__"));
            return AccessTools.Method(iterator, "MoveNext");
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getComponent = typeof(Component).GetMethods().Single(method =>
                method.Name == nameof(Component.GetComponent) && method.IsGenericMethodDefinition &&
                method.GetParameters().Length == 0);

            // Native Load creates ProficiencyToggle, but Unload asks for the separate
            // CharacterInspectionToggle component. Keep the same ownership as Load.
            return instructions.ReplaceCalls(getComponent.MakeGenericMethod(typeof(CharacterInspectionToggle)),
                    "ProficienciesPanel.Unload.ToggleType",
                    new CodeInstruction(OpCodes.Callvirt, getComponent.MakeGenericMethod(typeof(ProficiencyToggle))))
                .ReplaceCalls(typeof(CharacterInspectionToggle).GetMethod(nameof(CharacterInspectionToggle.Unbind)),
                    "ProficienciesPanel.Unload.ToggleUnbind",
                    new CodeInstruction(OpCodes.Callvirt,
                        typeof(ProficiencyToggle).GetMethod(nameof(ProficiencyToggle.Unbind))));
        }
    }

    [HarmonyPatch(typeof(ProficienciesPanel), nameof(ProficienciesPanel.Unload))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unload_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ProficienciesPanel __instance)
        {
            CharacterInspectionScreenEnhancement.InvalidateProficiencyBindRefresh(__instance);
        }

        [UsedImplicitly]
        public static IEnumerator Postfix(
            [NotNull] IEnumerator values,
            ProficienciesPanel __instance)
        {
            var addedPanels = __instance.subPanels.Where(panel =>
                panel.GetComponent<CustomInvocationSubPanel>() ||
                panel.GetComponent<PermanentDefensesSubPanel>()).ToArray();

            try
            {
                foreach (var panel in addedPanels)
                {
                    foreach (var pair in __instance.subPanelsMap.Where(pair => pair.Value == panel))
                    {
                        pair.Key.Unbind();
                    }

                    yield return panel.Unload();
                }

                while (values.MoveNext())
                {
                    yield return values.Current;
                }
            }
            finally
            {
                // A subsequent Load recreates only the native sections. Keep no stale
                // clones whose toggles have been released by native Unload.
                __instance.subPanels = __instance.subPanels.Except(addedPanels).ToArray();
                foreach (var panel in addedPanels)
                {
                    UnityEngine.Object.Destroy(panel.gameObject);
                }
            }
        }
    }
}
