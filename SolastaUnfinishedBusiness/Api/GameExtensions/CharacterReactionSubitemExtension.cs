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
    private const float DefaultTargetChoiceWidth = 250f;
    private const float ExpandedTargetChoiceWidth = 300f;

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
        var tooltip = GetOrMakeBackgroundTooltip(toggle.transform);
        var target = reactionRequest.Candidates[option];
        var layoutState = instance.GetComponent<TargetChoiceLayoutState>() ??
                          instance.gameObject.AddComponent<TargetChoiceLayoutState>();

        layoutState.Capture(instance);

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

        layoutState.Apply(instance, GetTargetChoiceWidth(instance));

        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }
    }

    internal static bool RestoreTargetChoiceLayout([NotNull] this CharacterReactionSubitem instance)
    {
        if (!instance.TryGetComponent<TargetChoiceLayoutState>(out var layoutState))
        {
            return false;
        }

        layoutState.Restore();
        Object.DestroyImmediate(layoutState);

        return true;
    }

    internal static void CaptureTargetChoiceContainerLayout([NotNull] this CharacterReactionItem instance)
    {
        instance.RestoreTargetChoiceContainerLayout();

        var layoutState = instance.gameObject.AddComponent<TargetChoiceContainerLayoutState>();

        if (!layoutState.Capture(instance))
        {
            Object.DestroyImmediate(layoutState);
        }
    }

    internal static void ApplyTargetChoiceContainerLayout([NotNull] this CharacterReactionItem instance)
    {
        if (instance.TryGetComponent<TargetChoiceContainerLayoutState>(out var layoutState))
        {
            layoutState.Apply(ExpandedTargetChoiceWidth);
        }
    }

    private static float GetTargetChoiceWidth(CharacterReactionSubitem instance)
    {
        var reactionItem = instance.GetComponentInParent<CharacterReactionItem>();

        return reactionItem && reactionItem.TryGetComponent<TargetChoiceContainerLayoutState>(out _)
            ? ExpandedTargetChoiceWidth
            : DefaultTargetChoiceWidth;
    }

    internal static bool RestoreTargetChoiceContainerLayout([NotNull] this CharacterReactionItem instance)
    {
        if (!instance.TryGetComponent<TargetChoiceContainerLayoutState>(out var layoutState))
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

    private sealed class TargetChoiceContainerLayoutState : MonoBehaviour
    {
        private readonly RectTransformState _groupRect = new();
        private readonly LayoutElementState _groupLayout = new();
        private readonly RectTransformState _tableRect = new();
        private readonly LayoutElementState _tableLayout = new();

        private bool _captured;
        private RectTransform _itemRect;
        private RectTransform _resizableGroup;

        internal bool Capture(CharacterReactionItem instance)
        {
            if (_captured)
            {
                return true;
            }

            var group = instance.subItemsGroup;
            var table = instance.subItemsTable;
            var itemRect = instance.GetComponent<RectTransform>();

            if (!group || !table || !itemRect || table.parent != group ||
                group.parent is not RectTransform resizableGroup ||
                !resizableGroup.TryGetComponent<HorizontalLayoutGroup>(out _))
            {
                return false;
            }

            _groupRect.Capture(group);
            _groupLayout.Capture(group);
            _tableRect.Capture(table);
            _tableLayout.Capture(table);
            _itemRect = itemRect;
            _resizableGroup = resizableGroup;
            _captured = true;

            return true;
        }

        internal void Apply(float width)
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
        }

        internal void Restore()
        {
            if (!_captured)
            {
                return;
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
            LayoutRebuilder.ForceRebuildLayoutImmediate(_resizableGroup);
        }
    }

    private sealed class TargetChoiceLayoutState : MonoBehaviour
    {
        private readonly RectTransformState _labelRect = new();
        private readonly RectTransformState _rowRect = new();
        private readonly RectTransformState _toggleRect = new();
        private readonly LayoutElementState _rowLayout = new();

        private bool _autoSizeTextContainer;
        private bool _captured;
        private bool _enableAutoSizing;
        private bool _enableWordWrapping;
        private float _fontSize;
        private float _fontSizeMax;
        private float _fontSizeMin;
        private int _maxVisibleLines;
        private float _minimumRowHeight;
        private TextOverflowModes _overflowMode;
        private TMP_Text _text;
        private float _verticalPadding;

        internal void Capture(CharacterReactionSubitem instance)
        {
            if (_captured)
            {
                return;
            }

            _captured = true;

            _rowRect.Capture(instance.GetComponent<RectTransform>());
            _toggleRect.Capture(instance.toggle.GetComponent<RectTransform>());
            _labelRect.Capture(instance.label.RectTransform);
            _rowLayout.Capture(_rowRect.RectTransform);

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

            _minimumRowHeight = Mathf.Max(
                _rowRect.Height,
                _toggleRect.Height,
                _labelRect.Height);

            var singleLineHeight = _text.GetPreferredValues(
                "Ag",
                float.PositiveInfinity,
                float.PositiveInfinity).y;

            _verticalPadding = Mathf.Max(0f, _minimumRowHeight - singleLineHeight);
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

            toggleRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            LayoutRebuilder.ForceRebuildLayoutImmediate(toggleRect);

            _text.enableAutoSizing = false;
            _text.enableWordWrapping = true;
            _text.autoSizeTextContainer = false;
            _text.fontSize = _fontSize;
            _text.maxVisibleLines = int.MaxValue;
            _text.overflowMode = TextOverflowModes.Overflow;

            var margins = _text.margin;
            var availableWidth = Mathf.Min(width, labelRect.rect.width) - margins.x - margins.z;

            if (availableWidth <= 0f)
            {
                availableWidth = width - margins.x - margins.z;
            }

            var preferredHeight = availableWidth > 0f
                ? _text.GetPreferredValues(_text.text, availableWidth, float.PositiveInfinity).y
                : _minimumRowHeight;
            var labelHeight = Mathf.Max(_labelRect.Height, Mathf.Ceil(preferredHeight));
            var rowHeight = Mathf.Max(_minimumRowHeight, Mathf.Ceil(preferredHeight + _verticalPadding));

            rowRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, rowHeight);

            var layout = _rowLayout.GetOrCreate();

            if (layout)
            {
                layout.enabled = true;
                layout.ignoreLayout = false;
                layout.minHeight = rowHeight;
                layout.preferredHeight = rowHeight;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);

            if (toggleRect != rowRect && !IsVerticallyStretched(toggleRect))
            {
                toggleRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, rowHeight);
            }

            if (labelRect != rowRect && labelRect != toggleRect && !IsVerticallyStretched(labelRect))
            {
                labelRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, labelHeight);
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(toggleRect);

            if (instance.transform.parent is RectTransform table)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(table);
            }
        }

        private static bool IsVerticallyStretched(RectTransform rectTransform)
        {
            return !Mathf.Approximately(rectTransform.anchorMin.y, rectTransform.anchorMax.y);
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

            _labelRect.Restore();
            _toggleRect.Restore();
            _rowRect.Restore();
            _rowLayout.Restore();

            var rowRect = _rowRect.RectTransform;

            if (rowRect && rowRect.parent is RectTransform table)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(table);
            }
        }
    }

    private sealed class RectTransformState
    {
        private Vector2 _sizeDelta;

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
            Height = RectTransform.rect.height;
        }

        internal void Restore()
        {
            if (RectTransform)
            {
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
