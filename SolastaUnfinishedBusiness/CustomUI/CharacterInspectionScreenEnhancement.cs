using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class CharacterInspectionScreenEnhancement
{
    private const string DragonbornDraconicChoiceFeatureSetName = "FeatureSetDragonbornDraconicChoice";

    private static Transform ClassSelector { get; set; }

    private static int SelectedClassIndex { get; set; }

    private static void HideClassBadge([NotNull] Transform child)
    {
        child.GetComponent<CharacterInformationBadge>().Unbind();
        child.gameObject.SetActive(false);
    }

    [CanBeNull]
    private static RulesetCharacterHero GetInspectedHero(CharacterInformationPanel panel)
    {
        return Global.InspectedHero ?? panel?.InspectedCharacter?.RulesetCharacter as RulesetCharacterHero;
    }

    private static bool TryFindChoiceFeatureFromExclusionSet(
        IEnumerable<FeatureDefinition> features,
        System.Func<FeatureDefinition, bool> isMatchingChoice,
        out FeatureDefinition choiceFeature)
    {
        foreach (var featureDefinition in features)
        {
            if (featureDefinition is not FeatureDefinitionFeatureSet
                {
                    Mode: FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion
                } definitionFeatureSet || !definitionFeatureSet.FeatureSet.Any(isMatchingChoice))
            {
                continue;
            }

            choiceFeature = featureDefinition;

            return true;
        }

        choiceFeature = null;
        return false;
    }

    [CanBeNull]
    internal static CharacterClassDefinition SelectedClass
    {
        get
        {
            var hero = Global.InspectedHero;
            var classesAndLevels = hero?.ClassesAndLevels;
            var classesCount = classesAndLevels?.Count ?? 0;

            if (classesCount == 0)
            {
                return null;
            }

            SelectedClassIndex = Mathf.Clamp(SelectedClassIndex, 0, classesCount - 1);

            return classesAndLevels.Keys.ElementAtOrDefault(SelectedClassIndex);
        }
    }

    internal static RulesetSpellRepertoire[] GetInspectionSpellRepertoires(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        SpellBox.BindMode bindMode = SpellBox.BindMode.Inspection)
    {
        if (repertoire == null)
        {
            return [];
        }

        if (bindMode != SpellBox.BindMode.Inspection || character == null || !IsFeatSpellRepertoire(repertoire))
        {
            return [repertoire];
        }

        var feat = GetSpellSourceFeat(character, repertoire);
        var sourceTag = GetSpellSourceTag(repertoire);
        var classHolder = repertoire.SpellCastingFeature.GetFirstSubFeatureOfType<ClassHolder>()?.Class;

        var sources = character.SpellRepertoires.Where(candidate =>
        {
            if (!IsFeatSpellRepertoire(candidate) || !SpellSelectionContext.IsDisplayedRepertoire(candidate))
            {
                return false;
            }

            var candidateFeat = GetSpellSourceFeat(character, candidate);
            if (feat != null || candidateFeat != null)
            {
                return feat == candidateFeat;
            }

            return sourceTag == GetSpellSourceTag(candidate) &&
                   classHolder == candidate.SpellCastingFeature.GetFirstSubFeatureOfType<ClassHolder>()?.Class &&
                   repertoire.SpellCastingAbility == candidate.SpellCastingAbility &&
                   repertoire.SpellCastingFeature.SpellListDefinition == candidate.SpellCastingFeature.SpellListDefinition;
        }).ToArray();

        return sources.Length == 0 ? [repertoire] : sources;
    }

    private static bool IsFeatSpellRepertoire(RulesetSpellRepertoire repertoire)
    {
        return repertoire?.SpellCastingFeature is { SpellReadyness: RuleDefinitions.SpellReadyness.AllKnown } feature &&
               feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null &&
               !repertoire.SpellCastingClass && !repertoire.SpellCastingSubclass && !repertoire.SpellCastingRace;
    }

    private static FeatDefinition GetSpellSourceFeat(RulesetCharacter character, RulesetSpellRepertoire repertoire)
    {
        return character is RulesetCharacterHero hero
            ? hero.TrainedFeats.FirstOrDefault(feat => RulesetActorExtensions.FlattenFeatureList(feat.Features)
                .Contains(repertoire.SpellCastingFeature))
            : null;
    }

    private static string GetSpellSourceTag(RulesetSpellRepertoire repertoire)
    {
        return Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(
            repertoire.SpellCastingFeature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>().Name);
    }

    internal static bool IsInspectionSpellSourceRedundant(
        RulesetCharacter character,
        RulesetSpellRepertoire displayedRepertoire,
        RulesetSpellRepertoire castingSource,
        string sourceTag,
        SpellBox.BindMode bindMode)
    {
        return bindMode == SpellBox.BindMode.Inspection &&
               IsFeatSpellRepertoire(displayedRepertoire) && IsFeatSpellRepertoire(castingSource) &&
               GetSpellSourceTag(castingSource) == sourceTag &&
               GetInspectionSpellRepertoires(character, displayedRepertoire, bindMode).Contains(castingSource);
    }

    internal static IEnumerable<SpellDefinition> GetInspectionLearnedSpells(RulesetSpellRepertoire repertoire)
    {
        var feature = repertoire.SpellCastingFeature;
        var tag = feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();
        var fixedSpells = (tag?.ForceFixedList == true || feature.SpellKnowledge == RuleDefinitions.SpellKnowledge.FixedList) &&
                          feature.SpellListDefinition != null
            ? feature.SpellListDefinition.SpellsByLevel.SelectMany(level => level.Spells)
            : Enumerable.Empty<SpellDefinition>();

        return repertoire.KnownCantrips.Concat(repertoire.KnownSpells).Concat(repertoire.PreparedSpells)
            .Concat(repertoire.AutoPreparedSpells).Concat(repertoire.ExtraSpellsByTag.Values.SelectMany(spells => spells))
            .Concat(fixedSpells).Where(spell => spell != null).Distinct();
    }

    private static int GetRepertoireInspectionMaximum(RulesetSpellRepertoire repertoire)
    {
        return Math.Max(SharedSpellsContext.MaxSpellLevelOfSpellCastingLevel(repertoire),
            GetInspectionLearnedSpells(repertoire).Select(spell => spell.SpellLevel).DefaultIfEmpty(0).Max());
    }

    internal static int GetInspectionSpellLevelMaximum(
        RulesetSpellRepertoire repertoire, RulesetCharacter character, SpellBox.BindMode bindMode)
    {
        return Math.Max(repertoire.MaxSpellLevelOfSpellCastingLevel,
            GetInspectionSpellRepertoires(character, repertoire, bindMode)
                .Where(IsFeatSpellRepertoire).Select(GetRepertoireInspectionMaximum).DefaultIfEmpty(0).Max());
    }

    internal static void SelectInspectionLevelSource(
        RulesetCharacter character, int spellLevel, SpellBox.BindMode bindMode,
        ref RulesetSpellRepertoire repertoire, ref FeatureDefinitionCastSpell feature)
    {
        var sources = GetInspectionSpellRepertoires(character, repertoire, bindMode);
        if (sources.Length < 2)
        {
            return;
        }

        repertoire = sources.FirstOrDefault(source => GetInspectionLearnedSpells(source)
            .Any(spell => spell.SpellLevel == spellLevel)) ?? repertoire;
        feature = repertoire.SpellCastingFeature;
    }

    internal static void AddInspectionLearnedSpells(
        SpellsByLevelGroup group, RulesetCharacter caster, SpellBox.BindMode bindMode,
        List<SpellDefinition> spells, ref List<SpellDefinition> trainedSpells,
        Dictionary<SpellDefinition, string> tagBySpell)
    {
        var sources = GetInspectionSpellRepertoires(caster, group.SpellRepertoire, bindMode);
        if (sources.Length < 2)
        {
            return;
        }

        trainedSpells = trainedSpells == null ? [] : [..trainedSpells];
        foreach (var source in sources)
        {
            foreach (var spell in GetInspectionLearnedSpells(source).Where(spell => spell.SpellLevel == group.SpellLevel))
            {
                spells.TryAdd(spell);
                trainedSpells.TryAdd(spell);
                tagBySpell[spell] = GetSpellSourceTag(source);
            }
        }
    }

    internal static RulesetSpellRepertoire GetInspectionSpellSource(
        RulesetCharacter caster, RulesetSpellRepertoire repertoire, SpellDefinition spell, SpellBox.BindMode bindMode)
    {
        return GetInspectionSpellRepertoires(caster, repertoire, bindMode)
                   .FirstOrDefault(source => GetInspectionLearnedSpells(source).Contains(spell)) ?? repertoire;
    }

    internal static void RefreshInspectionSpellTabs(CharacterInspectionScreen screen, RulesetCharacterHero hero)
    {
        if (screen.inspectionToggles.Count == 0)
        {
            return;
        }

        for (var index = screen.staticTogglesNumber; index < screen.inspectionToggles.Count; index++)
        {
            var repertoireIndex = index - screen.staticTogglesNumber;
            if (repertoireIndex < 0 || repertoireIndex >= hero.SpellRepertoires.Count)
            {
                continue;
            }

            var repertoire = hero.SpellRepertoires[repertoireIndex];
            var sources = GetInspectionSpellRepertoires(hero, repertoire);
            var representative = sources.OrderByDescending(GetRepertoireInspectionMaximum).FirstOrDefault();
            var toggle = screen.inspectionToggles[index];
            toggle.gameObject.SetActive(SpellSelectionContext.IsDisplayedRepertoire(repertoire) && repertoire == representative);
            var sourceClass = repertoire.SpellCastingFeature.GetFirstSubFeatureOfType<ClassHolder>()?.Class;
            if (repertoire == representative && (sources.Length > 1 || IsFeatSpellRepertoire(repertoire) && sourceClass != null))
            {
                var feat = GetSpellSourceFeat(hero, repertoire);
                var tag = GetSpellSourceTag(repertoire);
                var titleKey = $"Screen/&{tag}ExtraSpellTitle";
                var hasSourceTitle = TranslatorContext.HasTranslation(titleKey);
                var title = hasSourceTitle ? Gui.Localize(titleKey)
                    : feat != null ? feat.FormatTitle() : repertoire.SpellCastingFeature.FormatTitle();
                if (hasSourceTitle && sourceClass != null)
                {
                    title = Gui.Format("Feat/&GeneralFeat2024VariantTitle", title, sourceClass.FormatTitle());
                }
                toggle.Bind(title, index == screen.currentToggle, index, toggle.valueChanged);
            }
        }

        var normalized = NormalizeInspectionToggle(screen, screen.currentToggle);
        if (normalized != screen.currentToggle)
        {
            screen.inspectionToggles[normalized].valueChanged?.Invoke(normalized);
        }
        var previousIgnore = screen.ignoreToggleCallback;
        screen.ignoreToggleCallback = true;
        try
        {
            foreach (var (toggle, index) in screen.inspectionToggles.Select((toggle, index) => (toggle, index)))
            {
                toggle.Toggle.isOn = index == screen.currentToggle;
            }
        }
        finally
        {
            screen.ignoreToggleCallback = previousIgnore;
        }
    }

    internal static int NormalizeInspectionToggle(CharacterInspectionScreen screen, int index)
    {
        if (screen.inspectionToggles.Count == 0)
        {
            return 0;
        }

        index = Mathf.Clamp(index, 0, screen.inspectionToggles.Count - 1);
        if (screen.inspectionToggles[index].gameObject.activeSelf)
        {
            return index;
        }

        var hero = screen.InspectedCharacter?.RulesetCharacterHero;
        if (hero != null && index >= screen.staticTogglesNumber &&
            index - screen.staticTogglesNumber < hero.SpellRepertoires.Count)
        {
            var repertoire = hero.SpellRepertoires[index - screen.staticTogglesNumber];
            var representative = GetInspectionSpellRepertoires(hero, repertoire)
                .OrderByDescending(GetRepertoireInspectionMaximum).FirstOrDefault();
            var representativeIndex = hero.SpellRepertoires.IndexOf(representative) + screen.staticTogglesNumber;
            if (representativeIndex != screen.currentToggle && representativeIndex >= screen.staticTogglesNumber &&
                representativeIndex < screen.inspectionToggles.Count &&
                screen.inspectionToggles[representativeIndex].gameObject.activeSelf)
            {
                return representativeIndex;
            }
        }

        var step = index < screen.currentToggle ? -1 : 1;
        for (var count = 0; count < screen.inspectionToggles.Count; count++)
        {
            index = (index + step + screen.inspectionToggles.Count) % screen.inspectionToggles.Count;
            if (screen.inspectionToggles[index].gameObject.activeSelf)
            {
                return index;
            }
        }

        return 0;
    }

    internal static void ConfigureInspectionTabsScroll(CharacterInspectionScreen screen)
    {
        var controller = screen.GetComponent<InspectionTabsScroll>() ?? screen.gameObject.AddComponent<InspectionTabsScroll>();
        controller.Configure(screen);
    }

    internal static void ReleaseInspectionTabsScroll(CharacterInspectionScreen screen)
    {
        var controller = screen.GetComponent<InspectionTabsScroll>();
        if (!controller)
        {
            return;
        }

        controller.Restore();
        UnityEngine.Object.DestroyImmediate(controller);
    }

    internal static void EnsureInspectionTabVisible(CharacterInspectionScreen screen)
    {
        screen.GetComponent<InspectionTabsScroll>()?.EnsureCurrentTabVisible();
    }

    private sealed class InspectionTabsScroll : MonoBehaviour
    {
        private const float ScrollbarHeight = 12f;
        private CharacterInspectionScreen _screen;
        private RectTransform _table;
        private RectTransform _root;
        private RectTransform _viewport;
        private RectTransform _bar;
        private ScrollRect _scroll;
        private Transform _originalParent;
        private RectTransform _availableBounds;
        private int _originalSibling;
        private Vector2 _originalAnchorMin;
        private Vector2 _originalAnchorMax;
        private Vector2 _originalPivot;
        private Vector2 _originalSize;
        private Vector2 _originalPosition;
        private float _tableHeight;
        private float _tableCenterY;
        private ContentSizeFitter _fitter;
        private bool _fitterEnabled;
        private int _lastToggle = -1;
        private GameObject _lastFocus;
        private float _lastWidth = -1f;
        private float _lastLeft = -1f;
        private float _lastContentWidth = -1f;
        private readonly Dictionary<Toggle, Navigation> _originalNavigation = [];

        internal void Configure(CharacterInspectionScreen screen)
        {
            _screen = screen;
            _table = (RectTransform)screen.toggleGroup.transform;
            foreach (var tab in screen.inspectionToggles)
            {
                _originalNavigation[tab.Toggle] = tab.Toggle.navigation;
            }
            _originalParent = _table.parent;
            // ToggleControl is a native zero-width anchor. Its containing panel defines the available row.
            for (var parent = _originalParent; parent != null; parent = parent.parent)
            {
                if (parent is RectTransform bounds && bounds.rect.width > 0f)
                {
                    _availableBounds = bounds;
                    break;
                }
            }
            _availableBounds ??= screen.RectTransform;
            _originalSibling = _table.GetSiblingIndex();
            _originalAnchorMin = _table.anchorMin;
            _originalAnchorMax = _table.anchorMax;
            _originalPivot = _table.pivot;
            _originalSize = _table.sizeDelta;
            _originalPosition = _table.anchoredPosition;
            _tableHeight = _table.rect.height;
            _tableCenterY = _table.localPosition.y + _table.rect.center.y - ((RectTransform)_originalParent).rect.center.y;
            _fitter = _table.GetComponent<ContentSizeFitter>();
            _fitterEnabled = _fitter && _fitter.enabled;
            if (_fitter)
            {
                _fitter.enabled = false;
            }

            _root = CreateRect("InspectionTabsScroll", _originalParent, _table.gameObject.layer);
            _root.SetSiblingIndex(_originalSibling);
            _root.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _root.anchorMin = _root.anchorMax = new Vector2(0f, 0.5f);
            _root.pivot = new Vector2(0f, 0.5f);
            _viewport = CreateRect("Viewport", _root, _table.gameObject.layer);
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.gameObject.AddComponent<RectMask2D>();
            _viewport.gameObject.AddComponent<Image>().color = Color.clear;
            _scroll = _root.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = _viewport;
            _scroll.content = _table;
            _scroll.vertical = false;
            _scroll.inertia = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 36f;
            _table.SetParent(_viewport, false);
            _table.anchorMin = _table.anchorMax = new Vector2(0f, 0.5f);
            _table.pivot = new Vector2(0f, 0.5f);
            _table.anchoredPosition = Vector2.zero;

            _bar = CreateRect("Scrollbar", _root, _table.gameObject.layer);
            _bar.anchorMin = Vector2.zero;
            _bar.anchorMax = new Vector2(1f, 0f);
            _bar.pivot = new Vector2(0.5f, 0f);
            _bar.sizeDelta = new Vector2(0f, ScrollbarHeight);
            _bar.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.15f, 0.16f, 0.85f);
            var handle = CreateRect("Handle", _bar, _table.gameObject.layer);
            handle.anchorMin = Vector2.zero;
            handle.anchorMax = Vector2.one;
            var image = handle.gameObject.AddComponent<Image>();
            image.color = new Color(0.65f, 0.75f, 0.78f);
            var scrollbar = _bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.LeftToRight;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = image;
            _scroll.horizontalScrollbar = scrollbar;
            RefreshLayout();
            EnsureCurrentTabVisible();
        }

        private static RectTransform CreateRect(string name, Transform parent, int layer)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.gameObject.layer = layer;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        private bool RefreshLayout()
        {
            var layout = _table.GetComponent<HorizontalLayoutGroup>();
            var tabs = _table.Cast<Transform>().Where(tab => tab.gameObject.activeSelf).ToArray();
            var width = tabs.Sum(tab => ((RectTransform)tab).rect.width) +
                        (layout ? layout.padding.horizontal + layout.spacing * Math.Max(0, tabs.Length - 1) : 0f);
            var bounds = _availableBounds.rect;
            var left = _originalParent.InverseTransformPoint(_availableBounds.TransformPoint(new Vector3(bounds.xMin, bounds.center.y, 0f)));
            var right = _originalParent.InverseTransformPoint(_availableBounds.TransformPoint(new Vector3(bounds.xMax, bounds.center.y, 0f)));
            var viewportWidth = Mathf.Abs(right.x - left.x);
            var viewportLeft = Mathf.Min(left.x, right.x) - ((RectTransform)_originalParent).rect.xMin;
            if (Mathf.Approximately(width, _lastContentWidth) && Mathf.Approximately(viewportWidth, _lastWidth) &&
                Mathf.Approximately(viewportLeft, _lastLeft))
            {
                return false;
            }

            _lastWidth = viewportWidth;
            _lastLeft = viewportLeft;
            _lastContentWidth = width;
            var overflow = width > viewportWidth + 1f;
            var barHeight = overflow ? ScrollbarHeight : 0f;
            _root.sizeDelta = new Vector2(viewportWidth, _tableHeight + barHeight);
            _root.anchoredPosition = new Vector2(viewportLeft, _tableCenterY - barHeight / 2f);
            _viewport.offsetMin = new Vector2(0f, barHeight);
            _viewport.offsetMax = Vector2.zero;
            _table.sizeDelta = new Vector2(width, _tableHeight);
            _table.anchoredPosition = new Vector2(Mathf.Clamp(_table.anchoredPosition.x,
                -Mathf.Max(0f, width - viewportWidth), 0f), 0f);
            _bar.gameObject.SetActive(overflow);
            _scroll.horizontal = overflow;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_table);
            return true;
        }

        private void LateUpdate()
        {
            if (!_screen || !_root)
            {
                return;
            }

            var resized = RefreshLayout();
            // Native OnEndShow recomputes Selectable navigation after the initial tab binding.
            if (HasNavigationDrift())
            {
                RefreshNavigation();
            }
            var focus = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            var changedFocus = focus != _lastFocus;
            _lastFocus = focus;
            if (changedFocus && focus && focus.transform.IsChildOf(_table))
            {
                var tab = focus.transform;
                while (tab.parent != _table)
                {
                    tab = tab.parent;
                }
                EnsureVisible((RectTransform)tab);
            }
            else if (resized || _lastToggle != _screen.currentToggle)
            {
                EnsureCurrentTabVisible();
            }
        }

        private bool HasNavigationDrift()
        {
            Toggle first = null;
            Toggle previous = null;
            foreach (var tab in _screen.inspectionToggles)
            {
                var toggle = tab.Toggle;
                if (!tab.gameObject.activeInHierarchy || !toggle.IsInteractable())
                {
                    continue;
                }

                var navigation = toggle.navigation;
                if (navigation.mode != Navigation.Mode.Explicit || previous &&
                    (previous.navigation.selectOnRight != toggle || navigation.selectOnLeft != previous))
                {
                    return true;
                }

                first ??= toggle;
                previous = toggle;
            }

            return first && (first.navigation.selectOnLeft != previous || previous.navigation.selectOnRight != first);
        }

        private void RefreshNavigation()
        {
            var tabs = _screen.inspectionToggles.Where(tab => tab.gameObject.activeInHierarchy && tab.Toggle.IsInteractable())
                .Select(tab => tab.Toggle).ToArray();
            for (var index = 0; index < tabs.Length; index++)
            {
                var toggle = tabs[index];
                var original = _originalNavigation[toggle];
                var navigation = original;
                navigation.mode = Navigation.Mode.Explicit;
                navigation.selectOnLeft = tabs[(index + tabs.Length - 1) % tabs.Length];
                navigation.selectOnRight = tabs[(index + 1) % tabs.Length];
                if (original.mode == Navigation.Mode.Automatic)
                {
                    toggle.navigation = original;
                    navigation.selectOnUp = toggle.FindSelectableOnUp();
                    navigation.selectOnDown = toggle.FindSelectableOnDown();
                }
                toggle.navigation = navigation;
            }
        }

        internal void EnsureCurrentTabVisible()
        {
            RefreshNavigation();
            _lastToggle = _screen.currentToggle;
            if (_lastToggle >= 0 && _lastToggle < _screen.inspectionToggles.Count)
            {
                EnsureVisible((RectTransform)_screen.inspectionToggles[_lastToggle].transform);
            }
        }

        private void EnsureVisible(RectTransform tab)
        {
            if (!tab.gameObject.activeSelf)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_table);
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_viewport, tab);
            var offset = bounds.min.x < _viewport.rect.xMin ? _viewport.rect.xMin - bounds.min.x :
                bounds.max.x > _viewport.rect.xMax ? _viewport.rect.xMax - bounds.max.x : 0f;
            if (Mathf.Approximately(offset, 0f))
            {
                return;
            }

            _scroll.StopMovement();
            _table.anchoredPosition = new Vector2(Mathf.Clamp(_table.anchoredPosition.x + offset,
                -Mathf.Max(0f, _table.rect.width - _viewport.rect.width), 0f), 0f);
        }

        internal void Restore()
        {
            if (!_root)
            {
                return;
            }

            foreach (var entry in _originalNavigation)
            {
                if (entry.Key)
                {
                    entry.Key.navigation = entry.Value;
                }
            }
            _scroll.content = null;
            _table.SetParent(_originalParent, false);
            _table.SetSiblingIndex(_originalSibling);
            _table.anchorMin = _originalAnchorMin;
            _table.anchorMax = _originalAnchorMax;
            _table.pivot = _originalPivot;
            _table.sizeDelta = _originalSize;
            _table.anchoredPosition = _originalPosition;
            if (_fitter)
            {
                _fitter.enabled = _fitterEnabled;
            }
            UnityEngine.Object.DestroyImmediate(_root.gameObject);
        }
    }

    internal static void ResetInspectionState()
    {
        SelectedClassIndex = 0;
    }

    [NotNull]
    internal static string GetSelectedClassSearchTerm(string original)
    {
        var selectedClass = SelectedClass;

        return original
               + (!selectedClass
                   ? string.Empty
                   : selectedClass.Name);
    }

    internal static void EnumerateClassBadges([NotNull] CharacterInformationPanel __instance)
    {
        var badgeDefinitions = __instance.badgeDefinitions;
        var classBadgesTable = __instance.classBadgesTable;
        var classBadgePrefab = __instance.classBadgePrefab;
        var hero = Global.InspectedHero;
        var selectedClass = SelectedClass;

        if (hero == null || !selectedClass)
        {
            badgeDefinitions.Clear();

            for (var childIndex = 0; childIndex < classBadgesTable.childCount; ++childIndex)
            {
                HideClassBadge(classBadgesTable.GetChild(childIndex));
            }

            return;
        }

        badgeDefinitions.SetRange(hero.ClassesAndSubclasses
            .Where(x => x.Key == selectedClass)
            .Select(classesAndSubclass => classesAndSubclass.Value));

        if (hero.DeityDefinition && (selectedClass == Paladin || selectedClass == Cleric))
        {
            badgeDefinitions.Add(hero.DeityDefinition);
        }

        badgeDefinitions.AddRange(GetTrainedFightingStyles());

        while (classBadgesTable.childCount < badgeDefinitions.Count)
        {
            Gui.GetPrefabFromPool(classBadgePrefab, classBadgesTable);
        }

        var index = 0;

        foreach (var badgeDefinition in badgeDefinitions)
        {
            var child = classBadgesTable.GetChild(index);

            child.gameObject.SetActive(true);
            child.GetComponent<CharacterInformationBadge>().Bind(badgeDefinition, classBadgesTable);
            ++index;
        }

        for (; index < classBadgesTable.childCount; ++index)
        {
            HideClassBadge(classBadgesTable.GetChild(index));
        }
    }

    [NotNull]
    // ReSharper disable once ReturnTypeCanBeEnumerable.Local
    private static HashSet<FightingStyleDefinition> GetTrainedFightingStyles()
    {
        var hero = Global.InspectedHero;
        var selectedClass = SelectedClass;
        var classBadges = new HashSet<FightingStyleDefinition>();

        if (hero == null || !selectedClass)
        {
            return classBadges;
        }

        var classLevelFightingStyles = hero.ActiveFeatures
            .Where(x => x.Key.Contains(AttributeDefinitions.TagClass))
            .SelectMany(x => x.Value
                .OfType<FeatureDefinitionFightingStyleChoice>(), (x, _) => x.Key)
            .ToList();

        for (var i = 0; i < classLevelFightingStyles.Count && i < hero.TrainedFightingStyles.Count; ++i)
        {
            if (classLevelFightingStyles[i].Contains(selectedClass.Name))
            {
                classBadges.Add(hero.TrainedFightingStyles[i]);
            }
        }

        return classBadges;
    }

    private static bool TryFindChoiceFeature(
        CharacterInformationPanel panel,
        FeatureDefinition subFeature,
        out FeatureDefinition choiceFeature)
    {
        if (TryFindChoiceFeature(subFeature, panel.InspectedCharacter.MainClassDefinition.FeatureUnlocks.Select(
                featureUnlock => featureUnlock.FeatureDefinition), out choiceFeature))
        {
            return true;
        }

        var subclass = panel.InspectedCharacter.SubclassDefinition;

        if (subclass != null
            && TryFindChoiceFeature(subFeature, subclass.FeatureUnlocks.Select(x => x.FeatureDefinition),
                out choiceFeature))
        {
            return true;
        }

        choiceFeature = null;
        return false;
    }

    internal static bool TryFindChoiceFeature(FeatureDefinition subFeature, IEnumerable<FeatureDefinition> features,
        out FeatureDefinition choiceFeature)
    {
        return TryFindChoiceFeatureFromExclusionSet(features, choice => choice == subFeature, out choiceFeature);
    }

    internal static bool TryFindChoiceFeature(
        string subFeature,
        RulesetCharacterHero hero,
        out FeatureDefinition choiceFeature)
    {
        foreach (var def in hero.ClassesAndLevels.Keys)
        {
            if (TryFindChoiceFeature(subFeature, def.featureUnlocks.Select(x => x.FeatureDefinition),
                    out choiceFeature))
            {
                return true;
            }

        }
        
        foreach (var def in hero.ClassesAndSubclasses.Values)
        {
            if (TryFindChoiceFeature(subFeature, def.featureUnlocks.Select(x => x.FeatureDefinition),
                    out choiceFeature))
            {
                return true;
            }

        }
        
        choiceFeature = null;
        return false;
    }
    
    internal static bool TryFindChoiceFeature(string subFeature, IEnumerable<FeatureDefinition> features,
        out FeatureDefinition choiceFeature)
    {
        return TryFindChoiceFeatureFromExclusionSet(features, choice => choice.Name == subFeature, out choiceFeature);
    }

    private static bool TryGetDragonbornDraconicChoiceInspectionDisplayFeature(
        FeatureDefinition sourceFeature,
        out FeatureDefinitionFeatureSet parentFeature,
        out FeatureDefinition selectedFeature)
    {
        parentFeature = null;
        selectedFeature = null;

        var featureSetDatabase = DatabaseRepository.GetDatabase<FeatureDefinitionFeatureSet>();
        var dragonbornDraconicChoice = featureSetDatabase.GetElement(DragonbornDraconicChoiceFeatureSetName, true);

        if (!dragonbornDraconicChoice ||
            !dragonbornDraconicChoice.FeatureSet.Contains(sourceFeature))
        {
            return false;
        }

        parentFeature = dragonbornDraconicChoice;
        selectedFeature = sourceFeature;

        return true;
    }

    private sealed class FeatureDisplay
    {
        internal FeatureDisplay(FeatureDefinition feature)
        {
            Title = CustomTooltipProvider.FormatTitle(feature);
            TooltipProvider = new CustomTooltipProvider(feature, null);
            TooltipContent = CustomTooltipProvider.GetActivationContent(feature);
        }

        internal string Title { get; set; }
        internal CustomTooltipProvider TooltipProvider { get; }
        internal string TooltipContent { get; set; }
    }

    private static void SetParentChoiceFeatureDisplay(
        FeatureDisplay display,
        FeatureDefinition parentFeature,
        FeatureDefinition selectedFeature)
    {
        var parentTitle = CustomTooltipProvider.FormatTitle(parentFeature);
        var selectedTitle = CustomTooltipProvider.FormatTitle(selectedFeature);
        var description = CustomTooltipProvider.FormatDescription(selectedFeature);

        display.Title = string.IsNullOrEmpty(parentTitle) ? selectedTitle : parentTitle;
        display.TooltipProvider.SetTitle(display.Title);
        display.TooltipProvider.SetSubtitle(selectedTitle);
        display.TooltipProvider.SetDescription(description);
        display.TooltipContent = string.IsNullOrEmpty(description)
            ? CustomTooltipProvider.GetActivationContent(selectedFeature)
            : description;
    }

    private static string FormatChoiceTitle(string parentTitle, string selectedTitle)
    {
        if (string.IsNullOrEmpty(parentTitle))
        {
            return selectedTitle;
        }

        return string.IsNullOrEmpty(selectedTitle)
            ? parentTitle
            : Gui.Format("{1} ({0})", selectedTitle, parentTitle);
    }

    [CanBeNull]
    private static FeatureDisplay ResolveFeatureDisplay(
        CharacterInformationPanel panel,
        FeatureDefinition feature,
        RulesetCharacterHero inspectedHero,
        CharacterHeroBuildingData buildingData)
    {
        if (feature == null || feature.GuiPresentation.Hidden)
        {
            return null;
        }

        var display = new FeatureDisplay(feature);
        var provider = display.TooltipProvider;

        if (feature is FeatureDefinitionPower)
        {
            var guiPowerDefinition = ServiceRepository.GetService<IGuiWrapperService>()
                .GetGuiPowerDefinition(feature.Name);

            if (!CustomTooltipProvider.IsUnavailableContent(guiPowerDefinition.Description))
            {
                display.TooltipContent = guiPowerDefinition.Description;
            }
        }

        if (Tabletop2024Context.TryGetHumanOriginInspectionDisplayFeature(
                inspectedHero,
                buildingData,
                feature,
                out var displayFeature,
                out var fallbackTitle))
        {
            var humanOriginTitle = Gui.Localize("Feature/&PointPoolHumanOriginFeatTitle");

            provider.SetTitle(humanOriginTitle);
            display.Title = displayFeature ? humanOriginTitle : fallbackTitle;

            if (displayFeature)
            {
                var description = Tabletop2024Context.FormatOriginFeatGainDescription(displayFeature);

                provider.SetSubtitle(CustomTooltipProvider.FormatTitle(displayFeature));
                provider.SetDescription(description);

                if (!string.IsNullOrEmpty(description))
                {
                    display.TooltipContent = description;
                }
            }
            else
            {
                // A stale native choice marker must not supply the unresolved origin's text.
                provider.BaseDefinition = DatabaseRepository.GetDatabase<FeatureDefinitionFeatureSet>()
                    .GetElement("FeatureSetHumanOriginFeat2024");
                provider.SetSubtitle(null);
                display.TooltipContent = CustomTooltipProvider.GetActivationContent(provider.BaseDefinition);
            }
        }
        else if (Tabletop2024Context.TryGetHalfElfVersatileBloodlineInspectionDisplayFeature(
                     feature,
                     out var halfElfParentFeature,
                     out var halfElfSelectedFeature))
        {
            SetParentChoiceFeatureDisplay(display, halfElfParentFeature, halfElfSelectedFeature);
        }
        else if (TryGetDragonbornDraconicChoiceInspectionDisplayFeature(
                     feature,
                     out var dragonbornParentFeature,
                     out var dragonbornSelectedFeature))
        {
            SetParentChoiceFeatureDisplay(display, dragonbornParentFeature, dragonbornSelectedFeature);
        }
        else if (TryFindChoiceFeature(panel, feature, out var choiceFeature))
        {
            var selectedTitle = display.Title;
            var choiceTitle = CustomTooltipProvider.FormatTitle(choiceFeature);

            display.Title = FormatChoiceTitle(choiceTitle, selectedTitle);

            if (string.IsNullOrEmpty(CustomTooltipProvider.FormatDescription(feature)))
            {
                provider.BaseDefinition = choiceFeature;
                provider.SetTitle(string.IsNullOrEmpty(choiceTitle) ? selectedTitle : choiceTitle);
                provider.SetSubtitle(selectedTitle);
                display.TooltipContent = CustomTooltipProvider.GetActivationContent(choiceFeature);
            }
            else
            {
                // A nameless child can still be represented by its meaningful choice parent.
                provider.SetTitle(string.IsNullOrEmpty(selectedTitle) ? choiceTitle : selectedTitle);
                provider.SetSubtitle(choiceTitle);
            }
        }

        // Resolve dynamic names and choice parents before deciding whether there is a row to display.
        // The level annotation and tooltip activation fallback are not feature names.
        return CustomTooltipProvider.IsUnavailableContent(display.Title) ? null : display;
    }

    internal static bool EnhanceFeatureList(
        CharacterInformationPanel panel,
        RectTransform table,
        List<FeatureUnlockByLevel> features,
        string insufficientLevelFormat,
        TooltipDefinitions.AnchorMode tooltipAnchorMode)
    {
        var inspectedHero = GetInspectedHero(panel);
        CharacterHeroBuildingData buildingData = null;

        if (inspectedHero != null)
        {
            inspectedHero.TryGetHeroBuildingData(out buildingData);
        }

        var index = 0;

        foreach (var feature in features)
        {
            var display = ResolveFeatureDisplay(panel, feature.FeatureDefinition, inspectedHero, buildingData);

            if (display == null)
            {
                continue;
            }

            if (index == table.childCount)
            {
                Gui.GetPrefabFromPool(panel.featurePrefab, table);
            }

            BindFeatureRow(panel, table.GetChild(index), feature, display, insufficientLevelFormat, tooltipAnchorMode);
            ++index;
        }

        for (; index < table.childCount; ++index)
        {
            HideFeatureRow(panel, table.GetChild(index));
        }

        return false;
    }

    private static void BindFeatureRow(
        CharacterInformationPanel panel,
        Transform child,
        FeatureUnlockByLevel feature,
        FeatureDisplay display,
        string insufficientLevelFormat,
        TooltipDefinitions.AnchorMode tooltipAnchorMode)
    {
        child.gameObject.SetActive(true);
        RestoreFeatureRowPresentation(panel, child);

        var label = child.GetComponent<GuiLabel>();
        var noLevel = feature.Level == 0;
        var provider = display.TooltipProvider;

        label.Text = display.Title + (!noLevel ? $" ({feature.Level})" : string.Empty);
        Gui.HexaKeyToColor(noLevel ? Gui.ColorAlmostWhite : Gui.ColorNegative, out var color);
        label.TMP_Text.color = color;

        var tooltip = child.GetComponent<GuiTooltip>();

        tooltip.Content = display.TooltipContent;
        tooltip.TooltipClass = "FeatDefinition";
        tooltip.DataProvider = provider;
        tooltip.Context = panel.InspectedCharacter?.RulesetCharacter;
        tooltip.AnchorMode = tooltipAnchorMode;

        if (!noLevel)
        {
            var levelRequirement = Gui.Format(insufficientLevelFormat, feature.Level.ToString());

            provider.SetPrerequisites(levelRequirement);
        }
    }

    private static void HideFeatureRow(CharacterInformationPanel panel, Transform child)
    {
        RestoreFeatureRowPresentation(panel, child);
        child.GetComponent<GuiLabel>().Text = string.Empty;

        var tooltip = child.GetComponent<GuiTooltip>();

        tooltip.Content = string.Empty;
        tooltip.Context = null;
        tooltip.DataProvider = null;
        child.gameObject.SetActive(false);
    }

    private static void RestoreFeatureRowPresentation(CharacterInformationPanel panel, Transform child)
    {
        var state = child.GetComponent<FeatureRowPresentationState>() ??
                    child.gameObject.AddComponent<FeatureRowPresentationState>();

        state.Capture(panel.featurePrefab);
        state.Restore(child);
    }

    private sealed class FeatureRowPresentationState : MonoBehaviour
    {
        private bool Captured { get; set; }

        private bool HasCanvasGroup { get; set; }

        private bool HasLayoutElement { get; set; }

        private bool HasText { get; set; }

        private Color[] ImageColors { get; set; }

        private float CanvasGroupAlpha { get; set; }

        private bool CanvasGroupBlocksRaycasts { get; set; }

        private bool CanvasGroupIgnoreParentGroups { get; set; }

        private bool CanvasGroupInteractable { get; set; }

        private bool LayoutElementIgnoreLayout { get; set; }

        private float LayoutElementMinWidth { get; set; }

        private float LayoutElementMinHeight { get; set; }

        private float LayoutElementPreferredWidth { get; set; }

        private float LayoutElementPreferredHeight { get; set; }

        private float LayoutElementFlexibleWidth { get; set; }

        private float LayoutElementFlexibleHeight { get; set; }

        private int LayoutElementPriority { get; set; }

        private Vector4 TextMargin { get; set; }

        private TextAlignmentOptions TextAlignment { get; set; }

        private bool TextAutoSizeTextContainer { get; set; }

        private bool TextEnableAutoSizing { get; set; }

        private bool TextEnableWordWrapping { get; set; }

        private int TextMaxVisibleLines { get; set; }

        private TextOverflowModes TextOverflowMode { get; set; }

        private float TextFontSize { get; set; }

        private float TextFontSizeMin { get; set; }

        private float TextFontSizeMax { get; set; }

        private float TextLineSpacing { get; set; }

        internal void Capture(GameObject source)
        {
            if (Captured)
            {
                return;
            }

            source = source ? source : gameObject;

            var images = source.GetComponentsInChildren<Image>(true);

            if (images.Length > 0)
            {
                ImageColors = new Color[images.Length];

                for (var i = 0; i < images.Length; i++)
                {
                    ImageColors[i] = images[i].color;
                }
            }

            if (source.TryGetComponent<CanvasGroup>(out var canvasGroup))
            {
                HasCanvasGroup = true;
                CanvasGroupAlpha = canvasGroup.alpha;
                CanvasGroupBlocksRaycasts = canvasGroup.blocksRaycasts;
                CanvasGroupIgnoreParentGroups = canvasGroup.ignoreParentGroups;
                CanvasGroupInteractable = canvasGroup.interactable;
            }

            if (source.TryGetComponent<LayoutElement>(out var layoutElement))
            {
                HasLayoutElement = true;
                LayoutElementIgnoreLayout = layoutElement.ignoreLayout;
                LayoutElementMinWidth = layoutElement.minWidth;
                LayoutElementMinHeight = layoutElement.minHeight;
                LayoutElementPreferredWidth = layoutElement.preferredWidth;
                LayoutElementPreferredHeight = layoutElement.preferredHeight;
                LayoutElementFlexibleWidth = layoutElement.flexibleWidth;
                LayoutElementFlexibleHeight = layoutElement.flexibleHeight;
                LayoutElementPriority = layoutElement.layoutPriority;
            }

            var text = GetText(source);

            if (text)
            {
                HasText = true;
                TextMargin = text.margin;
                TextAlignment = text.alignment;
                TextAutoSizeTextContainer = text.autoSizeTextContainer;
                TextEnableAutoSizing = text.enableAutoSizing;
                TextEnableWordWrapping = text.enableWordWrapping;
                TextMaxVisibleLines = text.maxVisibleLines;
                TextOverflowMode = text.overflowMode;
                TextFontSize = text.fontSize;
                TextFontSizeMin = text.fontSizeMin;
                TextFontSizeMax = text.fontSizeMax;
                TextLineSpacing = text.lineSpacing;
            }

            Captured = true;
        }

        internal void Restore(Transform child)
        {
            var target = child.gameObject;

            if (ImageColors is { Length: > 0 })
            {
                var images = target.GetComponentsInChildren<Image>(true);
                var count = Mathf.Min(ImageColors.Length, images.Length);

                for (var i = 0; i < count; i++)
                {
                    images[i].color = ImageColors[i];
                }
            }

            if (HasCanvasGroup && target.TryGetComponent<CanvasGroup>(out var canvasGroup))
            {
                canvasGroup.alpha = CanvasGroupAlpha;
                canvasGroup.blocksRaycasts = CanvasGroupBlocksRaycasts;
                canvasGroup.ignoreParentGroups = CanvasGroupIgnoreParentGroups;
                canvasGroup.interactable = CanvasGroupInteractable;
            }

            if (HasLayoutElement && target.TryGetComponent<LayoutElement>(out var layoutElement))
            {
                layoutElement.ignoreLayout = LayoutElementIgnoreLayout;
                layoutElement.minWidth = LayoutElementMinWidth;
                layoutElement.minHeight = LayoutElementMinHeight;
                layoutElement.preferredWidth = LayoutElementPreferredWidth;
                layoutElement.preferredHeight = LayoutElementPreferredHeight;
                layoutElement.flexibleWidth = LayoutElementFlexibleWidth;
                layoutElement.flexibleHeight = LayoutElementFlexibleHeight;
                layoutElement.layoutPriority = LayoutElementPriority;
            }

            var text = GetText(target);

            if (!HasText || !text)
            {
                return;
            }

            text.margin = TextMargin;
            text.alignment = TextAlignment;
            text.autoSizeTextContainer = TextAutoSizeTextContainer;
            text.enableAutoSizing = TextEnableAutoSizing;
            text.enableWordWrapping = TextEnableWordWrapping;
            text.maxVisibleLines = TextMaxVisibleLines;
            text.overflowMode = TextOverflowMode;
            text.fontSize = TextFontSize;
            text.fontSizeMin = TextFontSizeMin;
            text.fontSizeMax = TextFontSizeMax;
            text.lineSpacing = TextLineSpacing;
            text.SetLayoutDirty();
            text.SetVerticesDirty();
        }

        private static TMP_Text GetText(GameObject target)
        {
            if (!target)
            {
                return null;
            }

            var label = target.GetComponent<GuiLabel>();

            return label ? label.TMP_Text : target.GetComponent<TMP_Text>();
        }
    }

    internal static void SwapClassAndBackground(CharacterInformationPanel panel)
    {
        var backGroup = panel.transform.Find("BackgroundGroup")?.GetComponent<RectTransform>();
        var classGroup = panel.transform.Find("ClassGroup")?.GetComponent<RectTransform>();

        if (!classGroup || !backGroup)
        {
            return;
        }

        backGroup.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 32, 662);
        backGroup.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Bottom, 32, 458);

        classGroup.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Right, 32, 662);
        classGroup.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 32, 856);

        //this is actually top-right one
        var child = backGroup.Find("OrnamentBottomRight")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 5, 50);
        }

        child = backGroup.Find("BackgroundImageMask")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 0, 218);
        }

        child = backGroup.Find("BackgroundDescriptionGroup")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Bottom, 65, 175);
        }

        child = classGroup.Find("ClassFeaturesGroup")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 20, 642);
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 260, 590);

            var sizeDelta = child.sizeDelta;

            child.sizeDelta = new Vector2(sizeDelta.x, sizeDelta.y - 100);
        }

        child = classGroup.Find("ClassDescriptionGroup")?.GetComponent<RectTransform>();

        if (child)
        {
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Right, 0, 355);
            child.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 0, 270);
        }

        classGroup.FindChildRecursive("OrnamentBottomLeft")?.gameObject.SetActive(false);

        //
        // setup class buttons for MC scenarios
        //

        ResetInspectionState();

        var hero = Global.InspectedHero;

        // abort on a SC hero
        if (hero?.ClassesAndLevels == null || hero.ClassesAndLevels.Count == 1)
        {
            if (ClassSelector)
            {
                ClassSelector.gameObject.SetActive(false);
            }

            return;
        }

        Transform labelsGroup;

        if (!ClassSelector)
        {
            var voice = backGroup.FindChildRecursive("Voice");

            ClassSelector = Object.Instantiate(voice, classGroup.transform);
            ClassSelector.name = "Classes";
            ClassSelector.FindChildRecursive("PlayAudio").gameObject.SetActive(false);
            ClassSelector.FindChildRecursive("HeaderGroup").gameObject.SetActive(false);

            labelsGroup = ClassSelector.FindChildRecursive("LabelsGroup");

            var firstButton = labelsGroup.GetChild(0);

            for (var i = labelsGroup.childCount; i < MulticlassContext.MaxClasses; i++)
            {
                Object.Instantiate(firstButton, firstButton.parent);
            }
        }
        else
        {
            ClassSelector.gameObject.SetActive(true);

            labelsGroup = ClassSelector.FindChildRecursive("LabelsGroup");
        }

        var classesTitles = hero.ClassesAndLevels.Select(x => x.Key.FormatTitle()).ToArray();
        var classesCount = classesTitles.Length;

        for (var i = 0; i < classesCount; i++)
        {
            var childToggle = labelsGroup.GetChild(i);
            var labelChoiceToggle = childToggle.GetComponent<LabelChoiceToggle>();
            var uiToggle = childToggle.GetComponent<Toggle>();

            childToggle.gameObject.SetActive(true);

            labelChoiceToggle.Bind(i, classesTitles[i], x =>
            {
                if (!uiToggle.isOn)
                {
                    return;
                }

                SelectedClassIndex = x;
                panel.RefreshNow();

                for (var c = 0; c < classesCount; ++c)
                {
                    if (c != x)
                    {
                        labelsGroup.GetChild(c).GetComponent<LabelChoiceToggle>().Refresh(false, true);
                    }
                }
            });
        }

        labelsGroup.GetChild(0).GetComponent<Toggle>().isOn = true;

        for (var i = classesCount; i < MulticlassContext.MaxClasses; i++)
        {
            labelsGroup.GetChild(i).gameObject.SetActive(false);
        }
    }
}
