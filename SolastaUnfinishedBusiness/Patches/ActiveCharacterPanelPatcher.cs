using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ActiveCharacterPanelPatcher
{
    private static void OnCharacterPowerActivated(RulesetCharacter character, RulesetUsablePower power, int level)
    {
        character.RefreshAll();
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ActiveCharacterPanel __instance)
        {
            //prevent null check issues
            return __instance.GuiCharacter?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false };
        }

        [UsedImplicitly]
        public static void Postfix(ActiveCharacterPanel __instance)
        {
            if (__instance.GuiCharacter?.RulesetCharacter is not
                { IsDeadOrDyingOrUnconscious: false } character)
            {
                return;
            }

            //PATCH: support for custom point pools and concentration powers on portrait
            IconsOnPortrait.CharacterPanelRefresh(__instance);

            if (character is RulesetCharacterSimulacrum duplicate)
            {
                if (TryGetSimulacrumClassAndLevel(
                        __instance.GuiCharacter,
                        duplicate,
                        out var classAndLevel))
                {
                    __instance.classAndLevelLabel.Text = classAndLevel;
                }
            }

            // Japanese class/subclass combinations can exceed the fixed one-line HUD plate.
            // Keep this actual active-character caption on one line and shrink it to the
            // available width instead of allowing the bottom of a second line to be clipped.
            UiTextHelpers.FitConstrainedSingleLine(
                __instance.classAndLevelLabel,
                0.58f,
                7f);
            UiTextDiagnostics.ScheduleActiveCharacterIdentity(
                __instance,
                __instance.classAndLevelLabel);

            if (character is RulesetCharacterSimulacrum)
            {
                __instance.inspectButton.gameObject.SetActive(true);
                __instance.inspectButton.interactable = true;
            }

            //PATCH: support for button that shows info about non-Hero characters
            if (Main.Settings.ShowButtonWithControlledMonsterInfo &&
                __instance.GuiCharacter.RulesetCharacter is RulesetCharacterMonster)
            {
                CustomCharacterStatsPanel.Instance.Refresh();
            }
        }

        private static bool TryGetSimulacrumClassAndLevel(
            GuiCharacter guiCharacter,
            RulesetCharacterSimulacrum duplicate,
            out string classAndLevel)
        {
            classAndLevel = null;

            if (!SimulacrumBehavior.TryGetClassLevels(duplicate, out var classes) ||
                classes.Count == 0)
            {
                return false;
            }

            if (classes.Count > 1)
            {
                // Hero multiclass captions already use this compact class/level
                // formatter. Use the same path for the duplicate instead of expanding
                // every class to the much longer single-class localized sentence.
                classAndLevel = MulticlassGameUi.GetAllClassesLabel(guiCharacter, ' ');

                return !string.IsNullOrEmpty(classAndLevel);
            }

            var classLevel = classes[0];
            var level = classLevel.Level.ToString();
            var classTitle = classLevel.ClassDefinition.FormatTitle();

            classAndLevel = SimulacrumBehavior.TryGetPrimarySubclass(
                duplicate,
                classLevel.ClassDefinition,
                out var subclass)
                ? Gui.Format(
                    "Format/&LevelAndClassAndSubclassFormat",
                    level,
                    classTitle,
                    subclass.FormatTitle())
                : Gui.Format(
                    "Format/&LevelAndClassNoSubclassFormat",
                    level,
                    classTitle);

            return true;
        }
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            ActiveCharacterPanel __instance,
            WieldedConfigurationSelector.OnConfigurationSwitchedHandler __1)
        {
            //PATCH: properly update IconsOnPortrait
            var character = __instance.GuiCharacter?.RulesetCharacter;

            if (character is { IsDeadOrDyingOrUnconscious: false } and not RulesetCharacterEffectProxy)
            {
                character.CharacterRefreshed += __instance.ConcentrationChanged;
                character.PowerActivated += OnCharacterPowerActivated;
            }

            if (character is RulesetCharacterSimulacrum
                {
                    LifecycleState: SimulacrumLifecycleState.Ready
                })
            {
                // Native Bind only exposes inventory shortcuts to heroes. A Simulacrum owns a
                // real independent inventory, so bind the same HUD controls to that inventory.
                __instance.shortcutsGroup.gameObject.SetActive(true);
                __instance.shortcutsGroup.Bind(__instance.GuiCharacter, true, __1);
            }

            //PATCH: support a better ratio with custom portraits
            if (Main.Settings.EnableCustomPortraits &&
                PortraitsContext.HasCustomPortrait(__instance.GuiCharacter?.RulesetCharacter))
            {
                __instance.characterPortrait.rectTransform.sizeDelta = new Vector2(164, 247);
                __instance.characterPortrait.rectTransform.anchoredPosition = new Vector2(-48, 0);
            }
            else
            {
                __instance.characterPortrait.rectTransform.sizeDelta = new Vector2(212, 247);
                __instance.characterPortrait.rectTransform.anchoredPosition = new Vector2(0, 0);
            }

            //PATCH: support for button that shows info about non-Hero characters
            if (!Main.Settings.ShowButtonWithControlledMonsterInfo
                || __instance.GuiCharacter?.RulesetCharacter is not RulesetCharacterMonster)
            {
                return;
            }

            CustomCharacterStatsPanel.Instance.Bind(__instance.GuiCharacter.RulesetCharacter);
        }
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ActiveCharacterPanel __instance)
        {
            //PATCH: properly update IconsOnPortrait
            var character = __instance.GuiCharacter?.RulesetCharacter;

            // ReSharper disable once InvertIf
            if (character is { IsDeadOrDyingOrUnconscious: false } and not RulesetCharacterEffectProxy)
            {
                character.CharacterRefreshed -= __instance.ConcentrationChanged;
                character.PowerActivated -= OnCharacterPowerActivated;
            }
        }

        [UsedImplicitly]
        public static void Postfix()
        {
            //PATCH: support for button that shows info about non-Hero characters
            if (Main.Settings.ShowButtonWithControlledMonsterInfo)
            {
                CustomCharacterStatsPanel.Instance.Unbind();
            }
        }
    }
}
