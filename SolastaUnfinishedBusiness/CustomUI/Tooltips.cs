using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static RuleDefinitions.EffectDifficultyClassComputation;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class Tooltips
{
    private const string DistanceTextAnchorObjectName = "DistanceTextAnchorObject";

    private static GameObject _tooltipInfoCharacterDescription;
    private static GameObject _distanceTextObject;
    private static TextMeshProUGUI _tmpUGui;

    internal static void Unload(bool destroyUnityObjects)
    {
        if (destroyUnityObjects && _distanceTextObject)
        {
            var parent = _distanceTextObject.transform.parent;
            var owner = parent && parent.name == DistanceTextAnchorObjectName
                ? parent.gameObject
                : _distanceTextObject;

            Object.Destroy(owner);
        }

        _tooltipInfoCharacterDescription = null;
        _distanceTextObject = null;
        _tmpUGui = null;
    }

    internal static void AddContextToRecoveredFeature(RecoveredFeatureItem item, RulesetCharacterHero character)
    {
        item.GuiTooltip.Context = character;
    }

    internal static void UpdatePowerUses(ITooltip tooltip, TooltipFeaturePowerParameters parameters)
    {
        if (tooltip.DataProvider is not GuiPowerDefinition guiPowerDefinition)
        {
            return;
        }

        if (tooltip.Context is not RulesetCharacter character)
        {
            return;
        }

        var power = guiPowerDefinition.PowerDefinition;
        var usesLabel = parameters.usesLabel;

        usesLabel.Text = FormatUses(power, character, usesLabel.Text);
    }

    private static string FormatUses(FeatureDefinitionPower power, RulesetCharacter character, string def)
    {
        if (power.UsesDetermination != RuleDefinitions.UsesDetermination.Fixed)
        {
            return def;
        }

        if (power.RechargeRate == RuleDefinitions.RechargeRate.AtWill)
        {
            return def;
        }

        if (power.CostPerUse == 0)
        {
            return def;
        }

        var usablePower = PowerProvider.Get(power, character);
        var maxUses = character.GetMaxUsesOfPower(usablePower);
        // must use GetRemainingPowerUses as power could be a Shared Pool
        var remainingUses = character.GetRemainingPowerUses(power);

        return $"{remainingUses}/{maxUses}";
    }

    internal static void UpdatePowerSaveDC(ITooltip tooltip, TooltipFeaturePowerParameters parameters)
    {
        if (tooltip.DataProvider is not GuiPowerDefinition guiPowerDefinition)
        {
            return;
        }

        if (!guiPowerDefinition.HasSavingThrow)
        {
            return;
        }

        var effectDescription = guiPowerDefinition.EffectDescription;
        if (effectDescription.DifficultyClassComputation
            is not (AbilityScoreAndProficiency or SpellCastingFeature))
        {
            return;
        }

        if (tooltip.Context is not RulesetCharacter character)
        {
            return;
        }

        var attribute = DatabaseRepository.GetDatabase<SmartAttributeDefinition>()
            .GetElement(effectDescription.SavingThrowAbility);

        var power = guiPowerDefinition.PowerDefinition;
        var classDefinition = character.FindClassHoldingFeature(power);
        var saveDC = EffectHelpers.CalculateSaveDc(character, effectDescription, classDefinition);

        parameters.savingThrowLabel.Text = Gui.Format("{0} {1}", attribute.GuiPresentation.Title, saveDC.ToString());
    }

    internal static void UpdateCraftingTooltip(TooltipFeatureDescription description, ITooltip tooltip)
    {
        if (!Main.Settings.ShowCraftingRecipeInDetailedTooltips)
        {
            return;
        }

        if (tooltip.DataProvider is not IItemDefinitionProvider itemDefinitionProvider)
        {
            return;
        }

        var item = itemDefinitionProvider.ItemDefinition;

        if (!item.IsDocument || item.DocumentDescription.LoreType != RuleDefinitions.LoreType.CraftingRecipe)
        {
            return;
        }

        var guiWrapperService = ServiceRepository.GetService<IGuiWrapperService>();

        foreach (var contentFragmentDescription in item.DocumentDescription.ContentFragments
                     .Where(x => x.Type == ContentFragmentDescription.FragmentType.Body))
        {
            var guiRecipeDefinition =
                guiWrapperService.GetGuiRecipeDefinition(item.DocumentDescription.RecipeDefinition.Name);

            description.DescriptionLabel.Text = Gui.Format(contentFragmentDescription.Text,
                guiRecipeDefinition.Title, guiRecipeDefinition.IngredientsText);
        }
    }

    internal static void AddDistanceToTooltip(EntityDescription entityDescription)
    {
        _tooltipInfoCharacterDescription ??= GameObject.Find("TooltipFeatureCharacterDescription");

        if (_tooltipInfoCharacterDescription is null)
        {
            return;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();

        if (Main.Settings.EnableDistanceOnTooltip && battleService.Battle is not null)
        {
            entityDescription.header += "<br><br>";

            GameLocationCharacter characterToMeasureFrom = null;
            var distance = GetDistanceFromCharacter(ref characterToMeasureFrom, battleService);

            if (characterToMeasureFrom is null)
            {
                return;
            }

            // don't use ? on a type deriving from a unity object
            if (_tooltipInfoCharacterDescription)
            {
                _tmpUGui ??= _tooltipInfoCharacterDescription.transform.GetComponentInChildren<TextMeshProUGUI>();
            }

            if (!_distanceTextObject)
            {
                GenerateDistanceText(distance, _tmpUGui, characterToMeasureFrom);
            }
            else
            {
                UpdateDistanceText(distance, characterToMeasureFrom);
            }

            // don't use ? on a type deriving from a unity object
#pragma warning disable IDE0031
            if (_distanceTextObject)
#pragma warning restore IDE0031
            {
                _distanceTextObject.SetActive(true);
            }
        }
        else if (!Main.Settings.EnableDistanceOnTooltip || battleService.Battle is null)
        {
            // don't use ? on a type deriving from a unity object
#pragma warning disable IDE0031
            if (_distanceTextObject)
#pragma warning restore IDE0031
            {
                _distanceTextObject.SetActive(false);
            }
        }
    }

    private static void GenerateDistanceText(int distance, TextMeshProUGUI tmpUGui,
        GameLocationCharacter characterToMeasureFrom)
    {
        var anchorObject = new GameObject();

        anchorObject.name = DistanceTextAnchorObjectName;
        anchorObject.transform.SetParent(tmpUGui.transform, false);
        anchorObject.transform.localPosition = Vector3.zero;
        anchorObject.transform.localRotation = Quaternion.identity;
        anchorObject.transform.localScale = Vector3.one;
        _distanceTextObject = Object.Instantiate(tmpUGui).gameObject;
        _distanceTextObject.name = "DistanceTextObject";
        _distanceTextObject.transform.SetParent(anchorObject.transform, false);
        _distanceTextObject.transform.localPosition = new Vector3(0, -10, 0);
        _distanceTextObject.transform.localRotation = Quaternion.identity;
        _distanceTextObject.transform.localScale = Vector3.one;

        UpdateDistanceText(distance, characterToMeasureFrom);
    }

    private static int GetDistanceFromCharacter(
        ref GameLocationCharacter characterToMeasureFrom,
        IGameLocationBattleService battleService)
    {
        var gameLocationSelectionService = ServiceRepository.GetService<IGameLocationSelectionService>();

        if (gameLocationSelectionService.HoveredCharacters.Count is 0)
        {
            return 0;
        }

        var hoveredCharacter = gameLocationSelectionService.HoveredCharacters[0];
        var initiativeSortedContenders = battleService.Battle.InitiativeSortedContenders;
        var activePlayerController = ServiceRepository.GetService<IPlayerControllerService>().ActivePlayerController;
        var activePlayerControlledCharacters = activePlayerController.ControlledCharacters;
        var actingCharacter = battleService.Battle?.activeContender;

        if (actingCharacter is null)
        {
            return 0;
        }

        characterToMeasureFrom = activePlayerControlledCharacters.Contains(actingCharacter)
            ? actingCharacter
            : GetNextControlledCharacterInInitiative(
                initiativeSortedContenders, activePlayerController, actingCharacter);

        return (int)Math.Round(DistanceCalculation.GetDistanceFromCharacters(characterToMeasureFrom, hoveredCharacter));
    }

    private static GameLocationCharacter GetNextControlledCharacterInInitiative(
        List<GameLocationCharacter> initiativeSortedContenders,
        PlayerController activePlayerController,
        GameLocationCharacter actingCharacter)
    {
        return initiativeSortedContenders.Find(character =>
            character.controllerId == activePlayerController.controllerId
            && character.lastInitiative < actingCharacter.lastInitiative) ?? initiativeSortedContenders.Find(
            character =>
                character.controllerId == activePlayerController.controllerId);
    }

    private static void UpdateDistanceText(int distance, GameLocationCharacter characterToMeasureFrom)
    {
        _distanceTextObject.GetComponent<TextMeshProUGUI>().text =
            Gui.Format("UI/&DistanceFormat", Gui.FormatDistance(distance))
            + $" {Gui.Localize("UI/&From")} "
            + GetReducedName(characterToMeasureFrom.Name);
    }

    private static string GetReducedName(string characterName)
    {
        return characterName.Length >= 12
            ? characterName.Substring(0, 9) + "..."
            : characterName;
    }

    internal static void ModifyWidth<TMod, TParent>(TParent parent)
        where TMod : BaseTooltipWidthModifier<TParent>
        where TParent : MonoBehaviour
    {
        if (!parent.TryGetComponent<TMod>(out var component))
        {
            component = parent.gameObject.AddComponent<TMod>();
            component.Init(parent);
        }

        component.Apply();
    }

    internal static void ModifyWidth(TooltipFeature parent)
    {
        if (!parent.TryGetComponent<TooltipFeatureWidthMod>(out var component))
        {
            component = parent.gameObject.AddComponent<TooltipFeatureWidthMod>();
            component.Init(parent);
        }

        component.Apply();
    }

    internal static void NormalizeSpellAdvancement(TooltipFeatureSpellAdvancement parent)
    {
        if (!parent || !parent.advancementLabel)
        {
            return;
        }

        parent.advancementLabel.Text = UiTextHelpers.NormalizeSpellLevelBodyText(parent.advancementLabel.Text);
    }

    internal static void RefreshAdaptiveSpellParameterTopRow(TooltipFeatureSpellParameters parent)
    {
        RefreshAdaptiveParameterTopRow(parent?.verticalLayout);
    }

    internal static void RefreshAdaptivePowerParameterTopRow(TooltipFeaturePowerParameters parent)
    {
        RefreshAdaptiveParameterTopRow(parent?.verticalLayout);
    }

    private static void RefreshAdaptiveParameterTopRow(Transform verticalLayout)
    {
        if (!verticalLayout)
        {
            return;
        }

        if (verticalLayout.Find("TopTable") is not RectTransform topTable ||
            !topTable.gameObject.activeInHierarchy ||
            !TryGetTwoActiveParameterGroups(topTable, out var leftGroup, out var rightGroup))
        {
            return;
        }

        var topState = TooltipParameterLayoutState.Get(topTable);

        topState.Restore(topTable);

        var requiredHeight = Mathf.Max(
            topState.OriginalHeight,
            RefreshAdaptiveParameterGroup(leftGroup),
            RefreshAdaptiveParameterGroup(rightGroup));

        requiredHeight = Mathf.Ceil(requiredHeight);
        topTable.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);

        var layout = topState.EnsureLayoutElement(topTable);
        layout.preferredHeight = requiredHeight;
        layout.minHeight = Mathf.Max(layout.minHeight, requiredHeight);

        LayoutRebuilder.ForceRebuildLayoutImmediate(topTable);

        if (verticalLayout is RectTransform verticalRect)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(verticalRect);
        }
    }

    private static bool TryGetTwoActiveParameterGroups(
        RectTransform row,
        out RectTransform leftGroup,
        out RectTransform rightGroup)
    {
        leftGroup = null;
        rightGroup = null;

        for (var i = 0; i < row.childCount; i++)
        {
            if (row.GetChild(i) is not RectTransform child || !child.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (leftGroup == null)
            {
                leftGroup = child;
            }
            else
            {
                rightGroup = child;
                break;
            }
        }

        return leftGroup && rightGroup;
    }

    private static float RefreshAdaptiveParameterGroup(RectTransform group)
    {
        if (!group)
        {
            return 0f;
        }

        var groupState = TooltipParameterLayoutState.Get(group);

        groupState.Restore(group);

        var width = group.rect.width;

        if (width <= 1f)
        {
            return groupState.OriginalHeight;
        }

        var requiredHeight = groupState.OriginalHeight;

        foreach (var text in group.GetComponentsInChildren<TMP_Text>(false))
        {
            if (!text || text.rectTransform == null || !text.gameObject.activeInHierarchy)
            {
                continue;
            }

            var rectTransform = text.rectTransform;
            var textState = TooltipParameterLayoutState.Get(rectTransform);

            textState.Restore(rectTransform);

            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.maxVisibleLines = 99999;

            var preferred = text.GetPreferredValues(text.text, width, 0f);
            var height = Mathf.Ceil(Mathf.Max(textState.OriginalHeight, preferred.y));

            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            requiredHeight = Mathf.Max(requiredHeight, -rectTransform.anchoredPosition.y + height + 2f);
        }

        requiredHeight = Mathf.Ceil(requiredHeight);
        group.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);

        return requiredHeight;
    }
}

