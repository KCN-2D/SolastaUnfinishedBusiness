using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine.AddressableAssets;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class PlayerInfoGroupPatcher
{
    private static void HideForSimulacrum(CharacterPlateGameSelector selector)
    {
        if (selector?.GuiCharacter?.RulesetCharacter is not RulesetCharacterSimulacrum ||
            !selector.playerInfoGroup)
        {
            return;
        }

        selector.playerInfoGroup.Unbind();
        selector.playerInfoGroup.gameObject.SetActive(false);
    }

    [HarmonyPatch(
        typeof(CharacterPlateGame),
        nameof(CharacterPlateGame.BindPlayerInfoGroup),
        typeof(RulesetCharacter),
        typeof(PlayerInfoGroup),
        typeof(bool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindPlayerInfoGroup_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetCharacter __0, PlayerInfoGroup __1)
        {
            if (__0 is not RulesetCharacterSimulacrum)
            {
                return true;
            }

            __1.Unbind();
            __1.gameObject.SetActive(false);

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterPlateGameSelector), nameof(CharacterPlateGameSelector.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterPlateGameSelectorBind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterPlateGameSelector __instance)
        {
            HideForSimulacrum(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterPlateGameSelector), nameof(CharacterPlateGameSelector.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterPlateGameSelectorRefresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterPlateGameSelector __instance)
        {
            HideForSimulacrum(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterPlateGameSelector), nameof(CharacterPlateGameSelector.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterPlateGameSelectorUnbind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterPlateGameSelector __instance)
        {
            if (!__instance.playerInfoGroup)
            {
                return;
            }

            __instance.playerInfoGroup.Unbind();
            __instance.playerInfoGroup.gameObject.SetActive(true);
        }
    }

    [HarmonyPatch(typeof(PlayerInfoGroup), nameof(PlayerInfoGroup.RefreshPlayerAvatar))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshPlayerAvatar_Patch
    {
        [UsedImplicitly]
        public static void Prefix(List<AssetReferenceSprite> defaultPlayerSpriteReferences)
        {
            if (defaultPlayerSpriteReferences.Count == 0)
            {
                return;
            }

            //PATCH: allows up to 6 players to join the game if there are enough heroes available (PARTYSIZE)
            while (defaultPlayerSpriteReferences.Count < Main.Settings.OverridePartySize)
            {
                defaultPlayerSpriteReferences.Add(defaultPlayerSpriteReferences[0]);
            }
        }
    }
}
