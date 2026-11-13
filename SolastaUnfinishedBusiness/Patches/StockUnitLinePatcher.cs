using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.ItemCrafting;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class StockUnitLinePatcher
{
    private static Transform GetRecipeItem(Transform factionGroup, bool justRetrieve)
    {
        const string NAME = "RECIPE_ITEM";

        var t = factionGroup.parent.Find(NAME);

        if (t || justRetrieve)
        {
            return t;
        }

        var item = Object.Instantiate(factionGroup.gameObject, factionGroup.parent);
        item.name = NAME;
        item.SetActive(true);
        t = item.GetComponent<RectTransform>();
        t.localPosition = new Vector3(-100, 0);
        t.localScale = new Vector3(1.5f, 1.5f, 1f);

        return t;
    }

    private static Image SetupCraftedItem(Transform t, BaseDefinition item)
    {
        if (!t)
        {
            return null;
        }

        var img = t.Find("IncompatibleImage").GetComponent<Image>();
        var tooltip = t.GetComponent<GuiTooltip>();
        if (item)
        {
            img.color = Color.white;
            img.ClearAddressableSprite();
            img.sprite = img.LoadAddressableSprite(item.GuiPresentation.SpriteReference);
            ServiceRepository.GetService<IGuiWrapperService>()
                .GetGuiItemDefinition(item.Name)
                .SetupTooltip(tooltip);
        }
        else if (img.sprite)
        {
            img.ClearAddressableSprite();
            tooltip.Clear();
        }

        return img;
    }

    [HarmonyPatch(typeof(StockUnitLine), nameof(StockUnitLine.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(StockUnitLine __instance)
        {
            var crafted = Main.Settings.ShowCraftedItemOnRecipeIcon
                ? RecipeHelper.GetCraftedItem(__instance.StockUnit.ItemDefinition)
                : null;
            var spell = !crafted && SettingsContext.GuiModManagerInstance.ShowSpellIconOnScrolls
                ? ScrollsData.GetScrollSpell(__instance.StockUnit.ItemDefinition)
                : null;
            var item = GetRecipeItem(__instance.factionIncompatibleGroup, !crafted && !spell);
            __instance.itemImage.transform.localPosition = new Vector3(crafted || spell ? 58 : 33, 0, 0);
            if (!item) return;
            item.gameObject.SetActive(crafted || spell);

            if (crafted)
            {
                var img = SetupCraftedItem(item, crafted);

                if (Main.Settings.SwapCraftedItemAndRecipeIcons)
                {
                    __instance.itemImage.SwapAddressableSprites(img);
                }
            }
            else if (spell)
            {
                ScrollsData.SetupScrollSpellImage(item, spell);
            }
            else
            {
                SetupCraftedItem(item, null);
            }
        }
    }

    [HarmonyPatch(typeof(StockUnitLine), nameof(StockUnitLine.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(StockUnitLine __instance)
        {
            // Native Unbind releases the bitmap loaded for the stock item.
            __instance.itemImage.RestoreAddressableSpriteSwap();
        }

        [UsedImplicitly]
        public static void Postfix(StockUnitLine __instance)
        {
            var item = GetRecipeItem(__instance.factionIncompatibleGroup, true);

            SetupCraftedItem(item, null);
        }
    }
}