internal sealed class TooltipParameterLayoutState : MonoBehaviour
{
    private bool _captured;
    private bool _hadLayoutElement;
    private float _originalMinHeight;
    private float _originalPreferredHeight;
    private float _originalFlexibleHeight;

    internal float OriginalHeight { get; private set; }

    internal static TooltipParameterLayoutState Get(RectTransform rectTransform)
    {
        var state = rectTransform.GetComponent<TooltipParameterLayoutState>() ??
                    rectTransform.gameObject.AddComponent<TooltipParameterLayoutState>();

        state.Capture(rectTransform);

        return state;
    }

    internal LayoutElement EnsureLayoutElement(RectTransform rectTransform)
    {
        return rectTransform.GetComponent<LayoutElement>() ??
               rectTransform.gameObject.AddComponent<LayoutElement>();
    }

    internal void Restore(RectTransform rectTransform)
    {
        if (!_captured || !rectTransform)
        {
            return;
        }

        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, OriginalHeight);

        if (!rectTransform.TryGetComponent<LayoutElement>(out var layout))
        {
            return;
        }

        if (_hadLayoutElement)
        {
            layout.minHeight = _originalMinHeight;
            layout.preferredHeight = _originalPreferredHeight;
            layout.flexibleHeight = _originalFlexibleHeight;
        }
        else
        {
            layout.minHeight = -1f;
            layout.preferredHeight = -1f;
            layout.flexibleHeight = -1f;
        }
    }

    private void Capture(RectTransform rectTransform)
    {
        if (_captured || !rectTransform)
        {
            return;
        }

        OriginalHeight = rectTransform.rect.height;
        _hadLayoutElement = rectTransform.TryGetComponent<LayoutElement>(out var layout);

        if (_hadLayoutElement)
        {
            _originalMinHeight = layout.minHeight;
            _originalPreferredHeight = layout.preferredHeight;
            _originalFlexibleHeight = layout.flexibleHeight;
        }

        _captured = true;
    }
}

