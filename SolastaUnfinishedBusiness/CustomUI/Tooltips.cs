using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Validators;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static RuleDefinitions.EffectDifficultyClassComputation;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class Tooltips
{
    private const string DistanceTextAnchorObjectName = "DistanceTextAnchorObject";

    private static readonly ConditionalWeakTable<Image, RawImage> HeaderPortraits = new();
    private static readonly ConditionalWeakTable<TooltipPanel, AdaptiveTooltipLayoutState> AdaptiveTooltipLayouts = new();

    private static GameObject _tooltipInfoCharacterDescription;
    private static GameObject _distanceTextObject;
    private static TextMeshProUGUI _tmpUGui;

    internal static void Unload(bool destroyUnityObjects)
    {
        if (destroyUnityObjects)
        {
            foreach (var state in Resources.FindObjectsOfTypeAll<TooltipHeaderImageLayoutState>())
            {
                state.Restore();
                Object.DestroyImmediate(state);
            }
        }

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

    internal static RulesetCharacter ResolveCharacter(ITooltip tooltip)
    {
        return tooltip?.Context switch
        {
            RulesetCharacter rulesetCharacter => rulesetCharacter,
            GameLocationCharacter locationCharacter => locationCharacter.RulesetCharacter,
            GuiCharacter guiCharacter => guiCharacter.RulesetCharacter,
            _ => null
        };
    }

    internal static List<FeatureDefinition> GetConditionFeaturesForTooltip(ConditionDefinition condition)
    {
        var noContent = Gui.Localize(Gui.NoLocalization);
        var formattedText = condition.GetFirstSubFeatureOfType<FormattedDefinitionText>();
        var ownDescription = formattedText?.FormatDescription(string.Empty);
        var hasOwnDescription = !string.IsNullOrWhiteSpace(ownDescription) && ownDescription != noContent;

        // Hidden affinities can still generate useful descriptions (saves, checks, immunities).
        // A shared formatter gives the condition its contextual body; do not print the same feature body again.
        // All other native features retain their generated rules text and the gameplay feature list is untouched.
        return condition.Features.Where(feature => feature == null ||
            !(hasOwnDescription && formattedText.SharesDescriptionWith(
                feature.GetFirstSubFeatureOfType<FormattedDefinitionText>())) &&
            (!feature.GuiPresentation.Hidden ||
             feature.GuiPresentation.Description != Gui.NoLocalization ||
             feature.FormatDescription() != noContent)).ToList();
    }

    internal static bool TrySetupLiveFriendlyMonsterTooltip(
        ITooltip tooltip,
        RulesetCharacterMonster character)
    {
        if (tooltip == null ||
            character == null ||
            character.Side != RuleDefinitions.Side.Ally ||
            character.MonsterDefinition == null ||
            ServiceRepository.GetService<IGuiWrapperService>()
                ?.GetGuiMonsterDefinition(character.MonsterDefinition.Name) is not
                { } guiMonsterDefinition)
        {
            return false;
        }

        IImageProvider imageProvider;

        if (tooltip.DataProvider is LiveFriendlyMonsterTooltipProvider currentProvider)
        {
            if (ReferenceEquals(currentProvider.Character, character))
            {
                tooltip.TooltipClass = guiMonsterDefinition.TooltipClass;
                tooltip.Content = guiMonsterDefinition.Description;

                return true;
            }

            // Tooltip instances are pooled. A provider captured for another character
            // may itself carry that character's portrait, so fall back to the new
            // definition rather than retaining stale visual state.
            imageProvider = guiMonsterDefinition;
        }
        else
        {
            imageProvider = tooltip.DataProvider as IImageProvider;
        }

        tooltip.TooltipClass = guiMonsterDefinition.TooltipClass;
        tooltip.Content = guiMonsterDefinition.Description;
        tooltip.DataProvider = new LiveFriendlyMonsterTooltipProvider(
            guiMonsterDefinition,
            character,
            imageProvider);

        return true;
    }

    internal sealed class HideHeaderImage
    {
        internal static readonly HideHeaderImage Instance = new();

        private HideHeaderImage()
        {
        }
    }

    internal static void RestoreHeaderImageLayout(TooltipFeatureHeader header)
    {
        if (header && header.TryGetComponent<TooltipHeaderImageLayoutState>(out var state))
        {
            state.Restore();
        }
    }

    internal static bool ApplyHeaderImagePolicy(TooltipFeatureHeader header, ITooltip tooltip)
    {
        var definition = GetHeaderDefinition(tooltip);

        // Feat headers share a text-only presentation. Conditions still expose their
        // status symbols, while other definitions may opt into the same image policy.
        if (!header || definition is ConditionDefinition ||
            definition is not FeatDefinition && definition?.HasSubFeatureOfType<HideHeaderImage>() != true)
        {
            return false;
        }

        var state = header.GetComponent<TooltipHeaderImageLayoutState>() ??
                    header.gameObject.AddComponent<TooltipHeaderImageLayoutState>();

        state.Apply(header);

        return true;
    }

    private static BaseDefinition GetHeaderDefinition(ITooltip tooltip) => tooltip?.DataProvider switch
    {
        GuiActiveCondition condition => condition.ConditionDefinition,
        GuiBaseDefinitionWrapper wrapper => wrapper.BaseDefinition,
        _ => null
    };

    internal static void ResetHeaderImage(Image image)
    {
        if (!image)
        {
            return;
        }

        image.enabled = true;

        if (!HeaderPortraits.TryGetValue(image, out var portrait) || !portrait)
        {
            return;
        }

        SimulacrumPortraits.Release(portrait);
        portrait.gameObject.SetActive(false);
    }

    internal static void UpdateHeaderPortrait(
        ITooltip tooltip,
        Image image,
        RectTransform mask)
    {
        if (!image ||
            tooltip?.TooltipClass?.StartsWith(
                GuiMonsterDefinition.TooltipClassMonsterDefinition,
                StringComparison.Ordinal) != true ||
            tooltip.DataProvider is not IMonsterBasicInfoProvider ||
            tooltip.DataProvider is ISpellParametersProvider ||
            GetSimulacrum(tooltip) is not { } duplicate)
        {
            return;
        }

        if (!HeaderPortraits.TryGetValue(image, out var portrait) || !portrait)
        {
            var portraitObject = new GameObject(
                "SimulacrumPortrait",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(RawImage));
            var portraitTransform = portraitObject.GetComponent<RectTransform>();

            portraitTransform.SetParent(
                mask ? mask : image.rectTransform.parent,
                false);
            portraitTransform.anchorMin = Vector2.zero;
            portraitTransform.anchorMax = Vector2.one;
            portraitTransform.offsetMin = Vector2.zero;
            portraitTransform.offsetMax = Vector2.zero;
            portraitTransform.pivot = new Vector2(0.5f, 0.5f);
            portrait = portraitObject.GetComponent<RawImage>();
            portrait.raycastTarget = false;
            HeaderPortraits.Remove(image);
            HeaderPortraits.Add(image, portrait);
        }

        portrait.gameObject.SetActive(true);
        image.enabled = false;
        SimulacrumPortraits.TryAssign(duplicate, portrait);
    }

    private static RulesetCharacterSimulacrum GetSimulacrum(ITooltip tooltip)
    {
        var duplicate = ResolveCharacter(tooltip) as RulesetCharacterSimulacrum;

        return duplicate ??
               (tooltip?.DataProvider as LiveFriendlyMonsterTooltipProvider)
               ?.Character as RulesetCharacterSimulacrum;
    }

    internal static void FitConditionHeaderImage(TooltipFeatureHeader header, ITooltip tooltip)
    {
        if (!header || GetHeaderDefinition(tooltip) is not ConditionDefinition || !header.image ||
            !header.image.sprite)
        {
            return;
        }

        // Fit the complete sprite to the native slot, including small status glyphs.
        // Sprite dimensions, rather than its atlas texture, preserve its aspect.
        var spriteRect = header.image.sprite.rect;
        var maskRect = header.mask ? header.mask.rect : header.image.rectTransform.rect;

        if (spriteRect.width <= 0f || spriteRect.height <= 0f ||
            maskRect.width <= 0f || maskRect.height <= 0f)
        {
            return;
        }

        var scale = Mathf.Min(maskRect.width / spriteRect.width, maskRect.height / spriteRect.height);

        var state = header.GetComponent<TooltipHeaderImageLayoutState>() ??
                    header.gameObject.AddComponent<TooltipHeaderImageLayoutState>();

        state.ReserveVisibleImageSpace(header, spriteRect.size * scale);
    }

    internal static float GetHeaderSpriteWidth(Texture texture, TooltipFeatureHeader header)
    {
        var sprite = header.image ? header.image.sprite : null;

        return sprite ? sprite.rect.width : texture ? texture.width : 1f;
    }

    internal static float GetHeaderSpriteHeight(Texture texture, TooltipFeatureHeader header)
    {
        var sprite = header.image ? header.image.sprite : null;

        return sprite ? sprite.rect.height : texture ? texture.height : 1f;
    }

    internal static void UpdateSpellCastingTime(TooltipFeatureSpellParameters parameters, ITooltip tooltip)
    {
        if (tooltip is not GuiTooltip guiTooltip ||
            tooltip.DataProvider is not ISpellParametersProvider { SpellDefinition: { } spell })
        {
            return;
        }

        var spellBox = guiTooltip.GetComponentInParent<SpellActivationBox>();
        var character = ResolveCharacter(tooltip);
        var line = SpellActionTypeContext.GetRepertoireLine(spellBox);

        if (spellBox == null || spellBox.tooltip != guiTooltip ||
            character == null || line?.caster?.RulesetCharacter != character ||
            !SpellCastingValidation.TryGetTooltipRepertoire(tooltip, out var repertoire) ||
            repertoire != spellBox.spellRepertoire)
        {
            return;
        }

        var activationTime = SpellActionTypeContext.GetDisplayedActivationTime(spell, spellBox);

        if (activationTime != spell.ActivationTime)
        {
            parameters.castingTimeLabel.Text = Gui.FormatActivationTime(activationTime);
        }
    }

    internal static void UpdatePowerUses(ITooltip tooltip, TooltipFeaturePowerParameters parameters)
    {
        if (tooltip.DataProvider is not GuiPowerDefinition guiPowerDefinition)
        {
            return;
        }

        if (ResolveCharacter(tooltip) is not { } character)
        {
            return;
        }

        var power = guiPowerDefinition.PowerDefinition;
        var usesLabel = parameters.usesLabel;

        usesLabel.Text = FormatUses(power, character, usesLabel.Text);
    }

    private static string FormatUses(FeatureDefinitionPower power, RulesetCharacter character, string def)
    {
        if (power.RechargeRate == RuleDefinitions.RechargeRate.AtWill)
        {
            return def;
        }

        if (power.RechargeRate == RuleDefinitions.RechargeRate.HealingPool)
        {
            var maxPoints = Math.Max(0, character.TryGetAttributeValue(AttributeDefinitions.HealingPool));
            var remainingPoints = Math.Max(0, maxPoints - character.UsedHealingPool);

            // Variable healing spends a selected number of points rather than a fixed use.
            return power.CostPerUse <= 0
                ? Gui.Format("Tooltip/&PowerPointsRemainingFormat", remainingPoints.ToString(), maxPoints.ToString())
                : FormatAvailablePowerUses(remainingPoints / power.CostPerUse, maxPoints / power.CostPerUse);
        }

        if (power.CostPerUse <= 0)
        {
            return def;
        }

        var attributeName = power.RechargeRate switch
        {
            RuleDefinitions.RechargeRate.BardicInspiration => AttributeDefinitions.BardicInspirationNumber,
            RuleDefinitions.RechargeRate.BindChain => AttributeDefinitions.BindChainNumber,
            RuleDefinitions.RechargeRate.ChannelDivinity => AttributeDefinitions.ChannelDivinityNumber,
            RuleDefinitions.RechargeRate.MaxHitPoints => AttributeDefinitions.HitPoints,
            RuleDefinitions.RechargeRate.RagePoints => AttributeDefinitions.RagePoints,
            RuleDefinitions.RechargeRate.SorceryPoints => AttributeDefinitions.SorceryPoints,
            RuleDefinitions.RechargeRate.KiPoints => AttributeDefinitions.KiPoints,
            _ => null
        };
        int maxUses;

        if (attributeName != null)
        {
            maxUses = Math.Max(0, character.TryGetAttributeValue(attributeName)) / power.CostPerUse;
        }
        else
        {
            var usablePower = PowerProvider.Get(power, character);
            var maxCharges = PowerProvider.GetEffectiveMaxUses(character, usablePower);

            // Shared powers already expose their root pool in this power's use units.
            maxUses = power is FeatureDefinitionPowerSharedPool ? maxCharges : maxCharges / power.CostPerUse;
        }

        // Remaining uses also resolves shared pools and divides their charge cost.
        return FormatAvailablePowerUses(Math.Max(0, character.GetRemainingPowerUses(power)), maxUses);
    }

    private static string FormatAvailablePowerUses(int remainingUses, int maxUses)
    {
        return Gui.Format("Tooltip/&PowerUsesRemainingFormat", remainingUses.ToString(), maxUses.ToString());
    }

    internal static void UpdateContextualDescription(TooltipFeatureDescription description, ITooltip tooltip)
    {
        if (tooltip.DataProvider is not GuiBaseDefinitionWrapper definition ||
            definition.BaseDefinition.GetFirstSubFeatureOfType<FormattedDefinitionText>() is not { } formattedText)
        {
            return;
        }

        description.DescriptionLabel.Text = formattedText.FormatCharacterDescription(
            description.DescriptionLabel.TMP_Text.text, ResolveCharacter(tooltip));
    }

    internal static void UpdatePowerUseFailure(
        TooltipFeatureDescription description,
        ITooltip tooltip)
    {
        if (tooltip.DataProvider is not GuiPowerDefinition guiPowerDefinition ||
            ResolveCharacter(tooltip) is not { } character)
        {
            return;
        }

        var power = guiPowerDefinition.PowerDefinition;

        if (!ModifyPowerVisibility.ShouldKeepVisibleWhenUnavailable(power))
        {
            return;
        }

        string failure;

        if (character.GetRemainingPowerUses(power) <= 0)
        {
            failure = "Failure/&FailureFlagNoPowerUses";
        }
        else if (!ValidatorsValidatePowerUse.TryGetPowerUseFailure(character, power, out failure))
        {
            return;
        }

        description.DescriptionLabel.Text += $"\n\n{Gui.FormatFailure(string.Empty, failure)}";
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

        if (ResolveCharacter(tooltip) is not { } character)
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

    internal static void UpdateBardicInspirationPowerParameters(
        ITooltip tooltip,
        TooltipFeaturePowerParameters parameters)
    {
        if (tooltip.DataProvider is not GuiPowerDefinition guiPowerDefinition)
        {
            return;
        }

        var power = guiPowerDefinition.PowerDefinition;
        var character = ResolveCharacter(tooltip);
        var hasBardicRecharge =
            power.RechargeRate == RuleDefinitions.RechargeRate.BardicInspiration;
        var grantsBardicInspiration = guiPowerDefinition.EffectDescription.EffectForms.Any(
            form =>
                form.FormType == EffectForm.EffectFormType.Condition &&
                form.ConditionForm?.ConditionDefinition ==
                ConditionDefinitions.ConditionBardicInspiration);

        if (!hasBardicRecharge && !grantsBardicInspiration)
        {
            return;
        }

        var condition = ConditionDefinitions.ConditionBardicInspiration;

        if (hasBardicRecharge)
        {
            var rechargeRate = character?.HasFastBardicInspirationRecovery() == true
                ? RuleDefinitions.RechargeRate.ShortRest
                : RuleDefinitions.RechargeRate.LongRest;

            parameters.rechargeLabel.Text = Gui.FormatRechargeRate(rechargeRate);
        }

        if (grantsBardicInspiration)
        {
            parameters.durationLabel.Text =
                Gui.FormatDuration(condition.DurationType, condition.DurationParameter);
        }
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
        ModifyWidth<TooltipFeatureWidthMod, TooltipFeature>(parent);
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
        RefreshAdaptiveParameterRow(parent?.verticalLayout, "TopTable");
    }

    internal static void RefreshAdaptivePowerParameterTopRow(TooltipFeaturePowerParameters parent)
    {
        RefreshAdaptiveParameterRow(parent?.verticalLayout, "TopTable");
        RefreshAdaptiveParameterRow(parent?.verticalLayout, "MediumTable");

        if (parent && parent.GetComponentInParent<TooltipPanel>() is { } panel)
        {
            // Power modifiers can change the range after the common magic feature was bound.
            foreach (var magicParameters in panel.GetComponentsInChildren<TooltipFeatureBaseMagicParameters>(true))
            {
                RefreshAdaptiveBaseMagicParameters(magicParameters);
            }
        }
    }

    internal static void RefreshAdaptiveBaseMagicParameters(TooltipFeatureBaseMagicParameters parent)
    {
        if (!parent || parent.transform.Find("Table") is not RectTransform table)
        {
            return;
        }

        var feature = parent.RectTransform;
        var previousHeight = feature.rect.height;
        // Keep the native stack position when restoring the feature's size.
        var featureState = TooltipParameterLayoutState.Get(feature, restorePlacement: false);
        var tableState = TooltipParameterLayoutState.Get(table);

        featureState.Restore(feature);
        tableState.Restore(table);

        var top = feature.rect.yMax - GetParameterTop(feature, table);
        var bottomPadding = Mathf.Max(0f, featureState.OriginalHeight - top - tableState.OriginalHeight);
        var height = RefreshAdaptiveParameterRow(table);

        if (!table.gameObject.activeInHierarchy)
        {
            MarkAdaptiveTooltipLayoutDirty(parent, previousHeight);
            return;
        }

        SetParameterTop(table, top);

        var requiredHeight = Mathf.Ceil(Mathf.Max(featureState.OriginalHeight, top + height + bottomPadding));

        feature.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);
        var layout = featureState.EnsureLayoutElement(feature);
        layout.preferredHeight = requiredHeight;
        layout.minHeight = Mathf.Max(layout.minHeight, requiredHeight);
        MarkAdaptiveTooltipLayoutDirty(parent, previousHeight);

        if (feature.parent is RectTransform featureTable)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(featureTable);
        }
    }

    private static void MarkAdaptiveTooltipLayoutDirty(TooltipFeature feature, float previousHeight)
    {
        if (!Mathf.Approximately(previousHeight, feature.RectTransform.rect.height) &&
            feature.GetComponentInParent<TooltipPanel>() is { } panel)
        {
            AdaptiveTooltipLayouts.GetValue(panel, _ => new AdaptiveTooltipLayoutState()).Dirty = true;
        }
    }

    internal static void ResetAdaptiveTooltipLayout(TooltipPanel panel)
    {
        if (panel)
        {
            AdaptiveTooltipLayouts.Remove(panel);
        }
    }

    internal static void RefreshAdaptiveTooltipLayout(TooltipPanel panel)
    {
        if (!panel || !AdaptiveTooltipLayouts.TryGetValue(panel, out var state) || !state.Dirty)
        {
            return;
        }

        state.Dirty = false;

        if (!panel.featuresTable)
        {
            return;
        }

        // Native DelayedShow stacks each feature immediately after binding it. A later
        // modifier can resize an earlier feature, so stack their final heights once
        // UpdateCoroutine has finished the current binding pass. Do not bind them again.
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.featuresTable);
        var height = 0f;

        for (var i = 0; i < panel.featuresTable.childCount; i++)
        {
            var feature = panel.featuresTable.GetChild(i).GetComponent<TooltipFeature>();

            if (!feature || !feature.gameObject.activeSelf || !panel.activeFeatures.Contains(feature))
            {
                continue;
            }

            var rectTransform = feature.RectTransform;
            var position = rectTransform.anchoredPosition;

            switch (feature.VerticalLayout)
            {
                case TooltipDefinitions.VerticalLayout.Continuous:
                    position.y = -height;
                    height += rectTransform.rect.height;
                    break;
                case TooltipDefinitions.VerticalLayout.FixedY:
                    position.y = -feature.FixedY;
                    break;
                default:
                    position.y = 0f;
                    break;
            }

            rectTransform.anchoredPosition = position;
        }

        panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
    }

    private sealed class AdaptiveTooltipLayoutState
    {
        internal bool Dirty;
    }

    private static void RefreshAdaptiveParameterRow(Transform verticalLayout, string rowName)
    {
        if (!verticalLayout)
        {
            return;
        }

        if (verticalLayout.Find(rowName) is not RectTransform row)
        {
            return;
        }

        RefreshAdaptiveParameterRow(row);

        if (verticalLayout is RectTransform verticalRect)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(verticalRect);
        }
    }

    private static float RefreshAdaptiveParameterRow(RectTransform row)
    {
        var rowState = TooltipParameterLayoutState.Get(row);

        foreach (var state in row.GetComponentsInChildren<TooltipParameterLayoutState>(true))
        {
            state.Restore(state.transform as RectTransform);
        }

        if (!row.gameObject.activeInHierarchy)
        {
            return rowState.OriginalHeight;
        }

        var groups = new List<RectTransform>();

        for (var i = 0; i < row.childCount; i++)
        {
            if (row.GetChild(i) is RectTransform child && child.gameObject.activeInHierarchy)
            {
                groups.Add(child);
            }
        }

        groups.Sort((left, right) => GetParameterGroupLeft(row, left).CompareTo(GetParameterGroupLeft(row, right)));

        var requiredHeight = rowState.OriginalHeight;

        // Native labels can be wider than the space between the two column origins.
        var rowLayout = row.GetComponent<HorizontalLayoutGroup>();

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var start = GetParameterGroupLeft(row, group);
            var spacing = rowLayout ? rowLayout.spacing : group.GetComponentsInChildren<TMP_Text>(false)
                .Select(text => text.fontSize / 2f).DefaultIfEmpty(0f).Max();
            var end = i + 1 < groups.Count ? GetParameterGroupLeft(row, groups[i + 1]) - spacing : row.rect.xMax;
            var width = Mathf.Min(group.rect.width, end - start);

            requiredHeight = Mathf.Max(requiredHeight, RefreshAdaptiveParameterGroup(group, width));
        }

        requiredHeight = Mathf.Ceil(requiredHeight);
        row.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);

        var layout = rowState.EnsureLayoutElement(row);
        layout.preferredHeight = requiredHeight;
        layout.minHeight = Mathf.Max(layout.minHeight, requiredHeight);

        LayoutRebuilder.ForceRebuildLayoutImmediate(row);
        return requiredHeight;
    }

    private static float GetParameterGroupLeft(RectTransform row, RectTransform group)
    {
        return row.InverseTransformPoint(group.TransformPoint(new Vector3(group.rect.xMin, 0f, 0f))).x;
    }

    private static float GetParameterTop(RectTransform parent, RectTransform child)
    {
        return parent.InverseTransformPoint(child.TransformPoint(new Vector3(0f, child.rect.yMax, 0f))).y;
    }

    private static void SetParameterTop(RectTransform rectTransform, float top)
    {
        var height = rectTransform.rect.height;

        rectTransform.anchorMin = new Vector2(rectTransform.anchorMin.x, 1f);
        rectTransform.anchorMax = new Vector2(rectTransform.anchorMax.x, 1f);
        rectTransform.pivot = new Vector2(rectTransform.pivot.x, 1f);
        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        rectTransform.anchoredPosition = new Vector2(rectTransform.anchoredPosition.x, -top);
    }

    private static float RefreshAdaptiveParameterGroup(RectTransform group, float width)
    {
        if (!group)
        {
            return 0f;
        }

        var groupState = TooltipParameterLayoutState.Get(group);

        groupState.Restore(group);

        if (width <= 1f)
        {
            return groupState.OriginalHeight;
        }

        var requiredHeight = groupState.OriginalHeight;
        var addedHeight = 0f;
        var top = group.parent is RectTransform row ? row.rect.yMax - GetParameterTop(row, group) : 0f;

        SetParameterTop(group, top);

        var texts = group.GetComponentsInChildren<TMP_Text>(false)
            .Where(text => text && text.rectTransform && text.gameObject.activeInHierarchy)
            .OrderByDescending(text => GetParameterTop(group, text.rectTransform)).ToArray();
        var originalBottom = texts.Select(text => group.rect.yMax - GetParameterTop(group, text.rectTransform) +
                                                TooltipParameterLayoutState.Get(text.rectTransform).OriginalHeight)
            .DefaultIfEmpty(groupState.OriginalHeight).Max();
        var bottomPadding = Mathf.Max(0f, groupState.OriginalHeight - originalBottom);

        foreach (var text in texts)
        {
            if (!text || text.rectTransform == null || !text.gameObject.activeInHierarchy)
            {
                continue;
            }

            var rectTransform = text.rectTransform;
            var textState = TooltipParameterLayoutState.Get(rectTransform);

            textState.Restore(rectTransform);
            var labelTop = group.rect.yMax - GetParameterTop(group, rectTransform);

            SetParameterTop(rectTransform, rectTransform == group ? top : labelTop + addedHeight);

            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            text.enableAutoSizing = false;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.maxVisibleLines = 99999;

            var preferred = text.GetPreferredValues(text.text, width, 0f);
            var height = Mathf.Ceil(Mathf.Max(textState.OriginalHeight, preferred.y));

            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            requiredHeight = Mathf.Max(requiredHeight, labelTop + addedHeight + height + bottomPadding);
            addedHeight += height - textState.OriginalHeight;
        }

        requiredHeight = Mathf.Ceil(requiredHeight);
        group.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);

        return requiredHeight;
    }
}

