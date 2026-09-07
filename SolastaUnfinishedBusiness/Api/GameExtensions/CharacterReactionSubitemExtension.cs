using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class CharacterReactionSubitemExtension
{
    private const float DefaultReactionChoiceWidth = 250f;
    private const float ExpandedReactionChoiceWidth = 300f;

    internal static void BindTargetChoice(
        [NotNull] this CharacterReactionSubitem instance,
        [NotNull] ReactionRequestSelectTarget reactionRequest,
        int option,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        if (option < 0 || option >= reactionRequest.Candidates.Count)
        {
            return;
        }

        var label = instance.label;
        var toggle = instance.toggle;
        var target = reactionRequest.Candidates[option];
        var layoutState = instance.GetComponent<ReactionChoiceLayoutState>() ??
                          instance.gameObject.AddComponent<ReactionChoiceLayoutState>();

        layoutState.Capture(instance);

        var tooltip = GetOrMakeBackgroundTooltip(toggle.transform);

        if (tooltip)
        {
            tooltip.Disabled = true;
            tooltip.Content = string.Empty;
            tooltip.Context = null;
            tooltip.DataProvider = null;
        }

        label.Text = target.Guid == reactionRequest.Character.Guid
            ? Gui.Localize("Reaction/&CustomReactionSelfTitle")
            : ReactionCharacterNameFormatter.Format(target);
        toggle.interactable = interactable;
        instance.canvasGroup.interactable = interactable;
        instance.SubitemSelected = subitemSelected;

        layoutState.Apply(instance, GetReactionChoiceWidth(instance));

        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }
    }

    internal static void BindSpellResource(
        [NotNull] this CharacterReactionSubitem instance,
        [NotNull] SpellCastingResourceContext.ResourceOption option,
        [NotNull] RulesetCharacter character,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        var layoutState = instance.GetComponent<ReactionChoiceLayoutState>() ??
                          instance.gameObject.AddComponent<ReactionChoiceLayoutState>();
        layoutState.Capture(instance);

        // Ordinals identify choices; native rows must receive the actual casting level
        // and owner to draw the normal slot pips, including two choices at the same level.
        var displayRepertoire = option.Kind is SpellCastingResourceContext.ResourceKind.SpellMastery
            or SpellCastingResourceContext.ResourceKind.SignatureSpell
            ? SpellSelectionContext.CreateView(option)
            : option.Repertoire;
        instance.Bind(displayRepertoire, option.SlotLevel, option.FormatChoiceTitle(character),
            interactable, subitemSelected);

        var tooltip = GetOrMakeBackgroundTooltip(instance.toggle.transform);
        if (tooltip)
        {
            tooltip.Disabled = false;
            tooltip.TooltipClass = GuiManager.DefaultTooltipClass;
            tooltip.Content = option.FormatDescription(character);
            tooltip.Context = null;
            tooltip.DataProvider = null;
        }

        if (!option.IsFree)
        {
            return;
        }

        for (var index = 0; index < instance.slotStatusTable.childCount; ++index)
        {
            instance.slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }

        layoutState.Apply(instance, GetReactionChoiceWidth(instance));
    }

    internal static bool RestoreReactionChoiceLayout([NotNull] this CharacterReactionSubitem instance)
    {
        if (!instance.TryGetComponent<ReactionChoiceLayoutState>(out var layoutState))
        {
            return false;
        }

        layoutState.Restore();
        Object.DestroyImmediate(layoutState);

        return true;
    }

    internal static void CaptureReactionChoiceContainerLayout([NotNull] this CharacterReactionItem instance)
    {
        instance.RestoreReactionChoiceContainerLayout();

        var layoutState = instance.gameObject.AddComponent<ReactionChoiceContainerLayoutState>();

        if (!layoutState.Capture(instance))
        {
            Object.DestroyImmediate(layoutState);
        }
    }

    internal static void ApplyReactionChoiceContainerLayout(
        [NotNull] this CharacterReactionItem instance,
        bool scrollable = false)
    {
        if (instance.TryGetComponent<ReactionChoiceContainerLayoutState>(out var layoutState))
        {
            layoutState.Apply(ExpandedReactionChoiceWidth, scrollable);
        }
    }

    internal static void EnsureReactionChoiceVisible([NotNull] this CharacterReactionItem instance)
    {
        if (instance.TryGetComponent<ReactionChoiceContainerLayoutState>(out var layoutState))
        {
            layoutState.EnsureSelectedVisible();
        }
    }

    private static float GetReactionChoiceWidth(CharacterReactionSubitem instance)
    {
        var reactionItem = instance.GetComponentInParent<CharacterReactionItem>();

        return reactionItem && reactionItem.TryGetComponent<ReactionChoiceContainerLayoutState>(out _)
            ? ExpandedReactionChoiceWidth
            : DefaultReactionChoiceWidth;
    }

    internal static bool RestoreReactionChoiceContainerLayout([NotNull] this CharacterReactionItem instance)
    {
        if (!instance.TryGetComponent<ReactionChoiceContainerLayoutState>(out var layoutState))
        {
            return false;
        }

        layoutState.Restore();
        Object.DestroyImmediate(layoutState);

        return true;
    }

    internal static void BindWarcaster(
        [NotNull] this CharacterReactionSubitem instance,
        [NotNull] ReactionRequestWarcaster reactionRequest,
        int slotLevel,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        var spellRepertoire = reactionRequest.ReactionParams.SpellRepertoire;
        var label = instance.label;
        var toggle = instance.toggle;
        var tooltip = GetOrMakeBackgroundTooltip(toggle.transform);

        string title;

        if (slotLevel == 0)
        {
            title = "Reaction/&WarcasterAttackTitle";

            if (tooltip)
            {
                tooltip.Disabled = false;
                if (reactionRequest.ReactionParams.attackMode?.sourceObject is RulesetItem weapon)
                {
                    ServiceRepository.GetService<IGuiWrapperService>()
                        .GetGuiItemDefinition(weapon.Name)
                        .SetupTooltip(tooltip, reactionRequest.Character.RulesetActor);
                }
                else
                {
                    tooltip.Content = "Reaction/&WarcasterAttackDescription";
                }
            }
        }
        else
        {
            var spell = spellRepertoire.KnownSpells[slotLevel - 1];

            title = spell.GuiPresentation.Title;

            if (tooltip)
            {
                tooltip.Disabled = false;
                ServiceRepository.GetService<IGuiWrapperService>()
                    .GetGuiSpellDefinition(spell.Name)
                    .SetupTooltip(tooltip, reactionRequest.Character.RulesetActor);
            }
        }

        label.Text = title;
        toggle.interactable = interactable;
        instance.canvasGroup.interactable = interactable;
        instance.SubitemSelected = subitemSelected;

        var rectTransform = toggle.GetComponent<RectTransform>();

        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 250);

        // Hide all slots
        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }
    }

    internal static void BindSmite(
        [NotNull] this CharacterReactionSubitem instance,
        [NotNull] ReactionRequestSelectSmiteSpell reactionRequest,
        int slotLevel,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        var label = instance.label;
        var toggle = instance.toggle;
        var tooltip = GetOrMakeBackgroundTooltip(toggle.transform);

        var smite = reactionRequest.Smites[slotLevel];
        var spell = smite.Spell;

        var title = spell.GuiPresentation.Title;

        if (tooltip)
        {
            tooltip.Disabled = false;
            ServiceRepository.GetService<IGuiWrapperService>()
                .GetGuiSpellDefinition(spell.Name)
                .SetupTooltip(tooltip, reactionRequest.Character.RulesetActor);
        }

        label.Text = title;
        toggle.interactable = interactable;
        instance.canvasGroup.interactable = interactable;
        instance.SubitemSelected = subitemSelected;

        var rectTransform = toggle.GetComponent<RectTransform>();

        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 250);

        // Hide all slots
        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }
    }

    internal static void BindPowerBundle(
        [NotNull] this CharacterReactionSubitem instance,
        [NotNull] ReactionRequestSpendBundlePower reactionRequest,
        int slotLevel,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        var spellRepertoire = reactionRequest.ReactionParams.SpellRepertoire;
        var label = instance.label;
        var toggle = instance.toggle;
        var tooltip = GetOrMakeBackgroundTooltip(toggle.transform);
        var spell = spellRepertoire.KnownSpells[slotLevel];
        var power = PowerBundle.GetPower(spell);

        if (!power)
        {
            return;
        }

        if (tooltip)
        {
            tooltip.Disabled = false;
            ServiceRepository.GetService<IGuiWrapperService>()
                .GetGuiPowerDefinition(power.Name)
                .SetupTooltip(tooltip, reactionRequest.Character.RulesetActor);
        }

        label.Text = power.GuiPresentation.Title;
        toggle.interactable = interactable;
        instance.canvasGroup.interactable = interactable;
        instance.SubitemSelected = subitemSelected;

        var rectTransform = toggle.GetComponent<RectTransform>();

        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 250);

        // Hide all slots
        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }
    }

    internal static void BindSmiteSlot(
        [NotNull] this CharacterReactionSubitem instance,
        RulesetSpellRepertoire spellRepertoire,
        int slotLevel,
        string text,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        if (slotLevel == 0)
        {
            text = "Action/&ActionTypeFreeOnceTitle";
            var toggle = instance.toggle;
            var rectTransform = toggle.GetComponent<RectTransform>();

            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 100);

            var tooltip = GetOrMakeBackgroundTooltip(toggle.transform);
            if (tooltip != null)
            {
                tooltip.Disabled = false;
                tooltip.TooltipClass = GuiManager.DefaultTooltipClass;
                tooltip.Content = "Reaction/&ReactionDivineSmite2024SlotFreeDescription";
                tooltip.Context = null;
                tooltip.DataProvider = null;
            }
        }
        instance.Bind(spellRepertoire, slotLevel, text, interactable, subitemSelected);
    }

    private static GuiTooltip GetOrMakeBackgroundTooltip(Transform root)
    {
        var background = root.FindChildRecursive("Background");

        if (!background)
        {
            return null;
        }

        if (background.TryGetComponent<GuiTooltip>(out var tooltip))
        {
            return tooltip;
        }

        tooltip = background.gameObject.AddComponent<GuiTooltip>();
        tooltip.AnchorMode = TooltipDefinitions.AnchorMode.LEFT_CENTER;

        return tooltip;
    }

    private sealed class ReactionChoiceContainerLayoutState : MonoBehaviour
    {
        private readonly RectTransformState _groupRect = new();
        private readonly LayoutElementState _groupLayout = new();
        private readonly RectTransformState _tableRect = new();
        private readonly LayoutElementState _tableLayout = new();

        private bool _captured;
        private CharacterReactionItem _item;
        private RectTransform _itemRect;
        private RectTransform _resizableGroup;
        private Transform _tableParent;
        private int _tableSibling;
        private GuiTooltip _headerTooltip;
        private string _headerTooltipContent;
        private RectTransform _viewport;
        private ScrollRect _scrollRect;

        internal bool Capture(CharacterReactionItem instance)
        {
            if (_captured)
            {
                return true;
            }

            var group = instance.subItemsGroup;
            var table = instance.subItemsTable;
            var itemRect = instance.GetComponent<RectTransform>();

            if (!group || !table || !itemRect)
            {
                return false;
            }

            _groupRect.Capture(group);
            _groupLayout.Capture(group);
            _tableRect.Capture(table);
            _tableLayout.Capture(table);
            _item = instance;
            _itemRect = itemRect;
            _resizableGroup = group.parent as RectTransform;
            _tableParent = table.parent;
            _tableSibling = table.GetSiblingIndex();
            _headerTooltip = Gui.GetTooltip(instance.subItemsLabel.gameObject);
            _headerTooltipContent = _headerTooltip ? _headerTooltip.Content : null;
            _captured = true;

            return true;
        }

        internal void Apply(float width, bool scrollable)
        {
            if (!_captured)
            {
                return;
            }

            _groupRect.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            _tableRect.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            _groupLayout.ApplyWidthIfPresent(width);
            _tableLayout.ApplyWidthIfPresent(width);

            Rebuild();

            if (scrollable)
            {
                ApplyScroll(width);
                EnsureSelectedVisible();
            }
        }

        private void ApplyScroll(float width)
        {
            var table = _tableRect.RectTransform;
            var canvas = _item.GetComponentInParent<Canvas>();
            var canvasRect = canvas ? canvas.rootCanvas.GetComponent<RectTransform>() : null;
            var canvasHeight = canvasRect ? canvasRect.rect.height : Screen.height;
            var contentHeight = Mathf.Max(LayoutUtility.GetPreferredHeight(table), table.rect.height);
            // Keep the reaction controls outside the scroll area, even at small UI resolutions.
            var viewportHeight = Mathf.Min(contentHeight, Mathf.Max(1f, Mathf.Min(360f, canvasHeight * 0.45f)));
            var viewportObject = new GameObject("ReactionChoicesViewport",
                typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect), typeof(LayoutElement))
            {
                layer = table.gameObject.layer
            };

            _viewport = viewportObject.GetComponent<RectTransform>();
            _viewport.SetParent(_tableParent, false);
            _viewport.SetSiblingIndex(_tableSibling);
            _viewport.sizeDelta = new Vector2(width, viewportHeight);
            var viewportLayout = viewportObject.GetComponent<LayoutElement>();
            viewportLayout.minWidth = viewportLayout.preferredWidth = width;
            viewportLayout.minHeight = viewportLayout.preferredHeight = viewportHeight;
            viewportObject.GetComponent<Image>().color = Color.clear;

            table.SetParent(_viewport, false);
            table.anchorMin = new Vector2(0f, 1f);
            table.anchorMax = Vector2.one;
            table.pivot = new Vector2(0.5f, 1f);
            table.anchoredPosition = Vector2.zero;
            table.sizeDelta = new Vector2(0f, contentHeight);

            _scrollRect = viewportObject.GetComponent<ScrollRect>();
            _scrollRect.content = table;
            _scrollRect.viewport = _viewport;
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;
            _scrollRect.inertia = false;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 36f;

            Rebuild();
        }

        internal void EnsureSelectedVisible()
        {
            if (!_viewport || !_scrollRect || !_item)
            {
                return;
            }

            var table = _tableRect.RectTransform;
            var index = _item.GetSelectedSubItem();

            if (index < 0 || index >= table.childCount ||
                table.GetChild(index) is not RectTransform row || !row.gameObject.activeSelf)
            {
                return;
            }

            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_viewport, row);
            var viewportRect = _viewport.rect;
            var position = table.anchoredPosition;

            if (bounds.max.y > viewportRect.yMax)
            {
                position.y -= bounds.max.y - viewportRect.yMax;
            }
            else if (bounds.min.y < viewportRect.yMin)
            {
                position.y += viewportRect.yMin - bounds.min.y;
            }

            position.y = Mathf.Clamp(position.y, 0f, Mathf.Max(0f, table.rect.height - viewportRect.height));
            _scrollRect.StopMovement();
            table.anchoredPosition = position;
        }

        internal void Restore()
        {
            if (!_captured)
            {
                return;
            }

            if (_viewport)
            {
                _scrollRect.content = null;
                _tableRect.RectTransform.SetParent(_tableParent, false);
                _tableRect.RectTransform.SetSiblingIndex(_tableSibling);
                Object.DestroyImmediate(_viewport.gameObject);
                _viewport = null;
                _scrollRect = null;
            }

            if (_headerTooltip)
            {
                _headerTooltip.Content = _headerTooltipContent;
            }

            _tableLayout.Restore();
            _groupLayout.Restore();
            _tableRect.Restore();
            _groupRect.Restore();

            Rebuild();
        }

        private void Rebuild()
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_itemRect);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_groupRect.RectTransform);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_tableRect.RectTransform);

            if (_resizableGroup)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_resizableGroup);
            }
        }
    }

    private sealed class ReactionChoiceLayoutState : MonoBehaviour
    {
        private readonly RectTransformState _labelRect = new();
        private readonly RectTransformState _labelContainerRect = new();
        private ContentSizeFitter _labelFitter;
        private bool _labelFitterEnabled;
        private readonly RectTransformState _rowRect = new();
        private readonly RectTransformState _toggleRect = new();
        private readonly LayoutElementState _rowLayout = new();
        private readonly TooltipState _tooltip = new();

        private bool _autoSizeTextContainer;
        private bool _captured;
        private bool _enableAutoSizing;
        private bool _enableWordWrapping;
        private float _fontSize;
        private float _fontSizeMax;
        private float _fontSizeMin;
        private int _maxVisibleLines;
        private TextOverflowModes _overflowMode;
        private TMP_Text _text;

        internal void Capture(CharacterReactionSubitem instance)
        {
            if (_captured)
            {
                return;
            }

            _captured = true;

            _rowRect.Capture(instance.GetComponent<RectTransform>());
            _toggleRect.Capture(instance.toggle.GetComponent<RectTransform>());
            _labelContainerRect.Capture(instance.label.RectTransform);
            _labelRect.Capture(instance.label.TMP_Text.rectTransform);
            _labelFitter = instance.label.TMP_Text.GetComponent<ContentSizeFitter>();
            _labelFitterEnabled = _labelFitter && _labelFitter.enabled;
            _rowLayout.Capture(_rowRect.RectTransform);
            _tooltip.Capture(instance.toggle.transform.FindChildRecursive("Background"));

            _text = instance.label.TMP_Text;

            if (!_text)
            {
                return;
            }

            _enableAutoSizing = _text.enableAutoSizing;
            _enableWordWrapping = _text.enableWordWrapping;
            _autoSizeTextContainer = _text.autoSizeTextContainer;
            _fontSize = _text.fontSize;
            _fontSizeMin = _text.fontSizeMin;
            _fontSizeMax = _text.fontSizeMax;
            _maxVisibleLines = _text.maxVisibleLines;
            _overflowMode = _text.overflowMode;
        }

        internal void Apply(CharacterReactionSubitem instance, float width)
        {
            var toggleRect = _toggleRect.RectTransform;
            var labelRect = _labelRect.RectTransform;
            var rowRect = _rowRect.RectTransform;

            if (!toggleRect || !labelRect || !rowRect || !_text)
            {
                return;
            }

            // Use the actual TMP transform and constrain its fitter before measurement.
            // The GuiLabel wrapper is not necessarily the text's RectTransform.
            if (_labelFitter)
            {
                _labelFitter.enabled = false;
            }

            toggleRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            rowRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            _text.enableAutoSizing = false;
            _text.enableWordWrapping = true;
            _text.autoSizeTextContainer = false;
            _text.fontSize = Mathf.Min(_fontSize, 22f);
            _text.maxVisibleLines = int.MaxValue;
            _text.overflowMode = TextOverflowModes.Overflow;

            var container = _labelContainerRect.RectTransform;
            Stretch(container, 8f, 4f);
            if (labelRect != container)
            {
                Stretch(labelRect, 0f, 0f);
            }

            var margins = _text.margin;
            var availableWidth = Mathf.Max(1f, width - 16f - margins.x - margins.z);
            var height = Mathf.Max(30f,
                Mathf.Ceil(_text.GetPreferredValues(_text.text, availableWidth, float.PositiveInfinity).y) + 8f);
            rowRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            toggleRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

            var layout = _rowLayout.GetOrCreate();
            layout.enabled = true;
            layout.ignoreLayout = false;
            layout.minHeight = layout.preferredHeight = height;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);

            if (instance.transform.parent is RectTransform table)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(table);
            }
        }

        private static void Stretch(RectTransform rect, float horizontal, float vertical)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(horizontal, vertical);
            rect.offsetMax = new Vector2(-horizontal, -vertical);
        }

        internal void Restore()
        {
            if (_text)
            {
                _text.enableAutoSizing = _enableAutoSizing;
                _text.enableWordWrapping = _enableWordWrapping;
                _text.autoSizeTextContainer = _autoSizeTextContainer;
                _text.fontSize = _fontSize;
                _text.fontSizeMin = _fontSizeMin;
                _text.fontSizeMax = _fontSizeMax;
                _text.maxVisibleLines = _maxVisibleLines;
                _text.overflowMode = _overflowMode;
                _text.SetLayoutDirty();
                _text.SetVerticesDirty();
            }

            if (_labelFitter)
            {
                _labelFitter.enabled = _labelFitterEnabled;
            }

            _labelRect.Restore();
            _labelContainerRect.Restore();
            _toggleRect.Restore();
            _rowRect.Restore();
            _rowLayout.Restore();
            _tooltip.Restore();

            var rowRect = _rowRect.RectTransform;

            if (rowRect && rowRect.parent is RectTransform table)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(table);
            }
        }
    }

    private sealed class TooltipState
    {
        private Transform _background;
        private GuiTooltip _tooltip;
        private bool _disabled;
        private string _tooltipClass;
        private string _content;
        private object _context;
        private object _dataProvider;

        internal void Capture(Transform background)
        {
            _background = background;

            if (!_background || !_background.TryGetComponent(out _tooltip))
            {
                return;
            }

            _disabled = _tooltip.Disabled;
            _tooltipClass = _tooltip.TooltipClass;
            _content = _tooltip.Content;
            _context = _tooltip.Context;
            _dataProvider = _tooltip.DataProvider;
        }

        internal void Restore()
        {
            if (_tooltip)
            {
                _tooltip.Disabled = _disabled;
                _tooltip.TooltipClass = _tooltipClass;
                _tooltip.Content = _content;
                _tooltip.Context = _context;
                _tooltip.DataProvider = _dataProvider;
            }
            else if (_background && _background.TryGetComponent<GuiTooltip>(out var createdTooltip))
            {
                Object.DestroyImmediate(createdTooltip);
            }
        }
    }

    private sealed class RectTransformState
    {
        private Vector2 _sizeDelta;
        private Vector2 _anchorMin;
        private Vector2 _anchorMax;
        private Vector2 _pivot;
        private Vector2 _anchoredPosition;

        internal float Height { get; private set; }
        internal RectTransform RectTransform { get; private set; }

        internal void Capture(RectTransform rectTransform)
        {
            RectTransform = rectTransform;

            if (!RectTransform)
            {
                return;
            }

            _sizeDelta = RectTransform.sizeDelta;
            _anchorMin = RectTransform.anchorMin;
            _anchorMax = RectTransform.anchorMax;
            _pivot = RectTransform.pivot;
            _anchoredPosition = RectTransform.anchoredPosition;
            Height = RectTransform.rect.height;
        }

        internal void Restore()
        {
            if (RectTransform)
            {
                RectTransform.anchorMin = _anchorMin;
                RectTransform.anchorMax = _anchorMax;
                RectTransform.pivot = _pivot;
                RectTransform.anchoredPosition = _anchoredPosition;
                RectTransform.sizeDelta = _sizeDelta;
            }
        }
    }

    private sealed class LayoutElementState
    {
        private bool _enabled;
        private float _flexibleHeight;
        private float _flexibleWidth;
        private bool _hadLayout;
        private bool _ignoreLayout;
        private int _layoutPriority;
        private LayoutElement _layout;
        private float _minHeight;
        private float _minWidth;
        private float _preferredHeight;
        private float _preferredWidth;
        private RectTransform _rectTransform;

        internal void Capture(RectTransform rectTransform)
        {
            _rectTransform = rectTransform;

            if (!_rectTransform || !_rectTransform.TryGetComponent<LayoutElement>(out _layout))
            {
                return;
            }

            _hadLayout = true;
            _enabled = _layout.enabled;
            _ignoreLayout = _layout.ignoreLayout;
            _minWidth = _layout.minWidth;
            _minHeight = _layout.minHeight;
            _preferredWidth = _layout.preferredWidth;
            _preferredHeight = _layout.preferredHeight;
            _flexibleWidth = _layout.flexibleWidth;
            _flexibleHeight = _layout.flexibleHeight;
            _layoutPriority = _layout.layoutPriority;
        }

        internal LayoutElement GetOrCreate()
        {
            if (!_rectTransform)
            {
                return null;
            }

            return _layout ? _layout : _layout = _rectTransform.gameObject.AddComponent<LayoutElement>();
        }

        internal void ApplyWidthIfPresent(float width)
        {
            if (!_layout)
            {
                return;
            }

            _layout.enabled = true;
            _layout.ignoreLayout = false;
            _layout.minWidth = width;
            _layout.preferredWidth = width;
        }

        internal void Restore()
        {
            if (!_layout)
            {
                return;
            }

            if (!_hadLayout)
            {
                Object.DestroyImmediate(_layout);
                _layout = null;

                return;
            }

            _layout.enabled = _enabled;
            _layout.ignoreLayout = _ignoreLayout;
            _layout.minWidth = _minWidth;
            _layout.minHeight = _minHeight;
            _layout.preferredWidth = _preferredWidth;
            _layout.preferredHeight = _preferredHeight;
            _layout.flexibleWidth = _flexibleWidth;
            _layout.flexibleHeight = _flexibleHeight;
            _layout.layoutPriority = _layoutPriority;
        }
    }
}
