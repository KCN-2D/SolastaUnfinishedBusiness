using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellSelectionPanelPatcher
{
    [HarmonyPatch(typeof(SpellSelectionPanel), nameof(SpellSelectionPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            GuiCharacter caster,
            ref bool cantripOnly,
            ActionDefinitions.ActionType actionType)
        {
            var gameLocationCaster = caster.GameLocationCharacter;

            // CharacterActionPanel passes the vanilla UsedMainSpell/UsedBonusSpell restriction to Bind.
            // Rebuild the panel restriction when the 2024 slot-expenditure rule replaces that legacy rule.
            SpellSlotCastingLimit2024Context.RemoveLegacyBonusActionSpellRestriction(ref cantripOnly);

            //PATCH: supports `IReplaceAttackWithCantrip`
            if (gameLocationCaster.RulesetCharacter.HasSubFeatureOfType<IAttackReplaceWithCantrip>()
                && gameLocationCaster.UsedMainAttacks > 0 && actionType == ActionDefinitions.ActionType.Main)
            {
                cantripOnly = true;
            }

            ActionSwitching.CheckSpellcastingCantrips(gameLocationCaster, actionType, ref cantripOnly);
            MetamagicContext.RestrictToCantripsAfterQuickenedSpell2024(gameLocationCaster, ref cantripOnly);
        }

        [UsedImplicitly]
        public static void Postfix(
            SpellSelectionPanel __instance,
            GuiCharacter caster,
            SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
            ActionDefinitions.ActionType actionType,
            bool cantripOnly)
        {
            CampaignsContext.SpellSelectionPanelMultilineBind(
                __instance, caster, spellCastEngaged, actionType, cantripOnly);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: use the same casting-source visibility and free-use columns in both panel layouts
            var getRepertoires = typeof(RulesetCharacter).GetMethod("get_SpellRepertoires");
            var getVisibleRepertoires = new Func<RulesetCharacter, List<RulesetSpellRepertoire>>(GetRepertoires).Method;

            return instructions.ReplaceCalls(getRepertoires, "SpellSelectionPanel.Bind",
                new CodeInstruction(OpCodes.Call, getVisibleRepertoires));
        }

        private static List<RulesetSpellRepertoire> GetRepertoires(RulesetCharacter character)
        {
            return SpellSelectionContext.GetRepertoires(character);
        }
    }

    [HarmonyPatch(typeof(SpellSelectionPanel), nameof(SpellSelectionPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            CampaignsContext.SpellSelectionPanelMultilineUnbind();
        }
    }

    [HarmonyPatch(typeof(SpellsByLevelBox), nameof(SpellsByLevelBox.OnActivateStandardBox))]
    [UsedImplicitly]
    private static class OnActivateStandardBox_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(SpellsByLevelBox __instance, int index, out IDisposable __state)
        {
            __state = null;
            if (!__instance.spellsByIndex.TryGetValue(index, out var spell) || spell.SpellLevel == 0 ||
                __instance.spellRepertoire.IsMysticArcanumSpell(spell))
            {
                return true;
            }

            var repertoire = __instance.spellRepertoire;
            var option = SpellSelectionContext.GetBaseSelection(__instance.caster, repertoire, spell);
            return SpellSelectionContext.TryBeginSelection(__instance.caster, option, out __state);
        }

        [UsedImplicitly]
        private static void Finalizer(IDisposable __state) => __state?.Dispose();
    }

    [HarmonyPatch(typeof(SpellsByLevelBox), nameof(SpellsByLevelBox.OnActivateAdvancedBox))]
    [UsedImplicitly]
    private static class OnActivateAdvancedBox_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(SpellsByLevelBox __instance, int index, int slotLevel, out IDisposable __state)
        {
            GameLocationCharacter.GetFromActor(__instance.caster)?.RegisterShiftState();
            __state = null;
            return !__instance.spellsByIndex.TryGetValue(index, out var spell) ||
                   SpellSelectionContext.TryBeginSelection(__instance.caster,
                       SpellSelectionContext.GetSelection(__instance.spellRepertoire, spell, slotLevel),
                       out __state);
        }

        [UsedImplicitly]
        private static void Finalizer(IDisposable __state) => __state?.Dispose();
    }
}
