using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using I2.Loc;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterEditionScreenPatcher
{
    private static readonly Dictionary<string, string> CheckerLabelTerms = new(StringComparer.Ordinal)
    {
        ["VisualInfoTitle"] = "Stage/&VisualCheckerTitle",
        ["AnimationInfoTitle"] = "Stage/&AnimationCheckerTitle",
        ["HeroTitle"] = "Screen/&CharacterCheckerHeroTitle",
        ["MonsterTitle"] = "Screen/&CharacterCheckerMonsterTitle",
        ["MonsterHeader"] = "Screen/&CharacterCheckerMonsterTitle",
        ["RaceHeader"] = "Screen/&CharacterCheckerRaceTitle",
        ["SubRaceHeader"] = "Screen/&CharacterCheckerSubraceTitle",
        ["LipSyncHeader"] = "Screen/&CharacterCheckerLipSyncTitle",
        ["DialogGroupHeader"] = "Screen/&CharacterCheckerDialogTagTitle",
        ["DialogLineHeader"] = "Screen/&CharacterCheckerDialogLineTitle",
        ["EquipmentHeader"] = "Screen/&CharacterCheckerEquipmentTitle",
        ["TorsoHeader"] = "Screen/&CharacterCheckerTorsoTitle",
        ["HeadgearHeader"] = "Screen/&CharacterCheckerHeadgearTitle",
        ["MainHandHeader"] = "Screen/&CharacterCheckerMainHandTitle",
        ["OffHandHeader"] = "Screen/&CharacterCheckerOffHandTitle",
        ["TabardHeader"] = "Equipment/&Tabard_Base_Title",
        ["UtilityHeader"] = "Screen/&CharacterCheckerUtilityTitle",
        ["VariantHeader"] = "Screen/&CharacterCheckerVariantTitle",
        ["PortraitHeader"] = "Screen/&CharacterCheckerPortraitTitle",
        ["SpellTargetHeader"] = "Screen/&CharacterCheckerSpellTargetTitle",
        ["PowerAnimationHeader"] = "Screen/&CharacterCheckerPowerAnimationTitle",
        ["LipSyncButton"] = "Screen/&CharacterCheckerPlayLipSyncTitle",
        ["GeneratePortraitButton"] = "Screen/&CharacterCheckerGeneratePortraitTitle",
        ["CloseUpCameraToggle"] = "Screen/&CharacterCheckerCloseUpTitle",
        ["PhotoCameraToggle"] = "Screen/&CharacterCheckerPhotoTitle",
        ["StopIdleToggle"] = "Screen/&CharacterCheckerStopIdleTitle",
        ["RunCycleButton"] = "Screen/&CharacterCheckerRunCycleTitle",
        ["AttackButton"] = "Action/&AttackTitle",
        ["CastPowerButton"] = "Screen/&CharacterCheckerCastPowerTitle",
        ["InteractionButton"] = "Screen/&CharacterCheckerInteractionTitle",
        ["ShoveButton"] = "Action/&ShoveTitle",
        ["HitButton"] = "Screen/&CharacterCheckerHitTitle",
        ["DodgeButton"] = "Action/&DodgeTitle",
        ["DeathCycleButton"] = "Screen/&CharacterCheckerDeathCycleTitle",
        ["ClimbCycleButton"] = "Screen/&CharacterCheckerClimbCycleTitle",
        ["LookAtButton"] = "Screen/&CharacterCheckerLookAtTitle",
        ["ProneToggle"] = "Screen/&CharacterCheckerProneTitle",
        ["FallingToggle"] = "Screen/&CharacterCheckerFallingTitle",
        ["FlyingToggle"] = "Screen/&CharacterCheckerFlyingTitle",
        ["StealthyToggle"] = "Screen/&CharacterCheckerStealthyTitle",
        ["InCombatToggle"] = "Screen/&CharacterCheckerInCombatTitle",
        ["ThrowingToggle"] = "Screen/&CharacterCheckerThrowingTitle",
        ["QuickSpellToggle"] = "Screen/&CharacterCheckerQuickSpellTitle",
        ["SpellContactToggle"] = "Screen/&CharacterCheckerSpellContactTitle",
        ["ManualLookAtToggle"] = "Screen/&CharacterCheckerManualLookAtTitle"
    };

    private static readonly Dictionary<string, string> CheckerDiagnosticTerms = new(StringComparer.Ordinal)
    {
        ["BodyParts: "] = "Screen/&CharacterCheckerBodyPartsTitle",
        ["ArmorParts: "] = "Screen/&CharacterCheckerArmorPartsTitle",
        ["Wielded items:"] = "Screen/&CharacterCheckerWieldedItemsTitle",
        ["Selected voice line:"] = "Screen/&CharacterCheckerSelectedVoiceLineTitle",
        ["Clips:"] = "Screen/&CharacterCheckerAnimationClipsTitle",
        ["States:"] = "Screen/&CharacterCheckerAnimationStatesTitle"
    };

    [HarmonyPatch(typeof(CharacterEditionScreen), nameof(CharacterEditionScreen.LoadStagePanels))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LoadStagePanels_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterEditionScreen __instance)
        {
            //PATCH: Patch to support custom feature selection screen
            //adds new stage panel based on Spell Selection stage
            CustomInvocationSelectionPanel.InsertPanel(__instance);

            //PATCH: adds the Multiclass class selection panel to the level up screen (MULTICLASS)
            MulticlassGameUi.SetupLevelUpClassSelectionStep(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterEditionScreen), nameof(CharacterEditionScreen.DoAbort))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DoAbort_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterEditionScreen __instance)
        {
            //PATCH: Unregisters hero from level up context (MULTICLASS)
            LevelUpHelper.UnregisterHero(__instance.currentHero);
        }
    }

    [HarmonyPatch(typeof(CharacterEditionScreen), nameof(CharacterEditionScreen.MessageTitle), MethodType.Getter)]
    [UsedImplicitly]
    public static class MessageTitle_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterEditionScreen __instance, ref string __result)
        {
            if (__instance is CharacterCheckerScreen)
            {
                __result = "Message/&CharacterCheckerExitTitle";
            }
        }
    }

    [HarmonyPatch(typeof(CharacterEditionScreen), nameof(CharacterEditionScreen.MessageDescription), MethodType.Getter)]
    [UsedImplicitly]
    public static class MessageDescription_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterEditionScreen __instance, ref string __result)
        {
            if (__instance is CharacterCheckerScreen)
            {
                __result = "Message/&CharacterCheckerExitDescription";
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStagePanel), nameof(CharacterStagePanel.Title), MethodType.Getter)]
    [UsedImplicitly]
    public static class StageTitle_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStagePanel __instance, ref string __result)
        {
            __result = __instance switch
            {
                CharacterStageVisualCheckerPanel => "Stage/&VisualCheckerTitle",
                CharacterStageAnimationCheckerPanel => "Stage/&AnimationCheckerTitle",
                _ => __result
            };
        }
    }

    [HarmonyPatch(typeof(CharacterStagePanel), nameof(CharacterStagePanel.Description), MethodType.Getter)]
    [UsedImplicitly]
    public static class StageDescription_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStagePanel __instance, ref string __result)
        {
            __result = __instance switch
            {
                CharacterStageVisualCheckerPanel => "Stage/&VisualCheckerDescription",
                CharacterStageAnimationCheckerPanel => "Stage/&AnimationCheckerDescription",
                _ => __result
            };
        }
    }

    [HarmonyPatch]
    [UsedImplicitly]
    public static class CheckerStageNavigationDescription_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CharacterEditionScreen), nameof(CharacterEditionScreen.NextButtonDescription));
            yield return AccessTools.Method(typeof(CharacterEditionScreen), nameof(CharacterEditionScreen.FinishButtonDescription));
            yield return AccessTools.Method(typeof(CharacterEditionScreen), nameof(CharacterEditionScreen.ContinueButtonDescription));
        }

        [UsedImplicitly]
        public static void Postfix(CharacterEditionScreen __instance, ref string __result)
        {
            if (__instance is CharacterCheckerScreen)
            {
                __result = "Screen/&CharacterCheckerStageNavigationDescription";
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageAnimationCheckerPanel), nameof(CharacterStageAnimationCheckerPanel.CanProceedToNextStage))]
    [UsedImplicitly]
    public static class CheckerLastStage_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref string failureString)
        {
            failureString = Gui.Format("Screen/&CharacterCheckerLastStageDescription",
                Gui.Localize("Screen/&StagePreviousButtonTitle"), Gui.Localize("Screen/&AbortTitle"));
        }
    }

    [HarmonyPatch(typeof(CharacterStagePanel), nameof(CharacterStagePanel.OnBeginShow))]
    [UsedImplicitly]
    public static class CheckerStageShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStagePanel __instance)
        {
            if (__instance is not (CharacterStageVisualCheckerPanel or CharacterStageAnimationCheckerPanel))
            {
                return;
            }

            foreach (var label in __instance.GetComponentsInChildren<GuiLabel>(true))
            {
                var name = label.name is "Label" or "TitleLabel" ? label.transform.parent.name : label.name;
                if (name == "CastSpellButton")
                {
                    label.Text = DatabaseHelper.ActionDefinitions.CastMain.GuiPresentation.Title;
                }
                else if (CheckerLabelTerms.TryGetValue(name, out var term))
                {
                    label.Text = term;
                }
            }

            if (__instance is CharacterStageVisualCheckerPanel visual)
            {
                LocalizeVisualOptions(visual);
            }
            else if (__instance is CharacterStageAnimationCheckerPanel animation)
            {
                animation.floorAngleSlider.transform.parent.Find("HeaderLabel").GetComponent<GuiLabel>().Text =
                    "Screen/&CharacterCheckerFloorAngleTitle";

                var count = (int)AnimationDefinitions.AnimationSpellTarget.Count;
                if (animation.spellTargetDropdown.options.Count != count)
                {
                    return;
                }

                for (var i = 0; i < count; i++)
                {
                    animation.spellTargetDropdown.options[i].text =
                        Gui.Localize($"Screen/&CharacterCheckerSpellTarget{(AnimationDefinitions.AnimationSpellTarget)i}Title");
                }

                animation.spellTargetDropdown.RefreshShownValue();
            }
        }
    }

    [HarmonyPatch]
    [UsedImplicitly]
    public static class VisualOptions_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CharacterStageVisualCheckerPanel), nameof(CharacterStageVisualCheckerPanel.FillEquipmentDropdownOptions));
            yield return AccessTools.Method(typeof(CharacterStageVisualCheckerPanel), nameof(CharacterStageVisualCheckerPanel.FillMonsterDropdownOptions));
            yield return AccessTools.Method(typeof(CharacterStageVisualCheckerPanel), nameof(CharacterStageVisualCheckerPanel.OnCharacterToggleChangedCb));
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageVisualCheckerPanel __instance, MethodBase __originalMethod)
        {
            switch (__originalMethod.Name)
            {
                case nameof(CharacterStageVisualCheckerPanel.FillEquipmentDropdownOptions):
                    LocalizeEquipmentOptions(__instance);
                    break;
                case nameof(CharacterStageVisualCheckerPanel.FillMonsterDropdownOptions):
                    LocalizeDefinitionOptions(__instance.monsterDropdown, __instance.allMonsterDefinitions);
                    break;
                case nameof(CharacterStageVisualCheckerPanel.OnCharacterToggleChangedCb):
                    LocalizeVisualOptions(__instance);
                    break;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageVisualCheckerPanel), nameof(CharacterStageVisualCheckerPanel.FillMonsterVariantOptions))]
    [UsedImplicitly]
    public static class MonsterVariantOptions_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageVisualCheckerPanel __instance, MonsterDefinition monsterDefinition)
        {
            var presentation = monsterDefinition.MonsterPresentation;
            if (presentation.HumanoidMonsterPresentationDefinitions is { Length: > 0 })
            {
                LocalizeDefinitionOptions(__instance.variantDropdown, presentation.HumanoidMonsterPresentationDefinitions);
            }
            else if (presentation.HasPrefabVariants)
            {
                LocalizeDefinitionOptions(__instance.variantDropdown, presentation.MonsterPresentationDefinitions);
            }
        }
    }

    [HarmonyPatch]
    [UsedImplicitly]
    public static class CheckerDiagnosticText_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CharacterStageVisualCheckerPanel),
                nameof(CharacterStageVisualCheckerPanel.RefreshBodyPartsDebugText));
            yield return AccessTools.Method(typeof(CharacterStageAnimationCheckerPanel),
                nameof(CharacterStageAnimationCheckerPanel.UpdateStage));
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var localize = new Func<string, string>(LocalizeCheckerDiagnostic).Method;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode != OpCodes.Ldstr ||
                    !CheckerDiagnosticTerms.TryGetValue((string)instruction.operand, out var term))
                {
                    yield return instruction;
                    continue;
                }

                instruction.operand = term;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Call, localize);
            }
        }
    }

    private static string LocalizeCheckerDiagnostic(string term)
    {
        return Gui.Localize(term);
    }

    private static string GetCheckerDefinitionTitle(BaseDefinition definition)
    {
        var term = definition?.GuiPresentation?.Title;
        if (string.IsNullOrWhiteSpace(term) || term == Gui.NoLocalization ||
            term.Contains("/&") && !LocalizationManager.TryGetTranslation(term, out _))
        {
            return definition?.Name ?? string.Empty;
        }

        var title = Gui.Localize(term);
        return string.IsNullOrWhiteSpace(title) ? definition.Name : title;
    }

    private static void LocalizeVisualOptions(CharacterStageVisualCheckerPanel panel)
    {
        LocalizeDefinitionOptions(panel.raceDropdown, panel.eligibleRaces);
        LocalizeDefinitionOptions(panel.voiceDropdown, panel.compatibleVoices);
        if (panel.selectedRace >= 0 && panel.selectedRace < panel.eligibleRaces.Count &&
            panel.sortedSubRaces.TryGetValue(panel.eligibleRaces[panel.selectedRace], out var subraces))
        {
            LocalizeDefinitionOptions(panel.subraceDropdown, subraces);
        }

        LocalizeEquipmentOptions(panel);
        LocalizeDefinitionOptions(panel.monsterDropdown, panel.allMonsterDefinitions);
    }

    private static void LocalizeEquipmentOptions(CharacterStageVisualCheckerPanel panel)
    {
        LocalizeDefinitionOptions(panel.torsoDropdown, panel.allTorsoDefinitions, true);
        LocalizeDefinitionOptions(panel.tabardDropdown, panel.allTabardDefinitions, true);
        LocalizeDefinitionOptions(panel.headgearDropdown, panel.allHeadgearDefinitions, true);
        LocalizeDefinitionOptions(panel.mainHandDropdown, panel.allMainHandDefinitions, true);
        LocalizeDefinitionOptions(panel.offHandDropdown, panel.allOffHandDefinitions, true);
        LocalizeDefinitionOptions(panel.utilityDropdown, panel.allUtilityDefinitions, true);
    }

    private static void LocalizeDefinitionOptions<T>(GuiDropdown dropdown, IReadOnlyList<T> definitions,
        bool includeNone = false) where T : BaseDefinition
    {
        var offset = includeNone ? 1 : 0;
        if (!dropdown || definitions == null || dropdown.options.Count != definitions.Count + offset)
        {
            return;
        }

        if (includeNone)
        {
            dropdown.options[0].text = Gui.Localize("Screen/&JournalBestiaryNoneTitle");
        }

        for (var i = 0; i < definitions.Count; i++)
        {
            dropdown.options[i + offset].text = GetCheckerDefinitionTitle(definitions[i]);
        }

        // Options remain in native definition order. Refresh only the caption, without a value-change callback.
        dropdown.RefreshShownValue();
    }
}
