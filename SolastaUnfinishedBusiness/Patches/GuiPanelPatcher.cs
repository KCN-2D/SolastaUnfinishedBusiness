using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using System;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiPanelPatcher
{
    [HarmonyPatch(typeof(GuiPanel), nameof(GuiPanel.Hide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Hide_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GuiPanel __instance)
        {
            if (__instance is SpellSelectionPanel panel)
            {
                CampaignsContext.CancelPendingSpellSelectionBind(panel);
            }
        }
    }

    [HarmonyPatch(typeof(GuiPanel), nameof(GuiPanel.Show))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Show_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            UiHelpers.NativePanelShowInitializationAvailable = false;
            var code = instructions.ToList();
            var shown = AccessTools.PropertyGetter(typeof(GuiPanel), nameof(GuiPanel.Shown));
            var beginShow = AccessTools.Method(typeof(GuiPanel), "OnBeginShow");
            var guards = code.Where(instruction => instruction.Calls(shown)).ToArray();
            var callbacks = code.Where(instruction => instruction.Calls(beginShow)).ToArray();
            if (guards.Length != 1 || callbacks.Length != 1)
            {
                Main.Error("GuiPanel.Show native initialization sites differ.");
                return code;
            }

            guards[0].opcode = OpCodes.Call;
            guards[0].operand = new Func<GuiPanel, bool>(UiHelpers.WasShownBeforePanelInitialization).Method;
            var callbackIndex = code.IndexOf(callbacks[0]);
            var owner = new CodeInstruction(OpCodes.Ldarg_0);
            owner.labels.AddRange(callbacks[0].labels);
            callbacks[0].labels.Clear();
            code.Insert(callbackIndex, owner);
            code.Insert(callbackIndex + 1, new CodeInstruction(OpCodes.Call,
                new Action<GuiPanel>(UiHelpers.ObserveNativePanelBeginShow).Method));
            UiHelpers.NativePanelShowInitializationAvailable = true;
            return code;
        }

        [UsedImplicitly]
        public static void Postfix(GuiPanel __instance)
        {
            CampaignsContext.InvalidateSpellSelectionHud(__instance);

            if (__instance is SettingsPanel settingsPanel)
            {
                UiTextHelpers.FitSettingsTabs(settingsPanel);
            }

            //PATCH: Keeps last level up hero selected
            if (__instance is not MainMenuScreen mainMenuScreen || Global.LastLevelUpHeroFilename == null)
            {
                return;
            }

            mainMenuScreen.charactersPanel.Show();
        }
    }

    [HarmonyPatch(typeof(GuiPanel), nameof(GuiPanel.OnBeginHide))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnEndHide_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GuiPanel __instance, bool instant)
        {
            //PATCH: Power Bundle: hide sub-power selector when some panels start closing
            if (__instance is PowerSelectionPanel or InvocationSelectionPanel)
            {
                PowerBundle.CloseSubPowerSelectionModal(instant);
            }
        }
    }

    [HarmonyPatch(typeof(GuiPanel), nameof(GuiPanel.Unload))]
    [UsedImplicitly]
    public static class Unload_Patch
    {
        [UsedImplicitly]
        private static Exception Finalizer(GuiPanel __instance, Exception __exception) =>
            // Native GuiPanel.Unload only yields null once and reads no panel state.
            // ContainerPanel inherits it, so release its unbound rows at this boundary.
            __instance is ContainerPanel container
                ? ContainerPanelPatcher.ReleaseInventorySlots(container, __exception)
                : __exception;
    }

    [HarmonyPatch(typeof(GuiPanel), "OnDestroy")]
    [UsedImplicitly]
    public static class OnDestroy_Patch
    {
        [UsedImplicitly]
        private static Exception Finalizer(GuiPanel __instance, Exception __exception) =>
            __instance switch
            {
                CharacterStageIdentityDefinitionPanel identity =>
                    CharacterStageIdentityDefinitionPanelPatcher.ReleaseCustomizationChoices(identity, __exception),
                ContainerPanel container => ContainerPanelPatcher.ReleaseInventorySlots(container, __exception),
                _ => __exception
            };
    }
}
