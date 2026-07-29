using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
internal static class CharacterActionLootGroundPatcher
{
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class ExecuteMoveNext_Patch
    {
        [UsedImplicitly]
        private static MethodBase TargetMethod()
        {
            var iterator = typeof(CharacterActionLootGround)
                .GetNestedTypes(BindingFlags.NonPublic)
                .Single(type => type.Name.Contains("<ExecuteImpl>d__"));

            return AccessTools.Method(iterator, "MoveNext");
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var actorReplacement = AccessTools.Method(
                typeof(CharacterActionLootGroundPatcher),
                nameof(ResolveLootActor));
            var isCharacterControlled = AccessTools.Method(
                typeof(PlayerController),
                nameof(PlayerController.IsCharacterControlled),
                [typeof(GameLocationCharacter)]);
            var controlReplacement = AccessTools.Method(
                typeof(CharacterActionLootGroundPatcher),
                nameof(IsLootCharacterControlled));
            var replacedHeroGate = 0;
            var replacedControlGate = 0;

            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Isinst &&
                    instruction.operand as Type == typeof(RulesetCharacterHero))
                {
                    replacedHeroGate++;
                    var replacement = new CodeInstruction(
                        OpCodes.Call,
                        actorReplacement);

                    replacement.labels.AddRange(instruction.labels);
                    replacement.blocks.AddRange(instruction.blocks);
                    yield return replacement;
                    continue;
                }

                if ((instruction.opcode == OpCodes.Call ||
                     instruction.opcode == OpCodes.Callvirt) &&
                    Equals(instruction.operand, isCharacterControlled))
                {
                    replacedControlGate++;
                    var replacement = new CodeInstruction(
                        OpCodes.Call,
                        controlReplacement);

                    replacement.labels.AddRange(instruction.labels);
                    replacement.blocks.AddRange(instruction.blocks);
                    yield return replacement;
                    continue;
                }

                yield return instruction;
            }

            if (replacedHeroGate != 1 || replacedControlGate != 1)
            {
                throw new InvalidOperationException(
                    "Expected one CharacterActionLootGround Hero gate and control gate, " +
                    $"replaced {replacedHeroGate} and {replacedControlGate}.");
            }
        }
    }

    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    internal static class RefreshHover_Patch
    {
        [UsedImplicitly]
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(CursorLocationExplorationCharacter),
                "RefreshHover",
                Type.EmptyTypes);
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var actorReplacement = AccessTools.Method(
                typeof(CharacterActionLootGroundPatcher),
                nameof(ResolveLootActor));
            var replacedHeroGate = 0;

            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Isinst &&
                    instruction.operand as Type == typeof(RulesetCharacterHero))
                {
                    replacedHeroGate++;
                    var replacement = new CodeInstruction(
                        OpCodes.Call,
                        actorReplacement);

                    replacement.labels.AddRange(instruction.labels);
                    replacement.blocks.AddRange(instruction.blocks);
                    yield return replacement;
                    continue;
                }

                yield return instruction;
            }

            if (replacedHeroGate != 1)
            {
                throw new InvalidOperationException(
                    "Expected one CursorLocationExplorationCharacter.RefreshHover Hero gate, " +
                    $"replaced {replacedHeroGate}.");
            }
        }
    }

    private static RulesetCharacter ResolveLootActor(RulesetCharacter character)
    {
        if (character is RulesetCharacterHero)
        {
            return character;
        }

        if (character is not RulesetCharacterSimulacrum duplicate ||
            !SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
        {
            return null;
        }

        return character;
    }

    private static bool IsLootCharacterControlled(
        PlayerController controller,
        GameLocationCharacter locationCharacter)
    {
        var nativeResult = controller?.IsCharacterControlled(locationCharacter) == true;

        if (locationCharacter?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate)
        {
            return nativeResult;
        }

        if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
        {
            return false;
        }

        if (nativeResult)
        {
            return true;
        }

        var ownerControlled = SimulacrumBehavior.TryGetOwner(duplicate, out var owner) &&
                              GameLocationCharacter.GetFromActor(owner) is { } ownerLocation &&
                              controller?.IsCharacterControlled(ownerLocation) == true;

        return ownerControlled;
    }
}
