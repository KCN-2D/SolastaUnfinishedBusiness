using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using System;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterInspectionScreenPatcher
{
    private static bool NativeParentInitializationAvailable { get; set; }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.CommonShow))]
    [UsedImplicitly]
    public static class CommonShow_Patch
    {
        [UsedImplicitly]
        private static void Prefix(
            CharacterInspectionScreen __instance,
            RulesetCharacterHero __0,
            GuiScreen __1,
            ActionDefinitions.InventoryManagementMode __2,
            out UiHelpers.NativePanelInitialization __state)
        {
            __state = null;
            if (!NativeParentInitializationAvailable || __0 == null)
            {
                return;
            }

            // CommonShow owns this synchronous Bind -> Show operation. Before
            // native Bind the hidden screen is unbound; once active, every
            // request field must still identify this exact native invocation.
            __state = UiHelpers.BeginNativePanelInitialization(__instance, __instance,
                () => !__instance.Visible && __instance.InspectedCharacter == null ||
                      __instance.previousScreen == __1 && __instance.inventoryManagementMode == __2 &&
                      ReferenceEquals(__instance.InspectedCharacter?.RulesetCharacterHero, __0));
        }

        [UsedImplicitly]
        private static Exception Finalizer(UiHelpers.NativePanelInitialization __state, Exception __exception) =>
            __state == null ? __exception : __state.Finish(__exception);
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterInspectionScreen __instance,
            RulesetCharacterHero heroCharacter,
            ActionDefinitions.InventoryManagementMode __1,
            InventoryPanel.ItemSelectedHandler itemSelected,
            out bool __state)
        {
            CharacterInspectionScreenEnhancement.ReleaseInspectionTabsScroll(__instance);

            __state = SimulacrumEquipmentPanel.TryBind(
                __instance,
                heroCharacter,
                __1,
                itemSelected);

            //PATCH: sets the inspection context for MC heroes
            Global.InspectedHero = __state ? null : heroCharacter;
            CharacterInspectionScreenEnhancement.ResetInspectionState();

            //PATCH: gets more real state for the toggles on top (MULTICLASS)
            var transform = __instance.toggleGroup.transform;

            transform.position =
                new Vector3(__instance.characterPlate.transform.position.x / 2f, transform.position.y, 0);

            return !__state;
        }

        [UsedImplicitly]
        public static void Postfix(
            CharacterInspectionScreen __instance,
            RulesetCharacterHero heroCharacter,
            bool __state)
        {
            if (__state)
            {
                return;
            }

            // A pooled listing can retain a non-Hero binding after Simulacrum inspection.
            // Native already binds normal Hero boxes, including their refresh handlers.
            if (!CharacterStatsPanelPatcher.HasAbilityScoreBinding(
                    __instance.abilityScoresListingPanel, heroCharacter))
            {
                CharacterStatsPanelPatcher.BindAbilityScores(
                    __instance.abilityScoresListingPanel, heroCharacter);
            }

            //PATCH: support display max spell points on inspection screen (SPELL_POINTS)
            SpellPointsContext.DisplayMaxSpellPointsOnInspectionScreen(__instance, heroCharacter);

            CharacterInspectionScreenEnhancement.RefreshInspectionSpellTabs(__instance, heroCharacter);
            CharacterInspectionScreenEnhancement.ConfigureInspectionTabsScroll(__instance);

        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            NativeParentInitializationAvailable = false;
            var code = instructions.ToList();
            var setActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive));
            var getGameObject = AccessTools.PropertyGetter(typeof(Component), nameof(Component.gameObject));
            // Bind also activates each inspection toggle. Only the two temporary
            // activations whose receiver is this screen belong to the parent scope.
            var parents = Enumerable.Range(3, Math.Max(0, code.Count - 3)).Where(index =>
                code[index].Calls(setActive) && code[index - 3].opcode == OpCodes.Ldarg_0 &&
                code[index - 2].Calls(getGameObject)).ToArray();
            var enables = parents.Where(index => code[index - 1].opcode == OpCodes.Ldc_I4_1).ToArray();
            var sites = parents.Where(index => code[index - 1].opcode == OpCodes.Ldc_I4_0).ToArray();
            if (parents.Length != 2 || enables.Length != 1 || sites.Length != 1 || enables[0] >= sites[0])
            {
                Main.Error("CharacterInspectionScreen.Bind native parent initialization sites differ.");
                return code;
            }

            code[sites[0]].opcode = OpCodes.Call;
            code[sites[0]].operand = new Action<GameObject, bool>(UiHelpers.RestorePreparedPanelActive).Method;
            NativeParentInitializationAvailable = true;
            return code;
        }

        [UsedImplicitly]
        public static System.Exception Finalizer(
            CharacterInspectionScreen __instance,
            System.Exception __exception)
        {
            if (__exception != null)
            {
                SimulacrumEquipmentPanel.HandleBindFailure(__instance);
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            CharacterInspectionScreenEnhancement.ReleaseInspectionTabsScroll(__instance);

            //PATCH: resets the inspection context for MC heroes
            Global.InspectedHero = null;
            CharacterInspectionScreenEnhancement.ResetInspectionState();

            return !SimulacrumEquipmentPanel.TryUnbind(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterInspectionScreen __instance)
        {
            SimulacrumEquipmentPanel.AfterBeginShow(__instance);
            CharacterInspectionScreenEnhancement.EnsureInspectionTabVisible(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.ToggleValueChanged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ToggleValueChanged_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterInspectionScreen __instance, ref int rank)
        {
            if (!__instance.ignoreToggleCallback &&
                !SimulacrumEquipmentPanel.TryGetActiveCharacter(__instance, out _))
            {
                rank = CharacterInspectionScreenEnhancement.NormalizeInspectionToggle(__instance, rank);
            }
        }

        [UsedImplicitly]
        public static void Postfix(CharacterInspectionScreen __instance)
        {
            CharacterInspectionScreenEnhancement.EnsureInspectionTabVisible(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            return !SimulacrumEquipmentPanel.TryRefresh(__instance);
        }
    }

    [HarmonyPatch(
        typeof(CharacterInspectionScreen),
        nameof(CharacterInspectionScreen.FilterInspectionPanels))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FilterInspectionPanels_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(
            CharacterInspectionScreen __instance,
            out UiHelpers.NativePanelInitialization __state)
        {
            __state = null;
            if (SimulacrumEquipmentPanel.TryGetActiveCharacter(__instance, out _))
            {
                // Its owner already prepared the one supported panel and performs native Show.
                return false;
            }

            var index = __instance.currentToggle;
            if (index < 0 || index >= __instance.inspectionPanels.Count ||
                __instance.InspectedCharacter?.RulesetCharacterHero == null)
            {
                return true;
            }

            var panel = __instance.inspectionPanels[index];
            __state = UiHelpers.BeginNativePanelInitialization(__instance, panel,
                () => __instance.currentToggle == index &&
                      index < __instance.inspectionPanels.Count &&
                      ReferenceEquals(__instance.inspectionPanels[index], panel));
            return true;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var setActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive));
            var sites = Enumerable.Range(1, Math.Max(0, code.Count - 1)).Where(index =>
                code[index].Calls(setActive) && code[index - 1].opcode == OpCodes.Ldc_I4_0).ToArray();
            if (sites.Length != 5)
            {
                Main.Error("CharacterInspectionScreen.FilterInspectionPanels native initialization sites differ.");
                return code;
            }

            foreach (var index in sites)
            {
                code[index].opcode = OpCodes.Call;
                code[index].operand = new Action<GameObject, bool>(UiHelpers.RestorePreparedPanelActive).Method;
            }

            return code;
        }

        [UsedImplicitly]
        internal static Exception Finalizer(UiHelpers.NativePanelInitialization __state, Exception __exception) =>
            __state == null ? __exception : __state.Finish(__exception);
    }

    [HarmonyPatch(
        typeof(CharacterInspectionScreen),
        nameof(CharacterInspectionScreen.RefreshCharactersTable))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshCharactersTable_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            if (!SimulacrumEquipmentPanel.TryGetActiveCharacter(__instance, out _))
            {
                return true;
            }

            __instance.characterPlatesTable.gameObject.SetActive(false);

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.InventoryDragStarted))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InventoryDragStarted_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            return HandleSimulacrumInventoryDrag(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.InventoryDragStopped))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InventoryDragStopped_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            return HandleSimulacrumInventoryDrag(__instance);
        }
    }

    //PATCH: resets the inspection context for MC heroes otherwise we get class name bleeding on char pool
    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.DoClose))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DoClose_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterInspectionScreen __instance)
        {
            Global.InspectedHero = null;
            CharacterInspectionScreenEnhancement.ResetInspectionState();
        }
    }

    //PATCH: modify caption if unlimited inventory actions is enabled
    [HarmonyPatch(typeof(CharacterInspectionScreen), nameof(CharacterInspectionScreen.RefreshCaption))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshCaption_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterInspectionScreen __instance)
        {
            if (SimulacrumEquipmentPanel.TryGetActiveCharacter(__instance, out _))
            {
                __instance.screenCaption.gameObject.SetActive(true);
                __instance.screenCaption.Text =
                    Gui.Localize("Screen/&SimulacrumEquipmentTitle");
                __instance.screenCaption.TMP_Text.color =
                    __instance.inventoryActionAvailableColor;

                return false;
            }

            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch (__instance.inventoryManagementMode)
            {
                case ActionDefinitions.InventoryManagementMode.Battle:
                    __instance.screenCaption.gameObject.SetActive(true);
                    switch (Main.Settings.EnableUnlimitedInventoryActions)
                    {
                        case false when
                            __instance.InspectedCharacter.GameLocationCharacter.GetActionTypeStatus(ActionDefinitions
                                .ActionType
                                .FreeOnce) == ActionDefinitions.ActionStatus.Available:
                            __instance.screenCaption.Text =
                                Gui.Localize("Screen/&CharacterInspectionModeBattleAvailableTitle");
                            __instance.screenCaption.TMP_Text.color = __instance.inventoryActionAvailableColor;
                            return false; // Skip the original method
                        case true when
                            __instance.InspectedCharacter.GameLocationCharacter.GetActionTypeStatus(ActionDefinitions
                                .ActionType
                                .FreeOnce) == ActionDefinitions.ActionStatus.Available:
                            __instance.screenCaption.Text =
                                Gui.Localize("Screen/&CharacterInspectionModeBattleUnlimitedTitle");
                            __instance.screenCaption.TMP_Text.color = __instance.inventoryActionAvailableColor;
                            return false; // Skip the original method
                    }

                    __instance.screenCaption.Text = Gui.Localize("Screen/&CharacterInspectionModeBattleSpentTitle");
                    __instance.screenCaption.TMP_Text.color = __instance.inventoryActionSpentColor;
                    return false; // Skip the original method
                case ActionDefinitions.InventoryManagementMode.SelectItem:
                    __instance.screenCaption.gameObject.SetActive(true);
                    __instance.screenCaption.Text = __instance.itemSelectionType switch
                    {
                        ActionDefinitions.ItemSelectionType.Equiped
                            or ActionDefinitions.ItemSelectionType.EquippedNoLightSource => Gui.Localize(
                                "Screen/&CharacterInspectionModeSelectEquipedItemTitle"),
                        ActionDefinitions.ItemSelectionType.Carried => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectCarriedItemTitle"),
                        ActionDefinitions.ItemSelectionType.MagicalUnidentified => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectMagicalUnidentifiedItemTitle"),
                        ActionDefinitions.ItemSelectionType.Weapon => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectWeaponTitle"),
                        ActionDefinitions.ItemSelectionType.WeaponNonMagical => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectWeaponNonMagicalTitle"),
                        ActionDefinitions.ItemSelectionType.WieldedClubOrQuarterstaff => Gui.Localize(
                            "Screen/&CharacterInspectionModeSelectWieldedClubOrQuarterstaffTitle"),
                        ActionDefinitions.ItemSelectionType.Spellbook => Gui.Format(
                            "Screen/&CharacterInspectionModeSelectSpellbookTitle",
                            __instance.spellToScribe.SpellLevel.ToString()),
                        _ => __instance.screenCaption.Text
                    };

                    __instance.screenCaption.TMP_Text.color = __instance.inventoryActionAvailableColor;
                    return false; // Skip the original method
                default:
                    __instance.screenCaption.gameObject.SetActive(false);
                    return false; // Skip the original method
            }
        }
    }

    private static bool HandleSimulacrumInventoryDrag(CharacterInspectionScreen screen)
    {
        if (!SimulacrumEquipmentPanel.TryGetActiveCharacter(screen, out _))
        {
            return true;
        }

        ServiceRepository.GetService<ICommandService>()
            .AcknowledgePreviousCommandLocally(screen.OnInventoryDragCommandDone);

        return false;
    }
}
