using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using I2.Loc;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class FeatureDescriptionItemPatcher
{
    private const string BackgroundFeatDisplayFeaturePrefix = "FeatureBackgroundFeatDisplay_";
    private const float BackgroundFeatItemPadding = 4f;
    private const float BackgroundFeatTextSpacing = 2f;
    private const float SelectionFeatureItemPadding = 4f;
    private const float SelectionFeatureControlSpacing = 4f;

    private static readonly AccessTools.FieldRef<FeatureDescriptionItem, FeatureDefinition> FeatureRef =
        AccessTools.FieldRefAccess<FeatureDescriptionItem, FeatureDefinition>("<Feature>k__BackingField");

    private static readonly AccessTools.FieldRef<FeatureDescriptionItem, List<FeatureDefinition>> AvailableFeaturesRef =
        AccessTools.FieldRefAccess<FeatureDescriptionItem, List<FeatureDefinition>>("availableFeatures");

    private static readonly AccessTools.FieldRef<FeatureDescriptionItem, List<bool>> ContentValiditiesRef =
        AccessTools.FieldRefAccess<FeatureDescriptionItem, List<bool>>("contentValidities");

    private static readonly AccessTools.FieldRef<FeatureDefinitionFeatureSet, int> DefaultSelectionRef =
        AccessTools.FieldRefAccess<FeatureDefinitionFeatureSet, int>("defaultSelection");

    private static RulesetCharacterHero CurrentHero => ServiceRepository
        .GetService<ICharacterBuildingService>()
        ?.CurrentLocalHeroCharacter;

    private static bool TryGetSelectedFeatureChoice(
        FeatureDescriptionItem item,
        out FeatureDefinition choiceFeature)
    {
        choiceFeature = null;

        if (item == null)
        {
            return false;
        }

        var availableFeatures = AvailableFeaturesRef(item);

        if (availableFeatures is not { Count: > 0 })
        {
            return false;
        }

        var index = -1;

        if (item.choiceDropdown && item.choiceDropdown.gameObject.activeSelf)
        {
            index = item.choiceDropdown.value;
        }
        else if (item.choiceSelector && item.choiceSelector.gameObject.activeSelf)
        {
            index = item.choiceSelector.CurrentSelection;
        }

        if (index < 0 || index >= availableFeatures.Count)
        {
            index = availableFeatures.IndexOf(item.SelectedFeature);
        }

        if (index < 0 || index >= availableFeatures.Count)
        {
            return false;
        }

        choiceFeature = availableFeatures[index];

        return true;
    }

    internal static bool TryGetSelectedHumanOriginFeatChoice(
        FeatureDescriptionItem item,
        out FeatureDefinition choiceFeature)
    {
        choiceFeature = null;

        return item != null &&
               Tabletop2024Context.IsHumanOriginFeatSelectionFeature(FeatureRef(item)) &&
               TryGetSelectedFeatureChoice(item, out choiceFeature);
    }

    internal static bool TryPersistCharacterCreationFeatureSetDefaultSelection(FeatureDescriptionItem item)
    {
        if (item == null ||
            FeatureRef(item) is not FeatureDefinitionFeatureSet featureSet ||
            !Tabletop2024Context.IsPersistedCharacterCreationFeatureSetSelection(featureSet) ||
            !TryGetSelectedFeatureChoice(item, out var choiceFeature))
        {
            return false;
        }

        var index = featureSet.FeatureSet.IndexOf(choiceFeature);

        if (index < 0)
        {
            return false;
        }

        DefaultSelectionRef(featureSet) = index;

        return true;
    }

    private static void SaveHumanOriginFeatSelection(FeatureDescriptionItem item, bool clearTraining)
    {
        if (TryGetSelectedHumanOriginFeatChoice(item, out var choiceFeature))
        {
            Tabletop2024Context.TrySaveHumanOriginFeatSelection(CurrentHero, choiceFeature, clearTraining);
        }
    }

    private static void RestoreHumanOriginFeatSelection(FeatureDescriptionItem item)
    {
        if (Tabletop2024Context.IsHumanOriginFeatSelectionFeature(FeatureRef(item)) &&
            Tabletop2024Context.TryGetHumanOriginSelectionFeature(CurrentHero, out var selectionFeature))
        {
            RestoreSelectedFeature(item, selectionFeature);
        }
    }

    private static void RestoreSpeciesSpellcastingSelection(FeatureDescriptionItem item)
    {
        if (SpeciesSpellcastingContext.IsSelectionFeature(FeatureRef(item)) &&
            SpeciesSpellcastingContext.TryGetSelectedAbilityFeature(CurrentHero, out var selectionFeature))
        {
            RestoreSelectedFeature(item, selectionFeature);
        }
    }

    private static void RestoreSelectedFeature(FeatureDescriptionItem item, FeatureDefinition selectionFeature)
    {
        var availableFeatures = AvailableFeaturesRef(item);
        var index = availableFeatures?.IndexOf(selectionFeature) ?? -1;

        if (index < 0)
        {
            return;
        }

        // Restore the current hero's choice without a new change event or a shared definition default.
        if (item.choiceDropdown && item.choiceDropdown.gameObject.activeSelf)
        {
            item.choiceDropdown.SetValueWithoutNotify(index);
            item.choiceDropdown.RefreshShownValue();
            item.choiceDropdown.UpdateTooltip();
        }

        if (item.choiceSelector && item.choiceSelector.gameObject.activeSelf)
        {
            item.choiceSelector.CurrentSelection = index;
            item.choiceSelector.RefreshCurrent();
        }
    }

    private static bool TryGetBackgroundFeatDisplayPanel(
        FeatureDescriptionItem item,
        out CharacterStageBackgroundSelectionPanel backgroundPanel)
    {
        backgroundPanel = item.GetComponentInParent<CharacterStageBackgroundSelectionPanel>();

        return backgroundPanel &&
               FeatureRef(item) is { Name: var featureName } &&
               featureName.StartsWith(BackgroundFeatDisplayFeaturePrefix, System.StringComparison.Ordinal);
    }

    private static void RefreshBackgroundFeatDisplayLayout(
        FeatureDescriptionItem item,
        CharacterStageBackgroundSelectionPanel backgroundPanel)
    {
        var titleLabel = item.baseFeatureLabel;

        if (titleLabel?.TMP_Text == null)
        {
            return;
        }

        var state = item.GetComponent<SelectionFeatureItemLayoutState>() ??
                    item.gameObject.AddComponent<SelectionFeatureItemLayoutState>();

        state.Restore(item);

        var cursor = 0f;

        PlaceBackgroundFeatText(titleLabel, ref cursor);

        var descriptionLabel = item.featureDescription;

        if (descriptionLabel)
        {
            descriptionLabel.gameObject.SetActive(true);
            cursor += BackgroundFeatTextSpacing;
            PlaceBackgroundFeatText(descriptionLabel, ref cursor);
        }

        if (item.levelGroup && item.levelGroup.gameObject.activeSelf)
        {
            cursor += BackgroundFeatTextSpacing;
            var levelPosition = item.levelGroup.anchoredPosition;
            item.levelGroup.anchoredPosition = new Vector2(levelPosition.x, -cursor);
            cursor += item.levelGroup.rect.height;
        }

        var requiredHeight = Mathf.Ceil(Mathf.Max(1f, cursor + BackgroundFeatItemPadding));
        var itemRect = item.GetComponent<RectTransform>();

        if (itemRect)
        {
            itemRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);

            var layout = item.GetComponent<LayoutElement>() ?? item.gameObject.AddComponent<LayoutElement>();

            layout.minHeight = requiredHeight;
            layout.preferredHeight = requiredHeight;

            LayoutRebuilder.ForceRebuildLayoutImmediate(itemRect);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(backgroundPanel.backgroundFeaturesList);
        LayoutRebuilder.ForceRebuildLayoutImmediate(backgroundPanel.backgroundFeaturesGroup);

        if (backgroundPanel.backgroundFeaturesScrollview?.content)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(backgroundPanel.backgroundFeaturesScrollview.content);
        }
    }

    private static void PlaceBackgroundFeatText(GuiLabel label, ref float cursor)
    {
        if (!TryPrepareWrappedFeatureText(label, true, out var rectTransform, out var height))
        {
            return;
        }

        var position = rectTransform.anchoredPosition;

        rectTransform.anchoredPosition = new Vector2(position.x, -cursor);
        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        cursor += height;
    }

    internal static bool RefreshSelectionFeatureDisplayLayout(FeatureDescriptionItem item)
    {
        if (!item)
        {
            return false;
        }

        var itemRect = item.GetComponent<RectTransform>();

        if (!itemRect)
        {
            return false;
        }

        var selectionItem = IsSelectionFeatureItem(item);
        var hasChoice = AvailableFeaturesRef(item) is { Count: > 0 } &&
                        (item.choiceDropdown && item.choiceDropdown.gameObject.activeSelf ||
                         item.choiceSelector && item.choiceSelector.gameObject.activeSelf);
        var state = item.GetComponent<SelectionFeatureItemLayoutState>();

        if (!selectionItem && !hasChoice)
        {
            state?.RecordLayout(item);

            return false;
        }

        state ??= item.gameObject.AddComponent<SelectionFeatureItemLayoutState>();

        if (!item.gameObject.activeInHierarchy)
        {
            // Native Bind can run before its panel is shown. Keep the intended control
            // and native geometry; measure only once the real hierarchy has a width.
            state.RecordLayout(item);

            return false;
        }

        state.Restore(item);

        // All choice rows share the same text/control structure. Reflow the entire row
        // so current description wrapping and control height always agree, in any host.
        var changed = RefreshFeatureChoiceControls(item, itemRect);
        state.RecordLayout(item);

        var cursor = 0f;

        changed |= PlaceSelectionFeatureText(item.baseFeatureLabel, itemRect, ref cursor);
        changed |= PlaceSelectionFeatureText(item.featureDescription, itemRect, ref cursor);
        changed |= PlaceSelectionFeatureControl(item.choiceDropdown ? item.choiceDropdown.transform as RectTransform : null,
            ref cursor);
        changed |= PlaceSelectionFeatureControl(item.choiceSelector ? item.choiceSelector.transform as RectTransform : null,
            ref cursor);
        changed |= PlaceSelectionFeatureControl(item.levelGroup, ref cursor);

        var requiredHeight = Mathf.Ceil(Mathf.Max(1f, cursor + SelectionFeatureItemPadding));

        if (Mathf.Abs(requiredHeight - itemRect.rect.height) > 0.5f)
        {
            itemRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);
            changed = true;
        }

        var layout = item.GetComponent<LayoutElement>() ?? item.gameObject.AddComponent<LayoutElement>();

        if (Mathf.Abs(layout.preferredHeight - requiredHeight) > 0.5f ||
            Mathf.Abs(layout.minHeight - requiredHeight) > 0.5f)
        {
            layout.preferredHeight = requiredHeight;
            layout.minHeight = requiredHeight;
            changed = true;
        }

        if (changed)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(itemRect);
        }

        return changed;
    }

    private static bool RefreshFeatureChoiceControls(FeatureDescriptionItem item, RectTransform itemRect)
    {
        var features = AvailableFeaturesRef(item);

        if (features is not { Count: > 0 })
        {
            return false;
        }

        var titles = new List<string>(features.Count);

        foreach (var feature in features)
        {
            titles.Add(feature.FormatTitle());
        }

        var changed = false;
        var dropdown = item.choiceDropdown;

        if (dropdown && dropdown.gameObject.activeInHierarchy && dropdown.captionText)
        {
            var control = dropdown.transform as RectTransform;
            var caption = dropdown.captionText;
            var template = dropdown.template;

            if (template && template.TryGetComponent<ScrollRect>(out var scroll))
            {
                // ScrollRect owns its viewport geometry. Some native prefabs serialize
                // a zero-sized viewport until the first layout, even though the popup
                // already has a width. Resolve that layout without enabling the template.
                scroll.Rebuild(CanvasUpdate.Prelayout);
                scroll.SetLayoutHorizontal();
                scroll.SetLayoutVertical();
            }

            var optionText = dropdown.itemText;
            var option = GetChoiceOptionRect(optionText);
            var content = option ? option.parent as RectTransform : null;
            var oldWidth = control.rect.width;
            var templateSize = template ? GetChoiceRectSize(template, control, control.rect.size) : Vector2.zero;
            var optionSize = option ? GetChoiceRectSize(option, template, templateSize) : Vector2.zero;
            var textSize = optionText
                ? GetChoiceRectSize(optionText.rectTransform, template, templateSize) : Vector2.zero;
            var contentSize = content ? GetChoiceRectSize(content, template, templateSize) : Vector2.zero;
            var optionFrame = GetChoiceFrame(option);
            var optionFrameSize = optionFrame
                ? GetChoiceRectSize(optionFrame, template, templateSize) : Vector2.zero;

            changed |= ResizeFeatureChoiceControl(control, caption, titles, itemRect);
            dropdown.RefreshShownValue();
            dropdown.UpdateTooltip();

            if (template && option && optionText)
            {
                var width = control.rect.width;
                var optionWidth = Mathf.Max(1f, width + optionSize.x - oldWidth);
                var labelWidth = Mathf.Max(1f, optionWidth + textSize.x - optionSize.x);
                var height = Mathf.Max(optionSize.y,
                    GetChoiceTextHeight(optionText, titles, labelWidth) + optionSize.y - textSize.y);
                var popupSize = new Vector2(Mathf.Max(1f, width + templateSize.x - oldWidth),
                    Mathf.Max(templateSize.y, height + templateSize.y - optionSize.y));

                changed |= ResizeChoicePopupRect(template, popupSize, control, control.rect.size);

                if (content)
                {
                    changed |= ResizeChoicePopupRect(content,
                        new Vector2(Mathf.Max(1f, width + contentSize.x - oldWidth),
                            Mathf.Max(height, contentSize.y + height - optionSize.y)), template, popupSize);
                }

                changed |= ResizeChoicePopupRect(option, new Vector2(optionWidth, height), template, popupSize);

                if (optionFrame && optionFrame != option && optionFrame != optionText.rectTransform)
                {
                    changed |= ResizeChoicePopupRect(optionFrame,
                        new Vector2(Mathf.Max(1f, optionFrameSize.x + optionWidth - optionSize.x),
                            Mathf.Max(1f, optionFrameSize.y + height - optionSize.y)), template, popupSize);
                }

                changed |= ResizeChoicePopupRect(optionText.rectTransform,
                    new Vector2(labelWidth, Mathf.Max(1f, height + textSize.y - optionSize.y)), template, popupSize);
            }
        }

        var selector = item.choiceSelector;

        if (selector && selector.gameObject.activeInHierarchy && selector.currentLabel?.TMP_Text)
        {
            changed |= ResizeFeatureChoiceControl(selector.transform as RectTransform,
                selector.currentLabel.TMP_Text, titles, itemRect);
        }

        return changed;
    }

    private static Vector2 GetChoiceRectSize(RectTransform rect, RectTransform root, Vector2 rootSize)
    {
        if (rect == root)
        {
            return rootSize;
        }

        if (!root || rect.parent is not RectTransform parent || !rect.IsChildOf(root))
        {
            return rect.rect.size;
        }

        // Inactive dropdown templates can report zero-sized descendants before the
        // first canvas layout. Their authored anchors and sizeDelta still define the
        // correct row geometry; transient zero widths must not become wrapping limits.
        return Vector2.Scale(GetChoiceRectSize(parent, root, rootSize), rect.anchorMax - rect.anchorMin) + rect.sizeDelta;
    }

    private static bool ResizeChoicePopupRect(
        RectTransform rect, Vector2 size, RectTransform root, Vector2 rootSize)
    {
        var parent = rect.parent as RectTransform;
        var parentSize = parent ? GetChoiceRectSize(parent, root, rootSize) : Vector2.zero;
        var delta = size - Vector2.Scale(parentSize, rect.anchorMax - rect.anchorMin);

        if ((rect.sizeDelta - delta).sqrMagnitude <= 0.25f)
        {
            return false;
        }

        // Keep the authored anchors, pivot and position. Unlike live control layout,
        // an inactive popup cannot supply reliable world-space insets for resizing.
        rect.sizeDelta = delta;
        return true;
    }

    private static RectTransform GetChoiceOptionRect(TMP_Text text)
    {
        if (!text)
        {
            return null;
        }

        foreach (var toggle in text.GetComponentsInParent<Toggle>(true))
        {
            return toggle.transform as RectTransform;
        }

        return null;
    }

    private static bool ResizeFeatureChoiceControl(
        RectTransform control,
        TMP_Text text,
        List<string> titles,
        RectTransform itemRect)
    {
        if (!control || !text || control.rect.width <= 1f || itemRect.rect.width <= 1f)
        {
            return false;
        }

        var frame = GetChoiceFrame(control);
        var content = GetChoiceContentRect(control, text.rectTransform, frame);
        var label = text.rectTransform;
        var originalControlSize = control.rect.size;
        var originalContentSize = content.rect.size;
        var contentInset = GetChoiceChildInset(content, control);
        var labelInset = GetChoiceChildInset(label, content);
        var frameSize = frame ? frame.rect.size : Vector2.zero;
        var frameInset = frame ? GetChoiceChildInset(frame, content) : Vector2.zero;
        var widthInset = Mathf.Max(0f, content.rect.width - label.rect.width);
        var heightInset = Mathf.Max(0f, content.rect.height - label.rect.height);
        var contentLeft = itemRect.InverseTransformPoint(content.TransformPoint(content.rect.min)).x;
        var oldContentRight = control.InverseTransformPoint(content.TransformPoint(content.rect.max)).x;
        var selector = control.GetComponent<GuiGamepadSelector>();
        var counter = selector ? selector.countLabel?.RectTransform : null;
        var countBounds = counter ? GetChoiceCounterBounds(selector, control, titles.Count) : Vector2.zero;
        var countGap = counter ? Mathf.Max(0f, countBounds.x - oldContentRight) : 0f;
        var countWidth = counter ? countBounds.y - countBounds.x : 0f;
        var group = content.parent ? content.parent.GetComponent<HorizontalLayoutGroup>() : null;
        var managed = group && group.isActiveAndEnabled;
        var managedCounter = managed && counter && counter.parent == content.parent;
        var availableWidth = Mathf.Max(1f,
            itemRect.rect.xMax - contentLeft - SelectionFeatureItemPadding - countGap - countWidth);

        if (managed)
        {
            var otherWidth = (float)group.padding.horizontal;
            var children = 0;

            foreach (Transform childTransform in group.transform)
            {
                if (childTransform is not RectTransform child || !child.gameObject.activeInHierarchy ||
                    child.GetComponent<LayoutElement>() is { ignoreLayout: true })
                {
                    continue;
                }

                children++;

                if (child == content)
                {
                    continue;
                }

                otherWidth += child == counter ? Mathf.Ceil(countWidth) :
                    group.childControlWidth ? LayoutUtility.GetPreferredWidth(child) : child.rect.width;
            }

            var groupLeft = itemRect.InverseTransformPoint(((RectTransform)group.transform).TransformPoint(
                ((RectTransform)group.transform).rect.min)).x;
            availableWidth = Mathf.Max(1f, itemRect.rect.xMax - groupLeft - SelectionFeatureItemPadding -
                otherWidth - group.spacing * Mathf.Max(0, children - 1));
        }

        var naturalWidth = 0f;

        PrepareChoiceText(text);

        foreach (var title in titles)
        {
            naturalWidth = Mathf.Max(naturalWidth,
                text.GetPreferredValues(title, float.PositiveInfinity, 0f).x);
        }

        var width = Mathf.Min(availableWidth,
            Mathf.Max(originalContentSize.x, Mathf.Ceil(naturalWidth + widthInset + 2f)));
        var labelWidth = Mathf.Max(1f, width - widthInset);
        var height = Mathf.Max(originalContentSize.y,
            Mathf.Ceil(GetChoiceTextHeight(text, titles, labelWidth) + heightInset));
        var wrapperLeft = itemRect.InverseTransformPoint(control.TransformPoint(control.rect.min)).x;
        var wrapperWidth = content == control ? width :
            Mathf.Min(originalControlSize.x, Mathf.Max(1f, itemRect.rect.xMax - wrapperLeft - SelectionFeatureItemPadding));
        var wrapperHeight = Mathf.Max(originalControlSize.y, height + originalControlSize.y - originalContentSize.y);
        var layout = content.GetComponent<LayoutElement>() ?? content.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.flexibleWidth = 0f;
        layout.minHeight = height;
        layout.preferredHeight = height;
        layout.flexibleHeight = 0f;
        var changed = ResizeChoiceRect(control, wrapperWidth, wrapperHeight);

        if (content != control)
        {
            changed |= ResizeChoiceChild(content, control, width, height, contentInset);
        }

        if (managed)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(group.transform as RectTransform);
            width = content.rect.width;
            height = content.rect.height;
            labelWidth = Mathf.Max(1f, width - widthInset);
        }

        // Resize ancestors first. A label can stretch through the frame or an intermediate
        // container, so its final size must be assigned only after the complete frame branch.
        if (frame && frame != content && frame != label)
        {
            changed |= ResizeChoiceChild(frame, content,
                Mathf.Max(1f, frameSize.x + width - originalContentSize.x),
                Mathf.Max(1f, frameSize.y + height - originalContentSize.y), frameInset);
        }

        if (label != content)
        {
            changed |= ResizeChoiceChild(label, content, labelWidth,
                Mathf.Max(1f, height - heightInset), labelInset);
        }

        if (counter && !managedCounter)
        {
            // Some native selectors use a fixed sibling for the count. Preserve the actual
            // glyph gap from the content box, whether its anchors already follow that box or not.
            var contentRight = control.InverseTransformPoint(content.TransformPoint(content.rect.max)).x;
            var currentBounds = GetChoiceCounterBounds(selector, control, titles.Count);
            var delta = contentRight + countGap - currentBounds.x;

            if (Mathf.Abs(delta) > 0.5f)
            {
                counter.position += control.TransformVector(new Vector3(delta, 0f, 0f));
                changed = true;
            }
        }

        return changed;
    }

    private static RectTransform GetChoiceFrame(RectTransform control)
    {
        var graphic = control ? control.GetComponent<Selectable>()?.targetGraphic : null;
        var frame = graphic ? graphic.rectTransform : null;

        return frame && (frame == control || frame.IsChildOf(control)) ? frame : null;
    }

    private static RectTransform GetChoiceContentRect(RectTransform control, RectTransform label, RectTransform frame)
    {
        if (!frame)
        {
            return control;
        }

        var common = control;

        for (var ancestor = label; ancestor && ancestor != control; ancestor = ancestor.parent as RectTransform)
        {
            if (frame == ancestor || frame.IsChildOf(ancestor))
            {
                common = ancestor;
                break;
            }
        }

        for (var ancestor = common; ancestor && ancestor != control; ancestor = ancestor.parent as RectTransform)
        {
            // Input controls can be hidden during the first snapshot. Their layout role
            // must resolve to the same managed child before either input scheme is shown.
            if (ancestor.parent && ancestor.parent.GetComponent<HorizontalOrVerticalLayoutGroup>() is { enabled: true })
            {
                return ancestor;
            }
        }

        return common;
    }

    private static Vector2 GetChoiceCounterBounds(GuiGamepadSelector selector, RectTransform control, int count)
    {
        var text = selector.countLabel.TMP_Text;
        var rect = text.rectTransform.rect;
        var alignment = (int)text.alignment & 0xFF;
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;

        for (var index = 1; index <= count; index++)
        {
            var width = text.GetPreferredValues(
                Gui.Format(GuiGamepadSelector.CountFormat, index.ToString(), count.ToString()),
                float.PositiveInfinity, 0f).x;
            var offset = alignment == 2 ? (rect.width - width) * 0.5f :
                alignment == 4 ? rect.width - width : 0f;
            var left = control.InverseTransformPoint(text.rectTransform.TransformPoint(
                new Vector3(rect.xMin + offset, rect.yMin, 0f))).x;
            var right = control.InverseTransformPoint(text.rectTransform.TransformPoint(
                new Vector3(rect.xMin + offset + width, rect.yMin, 0f))).x;
            minimum = Mathf.Min(minimum, left);
            maximum = Mathf.Max(maximum, right);
        }

        return new Vector2(minimum, maximum);
    }

    private static Vector2 GetChoiceChildInset(RectTransform child, RectTransform control)
    {
        var corner = control.InverseTransformPoint(child.TransformPoint(
            new Vector3(child.rect.xMin, child.rect.yMax, 0f)));

        return new Vector2(corner.x - control.rect.xMin, control.rect.yMax - corner.y);
    }

    private static bool ResizeChoiceChild(
        RectTransform child,
        RectTransform control,
        float width,
        float height,
        Vector2 inset)
    {
        var changed = ResizeChoiceRect(child, width, height);
        var actualInset = GetChoiceChildInset(child, control);
        var delta = new Vector3(inset.x - actualInset.x, actualInset.y - inset.y, 0f);

        if (delta.sqrMagnitude > 0.25f)
        {
            child.position += control.TransformVector(delta);
            changed = true;
        }

        return changed;
    }

    private static float GetChoiceTextHeight(TMP_Text text, List<string> titles, float width)
    {
        PrepareChoiceText(text);
        var height = 0f;

        foreach (var title in titles)
        {
            height = Mathf.Max(height, text.GetPreferredValues(title, width, 0f).y);
        }

        return Mathf.Ceil(height + 2f);
    }

    private static void PrepareChoiceText(TMP_Text text)
    {
        // Measure the normal readable size, not the font left by a previous autosize pass.
        if (text.enableAutoSizing)
        {
            text.fontSize = text.fontSizeMax;
            text.enableAutoSizing = false;
        }

        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
        text.maxVisibleLines = 99999;
        text.autoSizeTextContainer = false;
    }

    private static bool ResizeChoiceRect(RectTransform rectTransform, float width, float height)
    {
        var oldSize = rectTransform.rect.size;

        if (Mathf.Abs(oldSize.x - width) <= 0.5f && Mathf.Abs(oldSize.y - height) <= 0.5f)
        {
            return false;
        }

        // Preserve the native left/top insets and leave arrows, counters and highlights anchored.
        var position = rectTransform.anchoredPosition;
        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        rectTransform.anchoredPosition = position + new Vector2(
            (width - oldSize.x) * rectTransform.pivot.x,
            -(height - oldSize.y) * (1f - rectTransform.pivot.y));

        return true;
    }

    internal static bool IsSelectionFeatureItem(FeatureDescriptionItem item)
    {
        return item &&
               (item.GetComponentsInParent<CharacterStageRaceSelectionPanel>(true).Length > 0 ||
                item.GetComponentsInParent<CharacterStageClassSelectionPanel>(true).Length > 0 ||
                item.GetComponentsInParent<CharacterStageSubclassSelectionPanel>(true).Length > 0);
    }

    internal static bool RefreshSpeciesBaseWalkSpeedDescription(FeatureDescriptionItem item)
    {
        var feature = item ? FeatureRef(item) : null;

        if (!item ||
            !item.GetComponentInParent<CharacterStageRaceSelectionPanel>() ||
            !item.featureDescription ||
            !RacesContext.TryFormatSpeciesBaseWalkSpeedDescription(
                feature,
                out var description,
                out _))
        {
            return false;
        }

        var changed = item.featureDescription.Text != description;

        item.featureDescription.Text = description;

        return changed;
    }

    private static bool PlaceSelectionFeatureText(GuiLabel label, RectTransform itemRect, ref float cursor)
    {
        var widthChanged = false;

        if (label && label.gameObject.activeInHierarchy)
        {
            var textRect = label.RectTransform;
            var left = itemRect.InverseTransformPoint(textRect.TransformPoint(textRect.rect.min)).x;
            var availableWidth = Mathf.Max(1f, itemRect.rect.xMax - left - SelectionFeatureItemPadding);

            if (textRect.rect.width > availableWidth)
            {
                widthChanged = ResizeChoiceRect(textRect, availableWidth, textRect.rect.height);
            }
        }

        if (!TryPrepareWrappedFeatureText(label, false, out var rectTransform, out var height))
        {
            return false;
        }

        var changed = widthChanged;
        var position = rectTransform.anchoredPosition;

        if (Mathf.Abs(position.y + cursor) > 0.5f)
        {
            rectTransform.anchoredPosition = new Vector2(position.x, -cursor);
            changed = true;
        }

        if (Mathf.Abs(height - rectTransform.rect.height) > 0.5f)
        {
            rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            changed = true;
        }

        cursor += height;

        return changed;
    }

    private static bool TryPrepareWrappedFeatureText(
        GuiLabel label,
        bool disableAutoSize,
        out RectTransform rectTransform,
        out float height)
    {
        rectTransform = null;
        height = 0f;

        if (!label || !label.gameObject.activeInHierarchy || label.TMP_Text == null)
        {
            return false;
        }

        var text = label.TMP_Text;
        rectTransform = label.RectTransform;

        if (!rectTransform)
        {
            return false;
        }

        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
        text.maxVisibleLines = 99999;

        if (disableAutoSize)
        {
            text.autoSizeTextContainer = false;
        }

        var width = rectTransform.rect.width;
        var preferredHeight = width > 0f
            ? text.GetPreferredValues(text.text, width, 0f).y
            : rectTransform.rect.height;

        height = Mathf.Ceil(Mathf.Max(text.fontSize * 1.45f, preferredHeight));

        return true;
    }

    private static bool PlaceSelectionFeatureControl(RectTransform rectTransform, ref float cursor)
    {
        if (!rectTransform || !rectTransform.gameObject.activeInHierarchy)
        {
            return false;
        }

        cursor += SelectionFeatureControlSpacing;

        var position = rectTransform.anchoredPosition;
        var changed = false;

        if (Mathf.Abs(position.y + cursor) > 0.5f)
        {
            rectTransform.anchoredPosition = new Vector2(position.x, -cursor);
            changed = true;
        }

        cursor += rectTransform.rect.height;

        return changed;
    }

    private sealed class SelectionFeatureItemLayoutState : MonoBehaviour
    {
        private readonly RectTransformState titleState = new();
        private readonly RectTransformState descriptionState = new();
        private readonly RectTransformState dropdownState = new();
        private readonly RectTransformState selectorState = new();
        private readonly RectTransformState levelState = new();
        private readonly Dictionary<RectTransform, RectTransformState> choiceStates = new();

        private FeatureDescriptionItem owner;
        private float lastWidth;
        private string lastLanguage;
        private bool layoutPending;
        private bool saved;
        private Vector2 itemSizeDelta;
        private bool hadLayoutElement;
        private float minHeight;
        private float preferredHeight;

        internal void Restore(FeatureDescriptionItem item)
        {
            Save(item);

            if (item.TryGetComponent<RectTransform>(out var itemRect))
            {
                // This layout owns row height only; the host may have changed its width.
                itemRect.sizeDelta = new Vector2(itemRect.sizeDelta.x, itemSizeDelta.y);
            }

            RestoreLayoutElement(item);
            titleState.Restore(item.baseFeatureLabel ? item.baseFeatureLabel.RectTransform : null);
            descriptionState.Restore(item.featureDescription ? item.featureDescription.RectTransform : null);
            dropdownState.Restore(item.choiceDropdown ? item.choiceDropdown.transform as RectTransform : null);
            selectorState.Restore(item.choiceSelector ? item.choiceSelector.transform as RectTransform : null);
            levelState.Restore(item.levelGroup);

            foreach (var pair in choiceStates)
            {
                pair.Value.Restore(pair.Key);
            }
        }

        internal void RecordLayout(FeatureDescriptionItem item)
        {
            owner = item;
            lastWidth = item.RectTransform.rect.width;
            lastLanguage = LocalizationManager.CurrentLanguageCode;
            layoutPending = !item.gameObject.activeInHierarchy;
        }

        private void OnEnable()
        {
            // Wait for the entire hierarchy to become active before measuring child controls.
            layoutPending = true;
        }

        private void LateUpdate()
        {
            if (!owner || !owner.gameObject.activeInHierarchy || !FeatureRef(owner) ||
                !layoutPending && Mathf.Abs(owner.RectTransform.rect.width - lastWidth) <= 0.5f &&
                lastLanguage == LocalizationManager.CurrentLanguageCode)
            {
                return;
            }

            RefreshSelectionFeatureDisplayLayout(owner);

            if (owner.RectTransform.parent is RectTransform parent)
            {
                LayoutRebuilder.MarkLayoutForRebuild(parent);
            }
        }

        private void SaveChoiceRect(RectTransform rectTransform)
        {
            if (!rectTransform || choiceStates.ContainsKey(rectTransform))
            {
                return;
            }

            var state = new RectTransformState();
            state.Save(rectTransform, true);
            choiceStates.Add(rectTransform, state);
        }

        private void Save(FeatureDescriptionItem item)
        {
            if (saved)
            {
                return;
            }

            saved = true;

            if (item.TryGetComponent<RectTransform>(out var itemRect))
            {
                itemSizeDelta = itemRect.sizeDelta;
            }

            if (item.TryGetComponent<LayoutElement>(out var layoutElement))
            {
                hadLayoutElement = true;
                minHeight = layoutElement.minHeight;
                preferredHeight = layoutElement.preferredHeight;
            }

            titleState.Save(item.baseFeatureLabel ? item.baseFeatureLabel.RectTransform : null);
            descriptionState.Save(item.featureDescription ? item.featureDescription.RectTransform : null);
            dropdownState.Save(item.choiceDropdown ? item.choiceDropdown.transform as RectTransform : null);
            selectorState.Save(item.choiceSelector ? item.choiceSelector.transform as RectTransform : null);
            levelState.Save(item.levelGroup);

            if (item.choiceDropdown)
            {
                SaveChoiceRect(item.choiceDropdown.captionText ? item.choiceDropdown.captionText.rectTransform : null);
                SaveChoiceRect(item.choiceDropdown.template);
                SaveChoiceRect(GetChoiceFrame(item.choiceDropdown.transform as RectTransform));
                SaveChoiceRect(item.choiceDropdown.captionText
                    ? GetChoiceContentRect(item.choiceDropdown.transform as RectTransform,
                        item.choiceDropdown.captionText.rectTransform,
                        GetChoiceFrame(item.choiceDropdown.transform as RectTransform))
                    : null);
                var option = GetChoiceOptionRect(item.choiceDropdown.itemText);
                SaveChoiceRect(option);
                SaveChoiceRect(GetChoiceFrame(option));
                SaveChoiceRect(option ? option.parent as RectTransform : null);
                SaveChoiceRect(item.choiceDropdown.itemText ? item.choiceDropdown.itemText.rectTransform : null);
            }

            if (item.choiceSelector && item.choiceSelector.currentLabel)
            {
                SaveChoiceRect(item.choiceSelector.currentLabel.RectTransform);
                SaveChoiceRect(GetChoiceFrame(item.choiceSelector.transform as RectTransform));
                SaveChoiceRect(GetChoiceContentRect(item.choiceSelector.transform as RectTransform,
                    item.choiceSelector.currentLabel.RectTransform,
                    GetChoiceFrame(item.choiceSelector.transform as RectTransform)));
                SaveChoiceRect(item.choiceSelector.countLabel ? item.choiceSelector.countLabel.RectTransform : null);
            }
        }

        private void RestoreLayoutElement(FeatureDescriptionItem item)
        {
            if (!item.TryGetComponent<LayoutElement>(out var layoutElement))
            {
                return;
            }

            if (!hadLayoutElement)
            {
                layoutElement.minHeight = -1f;
                layoutElement.preferredHeight = -1f;

                return;
            }

            layoutElement.minHeight = minHeight;
            layoutElement.preferredHeight = preferredHeight;
        }
    }

    private sealed class RectTransformState
    {
        private bool saved;
        private Vector2 anchoredPosition;
        private Vector2 sizeDelta;
        private bool hasText;
        private bool wordWrapping;
        private TextOverflowModes overflowMode;
        private int maxVisibleLines;
        private bool autoSizeTextContainer;
        private bool autoSizing;
        private float fontSize;
        private bool ownsLayoutInput;
        private bool hadLayoutElement;
        private Vector2 minimumSize;
        private Vector2 preferredSize;
        private Vector2 flexibleSize;

        internal void Save(RectTransform rectTransform, bool includeLayoutElement = false)
        {
            if (!rectTransform)
            {
                return;
            }

            saved = true;
            anchoredPosition = rectTransform.anchoredPosition;
            sizeDelta = rectTransform.sizeDelta;
            ownsLayoutInput = includeLayoutElement;
            hadLayoutElement = rectTransform.TryGetComponent<LayoutElement>(out var layout);

            if (hadLayoutElement)
            {
                minimumSize = new Vector2(layout.minWidth, layout.minHeight);
                preferredSize = new Vector2(layout.preferredWidth, layout.preferredHeight);
                flexibleSize = new Vector2(layout.flexibleWidth, layout.flexibleHeight);
            }

            if (!rectTransform.TryGetComponent<TMP_Text>(out var text))
            {
                return;
            }

            hasText = true;
            wordWrapping = text.enableWordWrapping;
            overflowMode = text.overflowMode;
            maxVisibleLines = text.maxVisibleLines;
            autoSizeTextContainer = text.autoSizeTextContainer;
            autoSizing = text.enableAutoSizing;
            fontSize = text.enableAutoSizing ? text.fontSizeMax : text.fontSize;
        }

        internal void Restore(RectTransform rectTransform)
        {
            if (!saved || !rectTransform)
            {
                return;
            }

            rectTransform.anchoredPosition = anchoredPosition;
            rectTransform.sizeDelta = sizeDelta;

            if (ownsLayoutInput && rectTransform.TryGetComponent<LayoutElement>(out var layout))
            {
                layout.minWidth = hadLayoutElement ? minimumSize.x : -1f;
                layout.minHeight = hadLayoutElement ? minimumSize.y : -1f;
                layout.preferredWidth = hadLayoutElement ? preferredSize.x : -1f;
                layout.preferredHeight = hadLayoutElement ? preferredSize.y : -1f;
                layout.flexibleWidth = hadLayoutElement ? flexibleSize.x : -1f;
                layout.flexibleHeight = hadLayoutElement ? flexibleSize.y : -1f;
            }

            if (!hasText || !rectTransform.TryGetComponent<TMP_Text>(out var text))
            {
                return;
            }

            text.enableWordWrapping = wordWrapping;
            text.overflowMode = overflowMode;
            text.maxVisibleLines = maxVisibleLines;
            text.autoSizeTextContainer = autoSizeTextContainer;
            text.enableAutoSizing = autoSizing;
            text.fontSize = fontSize;
        }
    }

    //PATCH: Disables choices dropdown for features already taken on previous levels (MULTICLASS)
    [HarmonyPatch(typeof(FeatureDescriptionItem), nameof(FeatureDescriptionItem.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(FeatureDescriptionItem __instance)
        {
            // Restore geometry before native Bind reuses a row for a different feature.
            __instance.GetComponent<SelectionFeatureItemLayoutState>()?.Restore(__instance);

            // Active pooled items can be rebound without Unbind. The native default selection
            // must not call the previous owner's handler before Bind installs the new one.
            __instance.ValueChanged = null;

            // Native Bind replaces the labels and descriptions but appends these flags.
            // A pooled row must not inherit another feature's content-pack availability.
            ContentValiditiesRef(__instance)?.Clear();
        }

        [UsedImplicitly]
        public static void Postfix([NotNull] FeatureDescriptionItem __instance)
        {
            var hero = Global.LevelUpHero;

            if (__instance.choiceDropdown &&
                hero != null &&
                LevelUpHelper.IsClassSelectionStage(hero))
            {
                // Keep the control active so native TryGetCurrentFeature can resolve
                // the selected child feature while this class preview remains read-only.
                __instance.choiceDropdown.interactable = false;
            }

            RestoreHumanOriginFeatSelection(__instance);
            RestoreSpeciesSpellcastingSelection(__instance);
            SaveHumanOriginFeatSelection(__instance, false);
            RefreshSpeciesBaseWalkSpeedDescription(__instance);
            RefreshSelectionFeatureDisplayLayout(__instance);

            // Assigning the same option index does not raise the native value-change event.
            // Refresh the selected description even when this row has not been laid out yet.
            __instance.choiceDropdown?.UpdateTooltip();

            if (TryGetBackgroundFeatDisplayPanel(__instance, out var backgroundPanel))
            {
                RefreshBackgroundFeatDisplayLayout(__instance, backgroundPanel);
            }
        }
    }
}
