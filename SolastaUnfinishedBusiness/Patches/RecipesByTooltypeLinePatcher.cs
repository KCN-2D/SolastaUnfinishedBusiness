using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RecipesByTooltypeLinePatcher
{
    [HarmonyPatch(typeof(RecipesByTooltypeLine), nameof(RecipesByTooltypeLine.Load))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Load_Patch
    {
        [UsedImplicitly]
        public static void Prefix(List<RecipeDefinition> recipes)
        {
            //PATCH: sort the recipes by crafted item title
            recipes.Sort(Sorting.CompareTitle);
        }
    }

    [HarmonyPatch(typeof(RecipesByTooltypeLine), nameof(RecipesByTooltypeLine.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ref List<RecipeDefinition> knownRecipes, out IDisposable __state)
        {
            //PATCH: adds a filter to the crafting panel screen
            CraftingContext.FilterRecipes(ref knownRecipes);
            __state = CraftingContext.BeginRecipeLineRefresh(knownRecipes);
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();

            return __exception;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceCall(
                instructions,
                AccessTools.Method(typeof(List<RecipeDefinition>), nameof(List<RecipeDefinition>.Contains)),
                "RecipesByTooltypeLine.Refresh.KnownRecipes",
                new CodeInstruction(OpCodes.Call,
                    new Func<List<RecipeDefinition>, RecipeDefinition, bool>(CraftingContext.IsKnownRecipe).Method));
        }
    }

    [HarmonyPatch(typeof(CraftingPanel), nameof(CraftingPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CraftingPanelRefresh_Patch
    {
        [UsedImplicitly]
        public static void Prefix(out IDisposable __state)
        {
            __state = CraftingContext.BeginRefresh();
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, IDisposable __state)
        {
            __state?.Dispose();

            return __exception;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceCall(
                instructions,
                AccessTools.PropertyGetter(typeof(Gui), nameof(Gui.GamepadActive)),
                "CraftingPanel.Refresh.CompleteRecipeLayout",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    new Func<CraftingPanel, bool>(CraftingContext.CompleteRecipeLayout).Method));
        }
    }

    [HarmonyPatch(typeof(RecipeItem), nameof(RecipeItem.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RecipeItemRefresh_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceCall(
                instructions,
                AccessTools.Method(typeof(Game), nameof(Game.CountItemsOfTypeInParty)),
                "RecipeItem.Refresh.CountIngredients",
                new CodeInstruction(OpCodes.Call,
                    new Func<Game, ItemDefinition, int>(CraftingContext.CountItemsOfTypeInParty).Method));
        }
    }

    private static IEnumerable<CodeInstruction> ReplaceCall(
        IEnumerable<CodeInstruction> instructions,
        System.Reflection.MethodInfo method,
        string context,
        params CodeInstruction[] replacement)
    {
        var code = instructions.ToList();

        if (method == null || code.Count(instruction => instruction.Calls(method)) != 1)
        {
            Main.Error($"Failed to apply transpiler patch [{context}]: expected exactly one native call.");

            return code;
        }

        var index = code.FindIndex(instruction => instruction.Calls(method));

        // The native final layout branch targets this call directly. Keep its labels and
        // exception boundaries on the first replacement instruction so every path executes it.
        replacement[0].labels.AddRange(code[index].labels);
        replacement[0].blocks.AddRange(code[index].blocks);
        code.RemoveAt(index);
        code.InsertRange(index, replacement);

        return code;
    }
}
