using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageBackgroundSelectionPanelPatcher
{
    [HarmonyPatch(typeof(CharacterStageBackgroundSelectionPanel),
        nameof(CharacterStageBackgroundSelectionPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterStageBackgroundSelectionPanel __instance)
        {
            //PATCH: avoids a restart when enabling / disabling backgrounds on the Mod UI panel
            __instance.compatibleBackgrounds.Clear();
            __instance.selectedBackgroundPersonalityFlagsMap.Clear();

            foreach (var key in
                     DatabaseRepository.GetDatabase<CharacterBackgroundDefinition>())
            {
                if (key.GuiPresentation.Hidden)
                {
                    continue;
                }

                __instance.compatibleBackgrounds.Add(key);
                __instance.selectedBackgroundPersonalityFlagsMap.Add(key,
                    key.OptionalPersonalityFlags.Count == 2
                        ? [..key.DefaultOptionalPersonalityFlags]
                        : []);
            }

            __instance.compatibleBackgrounds.Sort(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageBackgroundSelectionPanel),
        nameof(CharacterStageBackgroundSelectionPanel.OnFeatureChoiceChangedCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnFeatureChoiceChangedCb_Patch
    {
        [UsedImplicitly]
        public static void Prefix([CanBeNull] FeatureDescriptionItem __0)
        {
            FeatureDescriptionItemPatcher.TryPersistCharacterCreationFeatureSetDefaultSelection(__0);
        }
    }

    [HarmonyPatch(typeof(CharacterStageBackgroundSelectionPanel),
        nameof(CharacterStageBackgroundSelectionPanel.CanProceedToNextStage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanProceedToNextStage_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageBackgroundSelectionPanel __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            __result = !Tabletop2024Context.HasDuplicateHumanOriginFeat(__instance.currentHero, out _);
        }
    }
}