internal abstract class BaseTooltipWidthModifier<T> : MonoBehaviour where T : MonoBehaviour
{
    private const int DefWidth = 340;
    private const int Pad = 30; // default is 30?

    protected T Parent;
    protected static int FinalWidth => (int)(SettingsContext.GuiModManagerInstance.TooltipWidth * DefWidth);
    protected static int PaddedWidth => FinalWidth - (2 * Pad);

    internal void Apply()
    {
        Modify();
    }

    internal void Init(T parent)
    {
        Parent = parent;
    }

    protected abstract void Modify();

    protected static void SizeWithAnchors(GuiBehaviour obj, int width)
    {
        if (!obj) { return; }

        SizeWithAnchors(obj.GetComponent<RectTransform>(), width);
    }

    protected static void SizeWithAnchors(Transform t, float width)
    {
        if (!t) { return; }

        SizeWithAnchors(t.GetComponent<RectTransform>(), width);
    }

    protected static void SizeWithAnchors(RectTransform rt, float width)
    {
        if (!rt) { return; }

        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
    }

    protected static void FromEdge(RectTransform rt, float width, float pad = Pad)
    {
        if (!rt) { return; }

        rt.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, pad, width);
    }

    private static RectTransform Rect(Transform t, string path = null)
    {
        if (!t) { return null; }

        if (path == null) { return t.GetComponent<RectTransform>(); }

        t = t.Find(path);
        return !t ? null : t.GetComponent<RectTransform>();
    }

    protected static RectTransform Rect(MonoBehaviour b, string path = null)
    {
        return Rect(b.transform, path);
    }
}

