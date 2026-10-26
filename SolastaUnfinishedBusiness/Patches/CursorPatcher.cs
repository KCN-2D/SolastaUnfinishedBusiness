using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using TA;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CursorPatcher
{
    [HarmonyPatch(typeof(CursorLocationExplorationCharacter), nameof(CursorLocationExplorationCharacter.WouldBenefitFromPlanarMode),
        MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExplorationPlanarSelection_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CursorLocationExplorationCharacter __instance, ref bool __result) =>
            __result |= FreeJumpContext.CanUseExplorationPlanarSelection(__instance);
    }

    [HarmonyPatch(typeof(CursorLocationExplorationCharacter), nameof(CursorLocationExplorationCharacter.RefreshMovementHelper))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshExplorationMovementHelper_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CursorLocationExplorationCharacter __instance) =>
            !FreeJumpContext.TryRefreshExplorationMovementHelper(__instance);
    }

    [HarmonyPatch(typeof(CursorLocationExplorationCharacter), nameof(CursorLocationExplorationCharacter.RefreshChainHelper))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshExplorationChainHelper_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CursorLocationExplorationCharacter __instance) =>
            !FreeJumpContext.OwnsExplorationChainHelper(__instance);
    }

    [HarmonyPatch(typeof(CursorLocationExplorationCharacter), nameof(CursorLocationExplorationCharacter.RefreshHover))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshExplorationHover_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CursorLocationExplorationCharacter __instance) =>
            FreeJumpContext.RefreshExplorationSelection(__instance);
    }

    [HarmonyPatch(typeof(CursorLocationExplorationCharacter), nameof(CursorLocationExplorationCharacter.ExecuteMainClick))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteExplorationMainClick_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CursorLocationExplorationCharacter __instance,
            ref CursorDefinitions.CursorActionResult __0)
        {
            if (!FreeJumpContext.TryPrepareExplorationClick(__instance, out var valid) || valid)
            {
                return true;
            }

            __0 = CursorDefinitions.CursorActionResult.Invalid;
            return false;
        }
    }

    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ClearExplorationJumpPreview_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CursorLocationExplorationCharacter),
                nameof(CursorLocationExplorationCharacter.Deactivate));
            yield return AccessTools.Method(typeof(CursorLocationExplorationCharacter),
                nameof(CursorLocationExplorationCharacter.LocationUnloading));
        }

        [UsedImplicitly]
        public static void Prefix(CursorLocationExplorationCharacter __instance) =>
            FreeJumpContext.ClearExplorationPreview(__instance);
    }

    [HarmonyPatch(typeof(CursorLocation), nameof(CursorLocation.ValidateHorizontalHit))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ValidateHorizontalHit_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CursorLocation __instance, RaycastGridHit gridHit, ref int3 position,
            ref bool __result)
        {
            if (__result ||
                __instance is not CursorLocationExplorationCharacter { NeedsClimbingFeedback: true } ||
                (gridHit.SideHit & CellFlags.Side.AllHorizontalSides) == CellFlags.Side.None)
            {
                return;
            }

            // Native picking requires a floor even for wall walkers. Accept the adjacent cell
            // it chose from the ray only when this character can occupy its climbable wall.
            // This also applies to the first picking pass, before a floor behind the wall wins.
            var character = __instance.SelectionService.SelectedCharacters[0];
            var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

            __result = positioningService.CanPlaceCharacter(character, position,
                           CellHelpers.PlacementMode.PlanningStation) &&
                       positioningService.CanCharacterStayAtPosition_Walls(character, position, false);
        }
    }

    [HarmonyPatch(typeof(Cursor), nameof(Cursor.OnClickSecondaryPointer))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnClickSecondaryPointer_Patch
    {
        [UsedImplicitly]
        public static void Postfix(Cursor __instance)
        {
            //PATCH: Enable cancel action on right mouse click
            if (!Main.Settings.EnableCancelEditOnRightMouseClick)
            {
                return;
            }

            if (__instance is CursorCampaignDefault or CursorEditableGraphDefault or CursorLocationBattleDefault
                or CursorLocationEditorDefault or CursorLocationExplorationDefault)
            {
                return;
            }

            var screen = Gui.CurrentLocationScreen;

            // Don't use ?? on Unity object
            if (!screen)
            {
                screen = Gui.GuiService.GetScreen<UserLocationEditorScreen>();
            }

            if (!screen || !screen.Visible)
            {
                return;
            }

            // ReSharper disable once InvocationIsSkipped
            Main.Log($"Cancelling {screen.GetType().Name} cursor");
            screen.HandleInput(InputCommands.Id.Cancel);
        }
    }
}
