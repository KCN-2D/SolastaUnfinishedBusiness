using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ActiveCharacterPanelPatcher
{
    private const float IdentityGap = 1f;
    private const int IdentityLayoutDelayFrames = 2;
    private const float PositionTolerance = 0.01f;

    private static void OnCharacterPowerActivated(RulesetCharacter character, RulesetUsablePower power, int level)
    {
        character.RefreshAll();
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(ActiveCharacterPanel __instance)
        {
            //prevent null check issues
            return __instance.GuiCharacter?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false };
        }

        [UsedImplicitly]
        public static void Postfix(ActiveCharacterPanel __instance)
        {
            if (__instance.GuiCharacter?.RulesetCharacter is not
                { IsDeadOrDyingOrUnconscious: false } character)
            {
                return;
            }

            //PATCH: support for custom point pools and concentration powers on portrait
            IconsOnPortrait.CharacterPanelRefresh(__instance);

            if (character is RulesetCharacterSimulacrum duplicate)
            {
                if (TryGetSimulacrumClassAndLevel(
                        __instance.GuiCharacter,
                        duplicate,
                        out var classAndLevel))
                {
                    __instance.classAndLevelLabel.Text = classAndLevel;
                }
            }

            FitActiveCharacterIdentity(
                __instance,
                __instance.nameLabel,
                __instance.classAndLevelLabel);

            if (character is RulesetCharacterSimulacrum duplicateForInventory)
            {
                var canAccessInventory =
                    SimulacrumBehavior.CanAccessHumanoidInventory(duplicateForInventory);

                __instance.inspectButton.gameObject.SetActive(canAccessInventory);
                __instance.inspectButton.interactable = canAccessInventory;
            }

            //PATCH: support for button that shows info about non-Hero characters
            if (Main.Settings.ShowButtonWithControlledMonsterInfo &&
                __instance.GuiCharacter.RulesetCharacter is RulesetCharacterMonster)
            {
                CustomCharacterStatsPanel.Instance.Refresh();
            }
        }

        private static void FitActiveCharacterIdentity(
            ActiveCharacterPanel panel,
            GuiLabel nameLabel,
            GuiLabel classAndLevelLabel)
        {
            // Japanese class/subclass combinations can exceed the fixed one-line HUD plate.
            // Width fitting remains conditional. Resolve the separate vertical collision
            // only after TMP and the native layout have completed their deferred updates.
            UiTextHelpers.FitConstrainedSingleLine(classAndLevelLabel, 0.58f, 7f);

            if (!panel ||
                !panel.gameObject.activeInHierarchy)
            {
                return;
            }

            var layout = panel.GetComponent<ActiveCharacterIdentityLayout>() ??
                         panel.gameObject.AddComponent<ActiveCharacterIdentityLayout>();

            layout.Schedule(panel, nameLabel, classAndLevelLabel);
        }

        private static bool TryGetSimulacrumClassAndLevel(
            GuiCharacter guiCharacter,
            RulesetCharacterSimulacrum duplicate,
            out string classAndLevel)
        {
            classAndLevel = null;

            if (!SimulacrumBehavior.TryGetClassLevels(duplicate, out var classes) ||
                classes.Count == 0)
            {
                return false;
            }

            if (classes.Count > 1)
            {
                // Hero multiclass captions already use this compact class/level
                // formatter. Use the same path for the duplicate instead of expanding
                // every class to the much longer single-class localized sentence.
                classAndLevel = MulticlassGameUi.GetAllClassesLabel(guiCharacter, ' ');

                return !string.IsNullOrEmpty(classAndLevel);
            }

            var classLevel = classes[0];
            var level = classLevel.Level.ToString();
            var classTitle = classLevel.ClassDefinition.FormatTitle();

            classAndLevel = SimulacrumBehavior.TryGetPrimarySubclass(
                duplicate,
                classLevel.ClassDefinition,
                out var subclass)
                ? Gui.Format(
                    "Format/&LevelAndClassAndSubclassFormat",
                    level,
                    classTitle,
                    subclass.FormatTitle())
                : Gui.Format(
                    "Format/&LevelAndClassNoSubclassFormat",
                    level,
                    classTitle);

            return true;
        }
    }

    private sealed class ActiveCharacterIdentityLayout : MonoBehaviour
    {
        private Vector2 _appliedClassPosition;
        private Vector2 _appliedNamePosition;
        private Vector2 _baselineClassPosition;
        private Vector2 _baselineNamePosition;
        private GuiLabel _classAndLevelLabel;
        private RectTransform _classRect;
        private Coroutine _correction;
        private bool _hasAppliedPosition;
        private GuiLabel _nameLabel;
        private RectTransform _nameRect;
        private ActiveCharacterPanel _panel;

        internal void Schedule(
            ActiveCharacterPanel panel,
            GuiLabel nameLabel,
            GuiLabel classAndLevelLabel)
        {
            var nameRect = nameLabel?.TMP_Text?.rectTransform;
            var classRect = classAndLevelLabel?.TMP_Text?.rectTransform;

            if (!nameRect || !classRect)
            {
                return;
            }

            if (_nameRect != nameRect ||
                _classRect != classRect)
            {
                RestoreAppliedPositions();
                _nameRect = nameRect;
                _classRect = classRect;
                CaptureBaselines();
            }
            else
            {
                RestoreOrCaptureBaselines();
            }

            _panel = panel;
            _nameLabel = nameLabel;
            _classAndLevelLabel = classAndLevelLabel;

            if (_correction != null)
            {
                StopCoroutine(_correction);
            }

            _correction = StartCoroutine(CorrectAfterLayout());
        }

        private IEnumerator CorrectAfterLayout()
        {
            for (var frame = 0; frame < IdentityLayoutDelayFrames; frame++)
            {
                yield return null;
            }

            yield return new WaitForEndOfFrame();

            CorrectOverlap();
            _correction = null;
        }

        private void CorrectOverlap()
        {
            var nameText = _nameLabel?.TMP_Text;
            var classText = _classAndLevelLabel?.TMP_Text;

            if (!_panel ||
                !nameText ||
                !classText ||
                !nameText.gameObject.activeInHierarchy ||
                !classText.gameObject.activeInHierarchy ||
                !_nameRect ||
                !_classRect ||
                !_nameRect.parent ||
                !_classRect.parent ||
                _panel.transform is not RectTransform panelRect)
            {
                return;
            }

            if (!Approximately(_nameRect.anchoredPosition, _baselineNamePosition))
            {
                _baselineNamePosition = _nameRect.anchoredPosition;
            }

            if (!Approximately(_classRect.anchoredPosition, _baselineClassPosition))
            {
                _baselineClassPosition = _classRect.anchoredPosition;
            }

            var nameBounds = UiTextHelpers.GetWorldTextBounds(nameText);
            var classBounds = UiTextHelpers.GetWorldTextBounds(classText);

            if (!nameBounds.Overlaps(classBounds))
            {
                return;
            }

            var remainingOffset = classBounds.yMax + IdentityGap - nameBounds.yMin;

            if (remainingOffset <= 0f)
            {
                return;
            }

            var panelBounds = UiTextHelpers.GetWorldRect(panelRect);
            var lowerBoundary = panelBounds.yMin + IdentityGap;
            var mask = _classRect.GetComponentInParent<RectMask2D>();

            if (mask &&
                mask.isActiveAndEnabled &&
                mask.transform is RectTransform maskRect)
            {
                lowerBoundary = Mathf.Max(
                    lowerBoundary,
                    UiTextHelpers.GetWorldRect(maskRect).yMin + IdentityGap);
            }

            var classOffset = Mathf.Min(
                remainingOffset,
                Mathf.Max(0f, classBounds.yMin - lowerBoundary));

            if (classOffset > 0f)
            {
                _appliedClassPosition = ApplyWorldOffset(
                    _classRect,
                    _baselineClassPosition,
                    Vector3.down * classOffset);
                remainingOffset -= classOffset;
                _hasAppliedPosition = true;
            }

            if (remainingOffset <= 0f)
            {
                return;
            }

            var upperBoundary = panelBounds.yMax - IdentityGap;
            var inspectRect = _panel.inspectButton?.transform as RectTransform;

            if (inspectRect &&
                inspectRect.gameObject.activeInHierarchy)
            {
                var inspectBounds = UiTextHelpers.GetWorldRect(inspectRect);

                if (HorizontallyOverlaps(nameBounds, inspectBounds))
                {
                    upperBoundary = Mathf.Min(
                        upperBoundary,
                        inspectBounds.yMin - IdentityGap);
                }
            }

            var nameOffset = Mathf.Min(
                remainingOffset,
                Mathf.Max(0f, upperBoundary - nameBounds.yMax));

            if (nameOffset <= 0f)
            {
                return;
            }

            _appliedNamePosition = ApplyWorldOffset(
                _nameRect,
                _baselineNamePosition,
                Vector3.up * nameOffset);
            _hasAppliedPosition = true;
        }

        private void CaptureBaselines()
        {
            _baselineNamePosition = _nameRect.anchoredPosition;
            _baselineClassPosition = _classRect.anchoredPosition;
            _hasAppliedPosition = false;
        }

        private void RestoreOrCaptureBaselines()
        {
            if (!_hasAppliedPosition)
            {
                CaptureBaselines();

                return;
            }

            RestoreOrCaptureBaseline(
                _nameRect,
                ref _baselineNamePosition,
                _appliedNamePosition);
            RestoreOrCaptureBaseline(
                _classRect,
                ref _baselineClassPosition,
                _appliedClassPosition);
            _hasAppliedPosition = false;
        }

        private void RestoreAppliedPositions()
        {
            RestoreAppliedPosition(
                _nameRect,
                _baselineNamePosition,
                _appliedNamePosition);
            RestoreAppliedPosition(
                _classRect,
                _baselineClassPosition,
                _appliedClassPosition);
            _hasAppliedPosition = false;
        }

        private void OnDisable()
        {
            if (_correction != null)
            {
                StopCoroutine(_correction);
                _correction = null;
            }

            RestoreAppliedPositions();
        }

        private static Vector2 ApplyWorldOffset(
            RectTransform rectTransform,
            Vector2 baseline,
            Vector3 worldOffset)
        {
            var localOffset = rectTransform.parent.InverseTransformVector(worldOffset);
            var applied = baseline + new Vector2(localOffset.x, localOffset.y);

            rectTransform.anchoredPosition = applied;

            return applied;
        }

        private static bool HorizontallyOverlaps(Rect left, Rect right)
        {
            return left.xMin < right.xMax &&
                   right.xMin < left.xMax;
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return (left - right).sqrMagnitude <= PositionTolerance * PositionTolerance;
        }

        private static void RestoreAppliedPosition(
            RectTransform rectTransform,
            Vector2 baseline,
            Vector2 applied)
        {
            if (rectTransform &&
                Approximately(rectTransform.anchoredPosition, applied))
            {
                rectTransform.anchoredPosition = baseline;
            }
        }

        private static void RestoreOrCaptureBaseline(
            RectTransform rectTransform,
            ref Vector2 baseline,
            Vector2 applied)
        {
            if (Approximately(rectTransform.anchoredPosition, applied))
            {
                rectTransform.anchoredPosition = baseline;
            }
            else
            {
                baseline = rectTransform.anchoredPosition;
            }
        }
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            ActiveCharacterPanel __instance,
            WieldedConfigurationSelector.OnConfigurationSwitchedHandler __1)
        {
            //PATCH: properly update IconsOnPortrait
            var character = __instance.GuiCharacter?.RulesetCharacter;

            if (character is { IsDeadOrDyingOrUnconscious: false } and not RulesetCharacterEffectProxy)
            {
                character.CharacterRefreshed += __instance.ConcentrationChanged;
                character.PowerActivated += OnCharacterPowerActivated;
            }

            if (character is RulesetCharacterSimulacrum duplicate)
            {
                var canAccessInventory =
                    SimulacrumBehavior.CanAccessHumanoidInventory(duplicate);

                __instance.shortcutsGroup.gameObject.SetActive(canAccessInventory);

                if (canAccessInventory)
                {
                    // Native Bind only exposes inventory shortcuts to heroes. A humanoid
                    // Simulacrum owns a real independent inventory, so bind the same HUD controls.
                    __instance.shortcutsGroup.Bind(__instance.GuiCharacter, true, __1);
                }
            }

            //PATCH: support a better ratio with custom portraits
            if (Main.Settings.EnableCustomPortraits &&
                PortraitsContext.HasCustomPortrait(__instance.GuiCharacter?.RulesetCharacter))
            {
                __instance.characterPortrait.rectTransform.sizeDelta = new Vector2(164, 247);
                __instance.characterPortrait.rectTransform.anchoredPosition = new Vector2(-48, 0);
            }
            else
            {
                __instance.characterPortrait.rectTransform.sizeDelta = new Vector2(212, 247);
                __instance.characterPortrait.rectTransform.anchoredPosition = new Vector2(0, 0);
            }

            //PATCH: support for button that shows info about non-Hero characters
            if (!Main.Settings.ShowButtonWithControlledMonsterInfo
                || __instance.GuiCharacter?.RulesetCharacter is not RulesetCharacterMonster)
            {
                return;
            }

            CustomCharacterStatsPanel.Instance.Bind(__instance.GuiCharacter.RulesetCharacter);
        }
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ActiveCharacterPanel __instance)
        {
            //PATCH: properly update IconsOnPortrait
            var character = __instance.GuiCharacter?.RulesetCharacter;

            // ReSharper disable once InvertIf
            if (character is { IsDeadOrDyingOrUnconscious: false } and not RulesetCharacterEffectProxy)
            {
                character.CharacterRefreshed -= __instance.ConcentrationChanged;
                character.PowerActivated -= OnCharacterPowerActivated;
            }
        }

        [UsedImplicitly]
        public static void Postfix()
        {
            //PATCH: support for button that shows info about non-Hero characters
            if (Main.Settings.ShowButtonWithControlledMonsterInfo)
            {
                CustomCharacterStatsPanel.Instance.Unbind();
            }
        }
    }
}
