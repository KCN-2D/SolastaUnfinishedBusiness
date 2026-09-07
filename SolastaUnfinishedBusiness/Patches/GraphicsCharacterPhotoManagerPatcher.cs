using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GraphicsCharacterPhotoManagerPatcher
{
    [HarmonyPatch(typeof(GraphicsCharacterPhotoManager), nameof(GraphicsCharacterPhotoManager.RenderCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RenderCharacter_Patch
    {
        [UsedImplicitly]
        internal static void Prefix(
            GraphicsCharacterPhotoManager __instance,
            RulesetCharacter rulesetCharacter,
            RenderTexture renderTexture,
            bool isActivePhoto,
            ref Action<Texture> response,
            out PortraitsContext.PhotoRenderRequest __state)
        {
            // Register when the request is queued, before an unfinished native cache hit can occur.
            __state = PortraitsContext.BeginPhotoRequest(
                __instance, rulesetCharacter, renderTexture, isActivePhoto, ref response);
        }

        [UsedImplicitly]
        internal static IEnumerator Postfix(IEnumerator values, PortraitsContext.PhotoRenderRequest __state)
        {
            return PortraitsContext.ObservePhotoRequest(values, __state);
        }
    }

    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RenderCharacter_MoveNext_Patch
    {
        [UsedImplicitly]
        public static MethodBase TargetMethod()
        {
            var render = AccessTools.Method(typeof(GraphicsCharacterPhotoManager),
                nameof(GraphicsCharacterPhotoManager.RenderCharacter));
            var iterator = render.GetCustomAttribute<IteratorStateMachineAttribute>().StateMachineType;

            return AccessTools.Method(iterator, nameof(IEnumerator.MoveNext));
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // Temporary render targets can be reused after release. Keep the native abort/cleanup
            // branches, but do not let an older coroutine mistake a newer request for its own.
            var contains = AccessTools.Method(typeof(HashSet<RenderTexture>), nameof(HashSet<RenderTexture>.Contains));

            return instructions.ReplaceCalls(contains, 4, "GraphicsCharacterPhotoManager.RenderCharacter.MoveNext",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortraitsContext),
                    nameof(PortraitsContext.IsPendingPhotoRequest))));
        }
    }

    [HarmonyPatch(typeof(GraphicsCharacterPhotoManager), nameof(GraphicsCharacterPhotoManager.ReleaseCharacterPhoto),
        typeof(RulesetCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReleaseCharacterPhoto_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacterPhotoManager __instance, RulesetCharacter rulesetCharacter)
        {
            PortraitsContext.CancelPhotoRequest(__instance, rulesetCharacter, false);
        }
    }

    [HarmonyPatch(typeof(GraphicsCharacterPhotoManager), nameof(GraphicsCharacterPhotoManager.ReleaseActiveCharacterPhoto))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReleaseActiveCharacterPhoto_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacterPhotoManager __instance, RulesetCharacter rulesetCharacter)
        {
            PortraitsContext.CancelPhotoRequest(__instance, rulesetCharacter, true);
        }
    }

    [HarmonyPatch(typeof(GraphicsCharacterPhotoManager), nameof(GraphicsCharacterPhotoManager.ReleaseAllPhotos))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReleaseAllPhotos_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacterPhotoManager __instance)
        {
            // A cancellation callback can start another request. Notify only after the
            // native cache has released its old textures, so that new request survives.
            PortraitsContext.CancelAllPhotoRequests(__instance);
        }
    }
}
