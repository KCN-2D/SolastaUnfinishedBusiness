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
        public static bool Prefix(string __0)
        {
            return DungeonMakerContext.ShouldReadUserContentFile(__0, nameof(UserCampaign));
        }
    }

    //PATCH: allows the last X campaign files to be backed up in the mod folder
    [HarmonyPatch(typeof(UserCampaignPoolManager), nameof(UserCampaignPoolManager.SaveUserCampaign))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SaveUserCampaign_Patch
    {
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
}