internal sealed class TooltipHeaderImageLayoutState : MonoBehaviour
{
    private readonly List<TextLayoutState> _textStates = [];
    private readonly Vector3[] _corners = new Vector3[4];
    private TooltipFeatureHeader _visibleHeader;
    private (Rect Header, Rect Image, Rect Title, Rect Subtitle, string TitleText, string SubtitleText,
        float TitleFont, float SubtitleFont, bool ImageVisible, bool OwnedSlot) _visibleInputs;
    private bool _hasVisibleInputs;
    private GameObject _imageRoot;
    private Image _image;
    private TextLayoutState _imageLayout;
    private bool _imageRootActive;
    private bool _imageEnabled;
    private bool _applied;

    internal void ReserveVisibleImageSpace(TooltipFeatureHeader header, Vector2 imageSize)
    {
        Restore();
        var imageRect = header.image.rectTransform;
        _imageLayout = new TextLayoutState(imageRect);
        imageRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, imageSize.x);
        imageRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, imageSize.y);
        _visibleHeader = header;
        _applied = true;
        RefreshVisibleImageSpace();
    }

    private void LateUpdate()
    {
        if (_visibleHeader)
        {
            RefreshVisibleImageSpace();
        }
    }

    private void RefreshVisibleImageSpace()
    {
        var inputs = GetVisibleInputs();

        if (_hasVisibleInputs && _visibleInputs == inputs)
        {
            return;
        }

        foreach (var state in _textStates)
        {
            state.Restore();
        }

        _textStates.Clear();
        var header = _visibleHeader;

        // A mask or a native layout group owns its image slot. For the small
        // unmasked header, use spare outside padding before changing any text.
        if (inputs.ImageVisible && !inputs.OwnedSlot)
        {
            inputs = GetVisibleInputs();
            var title = header.titleLabel ? header.titleLabel.TMP_Text : null;
            var subtitle = header.subTitleLabel ? header.subTitleLabel.TMP_Text : null;

            if (OverlapsGlyphs(title, header.RectTransform, inputs.Image) ||
                OverlapsGlyphs(subtitle, header.RectTransform, inputs.Image))
            {
                var textBounds = inputs.Title.width > 0f ? inputs.Title : inputs.Subtitle;

                if (inputs.Subtitle.width > 0f)
                {
                    textBounds = Rect.MinMaxRect(Mathf.Min(textBounds.xMin, inputs.Subtitle.xMin),
                        Mathf.Min(textBounds.yMin, inputs.Subtitle.yMin),
                        Mathf.Max(textBounds.xMax, inputs.Subtitle.xMax),
                        Mathf.Max(textBounds.yMax, inputs.Subtitle.yMax));
                }

                const float spacing = 4f;
                var left = textBounds.xMin - spacing - inputs.Image.xMax;
                var right = textBounds.xMax + spacing - inputs.Image.xMin;
                var fitsHeight = inputs.Image.yMin >= inputs.Header.yMin &&
                                 inputs.Image.yMax <= inputs.Header.yMax;
                var fitsLeft = fitsHeight && inputs.Image.xMin + left >= inputs.Header.xMin &&
                               inputs.Image.xMax + left <= inputs.Header.xMax;
                var fitsRight = fitsHeight && inputs.Image.xMin + right >= inputs.Header.xMin &&
                                inputs.Image.xMax + right <= inputs.Header.xMax;

                if (textBounds.width > 0f && (fitsLeft || fitsRight))
                {
                    var offset = fitsLeft && (!fitsRight || Mathf.Abs(left) <= Mathf.Abs(right)) ? left : right;
                    var imageRect = header.image.rectTransform;
                    var state = new TextLayoutState(imageRect);

                    _textStates.Add(state);
                    var worldOffset = header.transform.TransformVector(new Vector3(offset, 0f, 0f));
                    imageRect.position += worldOffset;
                }
            }
        }

        _visibleInputs = GetVisibleInputs();
        _hasVisibleInputs = true;
    }

    private (Rect Header, Rect Image, Rect Title, Rect Subtitle, string TitleText, string SubtitleText,
        float TitleFont, float SubtitleFont, bool ImageVisible, bool OwnedSlot) GetVisibleInputs()
    {
        var header = _visibleHeader;
        var title = header.titleLabel ? header.titleLabel.TMP_Text : null;
        var subtitle = header.subTitleLabel ? header.subTitleLabel.TMP_Text : null;

        return (header.RectTransform.rect, BoundsInHeader(header.image.rectTransform),
            title && title.isActiveAndEnabled && !string.IsNullOrWhiteSpace(title.text)
                ? BoundsInHeader(title.rectTransform) : default,
            subtitle && subtitle.isActiveAndEnabled && !string.IsNullOrWhiteSpace(subtitle.text)
                ? BoundsInHeader(subtitle.rectTransform) : default,
            title ? title.text : null, subtitle ? subtitle.text : null,
            title ? title.fontSize : 0f, subtitle ? subtitle.fontSize : 0f,
            header.image.isActiveAndEnabled && header.image.sprite,
            header.mask || HasLayoutGroup(header.image.transform, header.transform) ||
            HasLayoutGroup(title ? title.transform : null, header.transform) ||
            HasLayoutGroup(subtitle ? subtitle.transform : null, header.transform));
    }

    private Rect BoundsInHeader(RectTransform rectTransform)
    {
        rectTransform.GetWorldCorners(_corners);
        var first = _visibleHeader.transform.InverseTransformPoint(_corners[0]);
        var min = (Vector2)first;
        var max = min;

        for (var index = 1; index < _corners.Length; index++)
        {
            var point = (Vector2)_visibleHeader.transform.InverseTransformPoint(_corners[index]);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static bool HasLayoutGroup(Transform transform, Transform header)
    {
        for (var parent = transform ? transform.parent : null; parent; parent = parent.parent)
        {
            if (parent.TryGetComponent<LayoutGroup>(out var group) && group.isActiveAndEnabled)
            {
                return true;
            }

            if (parent == header)
            {
                break;
            }
        }

        return false;
    }

    private static bool OverlapsGlyphs(TMP_Text text, RectTransform header, Rect imageBounds)
    {
        if (!text || !text.isActiveAndEnabled)
        {
            return false;
        }

        text.ForceMeshUpdate();

        for (var index = 0; index < text.textInfo.characterCount; index++)
        {
            var glyph = text.textInfo.characterInfo[index];

            if (!glyph.isVisible)
            {
                continue;
            }

            var bottomLeft = header.InverseTransformPoint(text.transform.TransformPoint(glyph.bottomLeft));
            var bottomRight = header.InverseTransformPoint(text.transform.TransformPoint(glyph.bottomRight));
            var topLeft = header.InverseTransformPoint(text.transform.TransformPoint(glyph.topLeft));
            var topRight = header.InverseTransformPoint(text.transform.TransformPoint(glyph.topRight));
            var min = Vector2.Min(Vector2.Min(bottomLeft, bottomRight), Vector2.Min(topLeft, topRight));
            var max = Vector2.Max(Vector2.Max(bottomLeft, bottomRight), Vector2.Max(topLeft, topRight));
            var glyphBounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

            if (glyphBounds.Overlaps(imageBounds))
            {
                return true;
            }
        }

        return false;
    }

    internal void Apply(TooltipFeatureHeader header)
    {
        Restore();

        if (!header.image)
        {
            return;
        }

        _image = header.image;
        _imageRoot = header.mask && header.mask != header.RectTransform &&
                     header.image.transform.IsChildOf(header.mask) &&
                     (!header.titleLabel || !header.titleLabel.transform.IsChildOf(header.mask)) &&
                     (!header.subTitleLabel || !header.subTitleLabel.transform.IsChildOf(header.mask))
            ? header.mask.gameObject
            : header.image.gameObject;
        // Some text-only header variants put Image on the feature itself. Hiding
        // that object would also hide its labels, so disable only its renderer there.
        if (_imageRoot == header.gameObject ||
            (header.titleLabel && header.titleLabel.transform.IsChildOf(_imageRoot.transform)) ||
            (header.subTitleLabel && header.subTitleLabel.transform.IsChildOf(_imageRoot.transform)))
        {
            _imageRoot = null;
        }

        _imageRootActive = _imageRoot && _imageRoot.activeSelf;
        _imageEnabled = _image.enabled;
        _applied = true;

        // Keep text branches and native font settings intact. Reclaim only the image
        // column; a shared layout group can then size the remaining content normally.
        foreach (var label in new[] { header.titleLabel, header.subTitleLabel })
        {
            if (!label || !_imageRoot)
            {
                continue;
            }

            var branch = label.RectTransform;

            while (branch.parent && branch.parent != header.transform &&
                   !header.image.transform.IsChildOf(branch.parent))
            {
                if (branch.parent is not RectTransform parent)
                {
                    break;
                }

                branch = parent;
            }

            if (branch == header.RectTransform ||
                _textStates.Exists(state => state.RectTransform == branch))
            {
                continue;
            }

            var state = new TextLayoutState(branch);

            _textStates.Add(state);
            state.Expand((RectTransform)_imageRoot.transform);
        }

        _image.enabled = false;

        if (_imageRoot)
        {
            _imageRoot.SetActive(false);
        }
    }

    internal void Restore()
    {
        if (!_applied)
        {
            return;
        }

        _applied = false;

        foreach (var state in _textStates)
        {
            state.Restore();
        }

        _textStates.Clear();
        _imageLayout?.Restore();
        _imageLayout = null;

        if (_image)
        {
            _image.enabled = _imageEnabled;
        }

        if (_imageRoot)
        {
            _imageRoot.SetActive(_imageRootActive);
        }

        _image = null;
        _imageRoot = null;
        _visibleHeader = null;
        _hasVisibleInputs = false;
    }

    private void OnDisable()
    {
        Restore();
    }

    private sealed class TextLayoutState
    {
        private readonly Vector2 _anchorMin;
        private readonly Vector2 _anchorMax;
        private readonly Vector2 _pivot;
        private readonly Vector2 _sizeDelta;
        private readonly Vector3 _anchoredPosition;
        private readonly bool _hadLayoutElement;
        private readonly bool _layoutElementEnabled;
        private readonly float _minWidth;
        private readonly float _preferredWidth;
        private readonly float _flexibleWidth;
        private LayoutElement _layoutElement;

        internal TextLayoutState(RectTransform rectTransform)
        {
            RectTransform = rectTransform;
            _anchorMin = rectTransform.anchorMin;
            _anchorMax = rectTransform.anchorMax;
            _pivot = rectTransform.pivot;
            _sizeDelta = rectTransform.sizeDelta;
            _anchoredPosition = rectTransform.anchoredPosition3D;
            _hadLayoutElement = rectTransform.TryGetComponent(out _layoutElement);

            if (_hadLayoutElement)
            {
                _layoutElementEnabled = _layoutElement.enabled;
                _minWidth = _layoutElement.minWidth;
                _preferredWidth = _layoutElement.preferredWidth;
                _flexibleWidth = _layoutElement.flexibleWidth;
            }
        }

        internal RectTransform RectTransform { get; }

        internal void Expand(RectTransform imageRoot)
        {
            if (RectTransform.parent is not RectTransform parent)
            {
                return;
            }

            if (parent.TryGetComponent<HorizontalLayoutGroup>(out var group) && group.isActiveAndEnabled)
            {
                _layoutElement ??= RectTransform.gameObject.AddComponent<LayoutElement>();
                _layoutElement.enabled = true;
                _layoutElement.minWidth = 0f;
                _layoutElement.preferredWidth = -1f;
                _layoutElement.flexibleWidth = 1f;

                return;
            }

            var imageBounds = BoundsInParent(parent, imageRoot);
            var textBounds = BoundsInParent(parent, RectTransform);
            var left = Mathf.Max(0f, Mathf.Min(imageBounds.xMin, textBounds.xMin) - parent.rect.xMin);
            var right = Mathf.Max(0f, parent.rect.xMax - textBounds.xMax);
            var anchorMin = RectTransform.anchorMin;
            var anchorMax = RectTransform.anchorMax;

            anchorMin.x = 0f;
            anchorMax.x = 1f;
            RectTransform.anchorMin = anchorMin;
            RectTransform.anchorMax = anchorMax;
            var offsetMin = RectTransform.offsetMin;
            var offsetMax = RectTransform.offsetMax;

            offsetMin.x = left;
            offsetMax.x = -right;
            RectTransform.offsetMin = offsetMin;
            RectTransform.offsetMax = offsetMax;
        }

        internal void Restore()
        {
            if (!RectTransform)
            {
                return;
            }

            if (_layoutElement)
            {
                if (_hadLayoutElement)
                {
                    _layoutElement.enabled = _layoutElementEnabled;
                    _layoutElement.minWidth = _minWidth;
                    _layoutElement.preferredWidth = _preferredWidth;
                    _layoutElement.flexibleWidth = _flexibleWidth;
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(_layoutElement);
                }
            }

            RectTransform.anchorMin = _anchorMin;
            RectTransform.anchorMax = _anchorMax;
            RectTransform.pivot = _pivot;
            RectTransform.sizeDelta = _sizeDelta;
            RectTransform.anchoredPosition3D = _anchoredPosition;
        }

        private static Rect BoundsInParent(RectTransform parent, RectTransform rectTransform)
        {
            var corners = new Vector3[4];

            rectTransform.GetWorldCorners(corners);
            var points = corners.Select(parent.InverseTransformPoint).ToArray();

            return Rect.MinMaxRect(points.Min(point => point.x), points.Min(point => point.y),
                points.Max(point => point.x), points.Max(point => point.y));
        }
    }
}

internal sealed class TooltipParameterLayoutState : MonoBehaviour
{
    private bool _captured;
    private bool _restorePlacement = true;
    private bool _hadLayoutElement;
    private float _originalMinHeight;
    private float _originalPreferredHeight;
    private float _originalFlexibleHeight;
    private float _originalAnchorMinY;
    private float _originalAnchorMaxY;
    private float _originalPivotY;
    private TMP_Text _text;
    private float _originalTextWidth;
    private bool _originalAutoSizing;
    private bool _originalWordWrapping;
    private TextOverflowModes _originalOverflowMode;
    private int _originalMaxVisibleLines;

    internal float OriginalHeight { get; private set; }
    internal float OriginalPositionY { get; private set; }

    internal static TooltipParameterLayoutState Get(RectTransform rectTransform, bool restorePlacement = true)
    {
        var state = rectTransform.GetComponent<TooltipParameterLayoutState>() ??
                    rectTransform.gameObject.AddComponent<TooltipParameterLayoutState>();

        state._restorePlacement = restorePlacement;
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

        if (_restorePlacement)
        {
            rectTransform.anchorMin = new Vector2(rectTransform.anchorMin.x, _originalAnchorMinY);
            rectTransform.anchorMax = new Vector2(rectTransform.anchorMax.x, _originalAnchorMaxY);
            rectTransform.pivot = new Vector2(rectTransform.pivot.x, _originalPivotY);
        }

        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, OriginalHeight);

        if (_restorePlacement)
        {
            rectTransform.anchoredPosition = new Vector2(rectTransform.anchoredPosition.x, OriginalPositionY);
        }

        if (_text)
        {
            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _originalTextWidth);
            _text.enableAutoSizing = _originalAutoSizing;
            _text.enableWordWrapping = _originalWordWrapping;
            _text.overflowMode = _originalOverflowMode;
            _text.maxVisibleLines = _originalMaxVisibleLines;
        }

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
        OriginalPositionY = rectTransform.anchoredPosition.y;
        _originalAnchorMinY = rectTransform.anchorMin.y;
        _originalAnchorMaxY = rectTransform.anchorMax.y;
        _originalPivotY = rectTransform.pivot.y;
        _text = rectTransform.GetComponent<TMP_Text>();

        if (_text)
        {
            _originalTextWidth = rectTransform.rect.width;
            _originalAutoSizing = _text.enableAutoSizing;
            _originalWordWrapping = _text.enableWordWrapping;
            _originalOverflowMode = _text.overflowMode;
            _originalMaxVisibleLines = _text.maxVisibleLines;
        }

        _hadLayoutElement = rectTransform.TryGetComponent<LayoutElement>(out var layout);

        if (_hadLayoutElement)
        {
            _originalMinHeight = layout.minHeight;
            _originalPreferredHeight = layout.preferredHeight;
            _originalFlexibleHeight = layout.flexibleHeight;
        }

        _captured = true;
    }

    private void OnDisable()
    {
        Restore(transform as RectTransform);
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
