using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterPlateDetailedPatcher
{
    [HarmonyPatch]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CharacterPlateDetailed), nameof(CharacterPlateDetailed.Bind),
                [typeof(GuiCharacter)]);
            yield return AccessTools.Method(typeof(CharacterPlateDetailed), nameof(CharacterPlateDetailed.Bind),
                [typeof(RulesetCharacterHero.Snapshot)]);
        }

        [UsedImplicitly]
        public static void Postfix(CharacterPlateDetailed __instance)
        {
            // Text belongs to the new binding; it must not wait for the asynchronous portrait.
            RefreshDetails(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterPlateDetailed), nameof(CharacterPlateDetailed.Refresh))]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterPlateDetailed __instance)
        {
            __instance.RefreshNameAndRace();
        }
    }

    private static void RefreshDetails(CharacterPlateDetailed plate)
    {
        var character = plate.GuiCharacter;
        if (character == null)
        {
            return;
        }

        plate.RefreshNameAndRace();
        // Hero and snapshot photo callbacks reveal the group, then synchronously bind
        // class and level. A completed cache hit needs no second icon release/load.
        if (plate.PortraitGroup && plate.PortraitGroup.gameObject.activeSelf &&
            (character.RulesetCharacterHero != null || character.Snapshot != null))
        {
            return;
        }

        plate.RefreshClassInfo();
        plate.RefreshLevelAndExperience();
    }

    [HarmonyPatch(typeof(CharacterPlateDetailed), nameof(CharacterPlateDetailed.RefreshLevelAndExperience))]
    [UsedImplicitly]
    public static class RefreshLevelAndExperience_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterPlateDetailed __instance)
        {
            var character = __instance.GuiCharacter;
            if (character == null)
            {
                return false;
            }

            // Native skips these bindings while the portrait group is hidden for its
            // pending photo, leaving the previous character's level beside the new name.
            __instance.levelLabel.Text = character.CharacterLevel.ToString();
            if (__instance.experienceGauge)
            {
                __instance.experienceGauge.fillAmount = character.ExperienceProgressRatio;
            }

            __instance.levelTooltip.Content = character.LevelAndExperienceTooltip;
            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterPlateDetailed), nameof(CharacterPlateDetailed.RefreshNameAndRace))]
    [UsedImplicitly]
    public static class RefreshNameAndRace_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterPlateDetailed __instance)
        {
            var creation = __instance.GetComponentInParent<CharacterCreationScreen>();

            if (creation != null && creation.commonData.CharacterPlate == __instance &&
                !string.IsNullOrEmpty(__instance.GuiCharacter?.FullName))
            {
                // The generic heading occupies the same space as the character's name.
                creation.commonData.CharacterInfoLabel.gameObject.SetActive(false);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterPlateDetailed), nameof(CharacterPlateDetailed.RefreshClassInfo))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshClassInfo_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterPlateDetailed __instance)
        {
            int classesCount;
            char separator;
            var guiCharacter = __instance.GuiCharacter;

            if (guiCharacter.RulesetCharacter is RulesetCharacterSimulacrum duplicate &&
                SimulacrumBehavior.TryGetClassLevels(duplicate, out var duplicateClasses))
            {
                separator = '\n';
                classesCount = duplicateClasses.Count;
            }
            else if (guiCharacter.RulesetCharacterHero != null)
            {
                separator = '\n';
                classesCount = guiCharacter.RulesetCharacterHero.ClassesAndLevels.Count;
            }
            else
            {
                if (guiCharacter.Snapshot == null)
                {
                    return;
                }

                separator = '\\';
                classesCount = guiCharacter.Snapshot.Classes.Length;
            }

            __instance.classLabel.Text = MulticlassGameUi.GetAllClassesLabel(guiCharacter, separator) ??
                                         __instance.classLabel.Text;
            // Class/subclass names share a fixed area above the ability scores.
            // Fit the complete localized text there rather than sizing by class count.
            UiTextHelpers.FitReadableTitle(__instance.classLabel.TMP_Text, 0.55f, 8f,
                Mathf.Max(2, classesCount));
        }
    }
}
