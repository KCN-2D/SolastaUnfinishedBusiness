using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RuntimeStateBootingPatcher
{
    [HarmonyPatch(typeof(RuntimeState_Booting), nameof(RuntimeState_Booting.Update))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Update_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var request = AccessTools.Method(typeof(FiniteStateMachine), nameof(FiniteStateMachine.RequestStateChange),
                [typeof(Type), typeof(object[])]);

            return instructions.ReplaceCalls(request, 1, "RuntimeState_Booting.Update",
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(BootContext), nameof(BootContext.CompleteStartup))));
        }
    }
}
