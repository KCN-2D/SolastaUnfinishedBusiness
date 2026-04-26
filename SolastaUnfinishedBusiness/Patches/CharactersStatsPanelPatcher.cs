using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStatsPanelPatcher
{
    [HarmonyPatch(typeof(CharacterStatsPanel), nameof(CharacterStatsPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStatsPanel __instance)
        {
            UiTextHelpers.KeepCharacterStatsPanelTextInside(__instance);

            //PATCH: Format hit dice box to support MC scenarios (MULTICLASS)
            var hero = __instance.guiCharacter?.RulesetCharacterHero;

            if (!__instance.hitDiceBox.Activated ||
                hero == null ||
                hero.ClassesAndLevels.Count <= 1)
            {
                return;
            }

            __instance.hitDiceBox.ValueLabel.Text =
                MulticlassGameUi.GetAllClassesHitDiceLabel(__instance.guiCharacter, out var dieTypeCount);
            __instance.hitDiceBox.ValueLabel.TMP_Text.fontSize = MulticlassGameUi.GetFontSize(dieTypeCount);
            UiTextHelpers.FitCharacterStatBox(__instance.hitDiceBox);
        }
    }
}
