using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionActionSurgePatcher
{
    //BUGFIX: vanilla always sets usedBonusSpell to false on action surge
    [HarmonyPatch(typeof(CharacterActionActionSurge), nameof(CharacterActionActionSurge.ExecuteImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteImpl_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ref IEnumerator __result, CharacterActionActionSurge __instance)
        {
            var actingCharacter = __instance.ActingCharacter;

            __result = Level20Context.CanUseActionSurgeThisTurn(actingCharacter)
                ? Process(__instance, actingCharacter.UsedBonusSpell)
                : DoNothing();

            return false;
        }

        private static IEnumerator DoNothing()
        {
            yield break;
        }

        private static IEnumerator Process(CharacterAction action, bool usedBonusSpell)
        {
            var actionService = ServiceRepository.GetService<IGameLocationActionService>();
            var actionParams = action.ActionParams.Clone();

            action.ActingCharacter.UsedMainSpell = false;
            action.ActingCharacter.UsedBonusSpell = usedBonusSpell;
            action.ActingCharacter.SetSpecialFeatureUses(Level20Context.ActionSurgeOncePerTurnName, 1);
            actionParams.ActionDefinition = actionService.AllActionDefinitions[ActionDefinitions.Id.PowerNoCost];
            //directly instantiate UsePower action instead of using CharacterAction.InstantiateAction - that one seems to fail here for some reason
            action.ResultingActions.Add(new CharacterActionUsePower(actionParams));

            yield break;
        }
    }
}
