using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RuleDefinitionsPatcher
{
    [HarmonyPatch(typeof(RuleDefinitions), nameof(ComputeAdvantage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeAdvantage_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            [NotNull] List<TrendInfo> trends,
            ref AdvantageType __result)
        {
            //PATCH: Apply SRD setting `UseOfficialAdvantageDisadvantageRules`
            if (!Main.Settings.UseOfficialAdvantageDisadvantageRules)
            {
                return;
            }

            var hasAdvantage = trends.Any(t => t.value > 0);
            var hasDisadvantage = trends.Any(t => t.value < 0);

            if (!(hasAdvantage ^ hasDisadvantage))
            {
                __result = AdvantageType.None;
            }
            else if (hasAdvantage)
            {
                __result = AdvantageType.Advantage;
            }
            else
            {
                __result = AdvantageType.Disadvantage;
            }
        }
    }

    //BUGFIX: immunity from positions was incorrectly using < instead of <= to calculate the final result
    [HarmonyPatch(typeof(RuleDefinitions), nameof(IsPositionImmuneToSpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsPositionImmuneToSpell_Patch
    {
        [UsedImplicitly]
        public static void Postfix(out bool __result, int spellLevel, ref int totalSpellLevel)
        {
            __result = totalSpellLevel != 0 && spellLevel <= totalSpellLevel;
        }
    }
}
