using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageDeitySelectionPanelPatcher
{
    private static void ClearDeitySubclassSelection([NotNull] CharacterStageDeitySelectionPanel __instance)
    {
        // Native EnterStage identifies domains by suffix even when deity filtering is disabled.
        __instance.hasDomain = false;
        __instance.compatibleSubclasses.Clear();
        __instance.selectedSubclass = -1;

        if (__instance.currentHero != null)
        {
            LevelUpHelper.SetSelectedSubclass(__instance.currentHero, null);
        }
    }

    [HarmonyPatch(typeof(CharacterStageDeitySelectionPanel), "EnumerateCompatibleSubclasses")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumerateCompatibleSubclasses_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] CharacterStageDeitySelectionPanel __instance)
        {
            if (!LevelUpHelper.IsClericDomainSelectionSeparate(__instance.currentHero))
            {
                return true;
            }

            ClearDeitySubclassSelection(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterStageDeitySelectionPanel), nameof(CharacterStageDeitySelectionPanel.EnterStage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnterStage_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] CharacterStageDeitySelectionPanel __instance)
        {
            if (!LevelUpHelper.IsClericDomainSelectionSeparate(__instance.currentHero))
            {
                return;
            }

            ClearDeitySubclassSelection(__instance);
            __instance.verticalLayout.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            __instance.EnumerateValidationGroups();
        }
    }

    [HarmonyPatch(typeof(CharacterStageDeitySelectionPanel), nameof(CharacterStageDeitySelectionPanel.UpdateRelevance))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UpdateRelevance_Patch
    {
        [UsedImplicitly]
        public static void Postfix([NotNull] CharacterStageDeitySelectionPanel __instance)
        {
            //PATCH: updates this panel relevance (MULTICLASS)
            if (LevelUpHelper.IsLevelingUp(__instance.currentHero))
            {
                __instance.isRelevant = LevelUpHelper.RequiresDeity(__instance.currentHero);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageDeitySelectionPanel), nameof(CharacterStageDeitySelectionPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterStageDeitySelectionPanel __instance)
        {
            if (LevelUpHelper.IsClericDomainSelectionSeparate(__instance.currentHero))
            {
                ClearDeitySubclassSelection(__instance);
            }
        }

        [UsedImplicitly]
        public static void Postfix([NotNull] CharacterStageDeitySelectionPanel __instance)
        {
            if (StrictTabletopSelectionContext.IsEnabled ||
                !Main.Settings.EnableClericToLearnDomainAtLevel3 ||
                __instance.selectedDeity < 0 ||
                __instance.selectedDeity >= __instance.compatibleDeities.Count ||
                LevelUpHelper.GetSelectedClass(__instance.currentHero) != Cleric)
            {
                return;
            }

            var deity = __instance.compatibleDeities[__instance.selectedDeity];
            var alignment = DatabaseHelper.GetDefinition<AlignmentDefinition>(deity.Alignment).FormatTitle();
            var domains = Gui.Localize("Screen/&DomainsTitle");
            var label = $"{alignment}\n\n<b><color=#B5D3DE>{domains}</color></b>\n";
            var finalText = deity.subclasses
                .Where(StrictTabletopSelectionContext.IsSubclassNameAllowedForCurrentMode)
                .Select(DatabaseHelper.GetDefinition<CharacterSubclassDefinition>)
                .Aggregate(label,
                    (current, subClass) =>
                        current +
                        $"<i><color=#B5F3FE>{subClass.FormatTitle()}</color></i>\n{subClass.FormatDescription()}\n\n");

            __instance.selectedDeityAlignment.Text = finalText;
        }
    }
}
