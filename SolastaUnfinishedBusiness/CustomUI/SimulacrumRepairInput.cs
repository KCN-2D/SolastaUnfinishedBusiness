using System.Collections.Generic;
using HarmonyLib;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

/// <summary>
/// Bridges the Simulacrum rest activity to the game's number-selection modal.
/// The repair power must not be spent until the player confirms a value.
/// </summary>
internal static class SimulacrumRepairInput
{
    private const int RepairModalSortingOrder = 30000;
    private static RepairSelection _selection;
    private static readonly Dictionary<ulong, int> ConfirmedRequests = [];
    private static readonly HashSet<ulong> ConfirmedExecutions = [];
    private static readonly HashSet<ulong> SuccessfulExecutions = [];

    internal static bool TryOpen(AfterRestActionItem item)
    {
        if (!SimulacrumBehavior.IsRepairActivity(item))
        {
            return false;
        }

        var owner = item.Hero;
        var ownerGuid = owner.Guid;

        if (ConfirmedExecutions.Contains(ownerGuid))
        {
            return false;
        }

        if (ConfirmedRequests.Remove(ownerGuid))
        {
            SimulacrumDiagnostics.RecordRepair(owner, "stale-request-cleared", 0, 0);
        }

        if (_selection != null)
        {
            return true;
        }

        if (!SimulacrumBehavior.TryGetMaximumRepairHitPoints(owner, out var maximum))
        {
            SimulacrumDiagnostics.RecordRepair(owner, "selection-unavailable", 0, 0);
            Gui.GuiService.ShowAlert("Failure/&SimulacrumInsufficientRepairMaterials", Gui.ColorFailure, 2.5f);

            return true;
        }

        var modal = Gui.GuiService.GetScreen<NumberSelectionModal>();
        var parentScreen = item.GetComponentInParent<GuiScreen>();
        var previousSortIndex = modal.SortIndex;

        _selection = new RepairSelection(
            item,
            modal,
            owner,
            previousSortIndex);
        modal.ShowPower(SimulacrumBehavior.RepairPower, 1, maximum, maximum, null);

        if (Traverse.Create(modal).Field<GuiLabel>("instructionsCaption").Value is { } instructions)
        {
            instructions.Text = Gui.Localize("Feature/&PowerSimulacrumRepairSelectionTitle");
        }

        // The rest activity screen and NumberSelectionModal are independent overlay
        // screens. SortIndex controls input priority, but does not guarantee that their
        // canvases are drawn in the same order. Give the selector a temporary nested
        // canvas and move it to the end of the shared hierarchy so its controls are both
        // visible and raycastable while the rest screen remains open.
        if (parentScreen)
        {
            modal.SortIndex = parentScreen.SortIndex + 1;
        }

        Gui.GuiService.ResetOverlayCanvasSortingOrder();
        modal.transform.SetAsLastSibling();
        _selection.ApplyForeground(parentScreen);
        SimulacrumDiagnostics.RecordRepair(
            owner,
            "selection-open",
            0,
            maximum,
            $"parentSort={parentScreen?.SortIndex.ToString() ?? "<none>"} " +
            $"modalSort={modal.SortIndex} " +
            $"parentCanvas={parentScreen?.GetComponentInParent<Canvas>()?.sortingOrder.ToString() ?? "<none>"} " +
            $"modalCanvas={_selection.ForegroundCanvas?.sortingOrder.ToString() ?? "<none>"} " +
            $"sibling={modal.transform.GetSiblingIndex()}");

        return true;
    }

    internal static bool TryConfirm(NumberSelectionModal modal, int requestedHitPoints)
    {
        if (!TryTake(modal, out var selection))
        {
            return false;
        }

        var owner = selection.Owner;
        var ownerGuid = owner.Guid;

        if (!SimulacrumBehavior.TryGetMaximumRepairHitPoints(owner, out var maximum))
        {
            Close(selection, modal);
            SimulacrumDiagnostics.RecordRepair(owner, "selection-confirm-rejected", requestedHitPoints, 0);
            Gui.GuiService.ShowAlert(
                "Failure/&SimulacrumInsufficientRepairMaterials",
                Gui.ColorFailure,
                2.5f);

            return true;
        }

        requestedHitPoints = System.Math.Min(requestedHitPoints, maximum);

        if (!selection.Item)
        {
            Close(selection, modal);
            SimulacrumDiagnostics.RecordRepair(
                owner,
                "selection-item-expired",
                requestedHitPoints,
                maximum);

            return true;
        }

        SimulacrumDiagnostics.RecordRepair(owner, "selection-confirm", requestedHitPoints, maximum);
        ConfirmedRequests[ownerGuid] = requestedHitPoints;
        ConfirmedExecutions.Add(ownerGuid);
        Close(selection, modal);

        try
        {
            selection.Item.OnExecuteCb();
        }
        finally
        {
            ConfirmedExecutions.Remove(ownerGuid);
        }

        return true;
    }

    internal static bool TryCancel(NumberSelectionModal modal)
    {
        if (!TryTake(modal, out var selection))
        {
            return false;
        }

        Close(selection, modal);
        SimulacrumDiagnostics.RecordRepair(selection.Owner, "selection-cancel", 0, 0);

        return true;
    }

    internal static void OnEndHide(NumberSelectionModal modal)
    {
        if (TryTake(modal, out var selection))
        {
            RestoreSortIndex(selection);
            SimulacrumDiagnostics.RecordRepair(selection.Owner, "selection-hide", 0, 0);
        }
    }

