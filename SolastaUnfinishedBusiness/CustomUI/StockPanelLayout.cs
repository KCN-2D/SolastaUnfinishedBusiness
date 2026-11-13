using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class StockPanelLayout : MonoBehaviour
{
    private StockPanel panel;
    private bool tableActive;
    private bool gridActive;

    internal static void Refresh(StockPanel owner, float multiplier)
    {
        var layout = owner.GetComponent<StockPanelLayout>() ?? owner.gameObject.AddComponent<StockPanelLayout>();
        layout.panel = owner;
        layout.tableActive = owner.stockTableSW.gameObject.activeSelf;
        layout.gridActive = owner.stockGridSW.gameObject.activeSelf;
        layout.RefreshRows(owner.stockTable, owner.stockUnitLinePrefab, layout.tableActive, multiplier);
        layout.RefreshRows(owner.stockGrid, owner.stockUnitCellPrefab, layout.gridActive, multiplier);
    }

    internal static void RefreshIfNeeded(StockPanel owner)
    {
        var layout = owner.GetComponent<StockPanelLayout>();
        if (layout && owner.GameMerchant != null &&
            (layout.tableActive != owner.stockTableSW.gameObject.activeSelf ||
             layout.gridActive != owner.stockGridSW.gameObject.activeSelf)) owner.RefreshNow();
    }

    [UsedImplicitly]
    private void LateUpdate()
    {
        if (panel) RefreshIfNeeded(panel);
    }

    private void RefreshRows(RectTransform parent, GameObject prefab, bool active, float multiplier)
    {
        var count = active ? panel.displayedUnits.Count : 0;
        while (parent.childCount < count)
        {
            // Activate after allocation, when the native row is ready to bind.
            var instance = Gui.GuiService.PrefabPoolManager.GetElement(prefab, false);
            var rect = (RectTransform)instance.transform;
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            rect.anchoredPosition = Vector2.zero;
        }

        var treasury = Gui.GameCampaign.Party.Treasury;
        StockUnitLine.UnitSelectedHandler selected = panel.UnitSelected;
        for (var index = 0; index < parent.childCount; index++)
        {
            var child = parent.GetChild(index);
            var row = child.GetComponent<StockUnitLine>();
            if (index >= count)
            {
                if (row.StockUnit != null) row.Unbind();
                if (child.gameObject.activeSelf) child.gameObject.SetActive(false);
                continue;
            }

            var unit = panel.displayedUnits[index];
            var stack = unit.ItemDefinition.CanBeStacked
                ? unit.UserItem?.StackCount ?? unit.StockUnitDescription?.StackCount ?? 1
                : 1;
            EquipmentDefinitions.ScaleAndRoundCosts(multiplier, unit.ItemDefinition.Costs, panel.scaledItemCosts);
            for (var currency = 0; currency < panel.scaledItemCosts.Length; currency++)
                panel.scaledItemCosts[currency] *= stack;
            EquipmentDefinitions.SimplifyCosts(panel.scaledItemCosts);
            child.gameObject.SetActive(true);
            row.Bind(panel.Buyer, unit, selected, panel.scaledItemCosts,
                treasury.CanAfford(panel.scaledItemCosts), panel.RectTransform, panel.anchorMode);
            row.Tooltip.SecondaryTooltip = ContainerPanel.BuildCompareasonTooltip(
                unit.UserItem?.ItemDefinition ?? unit.ItemDefinition, panel.InventoryPanel.CompareasonSlots);
        }

        if (active) LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
    }
}
