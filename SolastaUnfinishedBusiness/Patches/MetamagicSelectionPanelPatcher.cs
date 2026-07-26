using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors;

namespace SolastaUnfinishedBusiness.Patches;

public static class MetamagicSelectionPanelPatcher
{
    [HarmonyPatch(typeof(MetamagicSelectionPanel), nameof(MetamagicSelectionPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshActions_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var rulesetCharacterGetter = AccessTools.PropertyGetter(
                typeof(GameLocationCharacter),
                nameof(GameLocationCharacter.RulesetCharacter));

            // PATCH: support replacement options and Simulacrum identity snapshots.
            return ReplaceMetamagicOption.PatchMetamagicGetterFromCharacter(
                instructions,
                "MetamagicSelectionPanel.Bind",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Callvirt, rulesetCharacterGetter));
        }
    }
}
