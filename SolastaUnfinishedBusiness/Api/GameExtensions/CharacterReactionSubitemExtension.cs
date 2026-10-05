using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
        var layoutState = CaptureReactionChoiceLayout(instance);

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

        var portrait = instance.gameObject.AddComponent<ReactionTargetChoicePortrait>();
        portrait.Bind(instance, target);
        layoutState.Apply(instance, GetReactionChoiceWidth(instance));
        var preview = toggle.gameObject.AddComponent<ReactionTargetChoicePreview>();
        preview.Bind(instance, reactionRequest, option);

        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }
    }

    internal static void BindReactionTargetPreview([NotNull] this CharacterReactionItem instance)
    {
        var preview = instance.gameObject.AddComponent<ReactionTargetPreviewState>();
        preview.Bind(instance);
    }

    internal static void RefreshReactionTargetPreview([NotNull] this CharacterReactionItem instance)
    {
        if (instance.TryGetComponent<ReactionTargetPreviewState>(out var preview))
        {
            preview.RefreshSelection();
        }
    }

    internal static void GamepadSelectReactionTargetPreview(
        [NotNull] this CharacterReactionItem instance, bool selected)
    {
        if (Gui.GamepadActive && instance.TryGetComponent<ReactionTargetPreviewState>(out var preview))
        {
            preview.GamepadSelect(selected);
        }
    }

    internal static void ValidateReactionTargetPreview([NotNull] this CharacterReactionItem instance)
    {
        if (instance.TryGetComponent<ReactionTargetPreviewState>(out var preview))
        {
            preview.Validate();
        }
    }

    internal static void SuspendReactionTargetPreview([NotNull] this CharacterReactionItem instance)
    {
        if (instance.TryGetComponent<ReactionTargetPreviewState>(out var preview))
        {
            preview.Suspend();
        }
    }

    internal static void ClearReactionTargetPreview([NotNull] this CharacterReactionItem instance)
    {
        if (instance.TryGetComponent<ReactionTargetPreviewState>(out var preview))
        {
            preview.Unbind();
            Object.DestroyImmediate(preview);
        }
    }

    internal static void ClearReactionTargetPreview([NotNull] this ReactionModal instance)
    {
        foreach (var item in instance.reactionItems)
        {
            if (item)
            {
                item.SuspendReactionTargetPreview();
            }
        }
    }

    internal static void ClearReactionTargetChoice([NotNull] this CharacterReactionSubitem instance)
    {
        if (instance.TryGetComponent<ReactionTargetChoicePortrait>(out var portrait))
        {
            portrait.Unbind();
            Object.DestroyImmediate(portrait);
        }

        if (instance.toggle && instance.toggle.TryGetComponent<ReactionTargetChoicePreview>(out var preview))
        {
            preview.Unbind();
            Object.DestroyImmediate(preview);
        }
    }

    internal static void BindSpellResource(
        [NotNull] this CharacterReactionSubitem instance,
        [NotNull] SpellCastingResourceContext.ResourceOption option,
        [NotNull] RulesetCharacter character,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        var layoutState = CaptureReactionChoiceLayout(instance);

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
            if (option.CastingRepertoire != option.Repertoire)
            {
                layoutState.Apply(instance, GetReactionChoiceWidth(instance), true);
            }

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
        instance.ClearReactionTargetChoice();

        if (!instance.TryGetComponent<ReactionChoiceLayoutState>(out var layoutState))
        {
            return false;
        }

        layoutState.Restore();
        Object.DestroyImmediate(layoutState);

        return true;
    }

    internal static void RefreshReactionDescription([NotNull] this CharacterReactionItem instance)
    {
        if (instance.TryGetComponent<ReactionTargetPreviewState>(out var preview))
        {
            preview.RefreshDescription();
        }
    }

    private static ReactionChoiceLayoutState CaptureReactionChoiceLayout(CharacterReactionSubitem instance)
    {
        var layoutState = instance.GetComponent<ReactionChoiceLayoutState>() ??
                          instance.gameObject.AddComponent<ReactionChoiceLayoutState>();
        layoutState.Capture(instance);
        return layoutState;
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
        var layoutState = CaptureReactionChoiceLayout(instance);
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

        // Hide all slots
        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }

        layoutState.Apply(instance, GetReactionChoiceWidth(instance));
    }

    internal static void BindSmite(
        [NotNull] this CharacterReactionSubitem instance,
        [NotNull] ReactionRequestSelectSmiteSpell reactionRequest,
        int slotLevel,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        var layoutState = CaptureReactionChoiceLayout(instance);
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

        // Hide all slots
        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }

        layoutState.Apply(instance, GetReactionChoiceWidth(instance));
    }

    internal static void BindPowerBundle(
        [NotNull] this CharacterReactionSubitem instance,
        [NotNull] ReactionRequestSpendBundlePower reactionRequest,
        int slotLevel,
        bool interactable,
        CharacterReactionSubitem.SubitemSelectedHandler subitemSelected)
    {
        var layoutState = CaptureReactionChoiceLayout(instance);
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

        // Hide all slots
        var slotStatusTable = instance.slotStatusTable;

        for (var index = 0; index < slotStatusTable.childCount; ++index)
        {
            slotStatusTable.GetChild(index).gameObject.SetActive(false);
        }

        layoutState.Apply(instance, GetReactionChoiceWidth(instance));
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
            CaptureReactionChoiceLayout(instance);
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

    // The native hover ring is borrowed without changing character or action-target selection.
    // Native hover events update the restore state while a modal temporarily owns its display.
    private sealed class ReactionTargetPreviewState : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private static ReactionTargetPreviewState _active;
        private CharacterReactionItem _item;
        private ReactionRequestSelectTarget _request;
        private ReactionTargetChoicePreview _pointer;
        private ReactionTargetChoicePreview _focused;
        private IGameLocationSelectionService _selectionService;
        private bool _restoreNativeHover;
        private bool _applyingNativeHover;
        private GameLocationCharacter _target;
        private WorldLocationCharacter _world;
        private bool _restoreHover;
        private bool _gamepadSelected;
        private bool _pointerInside;
        private bool _suspended;
        private CharacterPlateGameInitiative _plate;
        private bool _restorePlateHover;
        private ScrollRect _initiativeScroll;
        private Vector2 _restoreScrollPosition;
        private Vector2 _appliedScrollPosition;
        private Vector2 _restoreScrollVelocity;
        private string _nativeReactTooltip;
        private string _appliedReactTooltip;
        private ReactionModalLayoutState _modalLayout;
        private readonly ReactionPortraitAspectState _portraitAspect = new();

        private bool CanPreview => !_suspended && _item && _item.gameObject.activeInHierarchy &&
                                   _item.CurrentState == CharacterReactionItem.ReactionState.Pending &&
                                   (!Gui.GamepadActive || _gamepadSelected);

        internal void Bind(CharacterReactionItem item)
        {
            _item = item;
            _request = item.ReactionRequest as ReactionRequestSelectTarget;
            _gamepadSelected = !Gui.GamepadActive;
            _suspended = false;
            _portraitAspect.Capture(item.portraitImage);
            _nativeReactTooltip = item.reactButtonTooltip ? item.reactButtonTooltip.Content : null;
            var modal = item.GetComponentInParent<ReactionModal>();
            if (modal)
            {
                _modalLayout = modal.GetComponent<ReactionModalLayoutState>() ??
                               modal.gameObject.AddComponent<ReactionModalLayoutState>();
                _modalLayout.Bind(this, modal);
            }

            RefreshDescription();

            if (_request != null && CanPreview)
            {
                Activate();
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _pointerInside = true;
            RefreshDescription();
            Activate();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _pointerInside = false;
        }

        internal void PointerChanged(ReactionTargetChoicePreview choice, bool entered)
        {
            if (entered)
            {
                _pointer = choice;
                Activate();
            }
            else if (_pointer == choice)
            {
                _pointer = null;
                Refresh();
            }
        }

        internal void FocusChanged(ReactionTargetChoicePreview choice, bool focused)
        {
            if (focused)
            {
                _focused = choice;
                Activate();
            }
            else if (_focused == choice)
            {
                _focused = null;
                Refresh();
            }
        }

        internal void RemoveChoice(ReactionTargetChoicePreview choice)
        {
            if (_pointer == choice)
            {
                _pointer = null;
            }

            if (_focused == choice)
            {
                _focused = null;
            }

            if (_target == choice.Target)
            {
                ReleaseHover();
            }
        }

        internal void RefreshSelection()
        {
            // An explicit UI choice also covers gamepad slot changes without moving focus.
            _pointer = null;
            _focused = null;
            RefreshDescription();
            Activate();
        }

        internal void GamepadSelect(bool selected)
        {
            _gamepadSelected = selected;

            if (selected)
            {
                _pointer = null;
                _focused = null;
                _pointerInside = false;
                RefreshDescription();
                Activate();
            }
            else
            {
                Deactivate();
            }
        }

        internal void RefreshDescription()
        {
            var modal = _item ? _item.GetComponentInParent<ReactionModal>() : null;
            if (!modal || _item.ReactionRequest == null)
            {
                return;
            }

            var request = _item.ReactionRequest;
            var selected = _item.GetSelectedSubItem();
            var option = selected >= 0 && selected < request.SubOptionsAvailability.Count
                ? request.SubOptionsAvailability.Keys.ElementAt(selected)
                : -1;
            var description = CustomReactionsContext.FormatReactionDescription(request, option);
            if (modal.description.Text != description || modal.description.TMP_Text.text != description)
            {
                modal.description.Text = description;
            }

            if (_item.reactButtonTooltip)
            {
                _appliedReactTooltip = CustomReactionsContext.FormatReactionTooltipDescription(request, option);
                if (CustomTooltipProvider.IsUnavailableContent(_appliedReactTooltip))
                {
                    _appliedReactTooltip = _nativeReactTooltip;
                }
                if (_item.reactButtonTooltip.Content != _appliedReactTooltip)
                {
                    _item.reactButtonTooltip.Content = _appliedReactTooltip;
                }
            }
        }

        private void Activate()
        {
            if (!CanPreview)
            {
                Deactivate();
                return;
            }

            if (_active && _active != this)
            {
                _active.Deactivate();
            }

            if (_request == null)
            {
                return;
            }

            _active = this;
            Refresh();
        }

        private void Refresh()
        {
            if (_active != this)
            {
                return;
            }

            if (!CanPreview)
            {
                Deactivate();
                return;
            }

            var choice = _pointer ? _pointer : _focused;

            if (!choice && _item.subItemsTable)
            {
                for (var index = 0; index < _item.subItemsTable.childCount; ++index)
                {
                    var row = _item.subItemsTable.GetChild(index).GetComponent<CharacterReactionSubitem>();

                    if (row && row.gameObject.activeSelf && row.Selected && row.toggle)
                    {
                        choice = row.toggle.GetComponent<ReactionTargetChoicePreview>();
                        break;
                    }
                }
            }

            SetTarget(choice && choice.IsValid(_item, _request) ? choice.Target : null);
        }

        private void SetTarget(GameLocationCharacter target)
        {
            if (_target == target && (!_plate || _plate.GuiCharacter?.RulesetCharacter?.Guid == target?.Guid))
            {
                EnsureNativeHover();
                return;
            }

            ReleaseHover();

            if (target == null)
            {
                return;
            }

            _target = target;
            _selectionService = ServiceRepository.GetService<IGameLocationSelectionService>();

            if (_selectionService != null)
            {
                _restoreNativeHover = _selectionService.IsCharacterHovered(target);
                _selectionService.CharacterHoverChange += NativeHoverChanged;
            }

            _plate = Resources.FindObjectsOfTypeAll<CharacterPlateGameInitiative>()
                .FirstOrDefault(plate => plate.gameObject.activeInHierarchy && plate.GuiCharacter?.RulesetCharacter?.Guid == target.Guid);
            if (_plate && _plate.HoverFrame)
            {
                _restorePlateHover = _plate.HoverFrame.gameObject.activeSelf;
                _plate.HoverFrame.gameObject.SetActive(true);
                RevealInitiativePlate();
            }

            if (ServiceRepository.GetService<IWorldLocationEntityFactoryService>() is { } factory &&
                factory.TryFindWorldCharacter(target, out var world) && world && world.hover)
            {
                _world = world;
                _restoreHover = world.hover.gameObject.activeSelf;
                world.Hover(true);
            }

            EnsureNativeHover();
        }

        private void EnsureNativeHover()
        {
            if (_selectionService?.HoveredCharacters == null || _target == null || _selectionService.IsCharacterHovered(_target))
            {
                return;
            }

            // BattleInitiativeTable derives its hover frame from this native hover state every
            // update. Borrow that state instead of briefly enabling a frame that native code hides.
            _applyingNativeHover = true;
            try
            {
                _selectionService.HoverCharacter(_target, null);
            }
            finally
            {
                _applyingNativeHover = false;
            }
        }

        private void RevealInitiativePlate()
        {
            var scroll = _plate.GetComponentInParent<ScrollRect>();
            var viewport = scroll ? scroll.viewport ? scroll.viewport : scroll.GetComponent<RectTransform>() : null;
            if (!scroll || !scroll.content || !viewport)
            {
                return;
            }

            var corners = new Vector3[4];
            _plate.RectTransform.GetWorldCorners(corners);
            var local = corners.Select(viewport.InverseTransformPoint).ToArray();
            var bounds = viewport.rect;
            var left = local.Min(point => point.x);
            var right = local.Max(point => point.x);
            var bottom = local.Min(point => point.y);
            var top = local.Max(point => point.y);
            var delta = new Vector2(
                scroll.horizontal ? left < bounds.xMin ? bounds.xMin - left : right > bounds.xMax ? bounds.xMax - right : 0f : 0f,
                scroll.vertical ? bottom < bounds.yMin ? bounds.yMin - bottom : top > bounds.yMax ? bounds.yMax - top : 0f : 0f);
            if (delta.sqrMagnitude < 1f)
            {
                return;
            }

            _initiativeScroll = scroll;
            _restoreScrollPosition = scroll.content.anchoredPosition;
            _restoreScrollVelocity = scroll.velocity;
            var offset = scroll.content.parent.InverseTransformVector(viewport.TransformVector(delta));
            scroll.content.anchoredPosition += (Vector2)offset;
            scroll.velocity = Vector2.zero;
            _appliedScrollPosition = scroll.content.anchoredPosition;
        }

        private void NativeHoverChanged(GameLocationCharacterSelection.HoverChangeMode mode,
            GameLocationCharacter character)
        {
            if (character != _target || _applyingNativeHover)
            {
                return;
            }

            _restoreHover = mode == GameLocationCharacterSelection.HoverChangeMode.Add;
            _restorePlateHover = _restoreHover;
            _restoreNativeHover = _restoreHover;
            EnsureNativeHover();

            if (_world && _active == this && CanPreview)
            {
                _world.Hover(true);
            }

            if (_plate && _plate.HoverFrame && _active == this && CanPreview)
            {
                _plate.HoverFrame.gameObject.SetActive(true);
            }
        }

        private void ReleaseHover()
        {
            if (_selectionService != null)
            {
                _selectionService.CharacterHoverChange -= NativeHoverChanged;
                if (_selectionService.HoveredCharacters != null && _target != null && !_restoreNativeHover &&
                    _selectionService.IsCharacterHovered(_target))
                {
                    _selectionService.UnhoverCharacter(_target);
                }
                _selectionService = null;
            }

            if (_world)
            {
                _world.Hover(_restoreHover);
            }

            if (_plate && _plate.HoverFrame && _plate.GuiCharacter?.RulesetCharacter?.Guid == _target?.Guid)
            {
                _plate.HoverFrame.gameObject.SetActive(_restorePlateHover);
            }

            if (_initiativeScroll && _initiativeScroll.content &&
                (_initiativeScroll.content.anchoredPosition - _appliedScrollPosition).sqrMagnitude < 1f)
            {
                // Native turn scrolling or a user's scroll takes ownership of a changed position.
                _initiativeScroll.content.anchoredPosition = _restoreScrollPosition;
                _initiativeScroll.velocity = _restoreScrollVelocity;
            }

            _initiativeScroll = null;

            _world = null;
            _plate = null;
            _target = null;
        }

        internal void Validate()
        {
            if (_active == this)
            {
                Refresh();
            }
        }

        internal void Suspend()
        {
            _suspended = true;
            _pointer = null;
            _focused = null;
            Deactivate();
            _modalLayout?.Unbind(this);
            _modalLayout = null;
        }

        private void Deactivate()
        {
            ReleaseHover();

            if (_active == this)
            {
                _active = null;
            }
        }

        private void Update()
        {
            if (_active != this)
            {
                return;
            }

            var selected = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            var selectedItem = selected ? selected.GetComponentInParent<CharacterReactionItem>() : null;

            if (!_pointerInside && !_pointer && selectedItem && selectedItem != _item)
            {
                Deactivate();
                return;
            }

            Validate();
        }

        private void OnEnable()
        {
            if (_item && !_suspended && _request != null)
            {
                Activate();
            }
        }

        private void OnDisable()
        {
            Suspend();
        }

        private void OnDestroy()
        {
            Unbind();
        }

        internal void Unbind()
        {
            Suspend();
            _portraitAspect.Restore();
            if (_item && _item.reactButtonTooltip && _item.reactButtonTooltip.Content == _appliedReactTooltip)
            {
                _item.reactButtonTooltip.Content = _nativeReactTooltip;
            }
        }

        private void LateUpdate()
        {
            _portraitAspect.Apply();
            if (_active == this && CanPreview)
            {
                // Native UnhoverAll notifies observers before clearing its list. Reacquire only
                // after the native update completes, and keep its own initiative frame visible.
                EnsureNativeHover();
                if (_plate && _plate.HoverFrame && _plate.GuiCharacter?.RulesetCharacter?.Guid == _target?.Guid)
                {
                    _plate.HoverFrame.gameObject.SetActive(true);
                }
            }
        }
    }

    private sealed class ReactionTargetChoicePortrait : MonoBehaviour
    {
        private GuiCharacter _character;
        private RawImage _portrait;
        private Image _background;
        private readonly ReactionPortraitAspectState _portraitAspect = new();

        internal void Bind(CharacterReactionSubitem row, GameLocationCharacter target)
        {
            var item = row.GetComponentInParent<CharacterReactionItem>();
            if (!item || !item.portraitImage || target?.RulesetCharacter == null)
            {
                return;
            }

            if (item.portraitBackgroundImage)
            {
                _background = Object.Instantiate(item.portraitBackgroundImage, row.toggle.transform, false);
                Configure(_background.rectTransform);
                _background.raycastTarget = false;
            }

            _portrait = Object.Instantiate(item.portraitImage, row.toggle.transform, false);
            Configure(_portrait.rectTransform);
            _portrait.raycastTarget = false;
            _character = new GuiCharacter(target.RulesetCharacter);
            _character.AssignPortraitImage(_portrait);
            _portraitAspect.Capture(_portrait);
        }

        private void LateUpdate() => _portraitAspect.Apply();

        private static void Configure(RectTransform rectangle)
        {
            rectangle.anchorMin = rectangle.anchorMax = new Vector2(0f, 0.5f);
            rectangle.pivot = new Vector2(0f, 0.5f);
            rectangle.anchoredPosition = new Vector2(8f, 0f);
            rectangle.sizeDelta = new Vector2(48f, 48f);
            rectangle.gameObject.SetActive(true);
        }

        internal void Unbind()
        {
            _portraitAspect.Restore();
            if (_portrait)
            {
                _character?.RemovePortrait(_portrait);
                Object.DestroyImmediate(_portrait.gameObject);
                _portrait = null;
            }

            if (_background)
            {
                Object.DestroyImmediate(_background.gameObject);
                _background = null;
            }

            _character = null;
        }

        private void OnDestroy() => Unbind();
    }

    // Portrait textures arrive asynchronously and native hero/monster portraits have
    // different dimensions. Crop the UVs to fill the existing frame without stretching.
    private sealed class ReactionPortraitAspectState
    {
        private RawImage _image;
        private Rect _originalUv;

        internal void Capture(RawImage image)
        {
            _image = image;
            _originalUv = image ? image.uvRect : default;
            Apply();
        }

        internal void Apply()
        {
            if (!_image || !_image.texture)
            {
                return;
            }

            var texture = _image.texture;
            var surface = _image.rectTransform.rect;
            if (texture.width <= 0 || texture.height <= 0 || surface.width <= 0f || surface.height <= 0f)
            {
                return;
            }

            var textureAspect = (float)texture.width / texture.height;
            var surfaceAspect = surface.width / surface.height;
            var width = Mathf.Min(1f, surfaceAspect / textureAspect);
            var height = Mathf.Min(1f, textureAspect / surfaceAspect);
            var uv = new Rect((1f - width) * 0.5f, (1f - height) * 0.5f, width, height);
            if (_image.uvRect != uv)
            {
                _image.uvRect = uv;
            }
        }

        internal void Restore()
        {
            if (_image)
            {
                _image.uvRect = _originalUv;
            }

            _image = null;
        }
    }

    private sealed class ReactionModalLayoutState : MonoBehaviour
    {
        private readonly HashSet<ReactionTargetPreviewState> _owners = [];
        private readonly RectTransformState _panel = new();
        private readonly LayoutElementState _layout = new();
        private ReactionModal _modal;
        private ContentSizeFitter _fitter;
        private ContentSizeFitter.FitMode _horizontalFit;
        private VerticalLayoutGroup _panelLayoutGroup;
        private TextAnchor _panelChildAlignment;
        private bool _captured;
        private string _reactionPromptText;
        private float _minimumWidth;
        private bool _seeded;
        private readonly RectTransformState _hourglass = new();
        private int _layoutFrames;
        private Vector2 _lastCanvasSize;
        private Vector2 _lastPanelSize;
        private string _lastTitle;
        private string _lastDescription;
        private int _lastActiveItemCount;
        private readonly HashSet<ulong> _casterGuids = [];
        private readonly List<BoundsGeometry> _geometry = [];
        private RectTransform _geometryCanvas;
        private Matrix4x4 _canvasMatrix;
        private Vector2 _visibleOffset;
        private Vector2 _visibleSize;
        private string _lastPrompt;

        internal void Bind(ReactionTargetPreviewState owner, ReactionModal modal)
        {
            _owners.Add(owner);
            _layoutFrames = 3;
            if (_captured)
            {
                return;
            }

            _modal = modal;
            _reactionPromptText = modal.reactionPromptLabel.Text;
            _minimumWidth = 480f;
            _panel.Capture(modal.mainPanel.RectTransform);
            _layout.Capture(_panel.RectTransform);
            _hourglass.Capture(modal.hourglassGroup);
            _panelLayoutGroup = _panel.RectTransform.GetComponent<VerticalLayoutGroup>();
            if (_panelLayoutGroup)
            {
                _panelChildAlignment = _panelLayoutGroup.childAlignment;
                // The native reaction table has a minimum width. A narrower popup must
                // center that wider child, otherwise UpperLeft shifts every actor and
                // portrait right by half the width difference. Preserve vertical alignment.
                _panelLayoutGroup.childAlignment = (TextAnchor)((int)_panelChildAlignment / 3 * 3 + 1);
            }
            _fitter = _panel.RectTransform.GetComponent<ContentSizeFitter>();
            if (_fitter)
            {
                _horizontalFit = _fitter.horizontalFit;
                _fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            }

            _captured = true;
        }

        private void LateUpdate() => Apply();

        private void Apply()
        {
            var panel = _panel.RectTransform;
            if (!_captured || !panel || !panel.gameObject.activeInHierarchy || _owners.Count == 0 ||
                !FloatingPanelBounds.TryGetCanvasLocalBounds(panel, out var bounds, out var canvas))
            {
                return;
            }

            var activeItemCount = 0;
            _casterGuids.Clear();
            foreach (var item in _modal.reactionItems)
            {
                if (!item || !item.gameObject.activeInHierarchy)
                {
                    continue;
                }

                activeItemCount++;
                if (item.CurrentState == CharacterReactionItem.ReactionState.Pending &&
                    item.ReactionRequest?.Character is { } caster)
                {
                    _casterGuids.Add(caster.Guid);
                }
            }

            var prompt = Gui.Localize(_casterGuids.Count > 1
                ? "Reaction/&CustomReactionChooseCasterTitle"
                : "Reaction/&CustomReactionCasterTitle");
            if (_modal.reactionPromptLabel.Text != prompt || _modal.reactionPromptLabel.TMP_Text.text != prompt)
            {
                _modal.reactionPromptLabel.Text = prompt;
            }

            var title = _modal.title.TMP_Text.text;
            var description = _modal.description.TMP_Text.text;
            if ((_lastCanvasSize - canvas.rect.size).sqrMagnitude > 1f ||
                (_lastPanelSize - panel.rect.size).sqrMagnitude > 1f ||
                _lastTitle != title || _lastDescription != description || _lastActiveItemCount != activeItemCount ||
                _lastPrompt != prompt || GeometryChanged(canvas))
            {
                _layoutFrames = 3;
            }

            if (_layoutFrames > 0)
            {
                var maximumWidth = Mathf.Max(1f, canvas.rect.width - 24f);
                if (!_seeded)
                {
                    // Bind can reuse a wide popup. Measure after the current item layout is complete.
                    var seedWidth = Mathf.Min(maximumWidth, _minimumWidth);
                    panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, seedWidth);
                    _layout.ApplyWidthIfPresent(seedWidth);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                    _seeded = true;
                }

                var padding = panel.TryGetComponent<LayoutGroup>(out var group) ? group.padding.horizontal : 80f;
                // Native left anchors shift content when the width changes. Retain the
                // required width while native layout settles, then stop rebuilding it.
                _minimumWidth = Mathf.Max(_minimumWidth, MeasureContentExtent(panel) * 2f + padding);
                var width = Mathf.Min(maximumWidth, _minimumWidth);
                panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                _layout.ApplyWidthIfPresent(width);
                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                bounds = MeasureVisibleBounds(panel);
                var excessHeight = bounds.height - (canvas.rect.height - 24f);
                if (excessHeight > 0.5f)
                {
                    foreach (var item in _modal.reactionItems.Where(item => item && item.gameObject.activeInHierarchy))
                    {
                        item.GetComponent<ReactionChoiceContainerLayoutState>()?.ReduceViewportHeight(excessHeight);
                    }

                    LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                    bounds = MeasureVisibleBounds(panel);
                }

                _layoutFrames--;
                _lastCanvasSize = canvas.rect.size;
                _lastPanelSize = panel.rect.size;
                _lastTitle = title;
                _lastDescription = description;
                _lastActiveItemCount = activeItemCount;
                _lastPrompt = prompt;

                FloatingPanelBounds.TryGetCanvasLocalBounds(panel, out var currentPanel, out _);
                _visibleOffset = bounds.center - currentPanel.center;
                _visibleSize = bounds.size;
                CaptureGeometry(panel, canvas);
            }
            else
            {
                // Stable content keeps its measured extent relative to the panel. Scrolling
                // masked candidates changes their content position, not the visible bounds.
                bounds = new Rect(bounds.center + _visibleOffset - _visibleSize * 0.5f, _visibleSize);
            }

            var externalHourglass = _modal.hourglassGroup && _modal.hourglassGroup.gameObject.activeInHierarchy &&
                                    !_modal.hourglassGroup.IsChildOf(panel);
            // Hover changes only the world/initiative highlight. Moving the modal in
            // response to hover changes the pointer hit target and causes enter/exit loops.
            FloatingPanelBounds.TryGetCanvasLocalBounds(panel, out var panelBounds, out _);
            var delta = canvas.rect.center - panelBounds.center;
            bounds.position += delta;
            delta.x += bounds.width > canvas.rect.width - 24f
                ? canvas.rect.center.x - bounds.center.x
                : bounds.xMin < canvas.rect.xMin + 12f ? canvas.rect.xMin + 12f - bounds.xMin
                : bounds.xMax > canvas.rect.xMax - 12f ? canvas.rect.xMax - 12f - bounds.xMax : 0f;
            delta.y += bounds.height > canvas.rect.height - 24f
                ? canvas.rect.center.y - bounds.center.y
                : bounds.yMin < canvas.rect.yMin + 12f ? canvas.rect.yMin + 12f - bounds.yMin
                : bounds.yMax > canvas.rect.yMax - 12f ? canvas.rect.yMax - 12f - bounds.yMax : 0f;
            FloatingPanelBounds.ApplyCanvasLocalDelta(panel, canvas, delta);
            if (externalHourglass)
            {
                FloatingPanelBounds.ApplyCanvasLocalDelta(_modal.hourglassGroup, canvas, delta);
            }

            // Centering is our own transform change. Snapshot its final geometry so the next
            // idle frame is not mistaken for a native animation or layout invalidation.
            if (_layoutFrames > 0 || delta != Vector2.zero)
            {
                foreach (var geometry in _geometry)
                {
                    geometry.Capture(canvas);
                }
            }
        }

        private bool GeometryChanged(RectTransform canvas)
        {
            if (_geometryCanvas != canvas || _geometry.Count == 0 || _canvasMatrix != canvas.localToWorldMatrix)
            {
                return true;
            }

            foreach (var geometry in _geometry)
            {
                if (geometry.HasChanged(canvas))
                {
                    return true;
                }
            }

            return false;
        }

        private void CaptureGeometry(RectTransform panel, RectTransform canvas)
        {
            _geometryCanvas = canvas;
            _canvasMatrix = canvas.localToWorldMatrix;
            _geometry.Clear();
            foreach (var rectangle in panel.GetComponentsInChildren<RectTransform>(true))
            {
                _geometry.Add(new BoundsGeometry(rectangle, panel, canvas));
            }

            if (_modal.hourglassGroup && !_modal.hourglassGroup.IsChildOf(panel))
            {
                _geometry.Add(new BoundsGeometry(_modal.hourglassGroup, null, canvas));
            }
        }

        private sealed class BoundsGeometry
        {
            private readonly RectTransform _rectangle;
            private readonly RectTransform _panel;
            private readonly RectMask2D[] _masks;
            private bool _active;
            private bool _masked;
            private int _children;
            private Rect _rect;
            private Matrix4x4 _matrix;

            internal BoundsGeometry(RectTransform rectangle, RectTransform panel, RectTransform canvas)
            {
                _rectangle = rectangle;
                _panel = panel;
                _masks = panel ? rectangle.GetComponentsInParent<RectMask2D>(true) : [];
                Capture(canvas);
            }

            private bool IsMasked()
            {
                foreach (var mask in _masks)
                {
                    if (mask && mask.isActiveAndEnabled && mask.transform != _rectangle &&
                        mask.transform.IsChildOf(_panel))
                    {
                        return true;
                    }
                }

                return false;
            }

            internal void Capture(RectTransform canvas)
            {
                if (!_rectangle)
                {
                    return;
                }

                _active = _rectangle.gameObject.activeInHierarchy;
                _children = _rectangle.childCount;
                _masked = IsMasked();
                _rect = _rectangle.rect;
                _matrix = canvas.worldToLocalMatrix * _rectangle.localToWorldMatrix;
            }

            internal bool HasChanged(RectTransform canvas)
            {
                if (!_rectangle || _active != _rectangle.gameObject.activeInHierarchy ||
                    _children != _rectangle.childCount || _masked != IsMasked())
                {
                    return true;
                }

                // Descendants of a candidate viewport may scroll freely. The viewport itself
                // remains measured, so its size, mask and native hierarchy changes still dirty.
                return _active && !_masked && (_rect != _rectangle.rect ||
                    _matrix != canvas.worldToLocalMatrix * _rectangle.localToWorldMatrix);
            }
        }

        private Rect MeasureVisibleBounds(RectTransform panel)
        {
            FloatingPanelBounds.TryGetCanvasLocalBounds(panel, out var bounds, out _);
            foreach (var rectangle in panel.GetComponentsInChildren<RectTransform>())
            {
                // The scroll content can be taller than the popup; only its viewport is visible.
                if (rectangle.GetComponentsInParent<RectMask2D>().Any(mask => mask.isActiveAndEnabled &&
                        mask.transform != rectangle &&
                        mask.transform.IsChildOf(panel)) ||
                    !FloatingPanelBounds.TryGetCanvasLocalBounds(rectangle, out var childBounds, out _))
                {
                    continue;
                }

                bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, childBounds.xMin), Mathf.Min(bounds.yMin, childBounds.yMin),
                    Mathf.Max(bounds.xMax, childBounds.xMax), Mathf.Max(bounds.yMax, childBounds.yMax));
            }
            var externalHourglass = _modal.hourglassGroup && _modal.hourglassGroup.gameObject.activeInHierarchy &&
                                    !_modal.hourglassGroup.IsChildOf(panel);
            if (externalHourglass && FloatingPanelBounds.TryGetCanvasLocalBounds(_modal.hourglassGroup, out var clockBounds, out _))
            {
                bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, clockBounds.xMin), Mathf.Min(bounds.yMin, clockBounds.yMin),
                    Mathf.Max(bounds.xMax, clockBounds.xMax), Mathf.Max(bounds.yMax, clockBounds.yMax));
            }

            return bounds;
        }

        private float MeasureContentExtent(RectTransform panel)
        {
            var center = panel.rect.center.x;
            var extent = 0f;
            var corners = new Vector3[4];
            foreach (var item in _modal.reactionItems.Where(item => item && item.gameObject.activeInHierarchy))
            {
                var itemRect = item.GetComponent<RectTransform>();
                LayoutRebuilder.ForceRebuildLayoutImmediate(itemRect);
                foreach (var rectangle in item.GetComponentsInChildren<RectTransform>())
                {
                    if (rectangle.GetComponentsInParent<RectMask2D>().Any(mask => mask.isActiveAndEnabled &&
                            mask.transform != rectangle &&
                            mask.transform.IsChildOf(itemRect)))
                    {
                        continue;
                    }

                    rectangle.GetWorldCorners(corners);
                    foreach (var corner in corners)
                    {
                        extent = Mathf.Max(extent, Mathf.Abs(panel.InverseTransformPoint(corner).x - center));
                    }
                }
            }

            // Native table anchors can offset the item center from the popup background.
            return extent;
        }

        internal void Unbind(ReactionTargetPreviewState owner)
        {
            _owners.Remove(owner);
            if (_owners.Count != 0 || !_captured)
            {
                return;
            }

            if (_fitter)
            {
                _fitter.horizontalFit = _horizontalFit;
            }

            if (_panelLayoutGroup)
            {
                _panelLayoutGroup.childAlignment = _panelChildAlignment;
            }

            _layout.Restore();
            if (_modal && _modal.reactionPromptLabel)
            {
                _modal.reactionPromptLabel.Text = _reactionPromptText;
            }

            _hourglass.Restore();
            _panel.Restore();
            _captured = false;
            _minimumWidth = 0f;
            _seeded = false;
            _layoutFrames = 0;
            _lastCanvasSize = _lastPanelSize = Vector2.zero;
            _lastTitle = _lastDescription = null;
            _lastActiveItemCount = 0;
            _lastPrompt = null;
            _geometry.Clear();
            _geometryCanvas = null;
            _casterGuids.Clear();
        }

        private void OnDisable()
        {
            foreach (var owner in _owners.ToArray())
            {
                Unbind(owner);
            }
        }
    }

    private sealed class ReactionTargetChoicePreview : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
    {
        private CharacterReactionSubitem _row;
        private ReactionRequestSelectTarget _request;
        private int _option;
        private GameLocationCharacter _target;

        internal GameLocationCharacter Target => _target;
        private CharacterReactionItem Item
        {
            get
            {
                // Native rows bind before the modal is shown, and pooled rows can move.
                // Walking the current parents also includes inactive items on this Unity version.
                for (var parent = _row ? _row.transform : null; parent; parent = parent.parent)
                {
                    var item = parent.GetComponent<CharacterReactionItem>();

                    if (item)
                    {
                        return item;
                    }
                }

                return null;
            }
        }

        internal void Bind(CharacterReactionSubitem row, ReactionRequestSelectTarget request, int option)
        {
            _row = row;
            _request = request;
            _option = option;
            _target = request.Candidates[option];
        }

        internal bool IsValid(CharacterReactionItem item, ReactionRequestSelectTarget request)
        {
            return _row && _row.gameObject.activeInHierarchy && _row.toggle.interactable &&
                   _row.canvasGroup.interactable && Item == item && _request == request &&
                   request.SubOptionsAvailability.TryGetValue(_option, out var available) && available &&
                   _option >= 0 && _option < request.Candidates.Count &&
                   request.Candidates[_option] == _target && ReactionRequestSelectTarget.IsCandidateValid(_target);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            Preview()?.PointerChanged(this, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Preview()?.PointerChanged(this, false);
        }

        public void OnSelect(BaseEventData eventData)
        {
            Preview()?.FocusChanged(this, true);
        }

        public void OnDeselect(BaseEventData eventData)
        {
            Preview()?.FocusChanged(this, false);
        }

        private ReactionTargetPreviewState Preview()
        {
            var item = Item;

            return item ? item.GetComponent<ReactionTargetPreviewState>() : null;
        }

        internal void Unbind()
        {
            Preview()?.RemoveChoice(this);
            _row = null;
            _request = null;
            _target = null;
        }

        private void OnDisable()
        {
            Preview()?.RemoveChoice(this);
        }

        private void OnDestroy()
        {
            Unbind();
        }
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

        internal void ReduceViewportHeight(float reductionInCanvas)
        {
            if (!_viewport || !FloatingPanelBounds.TryGetCanvasLocalBounds(_viewport, out var bounds, out _) ||
                bounds.height <= 0f)
            {
                return;
            }

            var minimumHeight = _tableRect.RectTransform.Cast<Transform>().OfType<RectTransform>()
                .Where(row => row.gameObject.activeSelf).Select(row => row.rect.height).DefaultIfEmpty(1f).Max();
            var height = Mathf.Max(minimumHeight,
                _viewport.rect.height - reductionInCanvas * _viewport.rect.height / bounds.height);
            if (height >= _viewport.rect.height - 0.5f)
            {
                return;
            }

            _viewport.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            var layout = _viewport.GetComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = height;
            Rebuild();
            EnsureSelectedVisible();
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
        private readonly RectTransformState _slotsRect = new();
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
            _slotsRect.Capture(instance.slotStatusTable);
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

        internal void Apply(CharacterReactionSubitem instance, float width, bool showSlots = false)
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
            var hasPortrait = instance.TryGetComponent<ReactionTargetChoicePortrait>(out _);
            if (hasPortrait)
            {
                container.offsetMin = new Vector2(64f, 4f);
            }
            if (labelRect != container)
            {
                Stretch(labelRect, 0f, 0f);
            }

            var slotsWidth = 0f;
            if (showSlots && _slotsRect.RectTransform)
            {
                var slots = _slotsRect.RectTransform;
                // Native Bind activates pooled pips before their layout has settled.
                // Measure the actual visible pips after rebuilding their table rather
                // than reserving its previous (often one-slot) width.
                LayoutRebuilder.ForceRebuildLayoutImmediate(slots);
                var corners = new Vector3[4];
                var minimum = float.PositiveInfinity;
                var maximum = float.NegativeInfinity;
                foreach (var pip in slots.Cast<Transform>().OfType<RectTransform>()
                             .Where(pip => pip.gameObject.activeSelf))
                {
                    pip.GetWorldCorners(corners);
                    foreach (var corner in corners)
                    {
                        var x = slots.InverseTransformPoint(corner).x;
                        minimum = Mathf.Min(minimum, x);
                        maximum = Mathf.Max(maximum, x);
                    }
                }

                slotsWidth = Mathf.Max(LayoutUtility.GetPreferredWidth(slots),
                    minimum <= maximum ? maximum - minimum : 0f);
                slots.anchorMin = slots.anchorMax = new Vector2(1f, 0.5f);
                slots.pivot = new Vector2(1f, 0.5f);
                slots.anchoredPosition = new Vector2(-8f, 0f);
                slots.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, slotsWidth);
                container.offsetMax = new Vector2(-slotsWidth - 20f, -4f);
            }

            var margins = _text.margin;
            var availableWidth = Mathf.Max(1f,
                width - (hasPortrait ? 72f : 16f) - slotsWidth - (showSlots ? 12f : 0f) - margins.x - margins.z);
            var height = Mathf.Max(hasPortrait ? 60f : 30f,
                Mathf.Ceil(_text.GetPreferredValues(_text.text, availableWidth, float.PositiveInfinity).y) + 8f);
            rowRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            toggleRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

            var layout = _rowLayout.GetOrCreate();
            layout.enabled = true;
            layout.ignoreLayout = false;
            layout.minWidth = layout.preferredWidth = width;
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
            _slotsRect.Restore();
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
