using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterFilteringGroupPatcher
{
    [HarmonyPatch(typeof(CharacterFilteringGroup), nameof(CharacterFilteringGroup.Compare))]
    [UsedImplicitly]
    public static class Compare_Patch
    {
        //PATCH: correctly offers on adventures with min/max caps on character level (MULTICLASS)
        private static int MyLevels(IEnumerable<int> levels)
        {
            return levels.Sum();
        }

        private static string GetDisplayName(RulesetCharacterHero.Snapshot snapshot)
        {
            return TranslatorContext.LocalizeBuiltInCharacterName(snapshot.Name, snapshot.BuiltIn, snapshot.DisplayName);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var myLevelMethod = new Func<IEnumerable<int>, int>(MyLevels).Method;
            var levelsField = typeof(RulesetCharacterHero.Snapshot).GetField("Levels");

            return instructions.ReplaceLoadField(levelsField,
                -1,
                2, "CharacterFilteringGroup.Compare",
                new CodeInstruction(OpCodes.Ldfld, levelsField),
                new CodeInstruction(OpCodes.Call, myLevelMethod))
                .ReplaceLoadField(typeof(RulesetCharacterHero.Snapshot).GetField(nameof(RulesetCharacterHero.Snapshot.Name)),
                    -1, 0, "CharacterFilteringGroup.Compare",
                    new CodeInstruction(OpCodes.Call,
                        new Func<RulesetCharacterHero.Snapshot, string>(GetDisplayName).Method));
        }
    }
}
