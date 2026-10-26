using System;
using System.Collections.Generic;
using System.Linq;
using I2.Loc;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using UnityEngine.UI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class PermanentDefensesSubPanel : MonoBehaviour
{
    private InvocationSubPanel _panel;
    private RectTransform _table;
    private Vector2 _tableSize;
    private Vector2 _panelSize;
    private LayoutGroup[] _layouts;
    private bool[] _layoutEnabled;
    private ContentSizeFitter _fitter;
    private bool _fitterEnabled;
    private bool _arranging;
    private bool _overriding;
    private float _lastWidth = -1f;
    private string _languageCode;

    internal readonly struct DefenseEntry(
        FeatureDefinition feature, string title, string description, string source, bool equipment)
    {
        internal FeatureDefinition Feature { get; } = feature;
        internal string Title { get; } = title;
        internal string Description { get; } = description;
        internal string Source { get; } = source;
        internal bool Equipment { get; } = equipment;
    }

    internal static void AddSubPanel(ProficienciesPanel parent)
    {
        if (!Main.Settings.EnablePermanentDefensesPanelDisplay || !parent.toggleGroup ||
            parent.GetComponentInChildren<PermanentDefensesSubPanel>(true))
        {
            return;
        }

        var source = (InvocationSubPanel)parent.subPanels[(int)ProficienciesPanel.ProficiencyType.Invocation];
        var panel = Instantiate(source, source.transform.parent);
        panel.name = nameof(PermanentDefensesSubPanel);
        panel.gameObject.AddComponent<PermanentDefensesSubPanel>();
        panel.relevantInvocations.Clear();

        // Instantiate copies hierarchy rows, but not their native pool ownership.
        for (var index = panel.Table.childCount - 1; index >= 0; index--)
        {
            DestroyImmediate(panel.Table.GetChild(index).gameObject);
        }

        var rank = parent.subPanels.Length;
        Array.Resize(ref parent.subPanels, rank + 1);
        parent.subPanels[rank] = panel;
        panel.transform.SetAsLastSibling();
        parent.trailingSeparator.SetAsLastSibling();

        var title = panel.transform.Find("ProficiencySectionHeader/Title").GetComponent<GuiLabel>();
        title.Text = "UI/&PermanentDefensesTitle";
        var toggle = Gui.GetPrefabFromPool(parent.proficiencyTogglePrefab, parent.toggleGroup)
            .GetComponent<ProficiencyToggle>();
        toggle.Bind("UI/&PermanentDefensesTitle", false, rank, parent.ProficiencyToggleChanged);
        parent.subPanelsMap.Add(toggle, panel);
        panel.gameObject.SetActive(false);
    }

    internal static bool IsDefensesPanel(InvocationSubPanel panel)
    {
        return panel.GetComponent<PermanentDefensesSubPanel>();
    }

    internal static bool IsDefenseRow(InvocationItem item)
    {
        return item.GetComponent<DefenseRowPresentation>()?.Overriding == true;
    }

    internal static bool TryBind(
        InvocationSubPanel panel,
        RulesetCharacterHero hero,
        ProficiencyBaseItem.OnItemClickedHandler itemSelected)
    {
        if (!IsDefensesPanel(panel))
        {
            return false;
        }

        panel.InspectedCharacter = hero;
        panel.Selected = false;
        panel.ElementsAndDescriptions.Clear();
        panel.OnItemClicked = itemSelected;
        var entries = Main.Settings.EnablePermanentDefensesPanelDisplay && hero != null
            ? GetEntries(hero)
            : [];
        var table = panel.Table;

        while (table.childCount < entries.Count)
        {
            Gui.GetPrefabFromPool(panel.ItemPrefab, table);
        }

        for (var index = 0; index < table.childCount; index++)
        {
            var child = table.GetChild(index);
            var visible = index < entries.Count;
            child.gameObject.SetActive(visible);
            var item = child.GetComponent<ProficiencyBaseItem>();
            item.Unbind();
            if (!visible)
            {
                continue;
            }

            var entry = entries[index];
            var presentation = child.GetComponent<DefenseRowPresentation>() ??
                               child.gameObject.AddComponent<DefenseRowPresentation>();
            presentation.Prepare(item);
            var source = Gui.Format(entry.Equipment
                ? "UI/&PermanentDefenseEquipmentSourceFormat"
                : "UI/&PermanentDefenseFeatureSourceFormat", entry.Source);
            var description = entry.Description + "\n" + source;
            item.Bind(entry.Title, description, null, false, string.Empty);
            item.Refresh(ProficiencyBaseItem.InteractiveMode.Static, HeroDefinitions.PointsPoolType.Feat);
            var provider = new CustomTooltipProvider(entry.Feature, null);
            provider.SetTitle(entry.Title);
            provider.SetSubtitle(source);
            provider.SetDescription(description);
            item.Tooltip.TooltipClass = "FeatDefinition";
            item.Tooltip.DataProvider = provider;
            item.Tooltip.Context = hero;
        }

        var controller = panel.GetComponent<PermanentDefensesSubPanel>();
        controller._panel = panel;
        controller._languageCode = LocalizationManager.CurrentLanguageCode;
        panel.gameObject.SetActive(entries.Count > 0);
        if (entries.Count > 0)
        {
            controller.Arrange();
        }

        return true;
    }

    private void Arrange()
    {
        if (_arranging || !_panel || _panel.Table.rect.width <= 0f)
        {
            return;
        }

        _arranging = true;
        try
        {
            if (!_overriding)
            {
                _table = _panel.Table;
                _tableSize = _table.sizeDelta;
                _panelSize = _panel.RectTransform.sizeDelta;
                _layouts = _table.GetComponents<LayoutGroup>();
                _layoutEnabled = _layouts.Select(layout => layout.enabled).ToArray();
                foreach (var layout in _layouts)
                {
                    layout.enabled = false;
                }

                _fitter = _table.GetComponent<ContentSizeFitter>();
                _fitterEnabled = _fitter && _fitter.enabled;
                if (_fitter)
                {
                    _fitter.enabled = false;
                }

                _overriding = true;
            }

            var width = _table.rect.width;
            var horizontalSpacing = Mathf.Max(8f, _panel.HorizontalSpacing);
            var verticalSpacing = Mathf.Max(4f, _panel.VerticalSpacing);
            var x = 0f;
            var y = 0f;
            var rowHeight = 0f;
            foreach (Transform child in _table)
            {
                if (!child.gameObject.activeSelf)
                {
                    continue;
                }

                var item = child.GetComponent<ProficiencyBaseItem>();
                var presentation = child.GetComponent<DefenseRowPresentation>();
                if (!presentation.Overriding)
                {
                    presentation.Prepare(item);
                }

                var rect = (RectTransform)child;
                item.Tooltip.Anchor = rect;
                item.Tooltip.AnchorMode = TooltipDefinitions.AnchorMode.LEFT_CENTER;
                var text = item.itemName.TMP_Text;
                text.enableWordWrapping = true;
                // Preferred advance width excludes glyph bearings. Keep the same
                // four-unit caption allowance used by the shared text fitters.
                text.margin = presentation.PaddedMargin;
                var preferred = text.GetPreferredValues(text.text, float.PositiveInfinity, float.PositiveInfinity);
                var itemWidth = Mathf.Min(width, Mathf.Max(96f, Mathf.Ceil(preferred.x + presentation.HorizontalInset)));
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, itemWidth);
                var captionWidth = Mathf.Max(1f, text.rectTransform.rect.width - text.margin.x - text.margin.z);
                preferred = text.GetPreferredValues(text.text, captionWidth, float.PositiveInfinity);
                var itemHeight = Mathf.Ceil(preferred.y + presentation.VerticalInset);
                if (x > 0f && x + itemWidth > width + 0.25f)
                {
                    y += rowHeight + verticalSpacing;
                    x = 0f;
                    rowHeight = 0f;
                }

                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, itemHeight);
                rect.anchoredPosition = new Vector2(x, -y);
                x += itemWidth + horizontalSpacing;
                rowHeight = Mathf.Max(rowHeight, itemHeight);
            }

            _table.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, y + rowHeight + verticalSpacing);
            // Keep the same section-height contract as native DispatchItems. Its
            // row advance assumes equal item heights, so mixed wrapping is owned here.
            _panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                _table.rect.height - _table.offsetMax.y);
            _lastWidth = width;
            LayoutRebuilder.MarkLayoutForRebuild(_panel.RectTransform);
        }
        finally
        {
            _arranging = false;
        }
    }

    private void LateUpdate()
    {
        if (!_panel || _panel.InspectedCharacter == null)
        {
            return;
        }

        if (_languageCode != LocalizationManager.CurrentLanguageCode)
        {
            TryBind(_panel, _panel.InspectedCharacter, _panel.OnItemClicked);
        }
        else if (!_overriding || Mathf.Abs(_panel.Table.rect.width - _lastWidth) > 0.25f)
        {
            Arrange();
        }
    }

    private void OnEnable()
    {
        _lastWidth = -1f;
    }

    private void OnDisable()
    {
        if (!_overriding)
        {
            return;
        }

        _table.sizeDelta = new Vector2(_table.sizeDelta.x, _tableSize.y);
        _panel.RectTransform.sizeDelta = new Vector2(_panel.RectTransform.sizeDelta.x, _panelSize.y);
        for (var index = 0; index < _layouts.Length; index++)
        {
            if (_layouts[index])
            {
                _layouts[index].enabled = _layoutEnabled[index];
            }
        }

        if (_fitter)
        {
            _fitter.enabled = _fitterEnabled;
        }

        _overriding = false;
        _lastWidth = -1f;
    }

    internal static List<DefenseEntry> GetEntries(RulesetCharacterHero hero)
    {
        List<DefenseEntry> entries = [];
        List<FeatureDefinition> features = [];
        HashSet<(FeatureDefinition Feature, string Item)> seen = [];

        void AddSource(List<FeatureDefinition> roots, string sourceName, bool equipment)
        {
            features.Clear();
            FeatureDefinitionFeatureSet.EnumerateFeaturesToBrowseHierarchicaly<FeatureDefinitionAffinity>(
                roots, features, FeatureSourceType.CharacterFeature, null, hero, null);

            foreach (var feature in features)
            {
                if (feature is not FeatureDefinitionDamageAffinity and not FeatureDefinitionConditionAffinity)
                {
                    continue;
                }

                var affinity = (FeatureDefinitionAffinity)feature;
                if (!affinity.IsMyFamilyIsValid(hero.CharacterFamily))
                {
                    continue;
                }

                var source = string.IsNullOrEmpty(sourceName) ? feature.FormatTitle() : sourceName;
                if (!seen.Add((feature, equipment ? source : string.Empty)))
                {
                    continue;
                }

                if (feature is FeatureDefinitionDamageAffinity damage &&
                    damage.DamageAffinityType is DamageAffinityType.Resistance or DamageAffinityType.Immunity)
                {
                    var damageType = damage.DamageType;
                    if (damage.AncestryDefinesDamageType &&
                        !TryGetAncestryDamageTypeFromCharacter(hero.Guid, damage.AncestryType, out damageType))
                    {
                        continue;
                    }

                    var definition = DatabaseRepository.GetDatabase<DamageDefinition>().GetElement(damageType, true);
                    if (!definition)
                    {
                        continue;
                    }

                    var immunity = damage.DamageAffinityType == DamageAffinityType.Immunity;
                    var title = Gui.Format(immunity
                        ? "UI/&PermanentDefenseImmunityFormat"
                        : "UI/&PermanentDefenseResistanceFormat", definition.FormatTitle());
                    var explanation = Gui.Format(immunity
                        ? "Feature/&DamageImmunityFormat"
                        : "Feature/&DamageResistanceFormat", definition.FormatTitle());
                    // Native affinity formatting includes bypass tags such as magical or silver weapons.
                    var bypass = damage.FormatTagsIgnoringAffinity();
                    title += bypass;
                    explanation += bypass;
                    var context = string.Empty;
                    if (damage.SituationalContext != SituationalContext.None)
                    {
                        context = damage.SituationalContext switch
                        {
                            SituationalContext.Raging => Gui.Localize("UI/&PermanentDefenseWhileRaging"),
                            SituationalContext.NotWearingHeavyArmor =>
                                Gui.Localize("UI/&PermanentDefenseWithoutHeavyArmor"),
                            _ => CustomTooltipProvider.FormatDescription(feature)
                        };
                        if (CustomTooltipProvider.IsUnavailableContent(context))
                        {
                            continue;
                        }

                        title = Gui.Format("UI/&PermanentDefenseConditionalFormat", title, context);
                    }

                    var creatureSource = GetCreatureSourceRestriction(affinity);
                    title = AddTitleCondition(title, creatureSource);
                    entries.Add(new DefenseEntry(feature, title,
                        GetDescription(feature, explanation, context, creatureSource), source, equipment));
                }
                else if (feature is FeatureDefinitionConditionAffinity
                         { ConditionAffinityType: ConditionAffinityType.Immunity } condition)
                {
                    var definition = DatabaseRepository.GetDatabase<ConditionDefinition>()
                        .GetElement(condition.ConditionType, true);
                    if (!definition)
                    {
                        continue;
                    }

                    var creatureSource = GetCreatureSourceRestriction(affinity);
                    var title = AddTitleCondition(
                        Gui.Format("UI/&PermanentDefenseImmunityFormat", definition.FormatTitle()), creatureSource);
                    var explanation = Gui.Format("Feature/&ConditionImmunityFormat", definition.FormatTitle());
                    entries.Add(new DefenseEntry(feature, title,
                        GetDescription(feature, explanation, creatureSource), source, equipment));
                }
            }
        }

        foreach (var feat in hero.TrainedFeats)
        {
            AddSource(feat.Features, feat.FormatTitle(), false);
        }

        foreach (var style in hero.ActiveFightingStyles)
        {
            AddSource(style.Features, style.FormatTitle(), false);
        }

        foreach (var invocation in hero.Invocations.Where(invocation => invocation.Active &&
                     invocation.InvocationDefinition.GrantedFeature))
        {
            AddSource([invocation.InvocationDefinition.GrantedFeature],
                invocation.InvocationDefinition.FormatTitle(), false);
        }

        foreach (var group in hero.ActiveFeatures)
        {
            foreach (var feature in group.Value)
            {
                var title = CustomTooltipProvider.FormatTitle(feature);
                if (CustomTooltipProvider.IsUnavailableContent(title))
                {
                    hero.LookForFeatureOrigin(feature, out var race, out var characterClass, out var feat);
                    title = feat ? feat.FormatTitle() : race ? race.FormatTitle() :
                        characterClass ? characterClass.FormatTitle() : Gui.Localize("UI/&PermanentDefensesTitle");
                }

                AddSource([feature], title, false);
            }
        }

        foreach (var item in hero.activeItemFeatures)
        {
            AddSource(item.Value, item.Key.FormatTitle(), true);
        }

        if (hero.CharacterInventory != null)
        {
            // Match the dynamic damage-affinity path used by ModulateSustainedDamage.
            // Native activeItemFeatures contains static item properties only.
            foreach (var slot in hero.CharacterInventory.InventorySlotsByName.Values)
            {
                if (slot.EquipedItem == null || slot.Disabled || slot.ConfigSlot)
                {
                    continue;
                }

                var item = slot.EquipedItem;
                AddSource(item.DynamicItemProperties.Where(property => property.FeatureDefinition is IDamageAffinityProvider)
                    .Where(IsPersistentItemProperty).Select(property => property.FeatureDefinition).ToList(),
                    item.ItemDefinition.FormatTitle(), true);
            }
        }

        return entries.OrderBy(entry => entry.Equipment).ThenBy(entry => entry.Title, StringComparer.CurrentCulture)
            .ThenBy(entry => entry.Source, StringComparer.CurrentCulture).ToList();
    }

    private static bool IsPersistentItemProperty(RulesetItemProperty property)
    {
        if (property.UsageLimitation != ItemPropertyUsage.Unlimited)
        {
            return false;
        }

        var effect = EffectHelpers.GetEffectByGuid(property.SourceEffectGuid);
        if (effect != null)
        {
            var duration = effect.EffectDescription.DurationType;
            return !effect.Terminated && (duration == DurationType.Permanent ||
                                         effect is RulesetEffectPower && duration == DurationType.UntilLongRest);
        }

        // A property without an effect owner or a countdown is intrinsic to the item.
        // A missing nonzero owner is an orphaned grant, rather than a permanent trait.
        return property.SourceEffectGuid == 0 && property.RemainingRounds == 0;
    }

    private static string GetDescription(FeatureDefinition feature, string explanation, params string[] conditions)
    {
        var description = CustomTooltipProvider.FormatDescription(feature);
        if (CustomTooltipProvider.IsUnavailableContent(description))
        {
            description = explanation;
        }
        else if (!description.StripXmlTags().Contains(explanation.StripXmlTags()))
        {
            description = explanation + "\n\n" + description;
        }

        foreach (var condition in conditions.Where(condition => !string.IsNullOrEmpty(condition)))
        {
            if (!description.StripXmlTags().Contains(condition.StripXmlTags()))
            {
                description += "\n" + condition;
            }
        }

        return description;
    }

    private static string AddTitleCondition(string title, string condition)
    {
        return string.IsNullOrEmpty(condition)
            ? title
            : Gui.Format("UI/&PermanentDefenseConditionalFormat", title, condition);
    }

    private static string GetCreatureSourceRestriction(FeatureDefinitionAffinity affinity)
    {
        if (affinity.otherCharacterFamilyRestrictions.Count == 0)
        {
            return string.Empty;
        }

        var database = DatabaseRepository.GetDatabase<CharacterFamilyDefinition>();
        var families = affinity.otherCharacterFamilyRestrictions
            .Select(name => database.GetElement(name, true))
            .Where(definition => definition)
            .Select(definition => definition.FormatTitle());
        return Gui.Format("UI/&PermanentDefenseCreatureSourceFormat", string.Join(Gui.ListSeparator(), families));
    }

    private sealed class DefenseRowPresentation : MonoBehaviour
    {
        private RectTransform _rect;
        private TMPro.TMP_Text _text;
        private Vector2 _size;
        private Vector2 _anchorMin;
        private Vector2 _anchorMax;
        private Vector2 _pivot;
        private Vector3 _position;
        private GuiTooltip _tooltip;
        private RectTransform _tooltipAnchor;
        private TooltipDefinitions.AnchorMode _tooltipAnchorMode;
        private bool _wrapping;
        private Vector4 _margin;
        private bool _overriding;

        internal bool Overriding => _overriding;
        internal float HorizontalInset { get; private set; }
        internal float VerticalInset { get; private set; }
        internal Vector4 PaddedMargin => _margin + new Vector4(2f, 0f, 2f, 0f);

        internal void Prepare(ProficiencyBaseItem item)
        {
            Restore();
            _rect = (RectTransform)item.transform;
            _text = item.itemName.TMP_Text;
            _size = _rect.sizeDelta;
            _anchorMin = _rect.anchorMin;
            _anchorMax = _rect.anchorMax;
            _pivot = _rect.pivot;
            _position = _rect.anchoredPosition3D;
            _wrapping = _text.enableWordWrapping;
            _margin = _text.margin;
            _tooltip = item.Tooltip;
            _tooltipAnchor = _tooltip.Anchor;
            _tooltipAnchorMode = _tooltip.AnchorMode;
            // TMP preferred values already include its margins.
            HorizontalInset = Mathf.Max(12f, _rect.rect.width - _text.rectTransform.rect.width);
            VerticalInset = Mathf.Max(12f, _rect.rect.height - _text.rectTransform.rect.height);
            _overriding = true;
        }

        private void Restore()
        {
            if (!_overriding)
            {
                return;
            }

            _rect.anchorMin = _anchorMin;
            _rect.anchorMax = _anchorMax;
            _rect.pivot = _pivot;
            _rect.sizeDelta = _size;
            _rect.anchoredPosition3D = _position;
            _text.enableWordWrapping = _wrapping;
            _text.margin = _margin;
            _tooltip.Anchor = _tooltipAnchor;
            _tooltip.AnchorMode = _tooltipAnchorMode;
            _overriding = false;
        }

        private void OnDisable()
        {
            Restore();
        }
    }
}
