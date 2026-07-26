using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameCampaignCharacterPatcher
{
    [HarmonyPatch(typeof(GameCampaignCharacter), nameof(GameCampaignCharacter.EngageRest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EngageRest_Patch
    {
        [UsedImplicitly]
        public static bool Prefix([NotNull] GameCampaignCharacter __instance, RestType restType)
        {
            //PATCH: terminates effects correctly on world travel
            // Use the complete rest-effect path for heroes. Simulacra do not rest, but ordinary
            // timed effects still have to advance by the elapsed travel/camp duration.
            var rulesetCharacter = __instance.RulesetCharacter;

            if (rulesetCharacter is RulesetCharacterSimulacrum duplicate)
            {
                SimulacrumBehavior.AdvanceTimedEffectsForRest(duplicate, restType);

                return false;
            }

            if (rulesetCharacter is not RulesetCharacterHero)
            {
                return true;
            }

            rulesetCharacter.RefreshEffectsForRest(restType);
            return false;
        }
    }
}
