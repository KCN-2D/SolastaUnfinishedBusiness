using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ActionModifierPatcher
{
    [HarmonyPatch(typeof(ActionModifier), nameof(ActionModifier.Clone))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Clone_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ActionModifier __instance, ActionModifier __result)
        {
            RulesetCharacterExtensions.CopyMagicEffectAttackModifiers(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(ActionModifier), nameof(ActionModifier.Reset))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Reset_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ActionModifier __instance)
        {
            RulesetCharacterExtensions.ClearMagicEffectAttackModifiers(__instance);
        }
    }

    [HarmonyPatch(typeof(ActionModifier), nameof(ActionModifier.AttackAdvantageTrend), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AttackAdvantageTrend_Getter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ActionModifier __instance, ref int __result)
        {
            //PATCH: Apply SRD setting `UseOfficialAdvantageDisadvantageRules`
            if (!Main.Settings.UseOfficialAdvantageDisadvantageRules)
            {
                return true;
            }

            var advantage = __instance.AttackAdvantageTrends.Any(t => t.value > 0) ? 1 : 0;
            var disadvantage = __instance.AttackAdvantageTrends.Any(t => t.value < 0) ? -1 : 0;

            __result = advantage + disadvantage;

            return false;
        }
    }
}
