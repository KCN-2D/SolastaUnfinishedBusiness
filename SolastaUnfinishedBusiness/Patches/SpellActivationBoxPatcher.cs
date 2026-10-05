using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellActivationBoxPatcher
{
    private static readonly ConditionalWeakTable<SpellActivationBox, SpellCastingResourceContext.ResourceOption> ResourceBindings = new();

    internal static void RefreshUpcastTooltip(SpellActivationBox box)
    {
        if (!box || !box.hasUpcast || !box.upTooltip || box.GuiSpellDefinition == null)
        {
            return;
        }

        var definition = box.GuiSpellDefinition;
        var content = Gui.Localize("Screen/&SpellAdvancementOpenDescription");
        if (definition.SpellDefinition.EffectDescription.HasAdditionalSlotAdvancement)
        {
            content += "\n\n" + Gui.Format("{0}: {1}", definition.AdvancementMethod, definition.AdvancementGain);
        }

        // Native Bind stores already formatted text. Refresh that display without rebinding
        // the pooled card, subscribing its handlers again, or replacing its resource owner.
        box.upTooltip.Content = content;
    }

    private static void DetachAdvancementPanel(SpellActivationBox box)
    {
        if (box.slotAdvancementPanel)
        {
            // Only the current owner's picker belongs to this pooled binding.
            if (box.slotAdvancementPanel.Visible && box.closeAdvancementButton.gameObject.activeSelf)
            {
                box.slotAdvancementPanel.Hide(true);
            }

            // Free display columns can clear hasUpcast after native Bind subscribed.
            // Cleanup follows the actual panel reference, not that mutable flag.
            box.slotAdvancementPanel.PanelStateChanged -= box.PanelStateChanged;
        }

        box.slotAdvancementPanel = null;
        box.hasUpcast = false;
        box.upcastButton.gameObject.SetActive(false);
        box.closeAdvancementButton.gameObject.SetActive(false);
    }

    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.BindSpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindSpell_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            SpellActivationBox __instance,
            RulesetCharacter __0,
            ref RulesetSpellRepertoire __1,
            SpellDefinition __2,
            SlotAdvancementBox.OnActivateHandler __5,
            ref SlotAdvancementPanel __6,
            out IDisposable __state)
        {
            DetachAdvancementPanel(__instance);
            ResourceBindings.Remove(__instance);
            var source = SpellCastingResourceContext.ResolveCastingRepertoire(__1, __2, __0);
            if (SpellSelectionContext.TryGetOption(__1, out var option))
            {
                ResourceBindings.Add(__instance, option);
                if (option.IsFree)
                {
                    __1 = option.CastingRepertoire;
                }
            }
            else if (__1 != null && source != __1)
            {
                // The native class column owns the slots; the feat remains the spell's casting source.
                ResourceBindings.Add(__instance, SpellSelectionContext.GetBaseSelection(__0, __1, __2));
            }

            SpellCastingValidation.BindTooltipRepertoire(__instance.tooltip, source);
            __state = SpellCastingValidation.EnterSelectedRepertoire(source);
            if (!__6 && __5 != null)
            {
                __6 = Gui.GuiService?.GetScreen<SlotAdvancementPanel>();
            }
        }

        [UsedImplicitly]
        public static void Postfix(
            SpellActivationBox __instance,
            RulesetCharacter __0,
            RulesetSpellRepertoire __1,
            SpellDefinition __2)
        {
            if (ResourceBindings.TryGetValue(__instance, out var option) && option.IsFree)
            {
                DetachAdvancementPanel(__instance);
            }

            RefreshUpcastTooltip(__instance);

            var line = SpellActionTypeContext.GetRepertoireLine(__instance);
            var hasAvailableAction = Gui.Battle == null || line?.actionType != ActionDefinitions.ActionType.None ||
                                     SpellActionTypeContext.TryGetAvailableSpellAction(
                                         GameLocationCharacter.GetFromActor(__0), __1, __2,
                                         ActionDefinitions.ActionScope.Battle, out _);

            if (hasAvailableAction && (!__instance.globalValid ||
                SpellCastingValidation.IsValid(__0,
                    SpellCastingResourceContext.ResolveCastingRepertoire(__1, __2, __0), __2, null, out _)))
            {
                return;
            }

            __instance.globalValid = false;
            __instance.canvasGroup.interactable = false;
            __instance.image.color = Color.grey;
            __instance.image.material = __instance.unavailableMaterial;
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();

            return __exception;
        }

        private static bool UniqueLevelSlots(
            FeatureDefinitionCastSpell featureDefinitionCastSpell,
            RulesetCharacter character)
        {
            //PATCH: MC casters must use the standard slot picker so shared and pact slots can coexist
            var caster = character.GetOriginalHero() ?? character;

            return featureDefinitionCastSpell.UniqueLevelSlots &&
                   (!featureDefinitionCastSpell.UsesSharedSpellSlots() ||
                    !SharedSpellsContext.IsMulticaster(caster));
        }

        [UsedImplicitly]
        public static void MyGetSlotsNumber(
            RulesetSpellRepertoire repertoire,
            int spellLevel,
            out int remaining,
            out int max,
            RulesetCharacter caster,
            SpellActivationBox spellActivationBox)
        {
            if (ResourceBindings.TryGetValue(spellActivationBox, out var option))
            {
                SpellSelectionContext.GetViewSlots(option, spellLevel, out remaining, out max);
                return;
            }

            if (SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire) &&
                repertoire.SpellCastingFeature.CannotUpcast &&
                spellActivationBox.GuiSpellDefinition?.SpellDefinition is { } fixedSpell)
            {
                var fixedUse = SpellSelectionContext.GetBaseSelection(caster, repertoire, fixedSpell);
                fixedUse.GetUses(caster, out remaining, out max);
                return;
            }

            if (repertoire.UsesSharedSpellSlots() && caster.IsSpellPointsEnabled())
            {
                max = 1;
                remaining = SpellPointsContext.CanCastSpellOfLevel(caster, repertoire, spellLevel) ? 1 : 0;
            }
            else
            {
                repertoire.GetDisplaySlotNumbers(caster.GetOriginalHero() ?? caster, spellLevel, out remaining, out max);
            }

            // The column owns the payment choice. A paid spell never borrows a free
            // exemption from another column when deciding whether its button is enabled.
            if (remaining > 0 && !SpellSlotCastingLimit2024Context.CanUseSpellSlotLevel(
                    caster, repertoire, null, spellLevel))
            {
                remaining = 0;
            }
        }

        private static int GetLowestAvailableSlotLevel(
            RulesetSpellRepertoire repertoire, RulesetCharacter caster, SpellActivationBox box)
        {
            var spell = box.GuiSpellDefinition?.SpellDefinition;
            if (spell == null || spell.SpellLevel == 0)
            {
                return repertoire.GetLowestAvailableSlotLevel();
            }

            var option = ResourceBindings.TryGetValue(box, out var free)
                ? free
                : SpellSelectionContext.GetBaseSelection(caster, repertoire, spell);
            return option.IsAvailable(caster) ? option.SlotLevel : 0;
        }

        private static bool CanUpcastSpell(
            RulesetSpellRepertoire repertoire, SpellDefinition spell, List<int> levels,
            RulesetCharacter caster, SpellActivationBox box, SlotAdvancementPanel panel)
        {
            levels.Clear();
            if (!panel || box.OnActivateAdvanced == null ||
                ResourceBindings.TryGetValue(box, out var option) && option.IsFree)
            {
                return false;
            }

            foreach (var resource in SpellCastingResourceContext.EnumerateUpcastSlots(caster, repertoire, spell))
            {
                levels.Add(resource.SlotLevel);
            }

            return levels.Count > 0;
        }

        private static bool CanKeepUpcast(EffectDescription _, SpellActivationBox box) =>
            box.higherLevelSlots.Count > 0;

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var uniqueLevelSlotsMethod = typeof(FeatureDefinitionCastSpell).GetMethod("get_UniqueLevelSlots");
            var myUniqueLevelSlotsMethod =
                new Func<FeatureDefinitionCastSpell, RulesetCharacterHero, bool>(UniqueLevelSlots).Method;

            var getLowestSlotMethod = AccessTools.Method(
                typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.GetLowestAvailableSlotLevel));
            var getOwnSlotMethod = new Func<RulesetSpellRepertoire, RulesetCharacter, SpellActivationBox, int>(
                GetLowestAvailableSlotLevel).Method;
            var getSlotsNumberMethod = typeof(RulesetSpellRepertoire).GetMethod("GetSlotsNumber");
            var myGetSlotsNumberMethod = typeof(BindSpell_Patch).GetMethod("MyGetSlotsNumber");
            var getActivationTimeMethod = typeof(SpellDefinition).GetMethod("get_ActivationTime");
            var canUpcastMethod = AccessTools.Method(typeof(RulesetSpellRepertoire), nameof(RulesetSpellRepertoire.CanUpcastSpell));
            var myCanUpcastMethod = new Func<RulesetSpellRepertoire, SpellDefinition, List<int>, RulesetCharacter,
                SpellActivationBox, SlotAdvancementPanel, bool>(CanUpcastSpell).Method;
            var additionalSlotMethod = AccessTools.PropertyGetter(typeof(EffectDescription), nameof(EffectDescription.HasAdditionalSlotAdvancement));
            var keepUpcastMethod = new Func<EffectDescription, SpellActivationBox, bool>(CanKeepUpcast).Method;
            var getDisplayedActivationTimeMethod =
                new Func<SpellDefinition, SpellActivationBox, RuleDefinitions.ActivationTime>(
                    SpellActionTypeContext.GetDisplayedActivationTime).Method;

            var code = instructions.ToList();
            var firstAdvancement = code.FindIndex(instruction => instruction.Calls(additionalSlotMethod));
            if (firstAdvancement >= 0 && code.Count(instruction => instruction.Calls(additionalSlotMethod)) == 2)
            {
                // Only the eligibility check changes. Keep native scaling metadata
                // for the plus/up icon and advancement description below it.
                code.Insert(firstAdvancement, new CodeInstruction(OpCodes.Ldarg_0));
                code[firstAdvancement + 1].opcode = OpCodes.Call;
                code[firstAdvancement + 1].operand = keepUpcastMethod;
            }
            else
            {
                Main.Error("Failed to apply spell slot selection: expected two advancement checks.");
            }

            return code
                .ReplaceCalls(canUpcastMethod, 1, "SpellActivationBox.BindSpell.CanUpcastSpell",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_S, (byte)7),
                    new CodeInstruction(OpCodes.Call, myCanUpcastMethod))
                .ReplaceCalls(getActivationTimeMethod, 2, "SpellActivationBox.BindSpell.ActivationTime",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, getDisplayedActivationTimeMethod))
                .ReplaceCalls(getLowestSlotMethod, "SpellActivationBox.BindSpell.GetLowestAvailableSlotLevel",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, getOwnSlotMethod))
                .ReplaceCalls(getSlotsNumberMethod, "SpellActivationBox.BindSpell.GetSlotsNumber",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, myGetSlotsNumberMethod))
                .ReplaceCalls(uniqueLevelSlotsMethod, "SpellActivationBox.BindSpell.UniqueLevelSlots",
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, myUniqueLevelSlotsMethod));
        }

    }

    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.OnOpenAdvancementCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnOpenAdvancementCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellActivationBox __instance)
        {
            if (__instance.spellRepertoire == null ||
                __instance.GuiSpellDefinition?.SpellDefinition is not { } spell ||
                !__instance.hasUpcast || !__instance.slotAdvancementPanel ||
                __instance.OnActivateAdvanced == null || !__instance.canvasGroup.interactable ||
                __instance.spellRepertoire.GetCaster() is not { IsDeadOrDyingOrUnconscious: false } caster)
            {
                return false;
            }

            // A pooled callback or a resource change must not reopen stale slot choices.
            var options = SpellCastingResourceContext.EnumerateUpcastSlots(
                caster, __instance.spellRepertoire, spell);
            __instance.higherLevelSlots.Clear();
            __instance.higherLevelSlots.AddRange(options.Select(option => option.SlotLevel));
            return __instance.higherLevelSlots.Count > 0;
        }

        [UsedImplicitly]
        public static void Postfix(SpellActivationBox __instance)
        {
            if (__instance.slotAdvancementPanel && __instance.slotAdvancementPanel.Visible &&
                __instance.closeAdvancementButton.gameObject.activeSelf)
            {
                FloatingPanelBounds.ClampToScreenForNextFrames(
                    __instance.slotAdvancementPanel, __instance.slotAdvancementPanel.RectTransform);
            }
        }

        [UsedImplicitly]
        public static Exception Finalizer(SpellActivationBox __instance, Exception __exception)
        {
            if (SpellActivationBox.currentBox == __instance)
            {
                SpellActivationBox.currentBox = null;
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.PanelStateChanged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PanelStateChanged_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellActivationBox __instance)
        {
            if (__instance.spellRepertoire != null && __instance.hasUpcast && __instance.slotAdvancementPanel)
            {
                return true;
            }

            __instance.upcastButton.gameObject.SetActive(false);
            __instance.closeAdvancementButton.gameObject.SetActive(false);
            return false;
        }
    }

    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.OnCloseAdvancementCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnCloseAdvancementCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellActivationBox __instance)
        {
            // Closing an existing picker remains valid after its slot is spent.
            // An unbound pooled box must not close another box's shared picker.
            return __instance.spellRepertoire != null && __instance.hasUpcast &&
                   __instance.slotAdvancementPanel && __instance.slotAdvancementPanel.Visible &&
                   __instance.closeAdvancementButton.gameObject.activeSelf;
        }
    }

    //PATCH: register on acting character if SHIFT is pressed on spell box activation
    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.OnActivateCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnActivateCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellActivationBox __instance)
        {
            if (__instance.spellRepertoire == null)
            {
                return true;
            }

            var rulesetCaster = __instance.tooltip.Context as RulesetCharacter
                                ?? __instance.spellRepertoire.GetCaster();
            var caster = GameLocationCharacter.GetFromActor(rulesetCaster);

            caster?.RegisterShiftState();

            return true;
        }
    }

    [HarmonyPatch(typeof(SpellActivationBox), nameof(SpellActivationBox.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(SpellActivationBox __instance)
        {
            DetachAdvancementPanel(__instance);
            ResourceBindings.Remove(__instance);
            SpellCastingValidation.BindTooltipRepertoire(__instance.tooltip, null);
        }
    }
}