internal class TooltipPanelWidthModifier : BaseTooltipWidthModifier<TooltipPanel>
{
    private const string BackgroundBlur = "BackgroundBlur";
    private const string Frame = "Frame";

    protected override void Modify()
    {
        var width = FinalWidth;

        SizeWithAnchors(Parent.RectTransform, width);
        SizeWithAnchors(Rect(Parent, BackgroundBlur), width);
        SizeWithAnchors(Parent.featuresTable, width);

        var frame = Rect(Parent, Frame);
        if (frame) { SizeWithAnchors(frame, width); }
    }
}

internal class TooltipFeatureWidthMod : BaseTooltipWidthModifier<TooltipFeature>
{
    protected override void Modify()
    {
        SizeWithAnchors(Parent.RectTransform, FinalWidth);
    }
}

internal class TooltipFeatureEffectsEnumWidthMod : BaseTooltipWidthModifier<TooltipFeatureEffectsEnumerator>
{
    protected override void Modify()
    {
        var table = Parent.effectFormater.Table;
        var width = PaddedWidth;
        SizeWithAnchors(table, width);
        for (var i = 0; i < table.childCount; i++)
        {
            var line = table.GetChild(i).GetComponent<FeatureElementEffectLine>();
            if (!line) { continue; }

            SizeWithAnchors(line.RectTransform, width);
            SizeWithAnchors(line.effectLabel.RectTransform, width);
            SizeWithAnchors(line.effectDescription.RectTransform, width);
        }
    }
}

