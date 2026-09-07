using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ReactionRequestCastSpellPatcher
{
    [HarmonyPatch(typeof(ReactionRequestCastSpell), nameof(ReactionRequestCastSpell.BuildSlotSubOptions))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BuildSlotSubOptions_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ReactionRequestCastSpell __instance)
        {
            return !SpellCastingResourceContext.BuildOptions(__instance);
        }
    }

    [HarmonyPatch(typeof(ReactionRequestCastSpell), nameof(ReactionRequestCastSpell.SelectSubOption))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SelectSubOption_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ReactionRequestCastSpell __instance, int option)
        {
            return !SpellCastingResourceContext.SelectOption(__instance, option);
        }
    }

    [HarmonyPatch(typeof(ReactionRequestCastSpell), nameof(ReactionRequestCastSpell.SelectedSubOption), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SelectedSubOption_Getter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ReactionRequestCastSpell __instance, ref int __result)
        {
            if (!SpellCastingResourceContext.IsManaged(__instance))
            {
                return true;
            }

            __result = SpellCastingResourceContext.GetSelectedOption(__instance);
            return false;
        }
    }
}
