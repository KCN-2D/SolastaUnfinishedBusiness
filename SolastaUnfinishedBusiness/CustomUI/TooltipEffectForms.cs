using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class TooltipEffectForms
{
    private static readonly Regex PresentationTags = new(
        @"</?(?:b|i|u|s|color|size|font|mark)(?:=[^>]*)?>|<#[0-9a-f]{6}(?:[0-9a-f]{2})?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+");

    internal static void RemoveRepeatedDescriptions(FeatureElementEffectLine line, EffectForm form = null)
    {
        var enumerator = line.GetComponentInParent<TooltipFeatureEffectsEnumerator>();
        var panel = enumerator?.GetComponentInParent<TooltipPanel>();
        var description = panel?.activeFeatures.OfType<TooltipFeatureDescription>().FirstOrDefault(feature =>
            feature.gameObject.activeSelf &&
            feature.transform.GetSiblingIndex() < enumerator.transform.GetSiblingIndex());

        // Only compare text already bound in this panel's current pass. A standalone condition or
        // item tooltip still needs its complete description, including any context-dependent additions.
        var mainText = description?.DescriptionLabel.TMP_Text.text;

        if (string.IsNullOrWhiteSpace(mainText) || mainText.Contains(Gui.LocalizationSymbolString))
        {
            return;
        }

        if (line.effectDescription && line.effectDescription.gameObject.activeSelf)
        {
            var text = line.effectDescription.TMP_Text.text;
            var remaining = RemoveRepeatedParagraphs(text, mainText);

            if (remaining != text)
            {
                line.effectDescription.Text = remaining;
                line.effectDescription.gameObject.SetActive(!string.IsNullOrEmpty(remaining));
            }
        }

        var label = line.effectLabel;
        var primaryText = label.TMP_Text.text;
        var remainingPrimary = primaryText;

        if (form == null)
        {
            // SpecialFormsDescription is prose, unlike structured damage/healing/condition rows.
            remainingPrimary = RemoveRepeatedParagraphs(primaryText, mainText);
        }
        else if (form.FormType == EffectForm.EffectFormType.ItemProperty &&
                 form.ItemPropertyForm?.FeatureBySlotLevel is { Count: > 0 } features &&
                 features[0].FeatureDefinition is { } feature)
        {
            // Native item-property formatting embeds the body in its primary label. Replace only
            // that body and its wrapper, retaining native usage counts and saving-throw suffixes.
            var body = feature.FormatDescription();
            var remainingBody = RemoveRepeatedParagraphs(body, mainText);

            if (body != remainingBody && !string.IsNullOrEmpty(body))
            {
                var wrapped = Gui.Format("Rules/&ItemPropertyFormFormat", body);
                var replacement = string.IsNullOrEmpty(remainingBody)
                    ? string.Empty
                    : Gui.Format("Rules/&ItemPropertyFormFormat", remainingBody);

                remainingPrimary = primaryText.Contains(wrapped)
                    ? primaryText.Replace(wrapped, replacement).Trim()
                    : primaryText.Replace(body, remainingBody).Trim();
            }
        }

        if (remainingPrimary != primaryText)
        {
            label.Text = remainingPrimary;
        }

        // The formatter reactivates pooled rows before each bind. Do not hide the primary label,
        // whose visibility is not reset by the native binder.
        if (string.IsNullOrWhiteSpace(remainingPrimary) &&
            (!line.effectDescription || !line.effectDescription.gameObject.activeSelf))
        {
            line.gameObject.SetActive(false);
        }
    }

    internal static string RemoveRepeatedParagraphs(string text, string mainText)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(mainText) ||
            text.Contains(Gui.LocalizationSymbolString) || mainText.Contains(Gui.LocalizationSymbolString))
        {
            return text;
        }

        if (Normalize(text) == Normalize(mainText))
        {
            return string.Empty;
        }

        var mainParagraphs = new HashSet<string>(SplitParagraphs(mainText).Select(Normalize), StringComparer.Ordinal);
        var remaining = new StringBuilder();
        var hasParagraph = false;
        var removed = false;

        foreach (var paragraph in SplitParagraphs(text))
        {
            if (!string.IsNullOrWhiteSpace(paragraph) && mainParagraphs.Contains(Normalize(paragraph)))
            {
                // A style can span several paragraphs. Keep its transitions so removing prose
                // does not leave the surviving text with an unmatched tag or the wrong color.
                foreach (Match tag in PresentationTags.Matches(paragraph))
                {
                    remaining.Append(tag.Value);
                }

                removed = true;
                continue;
            }

            if (hasParagraph)
            {
                remaining.Append('\n');
            }

            remaining.Append(paragraph);
            hasParagraph = true;
        }

        // Whole paragraphs only: an extra condition, negation, duration or recurrent effect must
        // never disappear merely because it shares part of a sentence with the spell description.
        if (!removed)
        {
            return text;
        }

        var result = remaining.ToString().Trim();

        return string.IsNullOrEmpty(Normalize(result)) ? string.Empty : result;
    }

    private static string[] SplitParagraphs(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static string Normalize(string text)
    {
        // Keep content-bearing tags (notably sprites) and punctuation in the comparison.
        return Whitespace.Replace(PresentationTags.Replace(text, string.Empty), " ").Trim();
    }

    internal static void RefreshLayout(TooltipFeatureEffectsEnumerator enumerator)
    {
        if (!enumerator.gameObject.activeSelf)
        {
            return;
        }

        var table = enumerator.effectFormater.table;
        var hasRows = false;

        for (var i = 0; i < table.childCount; i++)
        {
            var line = table.GetChild(i).GetComponent<FeatureElementEffectLine>();

            if (!line || !line.gameObject.activeSelf)
            {
                continue;
            }

            hasRows = true;
            var height = ResizeLabel(line.effectLabel);
            var secondary = line.effectDescription;

            if (secondary && secondary.gameObject.activeSelf)
            {
                secondary.RectTransform.anchoredPosition = new Vector2(
                    secondary.RectTransform.anchoredPosition.x, -height);
                height += ResizeLabel(secondary);
            }

            line.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        enumerator.gameObject.SetActive(hasRows);
    }

    private static float ResizeLabel(GuiLabel label)
    {
        var height = string.IsNullOrWhiteSpace(label.TMP_Text.text)
            ? 0
            : label.TMP_Text.GetPreferredValues().y;
        label.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

        return height;
    }
}