internal class TooltipSubSpellEnumWidthModifier : BaseTooltipWidthModifier<TooltipFeatureSubSpellsEnumerator>
{
    protected override void Modify()
    {
        var table = Parent.table;
        var width = PaddedWidth;
        SizeWithAnchors(table, width);
        for (var i = 0; i < table.childCount; i++)
        {
            var line = table.GetChild(i).GetComponent<FeatureElementSubSpell>();
            if (!line) { continue; }

            SizeWithAnchors(line.RectTransform, width);
        }
    }
}

internal class TooltipFeatureSpellParamsWidthModifier : BaseTooltipWidthModifier<TooltipFeatureSpellParameters>
{
    private const string VerticalLayout = "VerticalLayout";

    protected override void Modify()
    {
        var width = PaddedWidth;
        SizeWithAnchors(Rect(Parent, VerticalLayout), width);

        for (var i = 0; i < Parent.verticalLayout.childCount; i++)
        {
            SizeWithAnchors(Parent.verticalLayout.GetChild(i), width);
        }
    }
}

internal class TooltipFeatureBaseMagicParamsWidthModifier
    : BaseTooltipWidthModifier<TooltipFeatureBaseMagicParameters>
{
    private const string Table = "Table";

    protected override void Modify()
    {
        FromEdge(Rect(Parent, Table), PaddedWidth);
    }
}

