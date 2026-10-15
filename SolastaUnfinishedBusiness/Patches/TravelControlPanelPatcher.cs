using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class TravelControlPanelPatcher
{
    [HarmonyPatch(typeof(TravelControlPanel), nameof(TravelControlPanel.RefreshFood))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshFood_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(TravelControlPanel __instance)
        {
            var party = __instance.Party;
            var count = SimulacrumBehavior.CountPartyFood(
                party.CharactersList.Select(character => character.RulesetCharacter).OfType<RulesetCharacterHero>(),
                party);

            __instance.foodValue.Text = Gui.Format(
                count > 1 ? "Screen/&TravelFoodMultipleFormat" : "Screen/&TravelFoodSingleFormat",
                count.ToString());

            return false;
        }
    }
}