    internal static bool TryTakeConfirmedRequest(RulesetCharacter owner, out int requestedHitPoints)
    {
        requestedHitPoints = 0;

        return owner != null &&
               ConfirmedRequests.TryGetValue(owner.Guid, out requestedHitPoints) &&
               ConfirmedRequests.Remove(owner.Guid);
    }

    internal static void BeginExecution(RulesetCharacter owner)
    {
        if (owner != null)
        {
            SuccessfulExecutions.Remove(owner.Guid);
        }
    }

    internal static void AbortExecution(RulesetCharacter owner)
    {
        if (owner == null)
        {
            return;
        }

        ConfirmedRequests.Remove(owner.Guid);
        ConfirmedExecutions.Remove(owner.Guid);
        SuccessfulExecutions.Remove(owner.Guid);
    }

    internal static void MarkExecutionSuccessful(RulesetCharacter owner)
    {
        if (owner != null)
        {
            SuccessfulExecutions.Add(owner.Guid);
        }
    }

    internal static bool TryTakeSuccessfulExecution(RulesetCharacter owner)
    {
        return owner != null && SuccessfulExecutions.Remove(owner.Guid);
    }

    private static bool TryTake(NumberSelectionModal modal, out RepairSelection selection)
    {
        selection = _selection;

        if (selection?.Modal != modal)
        {
            return false;
        }

        _selection = null;

        return true;
    }

    private static void Close(RepairSelection selection, NumberSelectionModal modal)
    {
        RestoreSortIndex(selection);
        modal.Hide(false);
        Gui.GuiService.ResetOverlayCanvasSortingOrder();
    }

    private static void RestoreSortIndex(RepairSelection selection)
    {
        if (selection?.Modal == null)
        {
            return;
        }

        selection.RestoreForeground();
        selection.Modal.SortIndex = selection.PreviousSortIndex;
    }

    private sealed class RepairSelection
    {
        private ForegroundCanvasState _foregroundCanvasState;

        internal RepairSelection(
            AfterRestActionItem item,
            NumberSelectionModal modal,
            RulesetCharacterHero owner,
            int previousSortIndex)
        {
            Item = item;
            Modal = modal;
            Owner = owner;
            PreviousSortIndex = previousSortIndex;
            PreviousSiblingIndex = modal.transform.GetSiblingIndex();
        }

        internal AfterRestActionItem Item { get; }
        internal NumberSelectionModal Modal { get; }
        internal RulesetCharacterHero Owner { get; }
        internal int PreviousSortIndex { get; }
        private int PreviousSiblingIndex { get; }
        internal Canvas ForegroundCanvas => _foregroundCanvasState?.Canvas;

        internal void ApplyForeground(GuiScreen parentScreen)
        {
            RestoreForeground();

            var canvas = Modal.GetComponent<Canvas>();
            var addedCanvas = !canvas;

            if (addedCanvas)
            {
                canvas = Modal.gameObject.AddComponent<Canvas>();
            }

            var raycaster = Modal.GetComponent<GraphicRaycaster>();
            var addedRaycaster = addedCanvas && !raycaster;

            if (addedRaycaster)
            {
                raycaster = Modal.gameObject.AddComponent<GraphicRaycaster>();
            }

            _foregroundCanvasState = new ForegroundCanvasState(
                PreviousSiblingIndex,
                canvas,
                addedCanvas,
                raycaster,
                addedRaycaster);

            if (!canvas)
            {
                return;
            }

            var parentCanvas = parentScreen?.GetComponentInParent<Canvas>();

            canvas.overrideSorting = true;

            if (parentCanvas)
            {
                canvas.sortingLayerID = parentCanvas.sortingLayerID;
            }

            canvas.sortingOrder = System.Math.Max(
                RepairModalSortingOrder,
                (parentCanvas?.sortingOrder ?? 0) + 1);
        }

        internal void RestoreForeground()
        {
            _foregroundCanvasState?.Restore(Modal);
            _foregroundCanvasState = null;
        }
    }

    private sealed class ForegroundCanvasState
    {
        private readonly bool _addedCanvas;
        private readonly bool _addedRaycaster;
        private readonly bool _overrideSorting;
        private readonly int _previousSiblingIndex;
        private readonly int _sortingLayerId;
        private readonly int _sortingOrder;
        private readonly GraphicRaycaster _raycaster;

        internal ForegroundCanvasState(
            int previousSiblingIndex,
            Canvas canvas,
            bool addedCanvas,
            GraphicRaycaster raycaster,
            bool addedRaycaster)
        {
            _previousSiblingIndex = previousSiblingIndex;
            Canvas = canvas;
            _addedCanvas = addedCanvas;
            _raycaster = raycaster;
            _addedRaycaster = addedRaycaster;

            if (!canvas)
            {
                return;
            }

            _overrideSorting = canvas.overrideSorting;
            _sortingLayerId = canvas.sortingLayerID;
            _sortingOrder = canvas.sortingOrder;
        }

        internal Canvas Canvas { get; }

        internal void Restore(NumberSelectionModal modal)
        {
            if (modal && modal.transform.parent)
            {
                modal.transform.SetSiblingIndex(
                    System.Math.Min(
                        _previousSiblingIndex,
                        modal.transform.parent.childCount - 1));
            }

            if (!Canvas)
            {
                return;
            }

            if (_addedCanvas)
            {
                if (_addedRaycaster && _raycaster)
                {
                    Object.DestroyImmediate(_raycaster);
                }

                Object.DestroyImmediate(Canvas);

                return;
            }

            Canvas.overrideSorting = _overrideSorting;
            Canvas.sortingLayerID = _sortingLayerId;
            Canvas.sortingOrder = _sortingOrder;
        }
    }
}
