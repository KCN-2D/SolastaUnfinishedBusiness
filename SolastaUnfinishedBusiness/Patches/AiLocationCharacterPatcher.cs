using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class AiLocationCharacterPatcher
{
    //BUGFIX: a failed jump incapacitates the actor, but vanilla only terminates its AI loop
    //for dead, dying, or unconscious actors. Stop after the current activity has fully settled.
    [HarmonyPatch(typeof(AiLocationCharacter), nameof(AiLocationCharacter.ProcessBattleTurn))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ProcessBattleTurn_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, AiLocationCharacter __instance)
        {
            try
            {
                if (CombatAiContext.TryTerminateIncapacitatedAiTurn(__instance?.GameLocationCharacter))
                {
                    yield break;
                }

                while (values.MoveNext())
                {
                    yield return values.Current;

                    if (CombatAiContext.TryTerminateIncapacitatedAiTurn(__instance.GameLocationCharacter))
                    {
                        yield break;
                    }
                }
            }
            finally
            {
                (values as IDisposable)?.Dispose();
            }
        }
    }
}
