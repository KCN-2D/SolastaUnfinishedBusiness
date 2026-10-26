using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageIdentityDefinitionPanelPatcher
{
    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), "AfterMorphotypeChanged")]
    [UsedImplicitly]
    public static class AfterMorphotypeChanged_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterStageIdentityDefinitionPanel __instance,
            MorphotypeElementDefinition.ElementCategory elementCategory)
        {
            if (elementCategory != MorphotypeElementDefinition.ElementCategory.Musculature)
            {
                return true;
            }

            var viewport = __instance.CommonData.CharacterViewportCustomization;

            viewport.ActivateCamera(1, CharacterViewport.Target.Center);
            viewport.ReturnsToInitialPosition = false;

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.Refresh))]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        private static void SetFirstName(RulesetCharacterHero hero, string value) =>
            hero.Name = ToolsContext.FunctorRespec.ResolveIdentityInputName(hero, value, false);

        private static void SetLastName(RulesetCharacterHero hero, string value) =>
            hero.SurName = ToolsContext.FunctorRespec.ResolveIdentityInputName(hero, value, true);

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.PropertySetter(typeof(RulesetEntity), nameof(RulesetEntity.Name)),
                    1, "CharacterStageIdentityDefinitionPanel.Refresh.Name",
                    new CodeInstruction(OpCodes.Call, new Action<RulesetCharacterHero, string>(SetFirstName).Method))
                .ReplaceCalls(AccessTools.PropertySetter(typeof(RulesetCharacterHero), nameof(RulesetCharacterHero.SurName)),
                    1, "CharacterStageIdentityDefinitionPanel.Refresh.SurName",
                    new CodeInstruction(OpCodes.Call, new Action<RulesetCharacterHero, string>(SetLastName).Method));

        [UsedImplicitly]
        public static void Postfix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.RefreshIdentityNames(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.EnterStage))]
    [UsedImplicitly]
    public static class EnterStage_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.RestoreAppearance(__instance.CharacterBuildingService, __instance.currentHero);
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.OnBeginShow))]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.CaptureIdentityAppearanceBaseline(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.CancelStage))]
    [UsedImplicitly]
    public static class CancelStage_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.PreserveIdentityEdits(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel),
        nameof(CharacterStageIdentityDefinitionPanel.OnSexToggleChangedCb))]
    [UsedImplicitly]
    public static class OnSexToggleChangedCb_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.PreserveIdentityEdits(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.RestoreIdentityNames(__instance);
            ToolsContext.FunctorRespec.CaptureIdentityAppearanceBaseline(__instance);
        }
    }

    //PATCH: avoids issues during RESPEC if a hero with same name is in the pool
    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel),
        nameof(CharacterStageIdentityDefinitionPanel.CanProceedToNextStage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanProceedToNextStage_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            CharacterStageIdentityDefinitionPanel __instance, ref string failureString, ref bool __result)
        {
            var name = __instance.currentHero.Name;

            if (__result || !ToolsContext.FunctorRespec.IsOriginalName(__instance.currentHero, name) ||
                failureString != Gui.Localize("Stage/&IdentityDefinitionStageFailDuplicateName"))
            {
                return;
            }

            failureString = string.Empty;
            __result = true;
        }
    }
}
