using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Patches;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class MulticlassGameUi
{
    internal static void CaptureSpellPreparationLayout(SpellRepertoirePanel panel)
    {
        if (!panel.TryGetComponent<SpellPreparationLayoutState>(out _))
        {
            panel.gameObject.AddComponent<SpellPreparationLayoutState>().Capture(panel);
        }
    }

    internal static void RefreshSpellPreparationLayout(SpellRepertoirePanel panel)
    {
        if (panel.TryGetComponent<SpellPreparationLayoutState>(out var state))
        {
            state.Apply();
        }
    }

    internal static void RestoreSpellPreparationLayout(SpellRepertoirePanel panel)
    {
        if (panel.TryGetComponent<SpellPreparationLayoutState>(out var state))
        {
            state.Restore();
            UnityEngine.Object.DestroyImmediate(state);
        }
    }

    private sealed class SpellPreparationLayoutState : MonoBehaviour
    {
        private const float Padding = 12f;
        private readonly Dictionary<RectTransform, RectState> _rectangles = new();
        private readonly Dictionary<TMP_Text, TextState> _texts = new();
        private readonly Dictionary<ContentSizeFitter, bool> _fitters = new();
        private SpellRepertoirePanel _panel;
        private RectTransform _root;
        private RectTransform _viewport;
        private TMP_Text _title;
        private TMP_Text _description;
        private TMP_Text _instruction;
        private string _language;
        private string _content;
        private float _width;
        private bool _applied;
        private bool _applying;

        private readonly struct RectState
        {
            internal readonly Vector2 AnchorMin, AnchorMax, Pivot, Position, Size;
            internal readonly Bounds Bounds;

            internal RectState(RectTransform rect, RectTransform root)
            {
                AnchorMin = rect.anchorMin;
                AnchorMax = rect.anchorMax;
                Pivot = rect.pivot;
                Position = rect.anchoredPosition;
                Size = rect.sizeDelta;
                Bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(root, rect);
            }

            internal void Restore(RectTransform rect)
            {
                rect.anchorMin = AnchorMin;
                rect.anchorMax = AnchorMax;
                rect.pivot = Pivot;
                rect.anchoredPosition = Position;
                rect.sizeDelta = Size;
            }
        }

        private readonly struct TextState
        {
            internal readonly bool AutoSize, Wrap, SizeContainer;
            internal readonly float FontSize, LineSpacing;
            internal readonly int MaxLines;
            internal readonly TextOverflowModes Overflow;

            internal TextState(TMP_Text text)
            {
                AutoSize = text.enableAutoSizing;
                Wrap = text.enableWordWrapping;
                SizeContainer = text.autoSizeTextContainer;
                FontSize = text.fontSize;
                LineSpacing = text.lineSpacing;
                MaxLines = text.maxVisibleLines;
                Overflow = text.overflowMode;
            }

            internal void Restore(TMP_Text text)
            {
                text.enableAutoSizing = AutoSize;
                text.enableWordWrapping = Wrap;
                text.autoSizeTextContainer = SizeContainer;
                text.fontSize = FontSize;
                text.lineSpacing = LineSpacing;
                text.maxVisibleLines = MaxLines;
                text.overflowMode = Overflow;
            }
        }

        internal void Capture(SpellRepertoirePanel panel)
        {
            _panel = panel;
            _root = panel.PreparationPanel.RectTransform;
            _viewport = panel.spellsScrollRect.viewport;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_root);
            _title = _root.FindChildRecursive("Title")?.GetComponentInChildren<TMP_Text>(true);
            _description = _root.FindChildRecursive("Description")?.GetComponentInChildren<TMP_Text>(true);
            _instruction = panel.preparedSpellsInstructions.TMP_Text;

            foreach (var rect in _root.GetComponentsInChildren<RectTransform>(true))
            {
                _rectangles[rect] = new RectState(rect, _root);
            }

            if (_viewport)
            {
                _rectangles[_viewport] = new RectState(_viewport, panel.RectTransform);
            }

            foreach (var text in new[] { _title, _description, _instruction }.Where(text => text).Distinct())
            {
                _texts[text] = new TextState(text);
                if (text.TryGetComponent<ContentSizeFitter>(out var fitter))
                {
                    _fitters[fitter] = fitter.enabled;
                }
            }

            _language = I2.Loc.LocalizationManager.CurrentLanguageCode;
        }

        internal void Apply()
        {
            if (_applying || !_root || !_title || !_instruction)
            {
                return;
            }

            _applying = true;
            try
            {
                RestoreGeometry();
                foreach (var fitter in _fitters.Keys.Where(fitter => fitter))
                {
                    fitter.enabled = false;
                }

                var titleGrowth = FitText(_title, true);
                var instructionGrowth = FitText(_instruction, false);
                var descriptionGrowth = _description && _description.gameObject.activeSelf
                    ? FitText(_description, false)
                    : 0f;
                var gauge = DirectChild(_panel.preparedSpellsGauge.transform);

                MoveBelow(_rectangles[_title.rectTransform].Bounds.min.y + 1f, titleGrowth,
                    DirectChild(_title.transform));
                MoveBelow(_rectangles[_instruction.rectTransform].Bounds.min.y + 1f, instructionGrowth,
                    DirectChild(_title.transform), DirectChild(_instruction.transform), gauge);
                if (_description)
                {
                    MoveBelow(_rectangles[_description.rectTransform].Bounds.min.y + 1f, descriptionGrowth,
                        DirectChild(_title.transform), DirectChild(_instruction.transform),
                        DirectChild(_description.transform), gauge);
                }

                var growth = titleGrowth + instructionGrowth + descriptionGrowth;
                SetHeightKeepingTop(_root, _root.rect.height + growth);
                foreach (var child in _root.Cast<Transform>().OfType<RectTransform>()
                             .Where(child => Mathf.Approximately(child.anchorMin.y, child.anchorMax.y)))
                {
                    // Fixed anchors otherwise follow the enlarged parent as well as the flow movement.
                    // Stretch anchors keep the native background and containers growing with the panel.
                    child.anchoredPosition += Vector2.up * (growth * (1f - child.anchorMin.y));
                }

                if (_viewport)
                {
                    var popup = RectTransformUtility.CalculateRelativeRectTransformBounds(_panel.RectTransform, _root);
                    var overlap = Mathf.Max(0f, _rectangles[_viewport].Bounds.max.y - popup.min.y + Padding);
                    if (overlap > 0f)
                    {
                        var world = _panel.RectTransform.TransformVector(Vector3.up * overlap);
                        var local = _viewport.parent.InverseTransformVector(world).y;
                        _viewport.offsetMax += Vector2.down * local;
                    }
                }

                foreach (var text in _texts.Keys.Where(text => text))
                {
                    text.ForceMeshUpdate(true);
                }

                _applied = true;
                _width = _root.rect.width;
                _content = CurrentContent();
                _language = I2.Loc.LocalizationManager.CurrentLanguageCode;
            }
            finally
            {
                _applying = false;
            }
        }

        private float FitText(TMP_Text text, bool reserveCloseButton)
        {
            var state = _texts[text];
            var rect = text.rectTransform;
            var bounds = _rectangles[rect].Bounds;
            var right = _root.rect.xMax - Padding;
            if (reserveCloseButton && _panel.cancelPreparationButton)
            {
                right = Mathf.Min(right, RectTransformUtility.CalculateRelativeRectTransformBounds(
                    _root, _panel.cancelPreparationButton.transform).min.x - Padding);
            }

            var width = Mathf.Max(1f, right - bounds.min.x);
            text.enableAutoSizing = false;
            text.enableWordWrapping = true;
            text.autoSizeTextContainer = false;
            text.fontSize = state.FontSize;
            text.lineSpacing = Mathf.Max(0f, state.LineSpacing);
            text.maxVisibleLines = int.MaxValue;
            text.overflowMode = TextOverflowModes.Overflow;
            var widthGrowth = width - rect.rect.width;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            rect.anchoredPosition += Vector2.right * (widthGrowth * rect.pivot.x);
            var initial = rect.rect.height;
            var height = Mathf.Max(initial, Mathf.Ceil(text.GetPreferredValues(text.text, width,
                float.PositiveInfinity).y) + 2f);
            SetHeightKeepingTop(rect, height);
            return height - initial;
        }

        private Transform DirectChild(Transform current)
        {
            while (current && current.parent != _root && current != _root)
            {
                current = current.parent;
            }

            return current;
        }

        private void MoveBelow(float top, float distance, params Transform[] excluded)
        {
            if (distance <= 0f)
            {
                return;
            }

            var moved = new List<RectTransform>();
            foreach (var entry in _rectangles.OrderBy(entry => ParentDepth(entry.Key)))
            {
                var rect = entry.Key;
                if (!rect || rect == _root || rect == _viewport || entry.Value.Bounds.max.y > top ||
                    excluded.Any(parent => parent && (rect == parent || rect.IsChildOf(parent))) ||
                    moved.Any(parent => rect.IsChildOf(parent)))
                {
                    continue;
                }

                rect.anchoredPosition += Vector2.down * distance;
                moved.Add(rect);
            }
        }

        private static int ParentDepth(Transform current)
        {
            var depth = 0;
            while (current && current.parent)
            {
                depth++;
                current = current.parent;
            }

            return depth;
        }

        private static void SetHeightKeepingTop(RectTransform rect, float height)
        {
            var growth = height - rect.rect.height;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            rect.anchoredPosition += Vector2.down * (growth * (1f - rect.pivot.y));
        }

        private string CurrentContent()
        {
            return string.Join("\n", _texts.Keys.Where(text => text).Select(text => text.text)) +
                   (_description && _description.gameObject.activeSelf);
        }

        private void RestoreGeometry()
        {
            foreach (var entry in _rectangles.Where(entry => entry.Key))
            {
                entry.Value.Restore(entry.Key);
            }
        }

        internal void Restore()
        {
            RestoreGeometry();
            foreach (var entry in _texts.Where(entry => entry.Key))
            {
                entry.Value.Restore(entry.Key);
            }

            foreach (var entry in _fitters.Where(entry => entry.Key))
            {
                entry.Key.enabled = entry.Value;
            }

            _applied = false;
        }

        private void LateUpdate()
        {
            if (!_panel || !_root)
            {
                return;
            }

            if (!_root.gameObject.activeInHierarchy)
            {
                if (_applied)
                {
                    Restore();
                }

                return;
            }

            if (_language != I2.Loc.LocalizationManager.CurrentLanguageCode)
            {
                _language = I2.Loc.LocalizationManager.CurrentLanguageCode;
                _panel.RefreshPreparation(false);
            }
            else if (!_applied || _width != _root.rect.width || _content != CurrentContent())
            {
                Apply();
            }
        }

        private void OnDisable()
        {
            Restore();
        }
    }

    private static readonly float[] FontSizes = [17f, 17f, 16f, 14.75f, 13.5f, 13.5f, 13.5f];

    private static Sprite _regularSlotSprite;

    private static Sprite _pactSlotSprite;

    private static Sprite RegularSlotSprite => _regularSlotSprite ??= Resources
        // ReSharper disable once Unity.UnknownResource
        .Load<GameObject>("Gui/Prefabs/Location/Magic/SlotStatus")
        .GetComponent<SlotStatus>().Available
        .GetComponent<Image>().sprite;

    private static Sprite PactSlotSprite => _pactSlotSprite ??= Resources
        // ReSharper disable once Unity.UnknownResource
        .Load<GameObject>("Gui/Prefabs/Location/Magic/SlotStatusWarlock")
        .GetComponent<SlotStatus>().Available
        .GetComponent<Image>().sprite;

    private static void SetSlotState([NotNull] SlotStatus component, bool? used)
    {
        component.Used.gameObject.SetActive(used == true);
        component.Available.gameObject.SetActive(used == false);
    }

    internal static float GetFontSize(int classesCount)
    {
        return FontSizes[classesCount % (MulticlassContext.MaxClasses + 1)];
    }

    private static void SetRegularSlotImage(Image img)
    {
        img.sprite = RegularSlotSprite;
    }

    private static void SetPactSlotImage(Image img)
    {
        img.sprite = PactSlotSprite;
    }

    internal static void SetupLevelUpClassSelectionStep(CharacterEditionScreen characterEditionScreen)
    {
        if (!Main.Settings.EnableMulticlass || characterEditionScreen is not CharacterLevelUpScreen)
        {
            return;
        }

        var characterCreationScreen = Gui.GuiService.GetScreen<CharacterCreationScreen>();
        var stagePanelPrefabs = characterCreationScreen.stagePanelPrefabs;
        var classSelectionPanel = Gui
            .GetPrefabFromPool(stagePanelPrefabs[1], characterEditionScreen.StagesPanelContainer)
            .GetComponent<CharacterStagePanel>();
        var deitySelectionPanel = Gui
            .GetPrefabFromPool(stagePanelPrefabs[2], characterEditionScreen.StagesPanelContainer)
            .GetComponent<CharacterStagePanel>();
        var newLevelUpSequence =
            new Dictionary<string, CharacterStagePanel> { { "ClassSelection", classSelectionPanel } };

        foreach (var stagePanel in characterEditionScreen.stagePanelsByName)
        {
            newLevelUpSequence.Add(stagePanel.Key, stagePanel.Value);

            if (stagePanel.Key == "LevelGains")
            {
                newLevelUpSequence.Add("DeitySelection", deitySelectionPanel);
            }
        }

        characterEditionScreen.stagePanelsByName = newLevelUpSequence;
    }

    internal static void RebuildSlotsTable(SpellRepertoirePanel __instance)
    {
        var spellRepertoire = __instance.SpellRepertoire;
        var character = __instance.GuiCharacter.RulesetCharacter;
        var isMulticaster = SharedSpellsContext.IsMulticaster(character);
        var sharedSpellLevel = SharedSpellsContext.GetSharedSpellLevel(character);
        var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);
        var classSpellLevel = spellRepertoire.spellCastingRace
            ? spellRepertoire.MaxSpellLevelOfSpellCastingLevel
            : SharedSpellsContext.MaxSpellLevelOfSpellCastingLevel(spellRepertoire);

        SharedSpellsContext.FactorMysticArcanum(character, spellRepertoire, ref classSpellLevel);

        if (spellRepertoire.SpellCastingFeature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null)
        {
            classSpellLevel = Math.Max(classSpellLevel,
                CharacterInspectionScreenEnhancement.GetInspectionSpellLevelMaximum(spellRepertoire, character, __instance.BindMode));
        }

        var sources = CharacterInspectionScreenEnhancement.GetInspectionSpellRepertoires(character, spellRepertoire, __instance.BindMode);
        var hasKnownCantrips = sources.Any(source => source.KnownCantrips.Count > 0 ||
            source.SpellCastingFeature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null &&
            CharacterInspectionScreenEnhancement.GetInspectionLearnedSpells(source).Any(spell => spell.SpellLevel == 0));
        var slotLevel = !isMulticaster ? classSpellLevel : Math.Max(sharedSpellLevel, warlockSpellLevel);
        var accountForCantrips = spellRepertoire.SpellCastingFeature.SpellListDefinition.HasCantrips ? 1 : 0;

        while (__instance.levelButtonsTable.childCount < classSpellLevel + accountForCantrips)
        {
            Gui.GetPrefabFromPool(__instance.levelButtonPrefab, __instance.levelButtonsTable);

            var index = __instance.levelButtonsTable.childCount - 1;
            var child = __instance.levelButtonsTable.GetChild(index);

            child.GetComponent<SpellLevelButton>().Bind(index + (accountForCantrips == 0 ? 1 : 0), __instance.LevelSelected);
        }

        while (__instance.levelButtonsTable.childCount > classSpellLevel + accountForCantrips)
        {
            Gui.ReleaseInstanceToPool(
                __instance.levelButtonsTable.GetChild(__instance.levelButtonsTable.childCount - 1).gameObject);
        }

        for (var index = 0; index < __instance.levelButtonsTable.childCount; index++)
        {
            __instance.levelButtonsTable.GetChild(index).gameObject.SetActive(accountForCantrips == 0 || index > 0 || hasKnownCantrips);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.levelButtonsTable);

        // patches the panel to display higher level spell slots from shared slots table
        // but hide the spell panels if class level not there yet
        for (var i = 0; i < __instance.spellsByLevelTable.childCount; i++)
        {
            var spellsByLevel = __instance.spellsByLevelTable.GetChild(i);
            var group = spellsByLevel.GetComponent<SpellsByLevelGroup>();
            if (!group || !spellsByLevel.gameObject.activeSelf)
            {
                continue;
            }

            var spellLevel = group.SpellLevel;
            if (spellLevel == 0 && !hasKnownCantrips)
            {
                spellsByLevel.gameObject.SetActive(false);
                continue;
            }

            for (var j = 0; j < spellsByLevel.childCount; j++)
            {
                var transform = spellsByLevel.GetChild(j);

                if (transform.TryGetComponent(typeof(SlotStatusTable), out _))
                {
                    transform.gameObject.SetActive(spellLevel <= slotLevel); // table header (with slots)
                }
                else
                {
                    transform.gameObject.SetActive(spellLevel <= classSpellLevel); // table content
                }
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.spellsByLevelTable);
    }

    internal static void PaintPactSlots(
        [NotNull] RulesetCharacter character,
        int totalSlotsCount,
        int totalSlotsRemainingCount,
        int slotLevel,
        int spellsAtLevel,
        SlotStatusTable slotStatusTable,
        bool ignorePactSlots = false)
    {
        var rectTransform = slotStatusTable.table;
        var warlockSpellRepertoire = SharedSpellsContext.GetWarlockSpellRepertoire(character);
        var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);

        var pactSlotsCount = 0;
        var pactSlotsRemainingCount = 0;
        var pactSlotsUsedCount = 0;

        if (warlockSpellRepertoire != null)
        {
            pactSlotsCount = SharedSpellsContext.GetWarlockMaxSlots(character);
            pactSlotsUsedCount = SharedSpellsContext.GetWarlockUsedSlots(character);
            pactSlotsRemainingCount = pactSlotsCount - pactSlotsUsedCount;
        }

        var spellSlotsCount = totalSlotsCount - pactSlotsCount;
        var spellSlotsRemainingCount = totalSlotsRemainingCount - pactSlotsRemainingCount;
        var spellSlotsUsedCount = spellSlotsCount - spellSlotsRemainingCount;

        for (var index = 0; index < rectTransform.childCount; ++index)
        {
            var component = rectTransform.GetChild(index).GetComponent<SlotStatus>();

            // only tweak levels that have both spell and pact slots
            if (slotLevel <= warlockSpellLevel)
            {
                // these are spell slots that display after pact ones
                if (index >= pactSlotsCount)
                {
                    var used = index >= pactSlotsCount + spellSlotsRemainingCount;

                    SetSlotState(component, used);
                }
                // these are pact slots that should only display at their highest level
                else if (!ignorePactSlots && slotLevel == warlockSpellLevel)
                {
                    var used = index >= totalSlotsRemainingCount - spellSlotsRemainingCount;

                    SetSlotState(component, used);
                }
                else
                {
                    SetSlotState(component, null);
                }
            }

            // paint spell slots white
            if (index >= pactSlotsCount || slotLevel > warlockSpellLevel)
            {
                //PATCH: support display cost on spell level blocks (SPELL_POINTS)
                if (character.IsSpellPointsEnabled())
                {
                    SpellPointsContext.DisplayCostOnSpellLevelBlocks(slotStatusTable, component, slotLevel,
                        spellsAtLevel);
                }
                else
                {
                    SetRegularSlotImage(component.Available.GetComponent<Image>());
                }
            }
            else
            {
                SetPactSlotImage(component.Available.GetComponent<Image>());
            }
        }

        string str;

        if (totalSlotsRemainingCount == 0)
        {
            str = "Screen/&SpellSlotsUsedAllDescription";
        }
        else if (totalSlotsRemainingCount == totalSlotsCount)
        {
            str = "Screen/&SpellSlotsUsedNoneDescription";
        }
        else if (pactSlotsRemainingCount == pactSlotsCount)
        {
            str = Gui.Format("Screen/&SpellSlotsUsedLongDescription", spellSlotsUsedCount.ToString());
        }
        else if (spellSlotsRemainingCount == spellSlotsCount)
        {
            str = Gui.Format("Screen/&SpellSlotsUsedShortDescription", pactSlotsUsedCount.ToString());
        }
        else
        {
            str = Gui.Format("Screen/&SpellSlotsUsedShortLongDescription", pactSlotsUsedCount.ToString(),
                spellSlotsUsedCount.ToString());
        }

        rectTransform.GetComponent<GuiTooltip>().Content = str;
    }

    // reaction and flexible panels should paint white slots first
    internal static void PaintPactSlotsAlternate(
        [NotNull] RulesetCharacter character,
        int totalSlotsCount,
        int totalSlotsRemainingCount,
        int slotLevel,
        [NotNull] RectTransform rectTransform)
    {
        var warlockSpellRepertoire =
            SharedSpellsContext.GetWarlockSpellRepertoire(character);
        var warlockSpellLevel = SharedSpellsContext.GetWarlockSpellLevel(character);
        var pactSlotsCount = 0;
        var pactSlotsRemainingCount = 0;

        if (warlockSpellRepertoire != null)
        {
            var pactSlotsUsedCount = SharedSpellsContext.GetWarlockUsedSlots(character);

            pactSlotsCount = SharedSpellsContext.GetWarlockMaxSlots(character);
            pactSlotsRemainingCount = pactSlotsCount - pactSlotsUsedCount;
        }

        var spellSlotsCount = totalSlotsCount - pactSlotsCount;

        for (var index = 0; index < rectTransform.childCount; ++index)
        {
            var component = rectTransform.GetChild(index).GetComponent<SlotStatus>();

            if (slotLevel <= warlockSpellLevel)
            {
                if (index < spellSlotsCount)
                {
                    var used = index >= totalSlotsRemainingCount - pactSlotsRemainingCount;

                    SetSlotState(component, used);
                }
                else if (slotLevel == warlockSpellLevel)
                {
                    var used = index >= spellSlotsCount + pactSlotsRemainingCount;

                    SetSlotState(component, used);
                }
                else
                {
                    SetSlotState(component, null);
                }
            }

            if (index >= spellSlotsCount && slotLevel <= warlockSpellLevel)
            {
                SetPactSlotImage(component.Available.GetComponent<Image>());
            }
            else
            {
                //PATCH: support alternate spell system to avoid displaying spell slots on selection (SPELL_POINTS)
                if (character.IsSpellPointsEnabled())
                {
                    SetSlotState(component, null);
                }
                else
                {
                    SetRegularSlotImage(component.Available.GetComponent<Image>());
                }
            }
        }
    }

    internal static void PaintSlotsWhite([NotNull] RectTransform rectTransform)
    {
        for (var index = 0; index < rectTransform.childCount; ++index)
        {
            var child = rectTransform.GetChild(index);
            var component = child.GetComponent<SlotStatus>();

            SetRegularSlotImage(component.Available.GetComponent<Image>());
        }
    }

    /**Adds available slot level options to optionsAvailability and returns index of pre-picked option, or -1*/
    internal static int AddAvailableSubLevels(
        Dictionary<int, bool> optionsAvailability,
        RulesetCharacter character,
        [NotNull] RulesetSpellRepertoire spellRepertoire, int minSpellLevel = 1, int maxSpellLevel = 0,
        SpellDefinition spellDefinition = null,
        bool enforceSpellCastingLimit = false)
    {
        var availableSlotLevels = new List<int>();

        if (maxSpellLevel == 0)
        {
            maxSpellLevel = Math.Max(
                SharedSpellsContext.GetSharedSpellLevel(character),
                SharedSpellsContext.GetWarlockSpellLevel(character));
        }

        for (var level = minSpellLevel; level <= maxSpellLevel; ++level)
        {
            if (!spellRepertoire.TryGetAvailableSlotLevel(
                    character,
                    level,
                    spellDefinition,
                    out var isAvailable))
            {
                continue;
            }

            if (isAvailable &&
                enforceSpellCastingLimit &&
                !SpellSlotCastingLimit2024Context.CanUseSpellSlotLevel(
                    character,
                    spellRepertoire,
                    spellDefinition,
                    level))
            {
                isAvailable = false;
            }

            optionsAvailability.Add(level, isAvailable);

            if (isAvailable)
            {
                availableSlotLevels.Add(level);
            }
        }

        var selectedLevel = spellRepertoire.GetPreferredSlotLevel(character, availableSlotLevels);

        if (selectedLevel == 0)
        {
            return -1;
        }

        var option = 0;

        foreach (var slotLevel in optionsAvailability.Keys)
        {
            if (slotLevel == selectedLevel)
            {
                return option;
            }

            option++;
        }

        return -1;
    }

    [CanBeNull]
    internal static string GetAllClassesLabel([CanBeNull] GuiCharacter character, char separator)
    {
        var dbCharacterClassDefinition = DatabaseRepository.GetDatabase<CharacterClassDefinition>();
        var builder = new StringBuilder();
        var snapshot = character?.Snapshot;
        var hero = character?.RulesetCharacterHero;
        var duplicate = character?.RulesetCharacter as RulesetCharacterSimulacrum;

        if (duplicate != null &&
            SimulacrumBehavior.TryGetClassLevels(duplicate, out var duplicateClasses) &&
            duplicateClasses.Count > 0)
        {
            var classesCount = duplicateClasses.Count;

            if (classesCount == 1)
            {
                var classDefinition = duplicateClasses[0].ClassDefinition;

                builder
                    .Append(classDefinition.FormatTitle())
                    .Append(separator);

                if (SimulacrumBehavior.TryGetPrimarySubclass(
                        duplicate,
                        classDefinition,
                        out var subclassDefinition))
                {
                    builder
                        .Append(subclassDefinition.FormatTitle())
                        .Append(separator);
                }

                return builder.ToString().Remove(builder.Length - 1, 1);
            }

            var newLine = separator == '\n' || classesCount <= 4 ? 2 : 3;
            var index = 0;

            foreach (var classLevel in duplicateClasses
                         .OrderByDescending(entry => entry.Level)
                         .ThenBy(entry => entry.ClassDefinition.FormatTitle()))
            {
                builder
                    .Append(classLevel.ClassDefinition.FormatTitle())
                    .Append('/')
                    .Append(classLevel.Level);

                if (classesCount <= 3)
                {
                    builder.Append(separator);
                }
                else
                {
                    builder.Append(' ');
                    index++;

                    if (index % newLine == 0)
                    {
                        builder.Append('\n');
                    }
                }
            }
        }
        else if (snapshot != null && snapshot.Classes.Length > 1)
        {
            foreach (var className in snapshot.Classes)
            {
                var classTitle = dbCharacterClassDefinition.GetElement(className).FormatTitle();

                builder
                    .Append(classTitle)
                    .Append(separator);
            }
        }
        else if (hero != null && hero.ClassesAndLevels.Count > 1)
        {
            var i = 0;
            var classesCount = hero.ClassesAndLevels.Count;
            var newLine = separator == '\n' || classesCount <= 4 ? 2 : 3;
            var sortedClasses = hero.ClassesAndLevels
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key.FormatTitle());

            foreach (var kvp in sortedClasses)
            {
                builder
                    .Append(kvp.Key.FormatTitle())
                    .Append('/')
                    .Append(kvp.Value);

                if (classesCount <= 3)
                {
                    builder
                        .Append(separator);
                }
                else
                {
                    builder
                        .Append(' ');

                    i++;

                    if (i % newLine == 0)
                    {
                        builder
                            .Append('\n');
                    }
                }
            }
        }
        else
        {
            return null;
        }

        return builder.ToString().Remove(builder.Length - 1, 1);
    }

    [NotNull]
    internal static string GetAllClassesHitDiceLabel([NotNull] GuiCharacter character, out int dieTypeCount)
    {
        // Assert.IsNotNull(character, nameof(character));

        var builder = new StringBuilder();
        var hero = character.RulesetCharacterHero;
        var dieTypesCount = new Dictionary<DieType, int>();
        const char SEPARATOR = ' ';

        foreach (var characterClassDefinition in hero.ClassesAndLevels.Keys)
        {
            if (!dieTypesCount.ContainsKey(characterClassDefinition.HitDice))
            {
                dieTypesCount.Add(characterClassDefinition.HitDice, 0);
            }

            dieTypesCount[characterClassDefinition.HitDice] += hero.ClassesAndLevels[characterClassDefinition];
        }

        foreach (var dieType in dieTypesCount.Keys)
        {
            builder
                .Append(dieTypesCount[dieType])
                .Append(Gui.GetDieSymbol(dieType))
                .Append(SEPARATOR);
        }

        dieTypeCount = dieTypesCount.Count;

        return builder.Remove(builder.Length - 1, 1).ToString();
    }

    private static readonly ConditionalWeakTable<LearnStepItem, LearnStepButtonPresentation> AutoButtonPresentations = new();
    private static readonly ConditionalWeakTable<LearnStepItem, SpellLearnStepLayout> SpellLearnStepLayouts = new();

    private sealed class LearnStepButtonPresentation
    {
        private readonly Dictionary<GuiLabel, string> _labels;
        private readonly List<Action> _restoreTooltips = [];
        private readonly GuiTooltip[] _tooltips;

        internal LearnStepButtonPresentation(LearnStepItem item)
        {
            _labels = item.autoButton.GetComponentsInChildren<GuiLabel>(true)
                .ToDictionary(label => label, label => label.Text);
            _tooltips = item.autoButton.GetComponentsInChildren<GuiTooltip>(true);
            foreach (var tooltip in _tooltips)
            {
                var content = tooltip.Content;
                var tooltipClass = tooltip.TooltipClass;
                var disabled = tooltip.Disabled;
                var context = tooltip.Context;
                var provider = tooltip.DataProvider;
                _restoreTooltips.Add(() =>
                {
                    if (!tooltip) { return; }
                    tooltip.Content = content;
                    tooltip.TooltipClass = tooltipClass;
                    tooltip.Disabled = disabled;
                    tooltip.Context = context;
                    tooltip.DataProvider = provider;
                });
            }
        }

        internal void Restore()
        {
            foreach (var entry in _labels.Where(entry => entry.Key))
            {
                entry.Key.Text = entry.Value;
            }
            foreach (var restore in _restoreTooltips)
            {
                restore();
            }
        }

        internal void ShowKeepChoices()
        {
            foreach (var label in _labels.Keys.Where(label => label))
            {
                label.Text = Gui.Localize("Screen/&FeatSpellReplacementKeepTitle");
            }
            foreach (var tooltip in _tooltips.Where(tooltip => tooltip))
            {
                tooltip.TooltipClass = GuiManager.DefaultTooltipClass;
                tooltip.Content = "Screen/&FeatSpellReplacementKeepDescription";
                tooltip.Disabled = false;
                tooltip.Context = null;
                tooltip.DataProvider = null;
            }
        }
    }
    private sealed class SpellLearnStepLayout
    {
        private readonly Dictionary<RectTransform, (Vector2 Position, Vector2 Size)> _rectangles;
        private readonly Dictionary<TMP_Text, (bool AutoSize, bool Wrap, float FontSize, int MaxLines, float LineSpacing)> _headers;
        private readonly RectTransform _buttonBar;
        private readonly float _activeHeaderHeight;
        private readonly float _inactiveHeaderHeight;
        private readonly float _inactiveHeight;

        internal SpellLearnStepLayout(LearnStepItem item)
        {
            _buttonBar = (RectTransform)item.resetButton.transform.parent;
            _rectangles = new[]
                {
                    item.headerLabelActive.RectTransform, item.headerLabelInactive.RectTransform,
                    item.choicesLabel.RectTransform, item.inactiveGroup, _buttonBar
                }
                .ToDictionary(rect => rect, rect => (rect.anchoredPosition, rect.sizeDelta));
            _headers = new[] { item.headerLabelActive.TMP_Text, item.headerLabelInactive.TMP_Text }
                .ToDictionary(text => text, text => (text.enableAutoSizing, text.enableWordWrapping,
                    text.enableAutoSizing ? text.fontSizeMax : text.fontSize, text.maxVisibleLines, text.lineSpacing));
            _activeHeaderHeight = item.headerLabelActive.RectTransform.rect.height;
            _inactiveHeaderHeight = item.headerLabelInactive.RectTransform.rect.height;
            _inactiveHeight = item.inactiveGroup.rect.height;
        }

        internal void Restore()
        {
            foreach (var entry in _rectangles)
            {
                var rect = entry.Key;
                var state = entry.Value;
                rect.anchoredPosition = state.Position;
                rect.sizeDelta = state.Size;
            }

            foreach (var entry in _headers)
            {
                var text = entry.Key;
                var state = entry.Value;
                text.enableAutoSizing = state.AutoSize;
                text.enableWordWrapping = state.Wrap;
                text.fontSize = state.FontSize;
                text.maxVisibleLines = state.MaxLines;
                text.lineSpacing = state.LineSpacing;
            }
        }

        internal void Apply(LearnStepItem item, bool active)
        {
            // Keep the native font sizes. Give wrapped titles their measured height instead of shrinking them.
            foreach (var entry in _headers)
            {
                var text = entry.Key;
                var state = entry.Value;
                text.enableAutoSizing = false;
                text.enableWordWrapping = true;
                text.fontSize = state.FontSize;
                text.maxVisibleLines = int.MaxValue;
                // Native single-line headers use negative spacing that overlaps glyphs when wrapped.
                text.lineSpacing = Mathf.Max(0f, state.LineSpacing);
            }

            var inactiveHeader = item.headerLabelInactive.RectTransform;
            if (!item.backOneStepButton.gameObject.activeSelf)
            {
                // Locked rows need no space for the previous-step button.
                inactiveHeader.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                    item.inactiveGroup.rect.width - inactiveHeader.anchoredPosition.x - 12f);
            }

            var activeHeight = FitHeaderHeight(item.headerLabelActive.TMP_Text, _activeHeaderHeight);
            var inactiveHeight = FitHeaderHeight(item.headerLabelInactive.TMP_Text, _inactiveHeaderHeight);
            var addedHeight = activeHeight - _activeHeaderHeight;
            _buttonBar.anchoredPosition = _rectangles[_buttonBar].Position + Vector2.down * addedHeight;
            item.choicesLabel.RectTransform.anchoredPosition =
                _rectangles[item.choicesLabel.RectTransform].Position + Vector2.down * addedHeight;
            item.inactiveGroup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                _inactiveHeight + inactiveHeight - _inactiveHeaderHeight);

            LayoutRebuilder.ForceRebuildLayoutImmediate(item.choicesLabel.RectTransform);
            item.activeGroup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                item.choicesLabel.RectTransform.rect.height - item.choicesLabel.RectTransform.anchoredPosition.y + 12f);
            item.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                active ? item.activeGroup.rect.height : item.inactiveGroup.rect.height);
        }

        private static float FitHeaderHeight(TMP_Text text, float minimumHeight)
        {
            var height = Mathf.Max(minimumHeight,
                Mathf.Ceil(text.GetPreferredValues(text.text, text.rectTransform.rect.width, float.PositiveInfinity).y) + 2f);
            text.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            text.ForceMeshUpdate(true);
            return height;
        }
    }

    internal static void RefreshSpellLearnStepTitles(CharacterStageSpellSelectionPanel panel)
    {
        if (panel?.learnStepsTable == null)
        {
            return;
        }

        for (var index = 0; index < panel.learnStepsTable.childCount; ++index)
        {
            var item = panel.learnStepsTable.GetChild(index).GetComponent<LearnStepItem>();

            if (!item)
            {
                continue;
            }

            if (AutoButtonPresentations.TryGetValue(item, out var buttonPresentation))
            {
                buttonPresentation.Restore();
            }

            var replacement = LevelUpHelper.GetFeatSpellReplacement(panel.currentHero.GetHeroBuildingData(), item.Tag);
            if (replacement != null)
            {
                SetLearnStepTitle(item, Gui.Format("Screen/&FeatSpellReplacementTitle", replacement.FormatSourceTitle()));
                item.choicesLabel.Text = Gui.Localize("Screen/&FeatSpellReplacementDescription");
                if (index == panel.currentLearnStep)
                {
                    item.autoButton.gameObject.SetActive(true);
                    item.autoButton.interactable = true;
                    if (buttonPresentation == null)
                    {
                        buttonPresentation = new LearnStepButtonPresentation(item);
                        AutoButtonPresentations.Add(item, buttonPresentation);
                    }
                    buttonPresentation.ShowKeepChoices();
                }

                ApplySpellLearnStepLayout(item, index == panel.currentLearnStep);
                continue;
            }

            if (!Tabletop2024Context.TryGetTabletop2024FeatSpellLearnStepTitle(
                    item.PoolType,
                    item.Tag,
                    out var title))
            {
                continue;
            }

            SetLearnStepTitle(item, title);
            ApplySpellLearnStepLayout(item, index == panel.currentLearnStep);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.learnStepsTable);
    }

    private static void ApplySpellLearnStepLayout(LearnStepItem item, bool active)
    {
        if (!SpellLearnStepLayouts.TryGetValue(item, out var layout))
        {
            layout = new SpellLearnStepLayout(item);
            SpellLearnStepLayouts.Add(item, layout);
        }

        layout.Apply(item, active);
    }

    private static void SetLearnStepTitle(LearnStepItem item, string title)
    {
        if (!item || string.IsNullOrEmpty(title))
        {
            return;
        }

        SetLearnStepLabel(item.headerLabelActive, title);
        SetLearnStepLabel(item.headerLabelInactive, title);
    }

    private static void SetLearnStepLabel(GuiLabel label, string title)
    {
        if (!label || label.Text == title)
        {
            return;
        }

        label.Text = title;
    }

    internal static void RestoreSpellLearnStepLayouts(CharacterStageSpellSelectionPanel panel)
    {
        if (panel.learnStepsTable == null)
        {
            return;
        }

        foreach (Transform child in panel.learnStepsTable)
        {
            var item = child.GetComponent<LearnStepItem>();
            if (item && SpellLearnStepLayouts.TryGetValue(item, out var layout))
            {
                layout.Restore();
            }
            if (item && AutoButtonPresentations.TryGetValue(item, out var presentation))
            {
                presentation.Restore();
                AutoButtonPresentations.Remove(item);
            }
        }
    }

    private static bool TryBindFeatSpellReplacement(
        SpellsByLevelGroup group,
        SpellListDefinition spellListDefinition,
        int spellLevel,
        SpellBox.SpellBoxChangedHandler spellBoxChanged,
        string spellTag,
        bool canAcquireSpells,
        RectTransform tooltipAnchor,
        TooltipDefinitions.AnchorMode anchorMode,
        CharacterStageSpellSelectionPanel panel)
    {
        var data = panel.currentHero.GetHeroBuildingData();
        var replacement = LevelUpHelper.GetFeatSpellReplacement(data, spellTag);
        if (replacement == null)
        {
            return false;
        }

        var content = ServiceRepository.GetService<IGamingPlatformService>();
        var spells = spellListDefinition.SpellsByLevel.Where(level => level.Level == spellLevel)
            .SelectMany(level => level.Spells).Where(replacement.IsEligible)
            .Where(spell => content.IsContentPackAvailable(spell.ContentPack))
            .Concat(replacement.PreviousSpells.Where(spell => spell.SpellLevel == spellLevel)).Distinct().ToList();
        group.SpellLevel = spellLevel;
        group.extraSpellsMap.Clear();
        group.autoPreparedSpells.Clear();
        group.spellsTable.gameObject.SetActive(true);
        group.slotStatusTable.gameObject.SetActive(true);
        group.CommonBind(null, SpellBox.BindMode.Learning, spellBoxChanged, spells, null, null,
            group.autoPreparedSpells, null, new Dictionary<SpellDefinition, string>(), group.extraSpellsMap,
            tooltipAnchor, anchorMode);

        var selected = replacement.GetSelected(data);
        foreach (Transform child in group.spellsTable)
        {
            if (!child.gameObject.activeSelf)
            {
                continue;
            }

            var box = child.GetComponent<SpellBox>();
            if (canAcquireSpells)
            {
                box.RefreshLearningInProgress(replacement.CanSelect(data, box.SpellDefinition),
                    selected.Contains(box.SpellDefinition));
            }
            else
            {
                box.RefreshLearningInactive(selected.Contains(box.SpellDefinition));
            }
        }

        group.slotStatusTable.Bind(null, spellLevel, false, null, false);
        return true;
    }

    internal static void SpellsByLevelGroupBindLearning(
        [NotNull] SpellsByLevelGroup group,
        [NotNull] ICharacterBuildingService characterBuildingService,
        FeatureDefinitionCastSpell spellFeature,
        [NotNull] SpellListDefinition spellListDefinition,
        List<string> restrictedSchools,
        bool ritualOnly,
        int spellLevel,
        SpellBox.SpellBoxChangedHandler spellBoxChanged,
        List<SpellDefinition> knownSpells,
        List<SpellDefinition> unlearnedSpells,
        [NotNull] string spellTag,
        bool canAcquireSpells,
        bool unlearn,
        RectTransform tooltipAnchor,
        TooltipDefinitions.AnchorMode anchorMode,
        CharacterStageSpellSelectionPanel panel)
    {
        if (TryBindFeatSpellReplacement(group, spellListDefinition, spellLevel, spellBoxChanged,
                spellTag, canAcquireSpells, tooltipAnchor, anchorMode, panel))
        {
            return;
        }

        var localHeroCharacter = panel.currentHero;
        var heroBuildingData = localHeroCharacter.GetHeroBuildingData();
        var selectionRepertoire = localHeroCharacter.SpellRepertoires
            .Find(repertoire => repertoire.SpellCastingFeature == spellFeature);
        var pointPool = GetCurrentPool(panel, characterBuildingService, heroBuildingData, spellFeature);
        canAcquireSpells &= pointPool == null ||
                            (spellLevel >= pointPool.MinSpellLevel &&
                             (pointPool.MaxSpellLevel <= 0 || spellLevel <= pointPool.MaxSpellLevel));
        var effectiveSpellListDefinition = pointPool?.spellListOverride ?? spellListDefinition;
        var effectiveRitualOnly = ritualOnly || pointPool?.ritualOnly == true;
        var useDedicatedFeatSpellList =
            Tabletop2024Context.UsesDedicatedFeatSpellSelectionList2024(
                spellFeature,
                effectiveSpellListDefinition,
                spellTag);

        group.extraSpellsMap.Clear();
        group.spellsTable.gameObject.SetActive(true);
        group.slotStatusTable.gameObject.SetActive(true);
        group.SpellLevel = spellLevel;

        selectionRepertoire?.EnumerateExtraSpellsOfLevel(group.SpellLevel, group.extraSpellsMap);

        var allSpells = effectiveSpellListDefinition
            .SpellsByLevel[effectiveSpellListDefinition.HasCantrips ? spellLevel : spellLevel - 1]
            .Spells
            .Where(spell => restrictedSchools.Count == 0 || restrictedSchools.Contains(spell.SchoolOfMagic))
            .Where(spell => !effectiveRitualOnly || spell.Ritual)
            .ToList();

        if (!useDedicatedFeatSpellList)
        {
            allSpells.AddRange(characterBuildingService
                .EnumerateKnownAndAcquiredSpells(heroBuildingData, string.Empty)
                .Where(s => s.SpellLevel == spellLevel && !allSpells.Contains(s))
                .Where(spell => !effectiveRitualOnly || spell.Ritual)
            );

            if (!spellTag.Contains(AttributeDefinitions.TagRace)) // this is a patch over original TA code
            {
                localHeroCharacter.EnumerateFeaturesToBrowse<FeatureDefinitionMagicAffinity>(group.features);

                foreach (var spell in from FeatureDefinitionMagicAffinity feature in @group.features
                         where feature.ExtendedSpellList
                         from spell in feature.ExtendedSpellList
                             .SpellsByLevel[effectiveSpellListDefinition.HasCantrips ? spellLevel : spellLevel - 1]
                             .Spells
                         where !allSpells.Contains(spell) && (!effectiveRitualOnly || spell.Ritual) &&
                               (restrictedSchools.Count == 0 || restrictedSchools.Contains(spell.SchoolOfMagic))
                         select spell)
                {
                    allSpells.Add(spell);
                }
            }
        }

        var tagBySpell = new Dictionary<SpellDefinition, string>();

        group.autoPreparedSpells.Clear();

        if (!useDedicatedFeatSpellList && group.SpellLevel > 0)
        {
            LevelUpHelper.EnumerateExtraSpells(group.extraSpellsMap, localHeroCharacter, selectionRepertoire);

            foreach (var entry in group.extraSpellsMap
                         .Where(entry => entry.Key.SpellLevel == group.SpellLevel))
            {
                var spell = entry.Key;
                group.autoPreparedSpells.TryAdd(spell);
                tagBySpell[spell] = entry.Value;

                if (!effectiveRitualOnly || spell.Ritual)
                {
                    allSpells.TryAdd(spell);
                }
            }
        }

        var service = ServiceRepository.GetService<IGamingPlatformService>();

        for (var index = allSpells.Count - 1; index >= 0; --index)
        {
            if (!service.IsContentPackAvailable(allSpells[index].ContentPack))
            {
                allSpells.RemoveAt(index);
            }
        }

        if (!useDedicatedFeatSpellList &&
            !spellTag.Contains(AttributeDefinitions.TagRace)) // this is a patch over original TA code
        {
            FilterMulticlassBleeding(group, localHeroCharacter, allSpells, group.autoPreparedSpells, pointPool,
                group.extraSpellsMap);
        }

        NormalizeSpellSourceTags(tagBySpell);
        NormalizeSpellSourceTags(group.extraSpellsMap);

        group.CommonBind(null, unlearn ? SpellBox.BindMode.Unlearn : SpellBox.BindMode.Learning, spellBoxChanged,
            allSpells, null, null, group.autoPreparedSpells, unlearnedSpells, tagBySpell,
            group.extraSpellsMap, tooltipAnchor, anchorMode);

        if (unlearn)
        {
            group.RefreshUnlearning(characterBuildingService, knownSpells, unlearnedSpells, [],
                spellTag,
                canAcquireSpells && spellLevel > 0);
        }
        else
        {
            group.RefreshLearning(characterBuildingService, knownSpells, unlearnedSpells, spellTag,
                canAcquireSpells);
        }

        group.slotStatusTable.Bind(null, spellLevel, false, null, false);
    }

    private static PointPool GetCurrentPool(
        CharacterStageSpellSelectionPanel panel,
        ICharacterBuildingService characterBuildingService,
        CharacterHeroBuildingData heroBuildingData,
        FeatureDefinitionCastSpell spellFeature)
    {
        var tag = string.Empty;

        for (var index = 0; index < panel.learnStepsTable.childCount; ++index)
        {
            var child = panel.learnStepsTable.GetChild(index);

            if (!child.gameObject.activeSelf)
            {
                continue;
            }

            var component = child.GetComponent<LearnStepItem>();
            var status = index != panel.currentLearnStep
                ? index != panel.currentLearnStep - 1
                    ? LearnStepItem.Status.Locked
                    : LearnStepItem.Status.Previous
                : LearnStepItem.Status.InProgress;

            if (status == LearnStepItem.Status.InProgress)
            {
                tag = component.Tag;
            }
        }

        HeroDefinitions.PointsPoolType poolType;

        if (panel.IsFinalStep)
        {
            tag = panel.allTags[panel.allTags.Count - 1];
            poolType = panel.GetPoolTypeOfIndex(panel.currentLearnStep - 1);
        }
        else
        {
            poolType = panel.GetPoolTypeOfIndex(panel.currentLearnStep);
        }

        return CharacterBuildingManagerPatcher.ResolveFeatGrantedSpellSelectionPointPool(
            characterBuildingService,
            heroBuildingData,
            poolType,
            tag,
            spellFeature);
    }

    private static void NormalizeSpellSourceTags(IDictionary<SpellDefinition, string> tagsBySpell)
    {
        foreach (var pair in tagsBySpell.ToArray())
        {
            var normalizedTag = SpellBoxPatcher.NormalizeSpellSourceTag(pair.Value);

            if (normalizedTag != pair.Value)
            {
                tagsBySpell[pair.Key] = normalizedTag;
            }
        }
    }

    private static void FilterMulticlassBleeding(
        [NotNull] SpellsByLevelGroup __instance,
        [NotNull] RulesetCharacterHero caster,
        [NotNull] List<SpellDefinition> allSpells,
        [NotNull] List<SpellDefinition> autoPreparedSpells,
        PointPool pointPool,
        IDictionary<SpellDefinition, string> extraSpellsMap)
    {
        var spellsOverriden = pointPool?.spellListOverride;
        var spellLevel = __instance.SpellLevel;

        // avoids auto prepared spells from other classes to bleed in
        var allowedAutoPreparedSpells = LevelUpHelper.GetAllowedAutoPreparedSpells(caster)
            .Where(x => x.SpellLevel == spellLevel);

        autoPreparedSpells.SetRange(allowedAutoPreparedSpells);

        //Select allowed spells - all spells if list is overriden by the pool, or all allowed spells of current level
        var allowedSpells = spellsOverriden
            ? [..allSpells]
            : LevelUpHelper.GetAllowedSpells(caster).Where(x => x.SpellLevel == spellLevel).ToArray();

        var otherClassesKnownSpells = LevelUpHelper.GetOtherClassesKnownSpells(caster)
            .Where(x => x.Key.SpellLevel == spellLevel).ToArray();

        allSpells.RemoveAll(x => !allowedSpells.Contains(x) && otherClassesKnownSpells.All(p => p.Key != x));

        foreach (var pair in otherClassesKnownSpells)
        {
            var spell = pair.Key;

            //Add multiclass tag to spells known from other classes
            if (!Main.Settings.EnableRelearnSpells)
            {
                extraSpellsMap.TryAdd(spell, pair.Value);
            }

            // displays known spells from other classes
            if (!Main.Settings.DisplayAllKnownSpellsDuringLevelUp)
            {
                continue;
            }

            allSpells.TryAdd(spell);

            //allow re-learning already known spells from other classes
            if (!Main.Settings.EnableRelearnSpells || !allowedSpells.Contains(spell))
            {
                autoPreparedSpells.TryAdd(spell);
            }
        }

        // remove spells bleed from other classes
        if (!Main.Settings.DisplayAllKnownSpellsDuringLevelUp)
        {
            allSpells.RemoveAll(x => !allowedSpells.Contains(x));
        }
    }

    [CanBeNull]
    internal static string GetLevelAndExperienceTooltip([NotNull] GuiCharacter character)
    {
        var builder = new StringBuilder();
        var hero = character.RulesetCharacterHero;

        if (hero == null)
        {
            return null;
        }

        var characterLevelAttribute = hero.GetAttribute(AttributeDefinitions.CharacterLevel);
        var characterLevel = characterLevelAttribute.CurrentValue;
        var experience = hero.TryGetAttributeValue(AttributeDefinitions.Experience);

        if (characterLevel == characterLevelAttribute.MaxValue)
        {
            builder.Append(Gui.Format("Format/&LevelAndExperienceMaxedFormat", characterLevel.ToString("N0"),
                experience.ToString("N0")));
        }
        else
        {
            var num = Mathf.Max(0.0f, ExperienceThresholds[characterLevel] - experience);

            builder.Append(Gui.Format("Format/&LevelAndExperienceFormat", characterLevel.ToString("N0"),
                experience.ToString("N0"), num.ToString("N0"), (characterLevel + 1).ToString("N0")));
        }

        if (hero.ClassesAndLevels.Count <= 1)
        {
            return builder.ToString();
        }

        builder.Append('\n');

        for (var i = 0; i < hero.ClassesHistory.Count; i++)
        {
            var characterClassDefinition = hero.ClassesHistory[i];

            hero.ClassesAndSubclasses.TryGetValue(characterClassDefinition,
                out var characterSubclassDefinition);

            builder
                .AppendFormat("\n{0:00} - ", i + 1)
                .Append(characterClassDefinition.FormatTitle());

            // NOTE: don't use characterSubclassDefinition?. which bypasses Unity object lifetime check
            if (characterSubclassDefinition)
            {
                builder
                    .Append(' ')
                    .Append(characterSubclassDefinition.FormatTitle());
            }
        }

        return builder.ToString();
    }
}
