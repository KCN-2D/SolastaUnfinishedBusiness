using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GraphicsCharacterHeroPatcher
{
    [HarmonyPatch]
    [UsedImplicitly]
    public static class WieldedEquipmentChanged_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GraphicsCharacterHero), nameof(GraphicsCharacterHero.ItemEquiped));
            yield return AccessTools.Method(typeof(GraphicsCharacterHero), nameof(GraphicsCharacterHero.ItemUnequiped));
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod)
        {
            var getter = AccessTools.PropertyGetter(typeof(ItemDefinition), nameof(ItemDefinition.IsWeapon));
            var replacement = AccessTools.Method(typeof(CustomModels), nameof(CustomModels.RequiresWieldedEquipmentRefresh));

            return instructions.ReplaceCalls(getter, 1, __originalMethod.DeclaringType?.Name + "." +
                __originalMethod.Name, new CodeInstruction(OpCodes.Call, replacement));
        }
    }

    [HarmonyPatch(typeof(GraphicsCharacterHero), nameof(GraphicsCharacterHero.GetAttackAnimationData))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetAttackAnimationData_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            ref string __result,
            [NotNull] RulesetAttackMode attackMode,
            ref bool isThrown,
            ref bool leftHand)
        {
            ShieldAttack.UseOffhandForShieldAttackAnimation(attackMode, ref __result, ref isThrown, ref leftHand);
        }
    }

    //BUGFIX: fix a vanilla issue that passes a null slot here if hero is downed
    [HarmonyPatch(typeof(GraphicsCharacterHero), nameof(GraphicsCharacterHero.ItemAltered))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ItemAltered_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetInventorySlot slot, RulesetItem item)
        {
            return item != null && slot != null;
        }
    }
}
