using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageIdentityDefinitionPanelPatcher
{
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
            ToolsContext.FunctorRespec.PreserveIdentityAppearanceEdits(__instance);
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
            ToolsContext.FunctorRespec.PreserveIdentityAppearanceEdits(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageIdentityDefinitionPanel __instance)
        {
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
