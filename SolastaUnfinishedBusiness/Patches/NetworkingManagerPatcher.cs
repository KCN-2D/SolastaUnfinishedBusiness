using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NetworkingManagerPatcher
{
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.MoveCharacter))]
    [UsedImplicitly]
    public static class MoveCharacter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter __0)
        {
            if (CooperativeExplorationContext.Busy) return false;
            CooperativeExplorationContext.OnLocalMove(__0);
            return true;
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.MoveCharacterRPC))]
    [UsedImplicitly]
    public static class MoveCharacterRPC_Patch
    {
        [UsedImplicitly]
        public static void Postfix(long __1, int __2, int __3, int __4) =>
            CooperativeExplorationContext.OnMoveDestination((ulong)__1, new TA.int3(__2, __3, __4));
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.Update))]
    [UsedImplicitly]
    public static class Update_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NetworkingManager __instance)
        {
            MultiplayerSettingsContext.Refresh();
            CooperativeExplorationContext.Refresh(__instance);
            CooperativeExplorationContext.UpdateFollow();
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.OnJoinedRoom))]
    [UsedImplicitly]
    public static class OnJoinedRoom_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            CooperativeExplorationContext.Reset();
            MultiplayerSettingsContext.Invalidate();
            MultiplayerSettingsContext.Refresh(true);
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.OnPlayerEnteredRoom))]
    [UsedImplicitly]
    public static class OnPlayerEnteredRoom_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            CooperativeExplorationContext.OnPartyChanged();
            MultiplayerSettingsContext.Refresh(true);
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.OnPlayerLeftRoom))]
    [UsedImplicitly]
    public static class OnPlayerLeftRoom_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            CooperativeExplorationContext.OnPartyChanged();
            MultiplayerSettingsContext.Refresh(true);
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.OnMasterClientSwitched))]
    [UsedImplicitly]
    public static class OnMasterClientSwitched_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            CooperativeExplorationContext.OnPartyChanged();
            MultiplayerSettingsContext.Invalidate();
            MultiplayerSettingsContext.Refresh(true);
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.OnRoomPropertiesUpdate))]
    [UsedImplicitly]
    public static class OnRoomPropertiesUpdate_Patch
    {
        [UsedImplicitly]
        public static void Postfix(Hashtable __0) => MultiplayerSettingsContext.OnRoomPropertiesChanged(__0);
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.OnPlayerPropertiesUpdate))]
    [UsedImplicitly]
    public static class OnPlayerPropertiesUpdate_Patch
    {
        [UsedImplicitly]
        public static void Postfix(Hashtable __1) => MultiplayerSettingsContext.OnPlayerPropertiesChanged(__1);
    }

    [HarmonyPatch]
    [UsedImplicitly]
    public static class ClearRoom_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NetworkingManager), nameof(NetworkingManager.OnLeftRoom));
            yield return AccessTools.Method(typeof(NetworkingManager), nameof(NetworkingManager.OnDisconnected));
            yield return AccessTools.Method(typeof(NetworkingManager), nameof(NetworkingManager.Shutdown));
        }

        [UsedImplicitly]
        public static void Postfix()
        {
            CooperativeExplorationContext.Reset();
            MultiplayerSettingsContext.Invalidate();
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.ExecuteAction))]
    [UsedImplicitly]
    public static class ExecuteAction_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterActionParams __0, CharacterAction.ActionExecutedHandler __1)
        {
            SpellCastingResourceContext.PrepareNetworkAction(__0);
            return !CooperativeExplorationContext.Intercept(__0, __1);
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.ExecuteActionChain))]
    [UsedImplicitly]
    public static class ExecuteActionChain_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterActionChainParams __0, CharacterAction.ActionChainExecutedHandler __1)
        {
            foreach (var parameters in __0.actionsParams)
            {
                SpellCastingResourceContext.PrepareNetworkAction(parameters);
            }
            return !CooperativeExplorationContext.InterceptChain(__0, __1);
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.CreateRoom))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CreateOfflineRoomIfNeeded_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ref NetworkingDefinitions.RoomInfo roomInfo)
        {
            //PATCH: allows up to 6 players to join the game if there are enough heroes available (PARTYSIZE)
            roomInfo.maxPlayers = Main.Settings.OverridePartySize;
        }
    }

    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.CastIdentifySpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CastIdentifySpell_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            NetworkingManager __instance,
            RulesetCharacterHero __0,
            RulesetItem __1,
            LocalCommandManager ___localCommandManager)
        {
            if (CooperativeExplorationContext.Enabled && __0 != null && __1 != null &&
                ___localCommandManager != null && Gui.GameLocation != null && Gui.Battle == null)
            {
                ___localCommandManager.CastIdentifySpell(__0, __1);
                return false;
            }
            if (!SpellCastingResourceContext.SupportsItemResourceSelection || __0 == null || __1 == null ||
                ___localCommandManager == null || Gui.GameLocation == null || Gui.Battle != null ||
                !ReferenceEquals(ServiceRepository.GetService<ICommandService>(), __instance) ||
                Gui.GuiService.GetScreen<GameLocationScreenExploration>()?.CharacterControlPanel is not
                    CharacterControlPanelExploration { ExplorationActionPanel: not null } ||
                !SpellCastingResourceContext.EnumerateResources(__0, Identify).Any(option => option.IsFree))
            {
                return true;
            }

            // Select on the initiating client before sending the serialized CastSpell action.
            // CastIdentifySpellRPC carries only actor/item IDs and would open a picker on every peer;
            // ExecuteActionRPC already transports the selected repertoire, level, and resource kind.
            ___localCommandManager.CastIdentifySpell(__0, __1);
            return false;
        }
    }
}
