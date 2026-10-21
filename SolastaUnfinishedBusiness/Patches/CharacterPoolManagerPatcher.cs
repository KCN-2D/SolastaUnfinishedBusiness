using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterPoolManagerPatcher
{
    [HarmonyPatch(typeof(CharacterPoolManager), nameof(CharacterPoolManager.SaveCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SaveCharacter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RulesetCharacterHero heroCharacter, ref string __result)
        {
            if (ToolsContext.FunctorRespec.ShouldSuppressCharacterPoolSave(heroCharacter))
            {
                __result = string.Empty;
                return false;
            }

            //PATCH: Keeps last level up hero selected
            if (heroCharacter == null)
            {
                return true;
            }

            Global.LastLevelUpHeroName = Main.Settings.KeepCharactersPanelOpenAndHeroSelectedAfterLevelUp
                ? heroCharacter.Name
                : null;
            return true;
        }
    }
}