internal class TooltipFeatureTagsEnumWidthModifier : BaseTooltipWidthModifier<TooltipFeatureTagsEnumerator>
{
    private const string Label = "Background/PropertiesLabel";


    protected override void Modify()
    {
        var width = PaddedWidth;
        SizeWithAnchors(Parent.table, width);
        SizeWithAnchors(Rect(Parent, Label), width);
    }
}

internal class TooltipFeatureSpellAdvancementWidthMod : BaseTooltipWidthModifier<TooltipFeatureSpellAdvancement>
{
    private const string Title = "Title";
    private const string Label = "AdvancementLabel";

    protected override void Modify()
    {
        var width = PaddedWidth;
        FromEdge(Rect(Parent, Title), width);
        FromEdge(Rect(Parent, Label), width);
    }
}

internal class TooltipFeatureDeviceParametersWidthMod : BaseTooltipWidthModifier<TooltipFeatureDeviceParameters>
{
    protected override void Modify()
    {
        var width = PaddedWidth;
        SizeWithAnchors(Parent.usageGroup, width);
        SizeWithAnchors(Parent.attunementLabel, width);
    }
}

internal class
    TooltipFeatureItemPropertiesEnumWidthMod : BaseTooltipWidthModifier<TooltipFeatureItemPropertiesEnumerator>
{
    protected override void Modify()
    {
        SizeWithAnchors(Parent.propertiesTable, PaddedWidth);
    }
}

internal class TooltipFeatureDeviceFunctionsEnumWidthMod
    : BaseTooltipWidthModifier<TooltipFeatureDeviceFunctionsEnumerator>
{
    protected override void Modify()
    {
        SizeWithAnchors(Parent.functionsTable, PaddedWidth);
    }
}

internal class TooltipFeatureItemStatsWidthMod : BaseTooltipWidthModifier<TooltipFeatureItemStats>
{
    private const string SecondTable = "VerticalLayout/SecondTable";

    protected override void Modify()
    {
        var width = PaddedWidth;
        SizeWithAnchors(Parent.topTable, width);
        SizeWithAnchors(Rect(Parent, SecondTable), width);
    }
}

internal class TooltipFeatureWeaponParametersWidthMod : BaseTooltipWidthModifier<TooltipFeatureWeaponParameters>
{
    protected override void Modify()
    {
        SizeWithAnchors(Parent.masterTable, PaddedWidth);
    }
}

internal class TooltipFeatureArmorParamsWidthMod : BaseTooltipWidthModifier<TooltipFeatureArmorParameters>
{
    private const string HeaderLabel = "HeaderLabel";

    protected override void Modify()
    {
        var width = PaddedWidth;
        SizeWithAnchors(Parent.descriptionLabel, width);
        SizeWithAnchors(Rect(Parent, HeaderLabel), width);
    }
}

internal class TooltipFeatureLightSourceParamsWidthMod : BaseTooltipWidthModifier<TooltipFeatureLightSourceParameters>
{
    private const string HeaderLabel = "HeaderLabel";

    protected override void Modify()
    {
        var width = PaddedWidth;
        SizeWithAnchors(Parent.descriptionLabel, width);
        SizeWithAnchors(Rect(Parent, HeaderLabel), width);
    }
}

internal class TooltipFeaturePowerParamsWidthMod : BaseTooltipWidthModifier<TooltipFeaturePowerParameters>
{
    protected override void Modify()
    {
        var width = PaddedWidth;
        for (var i = 0; i < Parent.verticalLayout.childCount; i++)
        {
            SizeWithAnchors(Parent.verticalLayout.GetChild(i), width);
        }
    }
}

internal class TooltipFeaturePrerequisitesWidthMod : BaseTooltipWidthModifier<TooltipFeaturePrerequisites>
{
    private const string Header = "PrerequisitesTitle";

    protected override void Modify()
    {
        var width = PaddedWidth;
        SizeWithAnchors(Rect(Parent, Header), width);
        SizeWithAnchors(Parent.prerequisitesValue, width);
    }
}
