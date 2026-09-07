using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellCastingResourceItemPatcher
{
    private static bool _supportsResourceSelection;

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.CastIdentifySpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CastIdentifySpell_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            NetworkingManager __instance,
            RulesetCharacterHero __0,
            RulesetItem __1,
            LocalCommandManager ___localCommandManager)
        {
            if (!_supportsResourceSelection || __0 == null || __1 == null ||
                ___localCommandManager == null || Gui.GameLocation == null || Gui.Battle != null ||
                !ReferenceEquals(ServiceRepository.GetService<ICommandService>(), __instance) ||
                Gui.GuiService.GetScreen<GameLocationScreenExploration>()?.CharacterControlPanel is not
                    CharacterControlPanelExploration { ExplorationActionPanel: not null } ||
                !SpellCastingResourceContext.EnumerateResources(__0, Identify).Any(option => option.IsFree))
            {
                return true;
            }

            // Select on the initiating client before sending the serialized CastSpell action.
            // CastIdentifySpellRPC carries only actor/item IDs and would open a picker on every peer;
            // ExecuteActionRPC already transports the selected repertoire, level, and resource kind.
            ___localCommandManager.CastIdentifySpell(__0, __1);
            return false;
        }
    }

    [HarmonyPatch(typeof(ItemMenuModal), nameof(ItemMenuModal.SetupFromItem))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetupFromItem_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.ReplaceCalls(
                AccessTools.Method(typeof(RulesetSpellRepertoire),
                    nameof(RulesetSpellRepertoire.CanCastSpellOfLevel)),
                1,
                "ItemSpellResource.IdentifyAvailability",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(SetupFromItem_Patch), nameof(CanIdentify))));
        }

        private static bool CanIdentify(RulesetSpellRepertoire repertoire, int level, ItemMenuModal menu)
        {
            if (repertoire.CanCastSpellOfLevel(level))
            {
                return true;
            }

            var caster = menu.GuiCharacter?.RulesetCharacter;

            // Only replace the inventory button's resource check. Native preparation, battle,
            // and material-component checks still decide whether identification is permitted.
            return _supportsResourceSelection && level == Identify.SpellLevel && caster != null &&
                   SpellCastingResourceContext.EnumerateResources(caster, Identify, repertoire)
                       .Any(option => option.IsFree && option.IsAvailable(caster));
        }
    }

    [HarmonyPatch(typeof(CharacterActionPanel), nameof(CharacterActionPanel.OnCastSpellOnItemExternal))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnCastSpellOnItemExternal_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterActionPanel __instance,
            GameLocationCharacter __0,
            RulesetItem __1,
            bool __2)
        {
            var caster = __0?.RulesetCharacter;

            if (!_supportsResourceSelection || __2 || caster == null || __1 == null ||
                SpellCastingResourceContext.CurrentSelection != null)
            {
                return true;
            }

            caster.CanCastSpell(Identify, false, out var preferredRepertoire);
            var inspectionScreen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();
            var attachment = inspectionScreen != null && inspectionScreen.Visible
                ? inspectionScreen.GetComponent<RectTransform>()
                : Gui.GuiService.GetScreen<GameLocationScreenExploration>()?.GetComponent<RectTransform>();

            // Item identification does not enter SpellcastEngaged or show the exploration action panel.
            // Keep the picker on the visible screen, and begin the native action only after selection.
            return !SpellResourceSelectionPanel.ShowOptions(
                __instance.SpellSelectionPanel.SlotAdvancementPanel,
                caster,
                Identify,
                preferredRepertoire,
                0,
                (_, _) => __instance.OnCastSpellOnItemExternal(__0, __1, false),
                attachment);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var canCast = AccessTools.Method(typeof(RulesetCharacter), nameof(RulesetCharacter.CanCastSpell),
                [typeof(SpellDefinition), typeof(bool), typeof(RulesetSpellRepertoire).MakeByRefType()]);
            var lowestLevel = AccessTools.Method(typeof(RulesetSpellRepertoire),
                nameof(RulesetSpellRepertoire.GetLowestAvailableSlotLevel));
            var clone = AccessTools.Method(typeof(CharacterActionParams), nameof(CharacterActionParams.Clone));
            var canCastCalls = canCast == null ? 0 : code.Count(instruction => instruction.Calls(canCast));
            var lowestLevelCalls = lowestLevel == null ? 0 : code.Count(instruction => instruction.Calls(lowestLevel));
            var cloneCalls = clone == null ? 0 : code.Count(instruction => instruction.Calls(clone));

            _supportsResourceSelection = canCastCalls == 1 && lowestLevelCalls == 1 && cloneCalls == 2;

            if (!_supportsResourceSelection)
            {
                Main.Error("Failed to apply item spell resource selection: " +
                           $"expected CanCastSpell=1, lowestLevel=1, Clone=2; found " +
                           $"{canCastCalls}, {lowestLevelCalls}, {cloneCalls}.");
                return code;
            }

            return code
                .ReplaceCalls(canCast, 1, "ItemSpellResource.CanCastSpell",
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(OnCastSpellOnItemExternal_Patch), nameof(CanCastSelectedSpell))))
                .ReplaceCalls(lowestLevel, 1, "ItemSpellResource.LowestLevel",
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(OnCastSpellOnItemExternal_Patch), nameof(GetSelectedSlotLevel))))
                .ReplaceCalls(clone, 2, "ItemSpellResource.Clone",
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(OnCastSpellOnItemExternal_Patch), nameof(CloneWithSelection))));
        }

        private static bool CanCastSelectedSpell(
            RulesetCharacter caster,
            SpellDefinition spell,
            bool checkAvailableSlot,
            out RulesetSpellRepertoire repertoire)
        {
            var selection = SpellCastingResourceContext.CurrentSelection;

            if (selection == null || selection.Spell != spell)
            {
                return caster.CanCastSpell(spell, checkAvailableSlot, out repertoire);
            }

            repertoire = selection.Repertoire;

            // Retain knowledge, preparation, and existing casting validation without requiring a
            // conventional slot when the selected resource is a Wizard or repertoire free use.
            return selection.IsAvailable(caster) && repertoire.CanCastSpell(spell, false);
        }

        private static int GetSelectedSlotLevel(RulesetSpellRepertoire repertoire)
        {
            var selection = SpellCastingResourceContext.CurrentSelection;

            return selection?.Repertoire == repertoire
                ? selection.SlotLevel
                : repertoire.GetLowestAvailableSlotLevel();
        }

        private static CharacterActionParams CloneWithSelection(CharacterActionParams parameters)
        {
            if (parameters.ActionDefinition.Id != ActionDefinitions.Id.CastRitual &&
                SpellCastingResourceContext.CurrentSelection?.Spell == Identify &&
                parameters.RulesetEffect is RulesetEffectSpell
                {
                    OriginItem: null,
                    RulesetInvocation: null,
                    SlotLevel: > 0
                } effect &&
                effect is not RulesetEffectSpellWithOrigin && effect.SpellDefinition == Identify)
            {
                SpellCastingResourcePatcher.SetSelectedEffect(parameters, effect);
            }

            return parameters.Clone();
        }
    }
}
