using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class MultiplayerStatusModalPatcher
{
    private static void EnsurePlayerInfoGroups(List<PlayerInfoGroup> groups)
    {
        if (groups == null || groups.Count == 0)
        {
            return;
        }

        var template = groups[0];

        while (groups.Count < Main.Settings.OverridePartySize)
        {
            var newItem = Object.Instantiate(template.gameObject, template.transform.parent);
            var playerInfoGroup = newItem.GetComponent<PlayerInfoGroup>();

            groups.Add(playerInfoGroup);
        }
    }

    [HarmonyPatch(typeof(MultiplayerStatusModal), nameof(MultiplayerStatusModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow1_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] MultiplayerStatusModal __instance)
        {
            //PATCH: allows up to 6 players to join the game if there are enough heroes available (PARTYSIZE)
            switch (__instance)
            {
                case MultiplayerWaitModal multiplayerWaitModal:
                {
                    EnsurePlayerInfoGroups(multiplayerWaitModal.notReadyPlayerInfoGroups);
                    EnsurePlayerInfoGroups(multiplayerWaitModal.readyPlayerInfoGroups);

                    break;
                }
                case MultiplayerKickModal multiplayerKickModal:
                {
                    EnsurePlayerInfoGroups(multiplayerKickModal.playerInfoGroups);

                    break;
                }
                case MultiplayerVoteModal multiplayerVoteModal:
                {
                    EnsurePlayerInfoGroups(multiplayerVoteModal.playerInfoGroups);

                    break;
                }
            }
        }
    }
}
