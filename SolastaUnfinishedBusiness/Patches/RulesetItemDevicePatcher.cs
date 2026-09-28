using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetItemDevicePatcher
{
    [HarmonyPatch(typeof(RulesetItemDevice), nameof(RulesetItemDevice.IsFunctionAvailable))]
    [UsedImplicitly]
    public static class IsFunctionAvailable_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ref bool usedMainSpell, ref bool usedBonusSpell)
        {
            if (SpellSlotCastingLimit2024Context.UsesLegacyBonusActionSpellRestriction)
            {
                return;
            }

            // Item charges are not spell slots. Apply this at the shared device
            // boundary so inventory, action-panel, revive, and tooltip checks agree.
            usedMainSpell = false;
            usedBonusSpell = false;
        }

        [UsedImplicitly]
        public static void Postfix(
            RulesetItemDevice __instance,
            ref bool __result,
            RulesetDeviceFunction function,
            RulesetCharacter character,
            ref string failureFlag)
        {
            if (character is RulesetCharacterSimulacrum &&
                __instance?.ItemDefinition?.RequiresAttunement == true)
            {
                __result = false;
                failureFlag = "Failure/&SimulacrumCannotUseAttunedItem";

                return;
            }

            if (!__result)
            {
                return;
            }

            var power = function.DeviceFunctionDescription?.FeatureDefinitionPower;

            if (!power)
            {
                return;
            }

            __result = character.CanUsePower(power, false);
        }

        [UsedImplicitly]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var isOnSpellList = typeof(RulesetCharacter).GetMethod(nameof(RulesetCharacter.IsSpellDefinitionOnAnySpellList));
            var canUseScroll = new Func<RulesetCharacter, SpellDefinition, bool>(
                ThiefUseMagicDevice2024Context.CanUseScroll).Method;

            // Replace only the scroll-list requirement; keep verbal components and
            // native action, attunement, identification, and charge checks intact.
            return instructions.ReplaceCalls(isOnSpellList, 1, "Thief.IsFunctionAvailable.Scroll",
                new CodeInstruction(OpCodes.Call, canUseScroll));
        }
    }

    [HarmonyPatch(typeof(RulesetItemDevice), nameof(RulesetItemDevice.SerializeElements))]
    [UsedImplicitly]
    public static class SerializeElements_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetItemDevice __instance,  IElementsSerializer serializer)
        {
            if(serializer.Mode != Serializer.SerializationMode.Read) { return; }
            
            //PATCH: update availability of extra bonus action functions if 2024 item use rules are enabled
            Tabletop2024Context.UpdateDeviceBonusActions(__instance, GameConstants.TagPotion);
            Tabletop2024Context.UpdateDeviceBonusActions(__instance, GameConstants.TagPoison);
        }
    }
}
