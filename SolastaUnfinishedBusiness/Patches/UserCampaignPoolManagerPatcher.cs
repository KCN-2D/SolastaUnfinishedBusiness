using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class UserCampaignPoolManagerPatcher
{
    [HarmonyPatch(typeof(UserCampaignPoolManager), nameof(UserCampaignPoolManager.EnumeratePool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumeratePool_Patch
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignPoolManager __instance, out List<UserCampaign> __state)
        {
            __state = DungeonMakerContext.RetainCampaignFilesForEnumeration(__instance);
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, List<UserCampaign> __state)
        {
            __state?.Clear();

            return __exception;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var previousPathsField = AccessTools.Field(typeof(UserCampaignPoolManager), "previousContentPath");
            var clearPaths = AccessTools.Method(typeof(List<string>), nameof(List<string>.Clear));
            var previousPathsIndex = code.FindIndex(instruction => instruction.LoadsField(previousPathsField));
            var start = previousPathsIndex - 3;
            var end = previousPathsIndex < 0
                ? -1
                : code.FindIndex(previousPathsIndex, instruction => instruction.Calls(clearPaths)) - 2;

            if (start < 2 || end <= start ||
                (code[start - 2].opcode != OpCodes.Ldloc && code[start - 2].opcode != OpCodes.Ldloc_S) ||
                code[start + 1].opcode.FlowControl != FlowControl.Cond_Branch ||
                code[start + 1].operand is not Label target || !code[end].labels.Contains(target) ||
                !code[end + 1].LoadsField(previousPathsField))
            {
                Main.Error("Failed to locate workshop path comparison in UserCampaignPoolManager.EnumeratePool.");
                return code;
            }

            // Native code indexes the new list using the old count, and skips comparison
            // when counts match. Compare the ordered paths without changing pool loading.
            var replacement = new List<CodeInstruction>
            {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldfld, previousPathsField),
                code[start].Clone(),
                new(OpCodes.Call, AccessTools.Method(typeof(EnumeratePool_Patch), nameof(HaveUserContentPathsChanged))),
                new(code[start - 2].opcode == OpCodes.Ldloc_S ? OpCodes.Stloc_S : OpCodes.Stloc,
                    code[start - 2].operand)
            };

            replacement[0].MoveLabelsFrom(code[start]).MoveBlocksFrom(code[start]);
            code.RemoveRange(start, end - start);
            code.InsertRange(start, replacement);

            return code;
        }

        private static bool HaveUserContentPathsChanged(List<string> previousPaths, IGamingPlatformService platform)
        {
            return platform != null && !previousPaths.SequenceEqual(platform.UserContentPaths);
        }
    }

    [HarmonyPatch(typeof(UserCampaignPoolManager), "ReadCampaignFromDisk")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReadCampaignFromDisk_Patch
    {
        [UsedImplicitly]
        internal static bool Prefix(
            UserCampaignPoolManager __instance,
            string __0,
            bool __2,
            bool __3,
            List<string> __4,
            Dictionary<string, UserCampaign> ___pool,
            ref UserCampaign __result,
            out DungeonMakerContext.CampaignFileRead __state)
        {
            __state = null;

            if (!DungeonMakerContext.TryGetCachedCampaignFile(__instance, __0, __2, __3, __4, out var campaign, out __state))
            {
                return DungeonMakerContext.ShouldReadUserContentFile(__0, nameof(UserCampaign));
            }

            campaign.IsWorkshopItem = __2 || __3;

            if (__3 || !___pool.ContainsKey(campaign.Title))
            {
                ___pool[campaign.Title] = campaign;
            }

            __result = campaign;

            return false;
        }

        [UsedImplicitly]
        internal static void Postfix(
            UserCampaignPoolManager __instance,
            bool __1,
            List<string> __4,
            Dictionary<string, UserCampaign> ___pool,
            UserCampaign __result,
            DungeonMakerContext.CampaignFileRead __state)
        {
            DungeonMakerContext.CacheCampaignFile(__instance, __state, __result, __1, __4, ___pool);
        }
    }

    //PATCH: allows the last X campaign files to be backed up in the mod folder
    [HarmonyPatch(typeof(UserCampaignPoolManager), nameof(UserCampaignPoolManager.SaveUserCampaign))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SaveUserCampaign_Patch
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignPoolManager __instance, UserCampaign __0)
        {
            DungeonMakerContext.InvalidateCampaignFile(__instance, __0);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var deleteMethod = typeof(File).GetMethod("Delete");
            var backupAndDeleteMethod = new Action<string, UserContent>(DungeonMakerContext.BackupAndDelete).Method;

            return instructions.ReplaceCalls(deleteMethod, "UserCampaignPoolManager.SaveUserCampaign",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, backupAndDeleteMethod));
        }
    }

    [HarmonyPatch(typeof(UserCampaignPoolManager), nameof(UserCampaignPoolManager.Shutdown))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Shutdown_Patch
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignPoolManager __instance)
        {
            DungeonMakerContext.ReleaseCampaignFiles(__instance);
        }
    }
}
