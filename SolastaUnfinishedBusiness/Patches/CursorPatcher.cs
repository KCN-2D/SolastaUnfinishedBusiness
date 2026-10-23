using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using TA;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CursorPatcher
{
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
