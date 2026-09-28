using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NetworkingManagerPatcher
{
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
