using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Patches;
using TA;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Playables;
using UnityEngine.UI;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.GadgetBlueprints;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ItemDefinitions;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

internal static class CampaignsContext
{
    internal static bool IsVttCameraEnabled;

    internal static readonly string[] HighContrastColorStrings =
    [
        "#FFFFFF", // use white to represent default color in mod UI
        "#FF4040", "#40C040", "#8080FF",
        "#00FFFF", "#FF40FF", "#FFFF00"
    ];

    internal static readonly Color[] HighContrastColors =
    [
        new(0.110f, 0.311f, 0.287f, 1.000f),
        Color.red, Color.green, Color.blue,
        Color.cyan, Color.magenta, Color.yellow
    ];

    internal static readonly string[] GridColorStrings =
    [
        "#000000", "#FFFFFF",
        "#FF4040", "#40C040", "#8080FF",
        "#00FFFF", "#FF40FF", "#FFFF00"
    ];

    internal static readonly Color[] GridColors =
    [
        Color.black, Color.white,
        Color.red, Color.green, Color.blue,
        Color.cyan, Color.magenta, Color.yellow
    ];

    internal static Color GetGridColor(bool isHighlighted)
    {
        var selectedColor = GridColors[Main.Settings.GridSelectedColor];

        return new Color(
            selectedColor.r,
            selectedColor.g,
            selectedColor.b,
            isHighlighted ? 1f : 0.2f
        );
    }

    private static readonly int[][][] FormationGridSetTemplates =
    [
        [
            [0, 0, 1, 1, 0], //
            [0, 0, 1, 1, 0], //
            [0, 0, 1, 1, 0], //
            [0, 0, 1, 1, 0], //
            [0, 0, 0, 0, 0] //
        ],
        [
            [0, 0, 1, 0, 0], //
            [0, 1, 0, 1, 0], //
            [1, 0, 1, 0, 1], //
            [0, 1, 0, 1, 0], //
            [0, 0, 0, 0, 0] //
        ],
        [
            [0, 0, 1, 0, 0], //
            [0, 0, 0, 0, 0], //
            [0, 1, 1, 1, 0], //
            [0, 1, 0, 1, 0], //
            [1, 0, 0, 0, 1] //
        ],
        [
            [0, 0, 1, 0, 0], //
            [0, 1, 0, 1, 0], //
            [0, 0, 1, 0, 0], //
            [0, 1, 0, 1, 0], //
            [1, 0, 0, 0, 1] //
        ],
        [
            [0, 0, 1, 0, 0], //
            [0, 0, 1, 0, 0], //
            [0, 1, 0, 1, 0], //
            [0, 1, 0, 1, 0], //
            [0, 1, 0, 1, 0] //
        ]
    ];

    internal const int GridSize = 5;

    private const float SpellSelectionBottomFallbackCanvasRatio = 0.22f;
    private const float SpellSelectionDragThresholdRatio = 0.35f;
    private const float SpellSelectionMargin = 12f;
    private const float SpellSelectionControlsWidth = 40f;
    private const float SpellSelectionBodyPadding = 16f;
    private const float SpellSelectionCardGap = 10f;
    private const int SpellSelectionPreferredCardColumns = 3;

    private static readonly List<RectTransform> SpellLineTables = [];
    private static readonly HashSet<SpellSelectionPanelLayoutState> SpellSelectionPanelLayouts = [];
    private static readonly string[] LegacySpellSelectionRuntimeContainerNames =
    [
        "SpellSelection" + "Viewport",
        "SpellSelection" + "Scroll" + "Viewport",
        "SpellSelection" + "Line" + "Content"
    ];
    private static readonly Vector3[] SpellSelectionWorldCorners = new Vector3[4];
    private static SpellSelectionLinePager ActiveSpellSelectionLinePager { get; set; }
    private static SpellSelectionLineRefreshScope ActiveSpellSelectionLineRefresh { get; set; }
    private static ItemPresentation EmpressGarbOriginalItemPresentation { get; set; }

    internal static bool ShouldSuppressSpellSelectionBackgroundWheel()
    {
        return ActiveSpellSelectionLinePager && ActiveSpellSelectionLinePager.ShouldSuppressBackgroundWheel();
    }

    internal static bool ShouldSuppressSpellSelectionBackgroundScroll(Component source)
    {
        return ActiveSpellSelectionLinePager &&
               ActiveSpellSelectionLinePager.ShouldSuppressBackgroundWheel() &&
               !ActiveSpellSelectionLinePager.IsForegroundControl(source);
    }

    internal static bool TryRouteSpellSelectionWheel(float delta)
    {
        return ActiveSpellSelectionLinePager && ActiveSpellSelectionLinePager.RouteWheel(delta);
    }

    private static bool TryRouteSpellSelectionWheelAt(float delta, Vector2 position)
    {
        return ActiveSpellSelectionLinePager && ActiveSpellSelectionLinePager.RouteWheelAt(delta, position);
    }

    internal static void CancelPendingSpellSelectionBind(SpellSelectionPanel panel)
    {
        var state = panel.GetComponent<SpellSelectionPanelLayoutState>();
        if (state && state.HasPendingBind)
        {
            // Native Hide can return immediately for an already hidden panel. Its pending
            // callback and ordinary bound state must still be released through native Unbind.
            panel.Unbind();
        }
    }

    internal static void PrepareNativeSpellSelectionBind(SpellSelectionPanel panel)
    {
        var state = panel.GetComponent<SpellSelectionPanelLayoutState>();
        if (state && (state.IsApplied || state.HasPendingBind))
        {
            // The option can change while a custom picker is still bound. Return its native
            // columns once and restore the holder/fitters before the ordinary Bind runs.
            panel.Unbind();
        }
    }

    internal static void ToggleVttCamera()
    {
        var cameraService = ServiceRepository.GetService<ICameraService>();

        IsVttCameraEnabled = !IsVttCameraEnabled;
        cameraService.DebugCameraEnabled = IsVttCameraEnabled;
    }

    internal static IEnumerator SelectPosition(CharacterAction action, FeatureDefinitionPower power)
    {
        var character = action.ActingCharacter;

        // disable this feature in MP as we cannot offer selections during power execution
        if (Global.IsMultiplayer)
        {
            action.actionParams.Positions.SetRange(character.LocationPosition);

            yield break;
        }

        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();
        var rulesetCharacter = character.RulesetCharacter;
        var usablePower = PowerProvider.Get(power, rulesetCharacter);
        var actionParams = new CharacterActionParams(character, ActionDefinitions.Id.PowerNoCost)
        {
            RulesetEffect =
                implementationService.InstantiateEffectPower(rulesetCharacter, usablePower, true)
        };
        var cursorService = ServiceRepository.GetService<ICursorService>();

        ResetCamera();
        cursorService.ActivateCursor<CursorLocationSelectPosition>(actionParams);

        var position = int3.zero;

        while (cursorService.CurrentCursor is CursorLocationSelectPosition cursorLocationSelectPosition)
        {
            position = cursorLocationSelectPosition.hasValidPosition
                ? cursorLocationSelectPosition.HoveredLocation
                : actionParams.ActingCharacter.LocationPosition;

            yield return null;
        }

        action.actionParams.Positions.SetRange(position);
    }

    internal static void ResetCamera()
    {
        var viewLocationContextualManager =
            ServiceRepository.GetService<IViewLocationContextualService>() as ViewLocationContextualManager;

        if (!viewLocationContextualManager)
        {
            return;
        }

        if (viewLocationContextualManager.rangeAttackDirector.state == PlayState.Playing)
        {
            viewLocationContextualManager.rangeAttackDirector.Stop();
            viewLocationContextualManager.ContextualSequenceEnd?.Invoke();
        }

        // ReSharper disable once InvertIf
        if (viewLocationContextualManager.meleeAttackDirector.state == PlayState.Playing)
        {
            viewLocationContextualManager.meleeAttackDirector.Stop();
            viewLocationContextualManager.ContextualSequenceEnd?.Invoke();
        }
    }

    internal static void UpdateMovementGrid()
    {
        var cursorService = ServiceRepository.GetService<ICursorService>();

        if (cursorService.CurrentCursor is CursorLocationBattleFriendlyTurn currentCursor)
        {
            currentCursor.movementHelper.RefreshHover();
        }
    }

    // Converts continuous ratio into series of stepped values
    internal static float GetSteppedHealthRatio(float ratio)
    {
        return ratio switch
        {
            // Green
            >= 1f => 1f,
            // Green
            >= 0.5f => 0.75f,
            // Orange
            >= 0.25f => 0.5f,
            // Red
            > 0f => 0.25f,
            _ => ratio
        };
    }

    internal static void ModifyActionMaps()
    {
        var service = ServiceRepository.GetService<IInputService>();

        //copy `GamepadSelector` action from `CharacterEdition` map into `ModalListBrowse` - needed for save by location to be able to scroll through save location selector
        var map = service.InputActionAsset.FindActionMap("ModalListBrowse");
        var action = map.AddAction("GamepadSelector");
        var oldMap = service.InputActionAsset.FindActionMap("CharacterEdition").FindAction("GamepadSelector");

        foreach (var oldMapBinding in oldMap.bindings)
        {
            action.AddBinding(oldMapBinding);
        }
    }

    internal static void RefreshMetamagicOffering(MetaMagicSubPanel __instance)
    {
        if (__instance == null ||
            __instance.relevantMetamagicOptions == null)
        {
            return;
        }

        var metamagicOptions = MetamagicContext.GetVisibleMetamagicOptions();

        __instance.relevantMetamagicOptions.Clear();
        __instance.relevantMetamagicOptions.AddRange(metamagicOptions);

        if (!__instance.Table ||
            !__instance.ItemPrefab)
        {
            return;
        }

        Gui.ReleaseChildrenToPool(__instance.Table);

        while (__instance.Table.childCount < __instance.relevantMetamagicOptions.Count)
        {
            Gui.GetPrefabFromPool(__instance.ItemPrefab, __instance.Table);
        }
    }

    internal static void SpellSelectionPanelMultilineUnbind(SpellSelectionPanel panel)
    {
        foreach (var pager in panel.GetComponentsInChildren<SpellSelectionLinePager>(true))
        {
            pager.DisablePager();
        }

        foreach (var spellTable in SpellLineTables.Where(table => table && table.IsChildOf(panel.transform)).ToArray())
        {
            SetSpellSelectionLineTableVisible(spellTable, true);
            ReleaseSpellSelectionRowLines(spellTable);
            SpellLineTables.Remove(spellTable);
            spellTable.gameObject.SetActive(false);
            spellTable.SetParent(null);
            Object.Destroy(spellTable.gameObject);
        }

        SpellLineTables.RemoveAll(table => !table);
        // An empty native row can be excluded from custom layout. The next native Bind must
        // inherit an ordinary, visible table even when the multiline option is disabled.
        SetSpellSelectionLineTableVisible(panel.spellRepertoireLinesTable, true);
        panel.spellRepertoireLinesTable.parent.GetComponent<SpellSelectionHolderLayoutState>()?.Restore();
        panel.GetComponent<SpellSelectionPanelLayoutState>()?.Restore();
    }

    internal static void SpellSelectionPanelMultilineBind(
        SpellSelectionPanel __instance,
        GuiCharacter caster,
        SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
        ActionDefinitions.ActionType actionType,
        bool cantripOnly)
    {
        if (Main.Settings.DisableMultilineSpellOffering)
        {
            __instance.GetComponent<SpellSelectionPanelLayoutState>()?.CancelPendingBind();
            return;
        }

        var panelLayout = __instance.GetComponent<SpellSelectionPanelLayoutState>() ??
                          __instance.gameObject.AddComponent<SpellSelectionPanelLayoutState>();
        if (!__instance.gameObject.activeInHierarchy)
        {
            // Native panels can bind while hidden. Inactive fitters and ancestor-component
            // lookup cannot produce valid geometry; build once when native Show activates it.
            panelLayout.DeferBind(__instance, spellCastEngaged, actionType, cantripOnly);
            return;
        }

        panelLayout.CancelPendingBind();

        var spellRepertoireLines = __instance.spellRepertoireLines;
        var spellRepertoireSecondaryLine = __instance.spellRepertoireSecondaryLine;
        var spellRepertoireLinesTable = __instance.spellRepertoireLinesTable;
        var slotAdvancementPanel = __instance.SlotAdvancementPanel;

        foreach (var spellRepertoireLine in spellRepertoireLines)
        {
            spellRepertoireLine.Unbind();
        }

        spellRepertoireLines.Clear();
        SpellSelectionPanelMultilineUnbind(__instance);
        Gui.ReleaseChildrenToPool(spellRepertoireLinesTable);

        var spellLineHolder = EnsureSpellSelectionLineHolder(spellRepertoireLinesTable) ?? spellRepertoireLinesTable;

        // Restoring the initial holder can reactivate the native secondary line along with the tables.
        spellRepertoireSecondaryLine.Unbind();
        spellRepertoireSecondaryLine.gameObject.SetActive(false);
        SetSpellSelectionLineTableVisible(spellRepertoireLinesTable, true);
        panelLayout.Apply(__instance);
        var spellRepertoires = SpellSelectionContext.GetRepertoires(__instance.Caster.RulesetCharacter).ToArray();

        var curTable = spellRepertoireLinesTable;
        Canvas.ForceUpdateCanvases();
        var navigation = __instance.transform.Find("SpellSelectionNavigation");
        var hasCanvas = TryGetCanvasLocalBounds(__instance.RectTransform, out _, out var canvasRect);
        var safeCanvasBounds = hasCanvas ? GetSpellSelectionSafeCanvasBounds(__instance, canvasRect) : Rect.zero;
        var maximumWidth = hasCanvas
            ? safeCanvasBounds.width - SpellSelectionControlsWidth -
              __instance.GetComponentsInChildren<Button>(true)
                  .Where(button => !button.GetComponentInParent<SpellRepertoireLine>() &&
                                   (!navigation || !button.transform.IsChildOf(navigation)))
                  .Select(button => ((RectTransform)button.transform).rect.width)
                  .DefaultIfEmpty(0f).Max() - SpellSelectionMargin - 2f * SpellSelectionBodyPadding
            : float.MaxValue;
        var maximumHeight = canvasRect
            ? safeCanvasBounds.height - 2f * SpellSelectionBodyPadding
            : float.MaxValue;

        foreach (var rulesetSpellRepertoire in spellRepertoires)
        {
            var maxLevel = rulesetSpellRepertoire.MaxSpellLevelOfSpellCastingLevel;

            SharedSpellsContext.FactorMysticArcanum(caster.RulesetCharacter, rulesetSpellRepertoire,
                ref maxLevel);

            var levels = Enumerable.Range(0, cantripOnly ? 1 : maxLevel + 1)
                .Where(level => SpellActionTypeContext.HasSpellOfLevelAndActionType(
                    caster.RulesetCharacter, rulesetSpellRepertoire, level, actionType)).ToArray();

            foreach (var level in levels)
            {
                curTable = AddActiveSpellsToLine(
                    __instance,
                    spellCastEngaged,
                    actionType,
                    cantripOnly,
                    spellRepertoireLines,
                    curTable,
                    slotAdvancementPanel,
                    rulesetSpellRepertoire,
                    level,
                    level);

                // Keep each native level and its resource owner intact. Source grouping below
                // packs its actual card widths beneath a single acquisition heading.
                ConfigureMultilineSpellSelectionLine(
                    curTable.GetComponentInChildren<SpellRepertoireLine>(), maximumWidth, maximumHeight);
            }
        }

        // The native qualifier can exclude a source whose initial readiness estimate included it.
        // Retain the reusable native table, but omit empty source headings from the final layout.
        foreach (var emptyLine in spellRepertoireLines.Where(line => line.SpellsByLevelBoxes.Count == 0).ToArray())
        {
            var emptyTable = emptyLine.transform.parent as RectTransform;
            spellRepertoireLines.Remove(emptyLine);
            emptyLine.Unbind();
            Gui.ReleaseChildrenToPool(emptyTable);
            SetSpellSelectionLineTableVisible(emptyTable, false);
            if (emptyTable != spellRepertoireLinesTable)
            {
                SpellLineTables.Remove(emptyTable);
                emptyTable.gameObject.SetActive(false);
                emptyTable.SetParent(null);
                Object.Destroy(emptyTable.gameObject);
            }
        }

        GroupSpellSelectionSourceRows(__instance, spellLineHolder, maximumWidth);
        SetSpellSelectionLineTableVisible(spellRepertoireLinesTable, false);
        LayoutRebuilder.ForceRebuildLayoutImmediate(spellLineHolder);
        __instance.RectTransform.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Horizontal,
            spellRepertoireLinesTable.rect.width);

        ConfigureSpellSelectionLinePager(__instance, spellLineHolder);

        FloatingPanelBounds.ClampToScreen(__instance.RectTransform);
        FloatingPanelBounds.ClampToScreenForNextFrames(__instance, __instance.RectTransform);
    }

    private static RectTransform EnsureSpellSelectionLineHolder(RectTransform spellRepertoireLinesTable)
    {
        RestoreSpellSelectionLineTableHierarchy(spellRepertoireLinesTable);

        var holder = spellRepertoireLinesTable.parent as RectTransform;

        var panelRoot = spellRepertoireLinesTable.GetComponentInParent<SpellSelectionPanel>().RectTransform;
        if (holder && holder != panelRoot && holder.GetComponent<VerticalLayoutGroup>())
        {
            RestoreSpellSelectionHolderLayout(holder);
            ConfigureSpellSelectionHolder(holder);
            return holder;
        }

        holder = new GameObject("SpellSelectionLineHolder", typeof(RectTransform)).GetComponent<RectTransform>();
        holder.gameObject.layer = spellRepertoireLinesTable.gameObject.layer;

        var verticalLayoutGroup = holder.gameObject.AddComponent<VerticalLayoutGroup>();

        verticalLayoutGroup.spacing = 10;
        verticalLayoutGroup.childAlignment = TextAnchor.UpperLeft;
        verticalLayoutGroup.childForceExpandWidth = false;
        verticalLayoutGroup.childForceExpandHeight = false;
        verticalLayoutGroup.childControlWidth = true;
        verticalLayoutGroup.childControlHeight = true;
        var fitter = holder.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        holder.SetParent(spellRepertoireLinesTable.parent, true);
        holder.SetAsFirstSibling();
        holder.localScale = Vector3.one;
        ConfigureSpellSelectionHolder(holder);
        spellRepertoireLinesTable.SetParent(holder, true);

        return holder;
    }

    private static void ConfigureSpellSelectionHolder(RectTransform holder)
    {
        // Native holders center their children. Fix both the holder origin and its layout
        // so short feat rows and subsequent pages share the first class row's left edge.
        (holder.GetComponent<SpellSelectionHolderLayoutState>() ??
         holder.gameObject.AddComponent<SpellSelectionHolderLayoutState>()).Capture(holder);
        var layout = holder.GetComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        layout.childControlWidth = layout.childControlHeight = true;
        holder.anchorMin = holder.anchorMax = new Vector2(0f, 1f);
        holder.pivot = new Vector2(0f, 1f);
        holder.anchoredPosition = new Vector2(SpellSelectionBodyPadding, -SpellSelectionBodyPadding);
    }

    private sealed class SpellSelectionHolderLayoutState : MonoBehaviour
    {
        private RectTransform _holder;
        private VerticalLayoutGroup _layout;
        private Vector2 _anchorMin, _anchorMax, _pivot, _position;
        private TextAnchor _alignment;
        private bool _expandWidth, _expandHeight, _controlWidth, _controlHeight;

        internal void Capture(RectTransform holder)
        {
            if (_holder)
            {
                return;
            }

            _holder = holder;
            _anchorMin = holder.anchorMin;
            _anchorMax = holder.anchorMax;
            _pivot = holder.pivot;
            _position = holder.anchoredPosition;
            var layout = holder.GetComponent<VerticalLayoutGroup>();
            _layout = layout;
            _alignment = layout.childAlignment;
            _expandWidth = layout.childForceExpandWidth;
            _expandHeight = layout.childForceExpandHeight;
            _controlWidth = layout.childControlWidth;
            _controlHeight = layout.childControlHeight;
        }

        internal void EnsureApplied()
        {
            var origin = new Vector2(0f, 1f);
            var position = new Vector2(SpellSelectionBodyPadding, -SpellSelectionBodyPadding);
            if (_holder && (_holder.anchorMin != origin || _holder.anchorMax != origin ||
                            _holder.pivot != origin || _holder.anchoredPosition != position ||
                            _layout.childAlignment != TextAnchor.UpperLeft || _layout.childForceExpandWidth ||
                            _layout.childForceExpandHeight || !_layout.childControlWidth || !_layout.childControlHeight))
            {
                ConfigureSpellSelectionHolder(_holder);
            }
        }

        internal void Restore()
        {
            if (!_holder)
            {
                return;
            }

            _holder.anchorMin = _anchorMin;
            _holder.anchorMax = _anchorMax;
            _holder.pivot = _pivot;
            _holder.anchoredPosition = _position;
            var layout = _holder.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = _alignment;
            layout.childForceExpandWidth = _expandWidth;
            layout.childForceExpandHeight = _expandHeight;
            layout.childControlWidth = _controlWidth;
            layout.childControlHeight = _controlHeight;
            _holder = null;
            _layout = null;
        }
    }

    private static void ConfigureSpellSelectionLinePager(
        SpellSelectionPanel panel,
        RectTransform holder)
    {
        Canvas.ForceUpdateCanvases();
        SetAllSpellSelectionLineTablesVisible(holder, true);
        LayoutRebuilder.ForceRebuildLayoutImmediate(holder);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.RectTransform);

        var lineTables = GetSpellSelectionLineTables(holder);
        var rowHeights = new float[lineTables.Length];
        var spacing = holder.GetComponent<VerticalLayoutGroup>()?.spacing ?? 0f;
        var contentHeight = 0f;
        var contentWidth = 0f;
        var availableHeight = 0f;
        var safeCanvasBounds = Rect.zero;

        for (var index = 0; index < lineTables.Length; index++)
        {
            var lineTable = lineTables[index];
            var rowHeight = GetSpellSelectionLineTableHeight(lineTable);

            rowHeights[index] = rowHeight;
            contentHeight += rowHeight;
            contentWidth = Mathf.Max(contentWidth, GetSpellSelectionLineTableWidth(lineTable));
        }

        if (lineTables.Length > 1)
        {
            contentHeight += spacing * (lineTables.Length - 1);
        }

        if (TryGetCanvasLocalBounds(panel.RectTransform, out _, out var canvasRect) ||
            TryGetCanvasLocalBounds(holder, out _, out canvasRect))
        {
            safeCanvasBounds = GetSpellSelectionSafeCanvasBounds(panel, canvasRect);
            availableHeight = Mathf.Max(0f, safeCanvasBounds.height);
        }
        else
        {
            availableHeight = contentHeight;
        }

        if (lineTables.Length == 0 || contentWidth <= 1f || contentHeight <= 1f)
        {
            DisableSpellSelectionLinePager(holder);

            return;
        }

        var visibleRows = CalculateVisibleSpellSelectionRows(rowHeights, spacing,
            Mathf.Max(1f, availableHeight - 2f * SpellSelectionBodyPadding));
        var pager = holder.GetComponent<SpellSelectionLinePager>() ?? holder.gameObject.AddComponent<SpellSelectionLinePager>();

        pager.Configure(panel, holder, lineTables, rowHeights, spacing, visibleRows, safeCanvasBounds, canvasRect);
    }

    private static void RestoreSpellSelectionLineTableHierarchy(RectTransform spellRepertoireLinesTable)
    {
        if (!spellRepertoireLinesTable)
        {
            return;
        }

        var holder = spellRepertoireLinesTable.parent as RectTransform;

        if (holder &&
            IsLegacySpellSelectionRuntimeContainer(holder.name) &&
            holder.parent is RectTransform contentParent)
        {
            MoveChildren(holder, contentParent);
            Object.Destroy(holder.gameObject);
            holder = contentParent;
        }

        if (holder &&
            holder.parent is RectTransform viewport &&
            IsLegacySpellSelectionRuntimeContainer(viewport.name))
        {
            var viewportParent = viewport.parent as RectTransform;
            var viewportSiblingIndex = viewport.GetSiblingIndex();

            holder.SetParent(viewportParent, true);
            holder.SetSiblingIndex(viewportSiblingIndex);
            Object.Destroy(viewport.gameObject);
        }

        if (holder)
        {
            for (var childIndex = holder.childCount - 1; childIndex >= 0; childIndex--)
            {
                if (holder.GetChild(childIndex) is not RectTransform child ||
                    !IsLegacySpellSelectionRuntimeContainer(child.name))
                {
                    continue;
                }

                MoveChildren(child, holder);
                Object.Destroy(child.gameObject);
            }

            RestoreSpellSelectionHolderLayout(holder);
            SetAllSpellSelectionLineTablesVisible(holder, true);
        }
    }

    private static bool IsLegacySpellSelectionRuntimeContainer(string objectName)
    {
        return LegacySpellSelectionRuntimeContainerNames.Contains(objectName);
    }

    private static Rect GetSpellSelectionSafeCanvasBounds(SpellSelectionPanel panel, RectTransform canvasRect)
    {
        var canvasBounds = GetInsetCanvasRect(canvasRect, SpellSelectionMargin);
        var state = panel.GetComponent<SpellSelectionPanelLayoutState>();
        var bounds = new List<Rect>();
        foreach (var blocker in state.GetHudBlocks(canvasRect))
        {
            if (!blocker.Visible || !blocker.Rect || blocker.Rect.IsChildOf(panel.transform) ||
                !TryGetCanvasLocalBounds(blocker.Rect, canvasRect, out var blocked) || blocked.width <= 1f ||
                blocked.height <= 1f || !canvasBounds.Overlaps(blocked))
            {
                continue;
            }

            bounds.Add(Rect.MinMaxRect(blocked.xMin - SpellSelectionMargin, blocked.yMin - SpellSelectionMargin,
                blocked.xMax + SpellSelectionMargin, blocked.yMax + SpellSelectionMargin));
        }

        if (bounds.Count == 0)
        {
            // Standalone previews do not instantiate the action bar. Reserve its usual region.
            canvasBounds.yMin += canvasBounds.height * SpellSelectionBottomFallbackCanvasRatio;
            return canvasBounds;
        }

        // Pick the largest rectangle which contains no visible HUD block. Measuring actual tables
        // avoids treating the action panel (which also owns this picker) as one giant obstruction.
        var candidates = new List<Rect> { canvasBounds };
        foreach (var blocked in bounds)
        {
            var next = new List<Rect>();
            foreach (var available in candidates)
            {
                if (!available.Overlaps(blocked))
                {
                    next.Add(available);
                    continue;
                }

                if (blocked.xMin > available.xMin)
                {
                    next.Add(Rect.MinMaxRect(available.xMin, available.yMin,
                        Mathf.Min(blocked.xMin, available.xMax), available.yMax));
                }
                if (blocked.xMax < available.xMax)
                {
                    next.Add(Rect.MinMaxRect(Mathf.Max(blocked.xMax, available.xMin), available.yMin,
                        available.xMax, available.yMax));
                }
                if (blocked.yMin > available.yMin)
                {
                    next.Add(Rect.MinMaxRect(available.xMin, available.yMin, available.xMax,
                        Mathf.Min(blocked.yMin, available.yMax)));
                }
                if (blocked.yMax < available.yMax)
                {
                    next.Add(Rect.MinMaxRect(available.xMin, Mathf.Max(blocked.yMax, available.yMin),
                        available.xMax, available.yMax));
                }
            }

            candidates = next.Where(rect => rect.width > 1f && rect.height > 1f).Distinct().ToList();
        }

        return candidates.OrderByDescending(rect => rect.width * rect.height).FirstOrDefault();
    }

    internal static void InvalidateSpellSelectionHud(Component shownPanel)
    {
        // Screen Show is not a geometry event for unrelated spell/resource popups or
        // tooltips. Only a newly shown HUD owner can introduce uncached HUD rectangles.
        if (shownPanel is CharacterActionPanel or CharacterControlPanel or BattleInitiativeTable or
            PartyControlPanel or TimeAndNavigationPanel or GuiConsoleScreen)
        {
            foreach (var state in SpellSelectionPanelLayouts)
            {
                state.InvalidateHud(shownPanel);
            }
        }
    }

    private static void RestoreSpellSelectionHolderLayout(RectTransform holder)
    {
        var verticalLayoutGroup = holder.GetComponent<VerticalLayoutGroup>();

        if (verticalLayoutGroup)
        {
            verticalLayoutGroup.enabled = true;
        }

        var contentSizeFitter = holder.GetComponent<ContentSizeFitter>();

        if (contentSizeFitter)
        {
            contentSizeFitter.enabled = true;
        }
    }

    private static void MoveChildren(RectTransform source, RectTransform destination)
    {
        var children = new List<Transform>();

        for (var childIndex = 0; source && childIndex < source.childCount; childIndex++)
        {
            children.Add(source.GetChild(childIndex));
        }

        foreach (var child in children)
        {
            child.SetParent(destination, true);
        }
    }

    private static void SetAllSpellSelectionLineTablesVisible(RectTransform holder, bool visible)
    {
        foreach (var lineTable in GetSpellSelectionLineTables(holder))
        {
            SetSpellSelectionLineTableVisible(lineTable, visible);
        }
    }

    private static void SetSpellSelectionLineTableVisible(RectTransform lineTable, bool visible)
    {
        if (!lineTable)
        {
            return;
        }

        var canvasGroup = lineTable.GetComponent<CanvasGroup>() ?? lineTable.gameObject.AddComponent<CanvasGroup>();
        var layoutElement = lineTable.GetComponent<LayoutElement>() ?? lineTable.gameObject.AddComponent<LayoutElement>();

        if (canvasGroup.alpha != (visible ? 1f : 0f))
        {
            canvasGroup.alpha = visible ? 1f : 0f;
        }
        canvasGroup.blocksRaycasts = visible;
        canvasGroup.interactable = visible;
        if (layoutElement.ignoreLayout == visible)
        {
            layoutElement.ignoreLayout = !visible;
        }
        if (!lineTable.gameObject.activeSelf)
        {
            lineTable.gameObject.SetActive(true);
        }
    }

    private static void DisableSpellSelectionLinePager(RectTransform holder)
    {
        if (!holder)
        {
            return;
        }

        var pager = holder.GetComponent<SpellSelectionLinePager>();

        if (pager)
        {
            pager.DisablePager();
        }
    }

    private static RectTransform[] GetSpellSelectionLineTables(RectTransform holder)
    {
        var lineTables = new List<RectTransform>();

        for (var childIndex = 0; holder && childIndex < holder.childCount; childIndex++)
        {
            if (holder.GetChild(childIndex) is RectTransform child &&
                child.GetComponentsInChildren<SpellRepertoireLine>(true).Length > 0)
            {
                lineTables.Add(child);
            }
        }

        return [.. lineTables];
    }

    private static int CalculateVisibleSpellSelectionRows(float[] rowHeights, float spacing, float availableHeight)
    {
        var visibleRows = 0;
        var height = 0f;

        for (var index = 0; index < rowHeights.Length; index++)
        {
            var nextHeight = height + (visibleRows > 0 ? spacing : 0f) + rowHeights[index];

            if (visibleRows > 0 && nextHeight > availableHeight)
            {
                break;
            }

            height = nextHeight;
            visibleRows++;
        }

        return Mathf.Clamp(visibleRows, 1, rowHeights.Length);
    }

    private static float GetVisibleSpellSelectionRowsHeight(
        float[] rowHeights,
        float spacing,
        int firstRow,
        int visibleRows)
    {
        var height = 0f;
        var lastRow = Mathf.Min(rowHeights.Length, firstRow + visibleRows);

        for (var index = firstRow; index < lastRow; index++)
        {
            height += rowHeights[index];

            if (index > firstRow)
            {
                height += spacing;
            }
        }

        return height;
    }

    private static float GetSpellSelectionLineTableHeight(RectTransform lineTable)
    {
        if (lineTable.name == "SpellSelectionSourceRow")
        {
            return LayoutUtility.GetPreferredHeight(lineTable);
        }

        var bounds = GetChildrenLocalBounds(lineTable);

        return Mathf.Max(GetPreferredHeight(lineTable), bounds.height, lineTable.rect.height);
    }

    private static float GetSpellSelectionLineTableWidth(RectTransform lineTable)
    {
        if (lineTable.name == "SpellSelectionSourceRow")
        {
            return LayoutUtility.GetPreferredWidth(lineTable);
        }

        var bounds = GetChildrenLocalBounds(lineTable);

        return Mathf.Max(GetPreferredWidth(lineTable), bounds.width, lineTable.rect.width);
    }

    private static void RefreshSpellSelectionPanelSize(
        SpellSelectionPanel panel,
        RectTransform holder,
        float width,
        float height,
        Rect safeCanvasBounds,
        RectTransform canvasRect)
    {
        if (!panel || !holder)
        {
            return;
        }

        var panelWidth = Mathf.Max(1f, width + 2f * SpellSelectionBodyPadding);
        var panelHeight = Mathf.Max(1f, height + 2f * SpellSelectionBodyPadding);

        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(holder);
        panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, panelWidth);
        panel.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, panelHeight);
        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.RectTransform);

        if (canvasRect && safeCanvasBounds.width > 1f && safeCanvasBounds.height > 1f)
        {
            ClampSpellSelectionPanelToSafeBounds(panel.RectTransform, canvasRect, safeCanvasBounds);
        }

        FloatingPanelBounds.ClampToScreen(panel.RectTransform);
    }

    private static void ClampSpellSelectionPanelToSafeBounds(
        RectTransform panel,
        RectTransform canvasRect,
        Rect safeCanvasBounds)
    {
        if (!TryGetCanvasLocalBounds(panel, canvasRect, out var panelBounds))
        {
            return;
        }

        var delta = Vector2.zero;

        if (panelBounds.xMin < safeCanvasBounds.xMin)
        {
            delta.x = safeCanvasBounds.xMin - panelBounds.xMin;
        }
        else if (panelBounds.xMax > safeCanvasBounds.xMax)
        {
            delta.x = safeCanvasBounds.xMax - panelBounds.xMax;
        }

        if (panelBounds.yMin < safeCanvasBounds.yMin)
        {
            delta.y = safeCanvasBounds.yMin - panelBounds.yMin;
        }
        else if (panelBounds.yMax > safeCanvasBounds.yMax)
        {
            delta.y = safeCanvasBounds.yMax - panelBounds.yMax;
        }

        if (delta != Vector2.zero)
        {
            panel.position += canvasRect.TransformVector(new Vector3(delta.x, delta.y, 0f));
        }
    }

    private static Rect GetChildrenLocalBounds(RectTransform parent)
    {
        var hasBounds = false;
        var min = Vector2.zero;
        var max = Vector2.zero;

        for (var childIndex = 0; parent && childIndex < parent.childCount; childIndex++)
        {
            if (parent.GetChild(childIndex) is not RectTransform child || !child.gameObject.activeSelf)
            {
                continue;
            }

            child.GetWorldCorners(SpellSelectionWorldCorners);

            for (var cornerIndex = 0; cornerIndex < SpellSelectionWorldCorners.Length; cornerIndex++)
            {
                var point = (Vector2)parent.InverseTransformPoint(SpellSelectionWorldCorners[cornerIndex]);

                if (!hasBounds)
                {
                    min = point;
                    max = point;
                    hasBounds = true;
                }
                else
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }
        }

        return hasBounds ? Rect.MinMaxRect(min.x, min.y, max.x, max.y) : Rect.zero;
    }

    private static bool TryGetCanvasLocalBounds(
        RectTransform rectTransform,
        out Rect bounds,
        out RectTransform canvasRect)
    {
        bounds = default;
        canvasRect = null;

        if (!rectTransform)
        {
            return false;
        }

        var canvas = rectTransform.GetComponentsInParent<Canvas>(true).FirstOrDefault();

        if (!canvas)
        {
            return false;
        }

        canvasRect = (canvas.rootCanvas ? canvas.rootCanvas : canvas).transform as RectTransform;

        return canvasRect && TryGetCanvasLocalBounds(rectTransform, canvasRect, out bounds);
    }

    private static bool TryGetCanvasLocalBounds(RectTransform rectTransform, RectTransform canvasRect, out Rect bounds)
    {
        bounds = default;

        if (!rectTransform || !canvasRect)
        {
            return false;
        }

        rectTransform.GetWorldCorners(SpellSelectionWorldCorners);
        var min = (Vector2)canvasRect.InverseTransformPoint(SpellSelectionWorldCorners[0]);
        var max = min;

        for (var i = 1; i < SpellSelectionWorldCorners.Length; i++)
        {
            var point = (Vector2)canvasRect.InverseTransformPoint(SpellSelectionWorldCorners[i]);

            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

        return true;
    }

    private static bool IsActiveWithin(Transform child, Transform root)
    {
        while (child && child != root)
        {
            if (!child.gameObject.activeSelf)
            {
                return false;
            }

            child = child.parent;
        }

        return child == root;
    }

    private static TextMeshProUGUI CreateSpellSelectionText(RectTransform rect)
    {
        // TMP's native Awake replaces sizeDelta with its default text-container size.
        // Keep the geometry assigned by this layout, including stretched footer/button labels.
        var size = rect.sizeDelta;
        var position = rect.anchoredPosition;
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.autoSizeTextContainer = false;
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        return text;
    }

    private static Rect GetInsetCanvasRect(RectTransform canvasRect, float margin)
    {
        var rect = canvasRect.rect;
        var horizontalMargin = Mathf.Min(margin, rect.width * 0.5f);
        var verticalMargin = Mathf.Min(margin, rect.height * 0.5f);

        return Rect.MinMaxRect(
            rect.xMin + horizontalMargin,
            rect.yMin + verticalMargin,
            rect.xMax - horizontalMargin,
            rect.yMax - verticalMargin);
    }

    private static float GetPreferredHeight(RectTransform rectTransform)
    {
        return rectTransform
            ? Mathf.Max(rectTransform.rect.height, rectTransform.sizeDelta.y, LayoutUtility.GetPreferredHeight(rectTransform))
            : 0f;
    }

    private static float GetPreferredWidth(RectTransform rectTransform)
    {
        return rectTransform
            ? Mathf.Max(rectTransform.rect.width, rectTransform.sizeDelta.x, LayoutUtility.GetPreferredWidth(rectTransform))
            : 0f;
    }

    private sealed class SpellSelectionHudBlock
    {
        private readonly Behaviour _owner;
        private readonly Func<RectTransform> _rectangle;
        private readonly Func<bool> _ownerVisible;
        private Transform _parent;
        private Transform[] _ancestors = [];
        private Transform[] _ancestorParents = [];
        private CanvasGroup[] _groups = [];
        private Rect _bounds;
        private Matrix4x4 _matrix;

        internal RectTransform Rect { get; private set; }
        internal bool Visible { get; private set; }

        internal bool HasOwner(Component owner) => _owner == owner;

        internal SpellSelectionHudBlock(Behaviour owner, Func<RectTransform> rectangle, Func<bool> ownerVisible = null)
        {
            _owner = owner;
            _rectangle = rectangle;
            _ownerVisible = ownerVisible;
        }

        internal bool Update()
        {
            var rectangle = _owner ? _rectangle() : null;
            var changed = rectangle != Rect;
            var hierarchyChanged = rectangle && rectangle.parent != _parent;
            for (var index = 0; !hierarchyChanged && index < _ancestors.Length; index++)
            {
                hierarchyChanged = !_ancestors[index] || _ancestors[index].parent != _ancestorParents[index];
            }
            if (changed || hierarchyChanged)
            {
                Rect = rectangle;
                _parent = rectangle ? rectangle.parent : null;
                _groups = rectangle ? rectangle.GetComponentsInParent<CanvasGroup>(true) : [];
                var ancestors = new List<Transform>();
                for (var ancestor = rectangle ? rectangle.parent : null; ancestor; ancestor = ancestor.parent)
                {
                    ancestors.Add(ancestor);
                }
                _ancestors = ancestors.ToArray();
                _ancestorParents = _ancestors.Select(ancestor => ancestor.parent).ToArray();
                changed = true;
            }

            var visible = _owner && _owner.isActiveAndEnabled && (_ownerVisible == null || _ownerVisible()) &&
                          rectangle && rectangle.gameObject.scene.IsValid() && rectangle.gameObject.activeInHierarchy;
            if (visible)
            {
                foreach (var group in _groups)
                {
                    if (group && group.alpha <= 0.001f)
                    {
                        visible = false;
                        break;
                    }
                }
            }

            changed |= visible != Visible;
            Visible = visible;
            if (visible)
            {
                var matrix = rectangle.localToWorldMatrix;
                var bounds = rectangle.rect;
                changed |= !_matrix.Equals(matrix) || _bounds != bounds;
                _matrix = matrix;
                _bounds = bounds;
            }

            return changed;
        }
    }

    private sealed class SpellSelectionPanelLayoutState : MonoBehaviour
    {
        private SpellSelectionPanel _panel;
        private SpellSelectionPanel _pendingPanel;
        private SpellsByLevelBox.SpellCastEngagedHandler _pendingCallback;
        private ActionDefinitions.ActionType _pendingActionType;
        private bool _pendingCantripOnly;
        private RectTransform _backdrop;
        private RectTransform _body;
        private Vector2 _size;
        private Vector2 _position;
        private RectTransform _canvasRect;
        private SpellSelectionHolderLayoutState _holderState;
        private bool _backdropDirty;
        private Matrix4x4 _panelMatrix, _canvasMatrix;
        private Rect _panelRect, _canvasBounds;
        private readonly List<SpellSelectionHudBlock> _hudBlocks = new();
        private bool _hudDiscoveryDirty = true;
        private RectTransform _hudCanvas;
        private Matrix4x4 _hudCanvasMatrix;
        private Rect _hudCanvasBounds;
        private int _hudCanvasChildren;
        private readonly Dictionary<LayoutGroup, bool> _layouts = new();

        internal bool HasPendingBind => _pendingPanel;
        internal bool IsApplied => _panel;

        internal void DeferBind(SpellSelectionPanel panel,
            SpellsByLevelBox.SpellCastEngagedHandler callback, ActionDefinitions.ActionType actionType,
            bool cantripOnly)
        {
            // A second Bind replaces the pending choice rather than constructing hidden UI.
            _pendingPanel = panel;
            _pendingCallback = callback;
            _pendingActionType = actionType;
            _pendingCantripOnly = cantripOnly;
            enabled = true;
        }

        internal void CancelPendingBind()
        {
            _pendingPanel = null;
            _pendingCallback = null;
            _pendingActionType = default;
            _pendingCantripOnly = false;
            if (!_panel)
            {
                enabled = false;
            }
        }

        private void OnEnable()
        {
            if (!_pendingPanel || !_pendingPanel.gameObject.activeInHierarchy)
            {
                return;
            }

            var panel = _pendingPanel;
            var callback = _pendingCallback;
            var actionType = _pendingActionType;
            var cantripOnly = _pendingCantripOnly;
            CancelPendingBind();
            if (Main.Settings.DisableMultilineSpellOffering)
            {
                // The option may change between hidden Bind and Show. Re-enter the existing
                // native Bind path with the current caster/cancel callback and latest arguments.
                panel.Bind(panel.Caster, panel.SpellcastCancelled, callback, actionType, cantripOnly);
            }
            else
            {
                SpellSelectionPanelMultilineBind(panel, panel.Caster, callback, actionType, cantripOnly);
            }
        }

        internal void Apply(SpellSelectionPanel panel)
        {
            Restore();
            _panel = panel;
            SpellSelectionPanelLayouts.Add(this);
            _size = panel.RectTransform.sizeDelta;
            _position = panel.RectTransform.anchoredPosition;
            var canvas = panel.GetComponentsInParent<Canvas>(true).FirstOrDefault();
            _canvasRect = canvas ? (canvas.rootCanvas ? canvas.rootCanvas : canvas).transform as RectTransform : null;
            _holderState = panel.spellRepertoireLinesTable.parent.GetComponent<SpellSelectionHolderLayoutState>();
            _backdropDirty = true;
            enabled = true;
            foreach (var layout in panel.GetComponents<LayoutGroup>())
            {
                _layouts[layout] = layout.enabled;
                layout.enabled = false;
            }

            // Native screens share their parent canvas and raycaster. Adding a nested canvas
            // makes its graphics compete in a separate depth domain with later native popups.

            if (!_backdrop)
            {
                _backdrop = CreateBackground("SpellSelectionBackdrop", panel, new Color(0f, 0f, 0f, 0.18f));
                _backdrop.GetComponent<Image>().raycastTarget = false;
                _body = CreateBackground("SpellSelectionBody", panel, Color.white);
                ApplyNativePanelAppearance(_body.GetComponent<Image>(), panel);
                _body.gameObject.AddComponent<SpellSelectionWheelSurface>();
                _body.anchorMin = Vector2.zero;
                _body.anchorMax = Vector2.one;
                _body.offsetMin = _body.offsetMax = Vector2.zero;
            }

            _body.SetAsFirstSibling();
            _backdrop.SetAsFirstSibling();
            _body.gameObject.SetActive(true);
            _backdrop.gameObject.SetActive(true);
            UpdateBackdrop();
        }

        internal void InvalidateHud(Component owner)
        {
            if (_hudCanvas && !owner.transform.IsChildOf(_hudCanvas))
            {
                return;
            }

            if (!_hudBlocks.Any(block => block.HasOwner(owner)))
            {
                _hudDiscoveryDirty = true;
            }
        }

        internal bool HudBoundsChanged(RectTransform canvas)
        {
            var changed = _hudDiscoveryDirty || canvas != _hudCanvas ||
                          canvas && canvas.childCount != _hudCanvasChildren;
            if (changed)
            {
                DiscoverHudBlocks();
            }

            foreach (var block in _hudBlocks)
            {
                changed |= block.Update();
            }

            if (canvas)
            {
                var matrix = canvas.localToWorldMatrix;
                var bounds = canvas.rect;
                changed |= !_hudCanvasMatrix.Equals(matrix) || _hudCanvasBounds != bounds;
                _hudCanvas = canvas;
                _hudCanvasMatrix = matrix;
                _hudCanvasBounds = bounds;
                _hudCanvasChildren = canvas.childCount;
            }

            return changed;
        }

        internal IEnumerable<SpellSelectionHudBlock> GetHudBlocks(RectTransform canvas)
        {
            HudBoundsChanged(canvas);
            return _hudBlocks;
        }

        private void DiscoverHudBlocks()
        {
            _hudDiscoveryDirty = false;
            _hudBlocks.Clear();
            if (!_canvasRect)
            {
                return;
            }

            // Resolve HUD owners in the picker's native presentation tree. One local traversal
            // includes inactive HUD screens without six scans of all loaded objects/prefabs.
            foreach (var component in _canvasRect.GetComponentsInChildren<MonoBehaviour>(true))
            {
                switch (component)
                {
                    case CharacterActionPanel action:
                        _hudBlocks.Add(new SpellSelectionHudBlock(action,
                            () => action.characterActionsTable ? action.characterActionsTable.RectTransform : null,
                            () => action.Visible));
                        _hudBlocks.Add(new SpellSelectionHudBlock(action, () => action.actionPerformanceTable,
                            () => action.Visible));
                        break;
                    case CharacterControlPanel control:
                        _hudBlocks.Add(new SpellSelectionHudBlock(control,
                            () => control.activeCharacterPanel ? control.activeCharacterPanel.RectTransform : null));
                        break;
                    case BattleInitiativeTable table:
                        _hudBlocks.Add(new SpellSelectionHudBlock(table, () => table.characterPlatesTable));
                        break;
                    case PartyControlPanel party:
                        _hudBlocks.Add(new SpellSelectionHudBlock(party, () => party.partyPlatesTable));
                        _hudBlocks.Add(new SpellSelectionHudBlock(party, () => party.guestPlatesTable));
                        break;
                    case TimeAndNavigationPanel navigation:
                        _hudBlocks.Add(new SpellSelectionHudBlock(navigation, () => navigation.RectTransform));
                        break;
                    case GuiConsoleScreen console:
                        _hudBlocks.Add(new SpellSelectionHudBlock(console, () => console.viewport, () => console.Visible));
                        break;
                }
            }
        }

        private static RectTransform CreateBackground(string name, SpellSelectionPanel panel, Color color)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.gameObject.layer = panel.gameObject.layer;
            rect.SetParent(panel.transform, false);
            rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            rect.gameObject.AddComponent<Image>().color = color;
            return rect;
        }

        private static void ApplyNativePanelAppearance(Image body, SpellSelectionPanel panel)
        {
            // This native spell column owns the same blur, background and frame used by the
            // picker. Copy that stack in its native order, keeping the assets and palette.
            var nativeLine = panel.spellRepertoireSecondaryLine;
            var metamagicPanel = Gui.GuiService?.GetScreen<MetamagicSelectionPanel>();
            var owners = new[]
            {
                nativeLine ? nativeLine.uniqueLevelSlotsGroup : null,
                metamagicPanel ? metamagicPanel.RectTransform : null
            };
            foreach (var owner in owners.Where(owner => owner))
            {
                var images = owner.GetComponentsInChildren<Image>(true)
                    .Where(image => image.sprite && image.color.a > 0f &&
                                    !image.GetComponentInParent<Selectable>() &&
                                    !image.GetComponentInParent<MetamagicOptionItem>() &&
                                    TryGetCanvasLocalBounds(image.rectTransform, owner, out var bounds) &&
                                    bounds.width >= owner.rect.width * 0.5f &&
                                    bounds.height >= owner.rect.height * 0.5f)
                    .ToArray();
                if (images.Length == 0)
                {
                    continue;
                }

                CopyNativePanelImage(body, images[0]);
                // The native blur is white at full alpha. Tint it with this same panel's
                // charcoal fill so a missing scene texture cannot become an opaque gray sheet.
                var fill = images.FirstOrDefault(image => image.name == "Background");
                var palette = fill ? fill.color : new Color(0.157f, 0.157f, 0.157f, 0.588f);
                body.color = new Color(palette.r, palette.g, palette.b, Mathf.Min(palette.a, 0.48f));
                body.raycastTarget = true;
                foreach (var decoration in images.Skip(1))
                {
                    var rect = (RectTransform)new GameObject("SpellSelectionNativeFrame", typeof(RectTransform)).transform;
                    rect.gameObject.layer = panel.gameObject.layer;
                    rect.SetParent(body.transform, false);
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = rect.offsetMax = Vector2.zero;
                    rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                    var image = rect.gameObject.AddComponent<Image>();
                    CopyNativePanelImage(image, decoration);
                    if (decoration.name == "Background")
                    {
                        var color = image.color;
                        color.a = Mathf.Min(color.a, 0.58f);
                        image.color = color;
                    }

                    image.raycastTarget = false;
                }

                return;
            }
        }

        internal static void CopyNativePanelImage(Image destination, Image source)
        {
            destination.sprite = source.sprite;
            destination.material = source.material;
            destination.type = source.type;
            destination.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
            destination.preserveAspect = source.preserveAspect;
            destination.fillCenter = source.fillCenter;
            destination.color = source.color;
            destination.raycastTarget = source.raycastTarget;
        }

        private void LateUpdate()
        {
            if (_panel)
            {
                foreach (var layout in _layouts.Keys)
                {
                    if (layout && layout.enabled)
                    {
                        layout.enabled = false;
                    }
                }
                _holderState?.EnsureApplied();
            }
            if (!_panel || !_canvasRect || !_backdrop)
            {
                return;
            }

            if (_backdropDirty || !_panelMatrix.Equals(_panel.RectTransform.localToWorldMatrix) ||
                !_canvasMatrix.Equals(_canvasRect.localToWorldMatrix) || _panelRect != _panel.RectTransform.rect ||
                _canvasBounds != _canvasRect.rect)
            {
                UpdateBackdrop();
            }
        }

        private void UpdateBackdrop()
        {
            if (!_panel || !_backdrop || !_canvasRect ||
                !TryGetCanvasLocalBounds(_canvasRect, _panel.RectTransform, out var bounds))
            {
                return;
            }

            _backdrop.anchorMin = _backdrop.anchorMax = Vector2.zero;
            _backdrop.pivot = Vector2.zero;
            _backdrop.sizeDelta = bounds.size;
            _backdrop.anchoredPosition = bounds.min - _panel.RectTransform.rect.min;
            _backdropDirty = false;
            _panelMatrix = _panel.RectTransform.localToWorldMatrix;
            _canvasMatrix = _canvasRect.localToWorldMatrix;
            _panelRect = _panel.RectTransform.rect;
            _canvasBounds = _canvasRect.rect;
        }

        internal void Restore()
        {
            CancelPendingBind();
            if (!_panel)
            {
                return;
            }

            if (_backdrop)
            {
                _backdrop.gameObject.SetActive(false);
                _body.gameObject.SetActive(false);
            }

            foreach (var entry in _layouts.Where(entry => entry.Key))
            {
                entry.Key.enabled = entry.Value;
            }

            _layouts.Clear();
            // HUD owners survive native Hide/Unbind. Retain their cache until the canvas or
            // native HUD hierarchy changes, including a new owner shown while the picker is closed.
            _canvasRect = null;
            _holderState = null;
            _panel.RectTransform.sizeDelta = _size;
            _panel.RectTransform.anchoredPosition = _position;
            _panel = null;
            enabled = false;
        }

        private void OnDestroy()
        {
            SpellSelectionPanelLayouts.Remove(this);
            _hudBlocks.Clear();
        }
    }

    private sealed class SpellSelectionWheelSurface : MonoBehaviour, IScrollHandler
    {
        public void OnScroll(PointerEventData eventData)
        {
            if (TryRouteSpellSelectionWheelAt(eventData.scrollDelta.y, eventData.position))
            {
                eventData.Use();
            }
        }
    }

    private sealed class SpellSelectionLinePager : MonoBehaviour, IScrollHandler, IBeginDragHandler, IDragHandler
    {
        private RectTransform _holder;
        private SpellSelectionPanel _panel;
        private RectTransform[] _lineTables;
        private float[] _rowHeights;
        private Rect _safeCanvasBounds;
        private RectTransform _canvasRect;
        private float _dragAccumulator;
        private float _rowDragThreshold;
        private float _spacing;
        private int _firstVisibleRow;
        private int _lastWheelInputFrame = -1;
        private int _visibleRows;
        private RectTransform _controls;
        private Scrollbar _scrollbar;
        private Scrollbar _nativeScrollbar;
        private Button _previous;
        private Button _next;
        private float _contentWidth;
        private bool _updatingControls;
        private GameObject _lastSelection;
        private ContentSizeFitter _panelFitter;
        private bool _panelFitterEnabled;
        private SpellSelectionPanelLayoutState _panelLayout;
        private string _language;
        private readonly List<RaycastResult> _pointerHits = new();
        private PointerEventData _pointerEvent;
        private EventSystem _pointerEventSystem;
        private RectTransform[] _pointerControls;
        private Camera _pointerCamera;
        private bool _dragStartedInside;

        internal int FirstVisibleRow => _firstVisibleRow;

        internal void SetFirstVisibleRow(int firstVisibleRow)
        {
            var first = Mathf.Clamp(firstVisibleRow, 0, GetMaxFirstVisibleRow());
            if (first == _firstVisibleRow)
            {
                return;
            }
            _firstVisibleRow = first;
            ApplyVisibleRows();
        }

        internal void Configure(
            SpellSelectionPanel panel,
            RectTransform holder,
            RectTransform[] lineTables,
            float[] rowHeights,
            float spacing,
            int visibleRows,
            Rect safeCanvasBounds,
            RectTransform canvasRect)
        {
            _panel = panel;
            _panelLayout = panel.GetComponent<SpellSelectionPanelLayoutState>();
            if (!_panelFitter)
            {
                _panelFitter = panel.GetComponent<ContentSizeFitter>();
                _panelFitterEnabled = _panelFitter && _panelFitter.enabled;
            }
            if (_panelFitter)
            {
                _panelFitter.enabled = false;
            }

            _holder = holder;
            _lineTables = lineTables;
            _rowHeights = rowHeights;
            _spacing = spacing;
            _visibleRows = Mathf.Clamp(visibleRows, 1, lineTables.Length);
            _firstVisibleRow = 0;
            _safeCanvasBounds = safeCanvasBounds;
            _canvasRect = canvasRect;
            _rowDragThreshold = Mathf.Max(24f, GetAverageRowHeight() * SpellSelectionDragThresholdRatio);
            _contentWidth = lineTables.Max(GetSpellSelectionLineTableWidth);
            _lastSelection = null;
            _language = I2.Loc.LocalizationManager.CurrentLanguageCode;
            enabled = true;
            ActiveSpellSelectionLinePager = this;

            EnsureControls();
            _pointerControls = panel.GetComponentsInChildren<Button>(true)
                .Where(button => !panel.spellRepertoireLines.Any(line => button.transform.IsChildOf(line.transform)))
                .Select(button => (RectTransform)button.transform).ToArray();
            var canvas = canvasRect ? canvasRect.GetComponent<Canvas>() : null;
            _pointerCamera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            _controls.gameObject.SetActive(_visibleRows < _lineTables.Length);
            ApplyVisibleRows();
        }

        internal void DisablePager()
        {
            if (_lineTables != null)
            {
                foreach (var lineTable in _lineTables)
                {
                    SetSpellSelectionLineTableVisible(lineTable, true);
                }
            }

            _refreshPending = false;
            _refreshCallback = null;
            _refreshSelectedSpell = null;
            _refreshSelectedRepertoire = null;
            _refreshSelectedControl = null;
            _dragAccumulator = 0f;
            _dragStartedInside = false;
            _firstVisibleRow = 0;
            _lastSelection = null;
            _panelLayout = null;
            _pointerHits.Clear();
            _pointerEvent = null;
            _pointerEventSystem = null;
            _pointerControls = null;
            _pointerCamera = null;
            if (_panelFitter)
            {
                _panelFitter.enabled = _panelFitterEnabled;
                _panelFitter = null;
            }

            if (_controls)
            {
                _controls.gameObject.SetActive(false);
            }

            if (ActiveSpellSelectionLinePager == this)
            {
                ActiveSpellSelectionLinePager = null;
            }

            enabled = false;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragAccumulator = 0f;
            _dragStartedInside = CanPage() && ContainsPointer(eventData.position) &&
                                 OwnsForegroundInputAt(eventData.position);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragStartedInside || !CanPage())
            {
                return;
            }

            _dragAccumulator += eventData.delta.y;

            while (_dragAccumulator >= _rowDragThreshold)
            {
                MoveRows(1);
                _dragAccumulator -= _rowDragThreshold;
            }

            while (_dragAccumulator <= -_rowDragThreshold)
            {
                MoveRows(-1);
                _dragAccumulator += _rowDragThreshold;
            }
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (RouteWheelAt(eventData.scrollDelta.y, eventData.position))
            {
                eventData.Use();
            }
        }

        private bool _refreshPending;
        private SpellsByLevelBox.SpellCastEngagedHandler _refreshCallback;
        private SpellDefinition _refreshSelectedSpell;
        private RulesetSpellRepertoire _refreshSelectedRepertoire;
        private GameObject _refreshSelectedControl;
        private ActionDefinitions.ActionType _refreshActionType;
        private bool _refreshCantripOnly;
        private int _refreshFirstRow;

        internal void RequestRebind(SpellSelectionPanel panel,
            SpellsByLevelBox.SpellCastEngagedHandler callback, ActionDefinitions.ActionType actionType, bool cantripOnly,
            SpellDefinition selectedSpell, RulesetSpellRepertoire selectedRepertoire, GameObject selectedControl)
        {
            if (_refreshPending)
            {
                return;
            }

            _panel = panel;
            _refreshCallback = callback;
            _refreshSelectedSpell = selectedSpell;
            _refreshSelectedRepertoire = selectedRepertoire;
            _refreshSelectedControl = selectedControl;
            _refreshActionType = actionType;
            _refreshCantripOnly = cantripOnly;
            _refreshFirstRow = _firstVisibleRow;
            _refreshPending = true;
            enabled = true;
        }

        private void LateUpdate()
        {
            if (!_refreshPending && CanInteract() && _canvasRect && _panelLayout &&
                _panelLayout.HudBoundsChanged(_canvasRect))
            {
                var bounds = GetSpellSelectionSafeCanvasBounds(_panel, _canvasRect);
                if (Vector2.Distance(bounds.min, _safeCanvasBounds.min) > 1f ||
                    Vector2.Distance(bounds.max, _safeCanvasBounds.max) > 1f)
                {
                    var line = _panel.spellRepertoireLines.FirstOrDefault();
                    var resizedSelection = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
                    var box = resizedSelection && resizedSelection.transform.IsChildOf(_panel.transform)
                        ? resizedSelection.GetComponentInParent<SpellActivationBox>() : null;
                    if (line)
                    {
                        // A changed canvas or visible HUD can invalidate the grid's native
                        // wrapping too. Reuse the existing source-aware refresh path once.
                        RequestRebind(_panel, line.spellCastEngaged, line.actionType, line.cantripOnly,
                            box ? box.GuiSpellDefinition?.SpellDefinition : null,
                            box ? SpellActionTypeContext.GetRepertoireLine(box)?.spellRepertoire ?? box.spellRepertoire : null,
                            box ? resizedSelection : null);
                    }
                }
            }

            if (!_refreshPending)
            {
                if (CanInteract() && _language != I2.Loc.LocalizationManager.CurrentLanguageCode)
                {
                    RefreshLocalizedHeadings();
                }

                return;
            }

            var callback = _refreshCallback;
            var actionType = _refreshActionType;
            var cantripOnly = _refreshCantripOnly;
            var firstRow = _refreshFirstRow;
            var selectedSpell = _refreshSelectedSpell;
            var selectedRepertoire = _refreshSelectedRepertoire;
            var selectedControl = _refreshSelectedControl;
            _refreshPending = false;
            _refreshCallback = null;
            _refreshSelectedSpell = null;
            _refreshSelectedRepertoire = null;
            _refreshSelectedControl = null;
            if (!_panel || _panel.Caster?.RulesetCharacter == null || callback == null ||
                Main.Settings.DisableMultilineSpellOffering)
            {
                DisablePager();
                return;
            }

            var eventSystem = EventSystem.current;
            var selection = eventSystem ? eventSystem.currentSelectedGameObject : null;
            if (selectedSpell != null && selection && selection != selectedControl &&
                !selection.transform.IsChildOf(_panel.transform))
            {
                selectedSpell = null;
            }
            if (eventSystem && selectedSpell != null)
            {
                // Pooled buttons may be rebound to a different spell. Preserve the selection by
                // its spell and resource source, rather than keeping that reused GameObject.
                eventSystem.SetSelectedGameObject(null);
            }

            SpellSelectionPanelMultilineBind(_panel, _panel.Caster, callback, actionType, cantripOnly);
            if (enabled && _lineTables is { Length: > 0 })
            {
                SetFirstVisibleRow(firstRow);
            }

            if (eventSystem && selectedSpell != null)
            {
                var reboundBox = _panel.GetComponentsInChildren<SpellActivationBox>(true).FirstOrDefault(box =>
                    box.GuiSpellDefinition?.SpellDefinition == selectedSpell &&
                    IsSameSelectionSource(selectedRepertoire,
                        SpellActionTypeContext.GetRepertoireLine(box)?.spellRepertoire ?? box.spellRepertoire));
                if (reboundBox)
                {
                    _lastSelection = reboundBox.button.gameObject;
                    eventSystem.SetSelectedGameObject(_lastSelection);
                    if (enabled)
                    {
                        RevealSelection(_lastSelection);
                    }
                }
            }
        }

        private void RefreshLocalizedHeadings()
        {
            _language = I2.Loc.LocalizationManager.CurrentLanguageCode;
            foreach (var row in _lineTables)
            {
                foreach (var section in GetSpellSelectionSourceSections(row))
                {
                    var line = section.GetComponentsInChildren<SpellRepertoireLine>(true).FirstOrDefault();
                    var heading = GetSpellSelectionSourceHeading(section);
                    if (line && heading)
                    {
                        foreach (var boundLine in section.GetComponentsInChildren<SpellRepertoireLine>(true))
                        {
                            foreach (var level in boundLine.SpellsByLevelBoxes)
                            {
                                SlotStatusTablePatcher.RefreshCantripCaption(
                                    level.GetComponentInChildren<SlotStatusTable>(true), boundLine.minSpellLevel);
                            }
                        }

                        RefreshSpellSelectionRowHeading(section, heading, line,
                            line.GetComponent<SpellSelectionLineLayoutState>().MaximumWidth);
                    }
                }

                foreach (var box in row.GetComponentsInChildren<SpellActivationBox>(true))
                {
                    SpellActivationBoxPatcher.RefreshUpcastTooltip(box);
                }
            }

            // Repack display sections when translated titles require a different number of
            // card columns. Native cards, source owners and selected controls remain intact.
            var selection = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            var firstSection = GetSpellSelectionSourceSections(_lineTables[_firstVisibleRow]).FirstOrDefault();
            var maximumWidth = _lineTables.SelectMany(row => row.GetComponentsInChildren<SpellRepertoireLine>(true))
                .Select(line => line.GetComponent<SpellSelectionLineLayoutState>().MaximumWidth).First();
            PackSpellSelectionSourceSections(_holder, maximumWidth);
            _lineTables = GetSpellSelectionLineTables(_holder);
            var firstIndex = Array.FindIndex(_lineTables, row => firstSection && firstSection.IsChildOf(row));
            _firstVisibleRow = Mathf.Max(0, firstIndex);
            SetAllSpellSelectionLineTablesVisible(_holder, true);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_holder);
            _rowHeights = _lineTables.Select(GetSpellSelectionLineTableHeight).ToArray();
            _contentWidth = _lineTables.Max(GetSpellSelectionLineTableWidth);
            EnsureControls();
            // Translate and remeasure the display only. The same native columns, spell callbacks,
            // resource owners, focused GameObject and first row remain in place.
            ApplyVisibleRows();
            RevealSelection(selection);
        }

        private static bool IsSameSelectionSource(RulesetSpellRepertoire original, RulesetSpellRepertoire rebound)
        {
            return original == rebound ||
                   SpellSelectionContext.TryGetOption(original, out var originalOption) &&
                   SpellSelectionContext.TryGetOption(rebound, out var reboundOption) &&
                   originalOption.Kind == reboundOption.Kind && originalOption.Repertoire == reboundOption.Repertoire &&
                   originalOption.CastingRepertoire == reboundOption.CastingRepertoire &&
                   originalOption.SlotLevel == reboundOption.SlotLevel && originalOption.Spell == reboundOption.Spell;
        }

        private void Update()
        {
            if (!CanPage())
            {
                return;
            }

            // Native keyboard/gamepad navigation can select an off-page spell. Keep that selected
            // control visible without replacing native navigation or invoking a spell callback.
            var selection = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            if (selection != _lastSelection)
            {
                _lastSelection = selection;
                RevealSelection(selection);
            }

            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.pageDownKey.wasPressedThisFrame && !keyboard.pageUpKey.wasPressedThisFrame ||
                !(IsPointerInsidePanel() || selection && selection.transform.IsChildOf(_panel.transform)))
            {
                return;
            }

            if (keyboard.pageDownKey.wasPressedThisFrame)
            {
                MoveRows(Mathf.Max(1, _visibleRows));
            }
            else if (keyboard.pageUpKey.wasPressedThisFrame)
            {
                MoveRows(-Mathf.Max(1, _visibleRows));
            }
        }

        private void RevealSelection(GameObject selection)
        {
            if (!selection)
            {
                return;
            }

            for (var index = 0; index < _lineTables.Length; index++)
            {
                if (!selection.transform.IsChildOf(_lineTables[index]))
                {
                    continue;
                }

                if (index < _firstVisibleRow || index >= _firstVisibleRow + _visibleRows)
                {
                    _firstVisibleRow = index;
                    ApplyVisibleRows();
                }

                return;
            }
        }

        private void EnsureControls()
        {
            if (_controls)
            {
                var source = _lineTables.SelectMany(table => table.GetComponentsInChildren<GuiLabel>(true))
                    .FirstOrDefault(label => label.TMP_Text && label.TMP_Text.font);
                if (source)
                {
                    foreach (var text in _controls.GetComponentsInChildren<TMP_Text>(true))
                    {
                        text.font = source.TMP_Text.font;
                        text.fontSharedMaterial = source.TMP_Text.fontSharedMaterial;
                    }
                }

                return;
            }

            // Use the character screen's game-art scrollbar, never an arbitrary loaded
            // Unity/UMM settings control. Native scrollbars do not have a root Image.
            var inspection = Gui.GuiService?.GetScreen<CharacterInspectionScreen>();
            _nativeScrollbar = inspection ? inspection.GetComponentsInChildren<Scrollbar>(true)
                .FirstOrDefault(scrollbar => scrollbar.handleRect &&
                    scrollbar.handleRect.GetComponent<Image>()?.sprite?.name == "ScrollThumbVertical") : null;
            _controls = CreateControlRect("SpellSelectionNavigation", _panel.transform);
            _controls.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _controls.anchorMin = Vector2.zero;
            _controls.anchorMax = Vector2.one;
            _controls.offsetMin = _controls.offsetMax = Vector2.zero;
            _previous = CreatePageButton("PreviousSpellRows", "▲", true);
            _next = CreatePageButton("NextSpellRows", "▼", false);
            _previous.onClick.AddListener(() => MoveRows(-1));
            _next.onClick.AddListener(() => MoveRows(1));

            var bar = CreateControlRect("SpellSelectionScrollbar", _controls);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = Vector2.one;
            bar.offsetMin = new Vector2(-SpellSelectionControlsWidth + 12f, 36f);
            bar.offsetMax = new Vector2(-12f, -36f);
            var background = bar.gameObject.AddComponent<Image>();
            background.color = new Color(0.196f, 0.200f, 0.204f, 0.75f);
            if (_nativeScrollbar)
            {
                var nativeFill = _panel.spellRepertoireSecondaryLine.repertoireHeader.GetComponent<Image>();
                if (nativeFill)
                {
                    SpellSelectionPanelLayoutState.CopyNativePanelImage(background, nativeFill);
                }
            }
            background.raycastTarget = true;
            var handle = CreateControlRect("Handle", bar);
            handle.anchorMin = Vector2.zero;
            handle.anchorMax = Vector2.one;
            handle.offsetMin = handle.offsetMax = Vector2.zero;
            var image = handle.gameObject.AddComponent<Image>();
            image.color = new Color(0.8f, 0.64f, 0.48f, 1f);
            if (_nativeScrollbar)
            {
                SpellSelectionPanelLayoutState.CopyNativePanelImage(image, _nativeScrollbar.handleRect.GetComponent<Image>());
            }

            _scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            _scrollbar.direction = Scrollbar.Direction.TopToBottom;
            _scrollbar.handleRect = handle;
            _scrollbar.targetGraphic = image;
            if (_nativeScrollbar)
            {
                _scrollbar.transition = _nativeScrollbar.transition;
                _scrollbar.colors = _nativeScrollbar.colors;
                _scrollbar.spriteState = _nativeScrollbar.spriteState;
            }

            _scrollbar.onValueChanged.AddListener(value =>
            {
                if (_updatingControls)
                {
                    return;
                }

                SetFirstVisibleRow(Mathf.RoundToInt(value * GetMaxFirstVisibleRow()));
            });


        }

        private RectTransform CreateControlRect(string name, Transform parent)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.gameObject.layer = _holder.gameObject.layer;
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            return rect;
        }

        private TMP_Text CreateControlText(RectTransform rect, string value)
        {
            var text = CreateSpellSelectionText(rect);
            var source = _lineTables.SelectMany(table => table.GetComponentsInChildren<GuiLabel>(true))
                .FirstOrDefault(label => label.TMP_Text && label.TMP_Text.font);
            if (source)
            {
                text.font = source.TMP_Text.font;
                text.fontSharedMaterial = source.TMP_Text.fontSharedMaterial;
            }

            text.text = value;
            text.fontSize = 16f;
            text.enableAutoSizing = true;
            text.fontSizeMin = 11f;
            text.fontSizeMax = 16f;
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private Button CreatePageButton(string name, string label, bool top)
        {
            var rect = CreateControlRect(name, _controls);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, top ? 1f : 0f);
            rect.pivot = new Vector2(1f, top ? 1f : 0f);
            rect.sizeDelta = new Vector2(SpellSelectionControlsWidth - 4f, 32f);
            rect.anchoredPosition = new Vector2(-2f, top ? -2f : 2f);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(0.196f, 0.200f, 0.204f, 0.75f);
            if (_nativeScrollbar)
            {
                var nativeFill = _panel.spellRepertoireSecondaryLine.repertoireHeader.GetComponent<Image>();
                if (nativeFill)
                {
                    SpellSelectionPanelLayoutState.CopyNativePanelImage(image, nativeFill);
                }
            }

            image.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = colors.selectedColor = image.color * 1.25f;
            colors.pressedColor = image.color * 0.8f;
            colors.disabledColor = new Color(image.color.r, image.color.g, image.color.b, image.color.a * 0.4f);
            button.colors = colors;
            var text = CreateControlRect("Label", rect);
            text.anchorMin = Vector2.zero;
            text.anchorMax = Vector2.one;
            text.offsetMin = text.offsetMax = Vector2.zero;
            CreateControlText(text, label);
            return button;
        }

        private void OnDisable()
        {
            if (ActiveSpellSelectionLinePager == this)
            {
                ActiveSpellSelectionLinePager = null;
            }
        }

        private void OnEnable()
        {
            if (_panel && _lineTables is { Length: > 0 })
            {
                // Native callers briefly deactivate the bound panel before showing it. Restore
                // ownership immediately; the wheel guard independently checks current visibility.
                ActiveSpellSelectionLinePager = this;
            }
        }

        private void OnDestroy()
        {
            if (ActiveSpellSelectionLinePager == this)
            {
                ActiveSpellSelectionLinePager = null;
            }
        }

        internal bool ShouldSuppressBackgroundWheel()
        {
            return IsPointerInsidePanel() &&
                   !FloatingPanelBounds.ShouldSuppressBackgroundWheel(null);
        }

        internal bool IsForegroundControl(Component source)
        {
            if (!source)
            {
                return false;
            }

            if (source.transform.IsChildOf(_panel.transform))
            {
                return true;
            }

            var sourceCanvas = source.GetComponentsInParent<Canvas>(true)
                .FirstOrDefault(canvas => canvas.isRootCanvas || canvas.overrideSorting);
            var pickerCanvas = _panel.GetComponentsInParent<Canvas>(true)
                .FirstOrDefault(canvas => canvas.isRootCanvas || canvas.overrideSorting);
            if (!sourceCanvas || !pickerCanvas)
            {
                return false;
            }

            var sourceLayer = SortingLayer.GetLayerValueFromID(sourceCanvas.sortingLayerID);
            var pickerLayer = SortingLayer.GetLayerValueFromID(pickerCanvas.sortingLayerID);
            if (sourceLayer != pickerLayer)
            {
                return sourceLayer > pickerLayer;
            }

            if (sourceCanvas.sortingOrder != pickerCanvas.sortingOrder)
            {
                return sourceCanvas.sortingOrder > pickerCanvas.sortingOrder;
            }

            if (sourceCanvas != pickerCanvas)
            {
                return sourceCanvas.renderOrder > pickerCanvas.renderOrder;
            }

            // Screens in the same native canvas follow transform order, rather than the
            // GuiService's registry enumeration. Compare the branches below their common parent.
            var sourceBranch = source.transform;
            while (sourceBranch && sourceBranch.parent)
            {
                var pickerBranch = _panel.transform;
                while (pickerBranch && pickerBranch.parent)
                {
                    if (sourceBranch.parent == pickerBranch.parent)
                    {
                        return sourceBranch.GetSiblingIndex() > pickerBranch.GetSiblingIndex();
                    }

                    pickerBranch = pickerBranch.parent;
                }

                sourceBranch = sourceBranch.parent;
            }

            return false;
        }

        internal bool RouteWheel(float delta)
        {
            return RouteWheelAt(delta, GetPointerPosition());
        }

        internal bool RouteWheelAt(float delta, Vector2 position)
        {
            if (!CanInteract() || !ContainsPointer(position) || !OwnsForegroundInputAt(position) ||
                FloatingPanelBounds.ShouldSuppressBackgroundWheel(null))
            {
                return false;
            }

            if (Mathf.Abs(delta) > 0.001f && _lastWheelInputFrame != Time.frameCount)
            {
                CaptureWheelInput();
                if (CanPage())
                {
                    MoveRows(delta < 0f ? 1 : -1);
                }
            }

            return true;
        }

        private bool CanInteract()
        {
            return isActiveAndEnabled &&
                   _panel && _panel.isActiveAndEnabled && _panel.Visible && !_panel.Hiding &&
                   _holder && _holder.gameObject.activeInHierarchy &&
                   _lineTables is { Length: > 0 } && _visibleRows > 0;
        }

        private bool CanPage()
        {
            return CanInteract() && !_refreshPending && _lineTables.Length > _visibleRows;
        }

        private bool OwnsForegroundInput()
        {
            return OwnsForegroundInputAt(GetPointerPosition());
        }

        private bool OwnsForegroundInputAt(Vector2 position)
        {
            var eventSystem = EventSystem.current;
            if (!eventSystem)
            {
                return true;
            }

            if (_pointerEventSystem != eventSystem)
            {
                _pointerEventSystem = eventSystem;
                _pointerEvent = new PointerEventData(eventSystem);
            }
            _pointerEvent.position = position;
            _pointerHits.Clear();
            eventSystem.RaycastAll(_pointerEvent, _pointerHits);
            var hit = _pointerHits.FirstOrDefault(result => result.gameObject);
            // A modal or a scrollable tooltip above the picker retains ownership of its input.
            return !hit.gameObject || hit.gameObject.transform.IsChildOf(_panel.transform);
        }

        internal void InvalidateHud(Component owner)
        {
            if (_panelLayout)
            {
                _panelLayout.InvalidateHud(owner);
            }
        }

        private void CaptureWheelInput()
        {
            ActiveSpellSelectionLinePager = this;
            _lastWheelInputFrame = Time.frameCount;
        }

        private bool IsPointerInsidePanel()
        {
            return CanInteract() && ContainsPointer(GetPointerPosition()) && OwnsForegroundInput();
        }

        private bool ContainsPointer(Vector2 position)
        {
            if (RectTransformUtility.RectangleContainsScreenPoint(_panel.RectTransform, position, _pointerCamera))
            {
                return true;
            }

            // Native close/abort buttons can extend beyond the card body's rectangle.
            return _pointerControls != null && _pointerControls.Any(control => control &&
                control.gameObject.activeInHierarchy &&
                RectTransformUtility.RectangleContainsScreenPoint(control, position, _pointerCamera));
        }

        private static Vector2 GetPointerPosition()
        {
            // Keep native gamepad cursor/focus semantics. Mouse input uses the same existing
            // Input System device as our keyboard paging, with the native legacy fallback.
            if (Gui.GamepadActive && Gui.InputService != null)
            {
                return Gui.InputService.InputPointerPosition;
            }

            return Mouse.current != null ? Mouse.current.position.ReadValue() : (Vector2)UnityEngine.Input.mousePosition;
        }

        private int GetMaxFirstVisibleRow()
        {
            if (_lineTables == null)
            {
                return 0;
            }

            // The final page uses the actual row heights too, so its scrollbar endpoint never
            // leaves a blank page or hides the last source behind a taller preceding row.
            var first = _lineTables.Length - 1;
            var height = _rowHeights[first];
            var available = Mathf.Max(1f, _safeCanvasBounds.height - 2f * SpellSelectionBodyPadding);
            while (first > 0 && height + _spacing + _rowHeights[first - 1] <= available)
            {
                first--;
                height += _spacing + _rowHeights[first];
            }

            return first;
        }

        private float GetAverageRowHeight()
        {
            return _rowHeights is { Length: > 0 } ? Mathf.Max(1f, _rowHeights.Average()) : 1f;
        }

        private void MoveRows(int deltaRows)
        {
            var nextFirstVisibleRow = Mathf.Clamp(_firstVisibleRow + deltaRows, 0, GetMaxFirstVisibleRow());

            if (nextFirstVisibleRow == _firstVisibleRow)
            {
                return;
            }

            _firstVisibleRow = nextFirstVisibleRow;
            ApplyVisibleRows();
        }

        private void ApplyVisibleRows()
        {
            if (_lineTables == null)
            {
                return;
            }

            _firstVisibleRow = Mathf.Clamp(_firstVisibleRow, 0, GetMaxFirstVisibleRow());
            RefreshVisibleSourceHeadings();
            var availableHeight = Mathf.Max(1f, _safeCanvasBounds.height - 2f * SpellSelectionBodyPadding);
            _visibleRows = CalculateVisibleSpellSelectionRows(_rowHeights.Skip(_firstVisibleRow).ToArray(),
                _spacing, availableHeight);
            var lastVisibleRow = Mathf.Min(_lineTables.Length, _firstVisibleRow + _visibleRows);
            _controls.gameObject.SetActive(_lineTables.Length > _visibleRows);

            for (var index = 0; index < _lineTables.Length; index++)
            {
                SetSpellSelectionLineTableVisible(
                    _lineTables[index],
                    index >= _firstVisibleRow && index < lastVisibleRow);
            }

            var visibleHeight = GetVisibleSpellSelectionRowsHeight(
                _rowHeights,
                _spacing,
                _firstVisibleRow,
                _visibleRows);
            // Sparse feat pages need only their own cards and titles, not the widest class
            // on another page. The navigation frame follows the visible content's right edge.
            _contentWidth = _lineTables.Skip(_firstVisibleRow).Take(_visibleRows)
                .Max(GetSpellSelectionLineTableWidth);
            RefreshSpellSelectionPanelSize(
                _panel,
                _holder,
                _contentWidth + (_controls.gameObject.activeSelf ? SpellSelectionControlsWidth : 0f),
                visibleHeight,
                _safeCanvasBounds,
                _canvasRect);
            ArrangeControls(lastVisibleRow);
            _previous.interactable = _firstVisibleRow > 0;
            _next.interactable = _firstVisibleRow < GetMaxFirstVisibleRow();
            _updatingControls = true;
            _scrollbar.size = (float)_visibleRows / _lineTables.Length;
            _scrollbar.value = GetMaxFirstVisibleRow() > 0 ? (float)_firstVisibleRow / GetMaxFirstVisibleRow() : 0f;
            _updatingControls = false;
        }

        private void RefreshVisibleSourceHeadings()
        {
            var seenSources = new HashSet<object>();
            for (var index = 0; index < _lineTables.Length; index++)
            {
                if (index == _firstVisibleRow)
                {
                    // The first visible continuation identifies its source even when its
                    // previous section is on another page.
                    seenSources.Clear();
                }

                var row = _lineTables[index];
                var changed = false;
                foreach (var section in GetSpellSelectionSourceSections(row))
                {
                    var line = section.GetComponentsInChildren<SpellRepertoireLine>(true).First();
                    var heading = GetSpellSelectionSourceHeading(section);
                    var visible = seenSources.Add(GetSpellSelectionSourceKey(line.spellRepertoire));
                    if (heading.gameObject.activeSelf != visible)
                    {
                        heading.gameObject.SetActive(visible);
                        changed = true;
                    }
                }

                if (changed)
                {
                    ArrangeSpellSelectionRow(row);
                    _rowHeights[index] = GetSpellSelectionLineTableHeight(row);
                }
            }
        }

        private void ArrangeControls(int lastVisibleRow)
        {
            var panelRect = _panel.RectTransform;
            var hasContent = false;
            var contentBounds = Rect.zero;
            for (var index = _firstVisibleRow; index < lastVisibleRow; index++)
            {
                var rectangles = _lineTables[index].GetComponentsInChildren<SpellsByLevelBox>(true)
                    .Where(level => IsActiveWithin(level.transform, _panel.transform))
                    .Select(level => level.RectTransform)
                    .Concat(_lineTables[index].GetComponentsInChildren<TMP_Text>(true)
                        .Where(text => text.name == "SpellSourceHeading" &&
                                       IsActiveWithin(text.transform, _panel.transform))
                        .Select(text => text.rectTransform));
                foreach (var rectangle in rectangles)
                {
                    if (TryGetCanvasLocalBounds(rectangle, panelRect, out var bounds))
                    {
                        contentBounds = hasContent ? UnionBounds(contentBounds, bounds) : bounds;
                        hasContent = true;
                    }
                }
            }

            if (!hasContent)
            {
                return;
            }

            _controls.anchorMin = _controls.anchorMax = Vector2.zero;
            _controls.pivot = Vector2.zero;
            _controls.sizeDelta = new Vector2(Mathf.Max(contentBounds.width, _contentWidth) + SpellSelectionControlsWidth,
                contentBounds.height);
            _controls.anchoredPosition = new Vector2(contentBounds.xMin - panelRect.rect.xMin,
                contentBounds.yMin - panelRect.rect.yMin);

            if (!_canvasRect || _safeCanvasBounds.width <= 1f || _safeCanvasBounds.height <= 1f ||
                !TryGetCanvasLocalBounds(panelRect, _canvasRect, out var totalBounds))
            {
                return;
            }

            if (TryGetCanvasLocalBounds(_controls, _canvasRect, out var controlsBounds))
            {
                totalBounds = UnionBounds(totalBounds, controlsBounds);
            }

            // Native cancel/close controls can intentionally extend outside the panel rectangle.
            // Keep that whole popup visible, including the new navigation frame.
            foreach (var button in _panel.GetComponentsInChildren<Button>(true))
            {
                if (!IsActiveWithin(button.transform, _panel.transform) ||
                    _lineTables.Any(table => button.transform.IsChildOf(table) &&
                                             table.GetComponent<CanvasGroup>().alpha <= 0f) ||
                    !TryGetCanvasLocalBounds((RectTransform)button.transform, _canvasRect, out var buttonBounds))
                {
                    continue;
                }

                totalBounds = UnionBounds(totalBounds, buttonBounds);
            }

            var delta = new Vector2(
                totalBounds.xMin < _safeCanvasBounds.xMin ? _safeCanvasBounds.xMin - totalBounds.xMin :
                totalBounds.xMax > _safeCanvasBounds.xMax ? _safeCanvasBounds.xMax - totalBounds.xMax : 0f,
                totalBounds.yMin < _safeCanvasBounds.yMin ? _safeCanvasBounds.yMin - totalBounds.yMin :
                totalBounds.yMax > _safeCanvasBounds.yMax ? _safeCanvasBounds.yMax - totalBounds.yMax : 0f);
            panelRect.position += _canvasRect.TransformVector(new Vector3(delta.x, delta.y, 0f));
        }

        private static Rect UnionBounds(Rect left, Rect right)
        {
            return Rect.MinMaxRect(Mathf.Min(left.xMin, right.xMin), Mathf.Min(left.yMin, right.yMin),
                Mathf.Max(left.xMax, right.xMax), Mathf.Max(left.yMax, right.yMax));
        }
    }

    private static void ReleaseSpellSelectionRowLines(RectTransform row)
    {
        foreach (var line in row.GetComponentsInChildren<SpellRepertoireLine>(true))
        {
            Gui.GuiService.PrefabPoolManager.ReturnElement(line.gameObject);
        }
    }

    private static RectTransform AddActiveSpellsToLine(
        SpellSelectionPanel __instance,
        SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
        ActionDefinitions.ActionType actionType,
        bool cantripOnly,
        ICollection<SpellRepertoireLine> spellRepertoireLines,
        RectTransform spellRepertoireLinesTable,
        SlotAdvancementPanel slotAdvancementPanel,
        RulesetSpellRepertoire rulesetSpellRepertoire,
        int startLevel,
        int level)
    {
        var parent = spellRepertoireLinesTable.parent;
        spellRepertoireLinesTable = new GameObject("SpellSelectionSourceSection", typeof(RectTransform))
            .GetComponent<RectTransform>();
        spellRepertoireLinesTable.gameObject.layer = __instance.gameObject.layer;
        spellRepertoireLinesTable.SetParent(parent, false);
        spellRepertoireLinesTable.localScale = Vector3.one;
        var rowLayout = spellRepertoireLinesTable.gameObject.AddComponent<HorizontalLayoutGroup>();
        rowLayout.childAlignment = TextAnchor.UpperLeft;
        rowLayout.childForceExpandWidth = rowLayout.childForceExpandHeight = false;
        rowLayout.childControlWidth = rowLayout.childControlHeight = true;
        rowLayout.spacing = SpellSelectionCardGap;
        var fitter = spellRepertoireLinesTable.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        SpellLineTables.Add(spellRepertoireLinesTable);

        var curLine = SetUpNewLine(0, spellRepertoireLinesTable, spellRepertoireLines, __instance);

        curLine.Bind(
            __instance.Caster,
            rulesetSpellRepertoire,
            true,
            spellCastEngaged,
            slotAdvancementPanel,
            actionType,
            cantripOnly,
            startLevel,
            level,
            false);

        return spellRepertoireLinesTable;
    }

    private static (object Source, object Owner, string Ability, SpellCastingResourceContext.ResourceKind? Kind)
        GetSpellSelectionSourceKey(RulesetSpellRepertoire repertoire)
    {
        if (SpellSelectionContext.TryGetOption(repertoire, out var option))
        {
            return (option.Repertoire, option.CastingRepertoire,
                repertoire.SpellCastingAbility, option.Kind);
        }

        var feature = repertoire.SpellCastingFeature;
        var tag = feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>()?.Name;
        if (!string.IsNullOrEmpty(tag))
        {
            return (Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(tag),
                repertoire.GetCastingClass(), repertoire.SpellCastingAbility, null);
        }

        return ((object)repertoire.SpellCastingSubclass ?? repertoire.SpellCastingClass ??
                (object)repertoire.SpellCastingRace ?? feature,
            null, repertoire.SpellCastingAbility, null);
    }

    private static string GetSpellSelectionSourceTitle(RulesetSpellRepertoire repertoire)
    {
        if (SpellSelectionContext.TryGetOption(repertoire, out var option))
        {
            return option.SourceTitle;
        }

        var tag = repertoire.SpellCastingFeature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>()?.Name;
        if (!string.IsNullOrEmpty(tag))
        {
            var sourceTag = Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(tag);
            var owner = repertoire.GetCastingClass();
            if (owner && RulesetSpellRepertoirePatcher.TryLocalizeSpellSourceTitle(
                    sourceTag + owner.Name, out var variantTitle, out _))
            {
                return variantTitle;
            }

            if (RulesetSpellRepertoirePatcher.TryLocalizeSpellSourceTitle(sourceTag, out var title, out _))
            {
                return owner
                    ? Gui.Format("Feat/&GeneralFeat2024VariantTitle", title, owner.FormatTitle())
                    : title;
            }
        }

        return repertoire.FormatHeader();
    }

    private static void GroupSpellSelectionSourceRows(SpellSelectionPanel panel, RectTransform holder,
        float maximumWidth)
    {
        var columns = panel.spellRepertoireLines.ToArray();
        foreach (var line in columns)
        {
            line.GetComponent<SpellSelectionLineLayoutState>()?.HideColumnHeading();
        }

        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(holder);
        foreach (var source in columns.GroupBy(line => GetSpellSelectionSourceKey(line.spellRepertoire)))
        {
            var sourceLevels = source.GroupBy(line => line.minSpellLevel)
                .ToDictionary(group => group.Key, group => group.ToArray());
            // Distinct native repertoires can share a title and level while owning different uses.
            // Keep those columns on separate rows without inserting other sources' empty levels.
            for (var sourceIndex = 0; sourceIndex < sourceLevels.Values.Max(lines => lines.Length); sourceIndex++)
            {
                var sourceColumns = sourceLevels.OrderBy(entry => entry.Key)
                    .Select(entry => entry.Value.ElementAtOrDefault(sourceIndex)).Where(line => line).ToArray();
                var ranges = new List<List<SpellRepertoireLine>>();
                var width = 0f;
                foreach (var line in sourceColumns)
                {
                    var columnWidth = Mathf.Min(maximumWidth, line.layoutGroup.preferredWidth);
                    line.GetComponent<SpellSelectionLineLayoutState>()?.SetColumnWidth(columnWidth);
                    if (ranges.Count == 0 || width + SpellSelectionCardGap + columnWidth > maximumWidth)
                    {
                        ranges.Add([]);
                        width = 0f;
                    }

                    width += (ranges[ranges.Count - 1].Count > 0 ? SpellSelectionCardGap : 0f) + columnWidth;
                    ranges[ranges.Count - 1].Add(line);
                }

                foreach (var range in ranges)
                {
                    var firstLine = range[0];
                    var row = (RectTransform)firstLine.transform.parent;
                    row.SetAsLastSibling();
                    foreach (var line in range)
                    {
                        var candidate = (RectTransform)line.transform.parent;
                        line.transform.SetParent(row, false);
                        line.transform.SetAsLastSibling();
                        if (candidate != row)
                        {
                            SpellLineTables.Remove(candidate);
                            candidate.gameObject.SetActive(false);
                            candidate.SetParent(null);
                            Object.Destroy(candidate.gameObject);
                        }
                    }

                    foreach (var line in range)
                    {
                        line.GetComponent<SpellSelectionLineLayoutState>().SourceWraps = ranges.Count > 1;
                    }

                    SetSpellSelectionRowHeading(row, firstLine, maximumWidth);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(row);
                }
            }
        }
        PackSpellSelectionSourceSections(holder, maximumWidth);
    }

    private static RectTransform[] GetSpellSelectionSourceSections(RectTransform row)
    {
        return row.name == "SpellSelectionSourceSection" ? [row] : row.Cast<Transform>()
            .OfType<RectTransform>().Where(child => child.name == "SpellSelectionSourceSection").ToArray();
    }

    private static TMP_Text GetSpellSelectionSourceHeading(RectTransform section)
    {
        return section.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(text => text.transform.parent == section && text.name == "SpellSourceHeading");
    }

    private static void PackSpellSelectionSourceSections(RectTransform holder, float maximumWidth)
    {
        var oldRows = GetSpellSelectionLineTables(holder);
        var sections = oldRows.SelectMany(GetSpellSelectionSourceSections).ToArray();
        foreach (var section in sections)
        {
            section.SetParent(holder, false);
            SpellLineTables.Remove(section);
        }

        foreach (var oldRow in oldRows.Where(row => row.name == "SpellSelectionSourceRow"))
        {
            SpellLineTables.Remove(oldRow);
            oldRow.gameObject.SetActive(false);
            oldRow.SetParent(null);
            Object.Destroy(oldRow.gameObject);
        }

        RectTransform physicalRow = null;
        var physicalRows = new List<RectTransform>();
        var rowWidth = 0f;
        var acceptsSections = false;
        foreach (var section in sections)
        {
            var lines = section.GetComponentsInChildren<SpellRepertoireLine>(true);
            var singleCardRow = lines.SelectMany(line => line.SpellsByLevelBoxes)
                .All(level =>
                {
                    var cards = level.GetComponentsInChildren<SpellActivationBox>(true)
                        .Count(box => box.gameObject.activeSelf && box.GuiSpellDefinition != null);
                    var grid = level.spellsTable.GetComponent<GridLayoutGroup>();
                    return grid && cards <= grid.constraintCount;
                });
            // Keep a wrapped or tall source together. Every compact acquisition, including
            // racial and class-granted spells, otherwise participates in the same flow.
            var compact = !lines.Any(line => line.GetComponent<SpellSelectionLineLayoutState>().SourceWraps) && singleCardRow;
            var heading = GetSpellSelectionSourceHeading(section);
            heading.gameObject.SetActive(true);
            ArrangeSpellSelectionSourceSection(section, heading.rectTransform.rect.height);
            var width = LayoutUtility.GetPreferredWidth(section);
            if (!physicalRow || !acceptsSections || !compact ||
                rowWidth + SpellSelectionCardGap + width > maximumWidth)
            {
                physicalRow = (RectTransform)new GameObject("SpellSelectionSourceRow", typeof(RectTransform)).transform;
                physicalRow.gameObject.layer = holder.gameObject.layer;
                physicalRow.SetParent(holder, false);
                physicalRow.localScale = Vector3.one;
                physicalRow.gameObject.AddComponent<LayoutElement>();
                SpellLineTables.Add(physicalRow);
                physicalRows.Add(physicalRow);
                rowWidth = 0f;
                acceptsSections = compact;
            }

            section.SetParent(physicalRow, false);
            rowWidth += (rowWidth > 0f ? SpellSelectionCardGap : 0f) + width;
        }

        // Appending another source changes the shared height of its whole row.
        // Arrange each completed row once instead of revisiting its earlier sources.
        foreach (var row in physicalRows)
        {
            ArrangeSpellSelectionRow(row);
        }
    }

    private static void ArrangeSpellSelectionRow(RectTransform row)
    {
        var sections = GetSpellSelectionSourceSections(row);
        var lines = sections.SelectMany(section => section.GetComponentsInChildren<SpellRepertoireLine>(true)).ToArray();
        var gridHeight = lines.Select(line => line.GetComponent<SpellSelectionLineLayoutState>().ResetRowGridHeight())
            .DefaultIfEmpty(0f).Max();
        var headingHeight = sections.Select(GetSpellSelectionSourceHeading)
            .Where(heading => heading && heading.gameObject.activeSelf)
            .Select(heading => heading.rectTransform.rect.height).DefaultIfEmpty(0f).Max();
        row.anchorMin = row.anchorMax = row.pivot = new Vector2(0f, 1f);
        var left = 0f;
        var height = 0f;
        foreach (var section in sections)
        {
            ArrangeSpellSelectionSourceSection(section, headingHeight, gridHeight);
            section.anchorMin = section.anchorMax = section.pivot = new Vector2(0f, 1f);
            section.anchoredPosition = new Vector2(left, 0f);
            left += LayoutUtility.GetPreferredWidth(section) + SpellSelectionCardGap;
            height = Mathf.Max(height, LayoutUtility.GetPreferredHeight(section));
        }

        var layout = row.GetComponent<LayoutElement>();
        layout.preferredWidth = Mathf.Max(0f, left - SpellSelectionCardGap);
        layout.preferredHeight = height;
        row.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, layout.preferredWidth);
        row.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, layout.preferredHeight);
    }

    private static void SetSpellSelectionRowHeading(RectTransform row, SpellRepertoireLine line, float maximumWidth)
    {
        var headingRect = (RectTransform)new GameObject("SpellSourceHeading", typeof(RectTransform)).transform;
        headingRect.gameObject.layer = row.gameObject.layer;
        headingRect.SetParent(row, false);
        headingRect.anchorMin = new Vector2(0f, 1f);
        headingRect.anchorMax = Vector2.one;
        headingRect.pivot = new Vector2(0f, 1f);
        headingRect.offsetMin = new Vector2(0f, -26f);
        headingRect.offsetMax = new Vector2(0f, -2f);
        headingRect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        var heading = CreateSpellSelectionText(headingRect);
        RefreshSpellSelectionRowHeading(row, heading, line, maximumWidth);
    }

    private static void RefreshSpellSelectionRowHeading(RectTransform row, TMP_Text heading,
        SpellRepertoireLine line, float maximumWidth)
    {
        heading.font = line.headerLabel.TMP_Text.font;
        heading.fontSharedMaterial = line.headerLabel.TMP_Text.fontSharedMaterial;
        heading.text = GetSpellSelectionSourceTitle(line.spellRepertoire);
        heading.fontSize = heading.fontSizeMin = heading.fontSizeMax = 18f;
        heading.enableAutoSizing = false;
        heading.alignment = TextAlignmentOptions.Left;
        heading.enableWordWrapping = true;
        heading.raycastTarget = false;
        heading.color = new Color(0.95f, 0.77f, 0.60f, 1f);
        var layout = row.GetComponent<LayoutElement>() ?? row.gameObject.AddComponent<LayoutElement>();
        // Allocate a whole number of native card columns for the natural title width.
        // Wrapping at an arbitrary small-card cap breaks localized class/source names.
        layout.minWidth = Mathf.Min(maximumWidth, heading.GetPreferredValues(heading.text).x);
        var headingHeight = Mathf.Max(28f, heading.GetPreferredValues(heading.text,
            Mathf.Max(1f, layout.minWidth), float.PositiveInfinity).y + 4f);
        heading.rectTransform.offsetMin = new Vector2(0f, -headingHeight + 2f);
        ArrangeSpellSelectionSourceSection(row, heading.rectTransform.rect.height);
    }

    private static Rect GetSpellSelectionLocalRectBounds(RectTransform rect, RectTransform relativeTo)
    {
        TryGetCanvasLocalBounds(rect, relativeTo, out var bounds);
        return bounds;
    }

    private static void ArrangeSpellSelectionSourceSection(RectTransform row, float headingHeight,
        float sharedGridHeight = -1f)
    {
        // Native repertoire roots contain anchored inner tables and empty side-header space.
        // Place their actual level/card bounds, rather than trusting the root's fitted rectangle.
        row.GetComponent<HorizontalLayoutGroup>().enabled = false;
        row.GetComponent<ContentSizeFitter>().enabled = false;
        row.pivot = new Vector2(0f, 1f);
        var children = row.Cast<Transform>().OfType<RectTransform>()
            .Where(child => child.GetComponent<SpellRepertoireLine>()).ToArray();
        var lines = children.Select(child => child.GetComponent<SpellRepertoireLine>()).ToArray();
        var gridHeight = sharedGridHeight >= 0f ? sharedGridHeight : lines
            .Select(line => line.GetComponent<SpellSelectionLineLayoutState>().ResetRowGridHeight()).DefaultIfEmpty(0f).Max();
        foreach (var line in lines)
        {
            line.RectTransform.anchorMin = line.RectTransform.anchorMax = new Vector2(0f, 1f);
            line.RectTransform.pivot = new Vector2(0f, 1f);
            line.RectTransform.anchoredPosition = Vector2.zero;
            line.GetComponent<SpellSelectionLineLayoutState>().AlignRowGridHeight(gridHeight);
        }

        var width = children.Sum(LayoutUtility.GetPreferredWidth) +
                    SpellSelectionCardGap * Mathf.Max(0, children.Length - 1);
        var layout = row.GetComponent<LayoutElement>();
        var grid = lines.SelectMany(line => line.SpellsByLevelBoxes)
            .Select(level => level.spellsTable.GetComponent<GridLayoutGroup>()).FirstOrDefault(value => value);
        var stride = grid ? grid.cellSize.x + SpellSelectionCardGap : 1f;
        var maximumWidth = lines.Select(line => line.GetComponent<SpellSelectionLineLayoutState>().MaximumWidth)
            .DefaultIfEmpty(float.MaxValue).Min();
        var sectionWidth = Mathf.Min(maximumWidth,
            Mathf.Ceil((Mathf.Max(width, layout.minWidth) + SpellSelectionCardGap) / stride) * stride - SpellSelectionCardGap);
        row.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, sectionWidth);
        var heights = new Dictionary<SpellRepertoireLine, float>();
        foreach (var line in children.Select(child => child.GetComponent<SpellRepertoireLine>()).Where(line => line))
        {
            var cards = line.GetComponentsInChildren<SpellActivationBox>(true)
                .Where(box => box.gameObject.activeSelf && box.GuiSpellDefinition != null).ToArray();
            // A native refresh can temporarily empty one level. The deferred panel rebind
            // removes it; keep that synchronous transition measurable without losing other sources.
            var top = cards.Select(box => GetSpellSelectionLocalRectBounds((RectTransform)box.transform, row).yMax)
                .DefaultIfEmpty(0f).Max();
            var bottom = line.SpellsByLevelBoxes.Select(level =>
                GetSpellSelectionLocalRectBounds(level.RectTransform, row).yMin).DefaultIfEmpty(top).Min();
            heights[line] = top - bottom;
        }

        layout.preferredWidth = sectionWidth;
        layout.preferredHeight = (headingHeight > 0f ? headingHeight + 4f : 0f) + heights.Values.DefaultIfEmpty(0f).Max();
        row.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, layout.preferredHeight);
        var left = row.rect.xMin;
        var desiredTop = row.rect.yMax - (headingHeight > 0f ? headingHeight + 4f : 0f);
        foreach (var child in children)
        {
            var line = child.GetComponent<SpellRepertoireLine>();
            if (line && line.SpellsByLevelBoxes.Count > 0)
            {
                var bounds = line.SpellsByLevelBoxes.Select(level =>
                    GetSpellSelectionLocalRectBounds(level.RectTransform, row)).ToArray();
                var top = line.GetComponentsInChildren<SpellActivationBox>(true).Where(box => box.gameObject.activeSelf && box.GuiSpellDefinition != null)
                    .Select(box => GetSpellSelectionLocalRectBounds((RectTransform)box.transform, row).yMax)
                    .DefaultIfEmpty(bounds.Max(bound => bound.yMax)).Max();
                line.RectTransform.anchoredPosition += new Vector2(left - bounds.Min(bound => bound.xMin), desiredTop - top);
            }

            left += LayoutUtility.GetPreferredWidth(child) + SpellSelectionCardGap;
        }
    }

    internal static void ConfigureMultilineSpellSelectionLine(
        SpellRepertoireLine line, float maximumWidth, float maximumHeight)
    {
        var state = line.GetComponent<SpellSelectionLineLayoutState>() ??
                    line.gameObject.AddComponent<SpellSelectionLineLayoutState>();
        state.Apply(line, maximumWidth, maximumHeight);
    }

    internal static void RestoreMultilineSpellSelectionLine(SpellRepertoireLine line)
    {
        line.GetComponent<SpellSelectionLineLayoutState>()?.Restore();
    }

    internal static IDisposable BeginMultilineSpellSelectionLineRefresh(SpellRepertoireLine line)
    {
        var state = line.GetComponent<SpellSelectionLineLayoutState>();
        var panel = line.GetComponentInParent<SpellSelectionPanel>();
        return !Main.Settings.DisableMultilineSpellOffering && state && state.IsApplied && panel &&
               panel.spellRepertoireLines.Contains(line)
            ? new SpellSelectionLineRefreshScope(line, state, panel)
            : null;
    }

    internal static void RebindMultilineSpellSelectionLine(SpellRepertoireLine line)
    {
        var refresh = ActiveSpellSelectionLineRefresh;
        if (refresh != null && refresh.Line == line && !Main.Settings.DisableMultilineSpellOffering)
        {
            ConfigureMultilineSpellSelectionLine(line, refresh.MaximumWidth, refresh.MaximumHeight);
            var row = line.transform.parent as RectTransform;
            var heading = row ? row.GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(text => text.transform.parent == row && text.name == "SpellSourceHeading") : null;
            if (heading)
            {
                // Refresh reuses the native column synchronously, before the source rows are
                // rebuilt in LateUpdate. Keep its shared heading throughout that transition.
                var state = line.GetComponent<SpellSelectionLineLayoutState>();
                state.HideColumnHeading();
                ArrangeSpellSelectionSourceSection(row, heading.gameObject.activeSelf ? heading.rectTransform.rect.height : 0f);
                if (row.parent is RectTransform physicalRow && physicalRow.name == "SpellSelectionSourceRow")
                {
                    ArrangeSpellSelectionRow(physicalRow);
                }
            }

            refresh.Applied = true;
        }
    }

    private sealed class SpellSelectionLineRefreshScope : IDisposable
    {
        private readonly SpellSelectionLineRefreshScope _previous;
        private readonly SpellSelectionPanel _panel;
        private readonly SpellDefinition _selectedSpell;
        private readonly RulesetSpellRepertoire _selectedRepertoire;
        private readonly GameObject _selectedControl;

        internal SpellSelectionLineRefreshScope(
            SpellRepertoireLine line, SpellSelectionLineLayoutState state, SpellSelectionPanel panel)
        {
            _previous = ActiveSpellSelectionLineRefresh;
            _panel = panel;
            Line = line;
            MaximumWidth = state.MaximumWidth;
            MaximumHeight = state.MaximumHeight;
            // Capture before native Refresh releases and reuses any selected button.
            var selection = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
            var selectedBox = selection && selection.transform.IsChildOf(panel.transform)
                ? selection.GetComponentInParent<SpellActivationBox>()
                : null;
            _selectedSpell = selectedBox ? selectedBox.GuiSpellDefinition?.SpellDefinition : null;
            _selectedRepertoire = selectedBox
                ? SpellActionTypeContext.GetRepertoireLine(selectedBox)?.spellRepertoire ?? selectedBox.spellRepertoire
                : null;
            _selectedControl = selectedBox ? selection : null;
            ActiveSpellSelectionLineRefresh = this;
        }

        internal SpellRepertoireLine Line { get; }
        internal float MaximumWidth { get; }
        internal float MaximumHeight { get; }
        internal bool Applied { get; set; }

        public void Dispose()
        {
            ActiveSpellSelectionLineRefresh = _previous;
            if (!Applied || !_panel)
            {
                return;
            }

            var holder = _panel.spellRepertoireLinesTable.parent as RectTransform;
            if (!holder)
            {
                return;
            }

            var pager = holder.GetComponent<SpellSelectionLinePager>() ??
                        holder.gameObject.AddComponent<SpellSelectionLinePager>();
            // Native multicast refresh is still invoking the old rows. Rebuild once after that
            // invocation completes, so removed rows are never rebound with a cleared repertoire.
            pager.RequestRebind(_panel, Line.spellCastEngaged, Line.actionType, Line.cantripOnly,
                _selectedSpell, _selectedRepertoire, _selectedControl);
        }
    }

    private sealed class SpellSelectionLineLayoutState : MonoBehaviour
    {
        private SpellRepertoireLine _line;
        private TMP_Text _heading;
        private int _leftPadding;
        private int _topPadding;
        private TextAnchor _levelAlignment;
        private Vector2 _rootAnchorMin, _rootAnchorMax, _rootPivot, _rootPosition;
        private bool _headerActive;
        private LayoutElement _layout;
        private bool _createdLayout;
        private float _minimumWidth;
        private float _preferredWidth;
        private readonly List<(GridLayoutGroup Grid, GridLayoutGroup.Constraint Constraint, int Count,
            TextAnchor Alignment, GridLayoutGroup.Corner Corner, GridLayoutGroup.Axis Axis, int BottomPadding,
            Vector2 Spacing)> _grids = [];

        internal bool IsApplied => _line;
        internal float MaximumWidth { get; private set; }
        internal float MaximumHeight { get; private set; }
        internal bool SourceWraps { get; set; }

        internal void Apply(SpellRepertoireLine line, float maximumWidth, float maximumHeight)
        {
            Restore();
            _line = line;
            MaximumWidth = maximumWidth;
            MaximumHeight = maximumHeight;
            _leftPadding = line.layoutGroup.padding.left;
            _topPadding = line.layoutGroup.padding.top;
            _levelAlignment = line.layoutGroup.childAlignment;
            _rootAnchorMin = line.RectTransform.anchorMin;
            _rootAnchorMax = line.RectTransform.anchorMax;
            _rootPivot = line.RectTransform.pivot;
            _rootPosition = line.RectTransform.anchoredPosition;
            line.layoutGroup.childAlignment = TextAnchor.UpperLeft;
            _headerActive = line.repertoireHeader.gameObject.activeSelf;
            // The native side header contains the source title only. Level headings, spell slot
            // indicators and free-use counters remain on their native level boxes.
            line.repertoireHeader.gameObject.SetActive(false);
            line.layoutGroup.padding.left = 0;
            if (!_heading)
            {
                var rect = (RectTransform)new GameObject("SpellSourceHeading", typeof(RectTransform)).transform;
                rect.gameObject.layer = line.gameObject.layer;
                rect.SetParent(line.transform, false);
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(0f, 1f);
                rect.offsetMin = new Vector2(4f, -26f);
                rect.offsetMax = new Vector2(-4f, -2f);
                rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                _heading = CreateSpellSelectionText(rect);
                _heading.alignment = TextAlignmentOptions.Left;
                _heading.enableWordWrapping = true;
                _heading.enableAutoSizing = false;
                _heading.fontSize = _heading.fontSizeMax = 18f;
                _heading.fontSizeMin = 12f;
                _heading.raycastTarget = false;
                _heading.color = new Color(0.95f, 0.77f, 0.60f, 1f);
            }

            _heading.font = line.headerLabel.TMP_Text.font;
            _heading.fontSharedMaterial = line.headerLabel.TMP_Text.fontSharedMaterial;
            // The compact native side label can abbreviate a feat to its spell-list class.
            // Horizontal headings have room for the actual class, race or feat source title.
            _heading.text = GetSpellSelectionSourceTitle(line.spellRepertoire);
            _heading.gameObject.SetActive(true);
            _layout = line.GetComponent<LayoutElement>();
            _createdLayout = !_layout;
            if (!_layout)
            {
                _layout = line.gameObject.AddComponent<LayoutElement>();
            }

            _minimumWidth = _layout.minWidth;
            _preferredWidth = _layout.preferredWidth;
            var titleWidth = Mathf.Min(maximumWidth, _heading.GetPreferredValues(_heading.text).x + 8f);
            _layout.minWidth = Mathf.Max(_minimumWidth, titleWidth);
            var headingHeight = Mathf.Max(28f, _heading.GetPreferredValues(_heading.text,
                Mathf.Max(1f, titleWidth - 8f), float.PositiveInfinity).y + 4f);
            line.layoutGroup.padding.top += Mathf.CeilToInt(headingHeight);
            _heading.rectTransform.offsetMin = new Vector2(4f, -headingHeight + 2f);

            // Native grid binding already rebuilds its own layout. Settle only this column's
            // fitters; one final canvas pass handles the completed picker after all columns bind.
            LayoutRebuilder.ForceRebuildLayoutImmediate(line.levelsTable);

            // Reflow a single crowded level's native grid without changing its spell/source list.
            foreach (var level in line.SpellsByLevelBoxes)
            {
                var grid = level.spellsTable.GetComponent<GridLayoutGroup>();
                if (!grid || grid.cellSize.x <= 0f || grid.cellSize.y <= 0f)
                {
                    continue;
                }

                _grids.Add((grid, grid.constraint, grid.constraintCount, grid.childAlignment, grid.startCorner,
                    grid.startAxis, grid.padding.bottom, grid.spacing));
                grid.childAlignment = TextAnchor.UpperLeft;
                grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
                grid.startAxis = GridLayoutGroup.Axis.Horizontal;
                grid.spacing = new Vector2(SpellSelectionCardGap, SpellSelectionCardGap);
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                var spellCount = level.spellsTable.GetComponentsInChildren<SpellActivationBox>()
                    .Count(box => box.gameObject.activeSelf);
                grid.constraintCount = Mathf.Clamp(spellCount, 1, SpellSelectionPreferredCardColumns);
                // Preferred dimensions determine the final column count without laying out
                // the same cards twice. Native binding supplied the level's footer height.
                grid.CalculateLayoutInputHorizontal();
                grid.CalculateLayoutInputVertical();
                var availableGridHeight = Mathf.Max(1f, maximumHeight - headingHeight -
                    Mathf.Max(0f, level.RectTransform.rect.height - level.spellsTable.rect.height));
                if (grid.preferredWidth > maximumWidth || grid.preferredHeight > availableGridHeight)
                {
                    var maximumColumns = Mathf.Max(1, Mathf.FloorToInt(
                        (maximumWidth - grid.padding.horizontal + grid.spacing.x) / (grid.cellSize.x + grid.spacing.x)));
                    var maximumRows = Mathf.Max(1, Mathf.FloorToInt(
                        (availableGridHeight - grid.padding.vertical + grid.spacing.y) / (grid.cellSize.y + grid.spacing.y)));
                    grid.constraintCount = Mathf.Min(maximumColumns,
                        Mathf.Max(1, Mathf.CeilToInt((float)spellCount / maximumRows)));
                    grid.CalculateLayoutInputHorizontal();
                }

                level.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, grid.preferredWidth);
                LayoutRebuilder.ForceRebuildLayoutImmediate(level.RectTransform);
            }

            line.layoutGroup.CalculateLayoutInputHorizontal();
            // The repertoire's layout group is on its inner level table. The root fitter cannot
            // infer that nested preferred width once a title LayoutElement is present.
            _layout.preferredWidth = Mathf.Max(_preferredWidth, titleWidth, line.layoutGroup.preferredWidth);
            LayoutRebuilder.ForceRebuildLayoutImmediate(line.RectTransform);
        }

        internal float ResetRowGridHeight()
        {
            foreach (var (grid, _, _, _, _, _, bottomPadding, _) in _grids)
            {
                grid.padding.bottom = bottomPadding;
                grid.CalculateLayoutInputVertical();
            }

            return _grids.Select(entry => entry.Grid.preferredHeight).DefaultIfEmpty(0f).Max();
        }

        internal void AlignRowGridHeight(float height)
        {
            // Equal card areas put all level/slot footers on the same row while short levels
            // keep their cards at the top. No empty columns are added to sparse acquisitions.
            foreach (var (grid, _, _, _, _, _, bottomPadding, _) in _grids)
            {
                grid.padding.bottom = bottomPadding + Mathf.CeilToInt(Mathf.Max(0f, height - grid.preferredHeight));
            }

            // Rebuild from the common owner so every grid and native footer settles together.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_line.RectTransform);
        }

        internal void SetColumnWidth(float width)
        {
            if (!_line || !_layout)
            {
                return;
            }

            _layout.minWidth = _layout.preferredWidth = Mathf.Max(1f, width);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_line.RectTransform);
        }

        internal void HideColumnHeading()
        {
            if (!_line || !_heading || !_layout)
            {
                return;
            }

            _heading.gameObject.SetActive(false);
            _line.layoutGroup.padding.top = _topPadding;
            _layout.minWidth = _minimumWidth;
            _layout.preferredWidth = Mathf.Max(_preferredWidth, _line.layoutGroup.preferredWidth);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_line.RectTransform);
        }

        internal void Restore()
        {
            if (!_line)
            {
                return;
            }

            _line.layoutGroup.padding.left = _leftPadding;
            _line.layoutGroup.padding.top = _topPadding;
            _line.layoutGroup.childAlignment = _levelAlignment;
            _line.RectTransform.anchorMin = _rootAnchorMin;
            _line.RectTransform.anchorMax = _rootAnchorMax;
            _line.RectTransform.pivot = _rootPivot;
            _line.RectTransform.anchoredPosition = _rootPosition;
            _line.repertoireHeader.gameObject.SetActive(_headerActive);
            if (_layout)
            {
                if (_createdLayout)
                {
                    // Pooled lines can be bound again during the same frame.
                    UnityEngine.Object.DestroyImmediate(_layout);
                }
                else
                {
                    _layout.minWidth = _minimumWidth;
                    _layout.preferredWidth = _preferredWidth;
                }
            }

            _layout = null;
            foreach (var (grid, constraint, count, alignment, corner, axis, bottomPadding, spacing) in _grids)
            {
                if (grid)
                {
                    grid.constraint = constraint;
                    grid.constraintCount = count;
                    grid.childAlignment = alignment;
                    grid.startCorner = corner;
                    grid.startAxis = axis;
                    grid.padding.bottom = bottomPadding;
                    grid.spacing = spacing;
                }
            }

            _grids.Clear();
            if (_heading)
            {
                _heading.gameObject.SetActive(false);
            }

            _line = null;
            SourceWraps = false;
        }
    }

    private static SpellRepertoireLine SetUpNewLine(
        int index,
        Transform spellRepertoireLinesTable,
        ICollection<SpellRepertoireLine> spellRepertoireLines,
        SpellSelectionPanel __instance)
    {
        GameObject newLine;

        if (spellRepertoireLinesTable.childCount <= index)
        {
            newLine = Gui.GetPrefabFromPool(__instance.spellRepertoireLinePrefab,
                spellRepertoireLinesTable);
        }
        else
        {
            newLine = spellRepertoireLinesTable.GetChild(index).gameObject;
        }

        newLine.SetActive(true);

        var component = newLine.GetComponent<SpellRepertoireLine>();

        spellRepertoireLines.Add(component);

        return component;
    }

    internal static void SetTeleporterGadgetActiveAnimation(WorldGadget worldGadget, bool visibility = false)
    {
        if (worldGadget.UserGadget == null)
        {
            return;
        }

        if (worldGadget.UserGadget.GadgetBlueprint == TeleporterIndividual)
        {
            var visualEffect = worldGadget.transform.FindChildRecursive("Vfx_Teleporter_Individual_Idle_01");

            // NOTE: don't use visualEffect?. which bypasses Unity object lifetime check
            if (visualEffect)
            {
                visualEffect.gameObject.SetActive(visibility);
            }
        }
        else if (worldGadget.UserGadget.GadgetBlueprint == TeleporterParty)
        {
            var visualEffect = worldGadget.transform.FindChildRecursive("Vfx_Teleporter_Party_Idle_01");

            // NOTE: don't use visualEffect?. which bypasses Unity object lifetime check
            if (visualEffect)
            {
                visualEffect.gameObject.SetActive(visibility);
            }
        }
    }

    private static bool IsGadgetExit(GadgetBlueprint gadgetBlueprint, bool onlyWithGizmos = false)
    {
        const int ExitsWithGizmos = 2;

        GadgetBlueprint[] gadgetExits =
        [
            VirtualExit, VirtualExitMultiple, Exit, ExitMultiple, TeleporterIndividual, TeleporterParty
        ];

        return Array.IndexOf(gadgetExits, gadgetBlueprint) >= (onlyWithGizmos ? ExitsWithGizmos : 0);
    }

    internal static void HideExitsAndTeleportersGizmosIfNotDiscovered(
        GameGadget __instance,
        int conditionIndex,
        bool state)
    {
        if (conditionIndex < 0 || conditionIndex >= __instance.conditionNames.Count)
        {
            return;
        }

        if (!__instance.CheckIsEnabled() || !__instance.IsTeleport())
        {
            return;
        }

        var service = ServiceRepository.GetService<IGameLocationService>();

        if (service == null)
        {
            return;
        }

        var worldGadget = service.WorldLocation.WorldSectors
            .SelectMany(ws => ws.WorldGadgets)
            .FirstOrDefault(wg => wg.GameGadget == __instance);

        if (!worldGadget)
        {
            return;
        }

        SetTeleporterGadgetActiveAnimation(worldGadget, state);
    }

    internal static void ComputeIsRevealedExtended(GameGadget __instance, ref bool __result)
    {
        var userGadget = Gui.GameLocation.UserLocation.UserRooms
            .SelectMany(a => a.UserGadgets)
            .FirstOrDefault(b => b.UniqueName == __instance.UniqueNameId);

        if (userGadget == null || !IsGadgetExit(userGadget.GadgetBlueprint))
        {
            return;
        }

        // reverts the revealed state and recalculates it
        __instance.revealed = false;
        __result = false;

        var referenceBoundingBox = __instance.ReferenceBoundingBox;
        var gridAccessor = GridAccessor.Default;

        // required for gadgets that are enabled from conditional states
        if (!referenceBoundingBox.IsValid)
        {
            __instance.revealed = true;
            __result = true;

            return;
        }

        foreach (var position in referenceBoundingBox.EnumerateAllPositionsWithin())
        {
            if (!gridAccessor.Visited(position))
            {
                continue;
            }

            var gameLocationService = ServiceRepository.GetService<IGameLocationService>();
            var worldGadgets = gameLocationService.WorldLocation.WorldSectors.SelectMany(ws => ws.WorldGadgets);
            var worldGadget = worldGadgets.FirstOrDefault(wg => wg.GameGadget == __instance);

            var isInvisible = __instance.IsInvisible();
            var isEnabled = __instance.CheckIsEnabled();

            if (worldGadget)
            {
                SetTeleporterGadgetActiveAnimation(worldGadget, isEnabled && !isInvisible);
            }

            __instance.revealed = true;
            __result = true;

            break;
        }
    }

    internal static void SetHighlightVisibilityExtended(WorldGadget __instance, ref bool visible)
    {
        if (IsGadgetExit(__instance.UserGadget.GadgetBlueprint, true))
        {
            return;
        }

        var activator = DatabaseHelper.GadgetDefinitions.Activator;
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        var visibilityService = ServiceRepository.GetService<IGameLocationVisibilityService>();
        var feedbackPosition = __instance.GameGadget.FeedbackPosition;

        // activators aren't detected in their original position so we handle them in a different way
        if (!__instance.GadgetDefinition == activator)
        {
            var position = new int3((int)feedbackPosition.x, (int)feedbackPosition.y, (int)feedbackPosition.z);

            foreach (var gameLocationCharacter in characterService.PartyCharacters)
            {
                visible = visibilityService.IsCellPerceivedByCharacter(position, gameLocationCharacter);

                if (visible)
                {
                    return;
                }
            }

            return;
        }

        // scan activators surrounding cells
        for (var x = -1; x <= 1; x++)
        {
            for (var z = -1; z <= 1; z++)
            {
                // jump original position
                if (x == 0 && z == 0)
                {
                    continue;
                }

                var position = new int3(
                    (int)feedbackPosition.x + x, (int)feedbackPosition.y, (int)feedbackPosition.z + z);

                foreach (var gameLocationCharacter in characterService.PartyCharacters)
                {
                    visible = visibilityService.IsCellPerceivedByCharacter(position, gameLocationCharacter);

                    if (visible)
                    {
                        return;
                    }
                }
            }
        }
    }

    private static void LoadRemoveBugVisualModels()
    {
        if (!Main.Settings.RemoveBugVisualModels)
        {
            return;
        }

        // Spiderlings, fire spider, kindred spirit spider, BadlandsSpider(normal, conjured and wildshape versions)
        const string ASSET_REFERENCE_SPIDER_1 = "362fc51df586d254ab182ef854396f82";
        //CrimsonSpiderling, PhaseSpider, SpectralSpider, CrimsonSpider, deep spider(normal, conjured and wildshape versions)
        const string ASSET_REFERENCE_SPIDER_2 = "40b5fe532a9a0814097acdb16c74e967";
        // spider queen
        const string ASSET_REFERENCE_SPIDER_3 = "8fc96b2a8c5fcc243b124d31c63df5d9";
        //Giant_Beetle, Small_Beetle, Redeemer_Zealot, Redeemer_Pilgrim
        const string ASSET_REFERENCE_BEETLE = "04dfcec8c8afb8642a80c1116de218d4";
        //Young_Remorhaz, Remorhaz
        const string ASSET_REFERENCE_REMORHAZ = "ded896e0c4ef46144904375ecadb1bb1";

        var brownBear = DatabaseHelper.MonsterDefinitions.BrownBear;
        var bearPrefab = new AssetReference("cc36634f504fa7049a4499a91749d7d5");

        var wolf = DatabaseHelper.MonsterDefinitions.Wolf;
        var wolfPrefab = new AssetReference("6e02c9bcfb5122042a533e7732182b1d");

        var ape = DatabaseHelper.MonsterDefinitions.Ape_MonsterDefinition;
        var apePrefab = new AssetReference("8f4589a9a294b444785fab045256a713");

        var dbMonsterDefinition = DatabaseRepository.GetDatabase<MonsterDefinition>();

        // check every monster for targeted prefab guid references
        foreach (var monster in dbMonsterDefinition)
        {
            // get monster asset reference for prefab guid comparison
            var value = monster.MonsterPresentation.malePrefabReference;

            switch (value.AssetGUID)
            {
                // swap bears for spiders
                case ASSET_REFERENCE_SPIDER_1:
                case ASSET_REFERENCE_SPIDER_2:
                case ASSET_REFERENCE_SPIDER_3:
                    monster.MonsterPresentation.malePrefabReference = bearPrefab;
                    monster.MonsterPresentation.femalePrefabReference = bearPrefab;
                    monster.GuiPresentation.spriteReference = brownBear.GuiPresentation.SpriteReference;
                    monster.bestiarySpriteReference = brownBear.BestiarySpriteReference;
                    monster.MonsterPresentation.monsterPresentationDefinitions = brownBear.MonsterPresentation
                        .MonsterPresentationDefinitions;
                    break;
                // swap apes for remorhaz
                case ASSET_REFERENCE_REMORHAZ:
                    monster.MonsterPresentation.malePrefabReference = apePrefab;
                    monster.MonsterPresentation.femalePrefabReference = apePrefab;
                    monster.GuiPresentation.spriteReference = ape.GuiPresentation.SpriteReference;
                    monster.bestiarySpriteReference = ape.BestiarySpriteReference;
                    monster.MonsterPresentation.monsterPresentationDefinitions = ape.MonsterPresentation
                        .MonsterPresentationDefinitions;
                    break;
                // swap wolves for beetles
                case ASSET_REFERENCE_BEETLE:
                    monster.MonsterPresentation.malePrefabReference = wolfPrefab;
                    monster.MonsterPresentation.femalePrefabReference = wolfPrefab;
                    monster.GuiPresentation.spriteReference = wolf.GuiPresentation.SpriteReference;
                    monster.bestiarySpriteReference = wolf.BestiarySpriteReference;
                    monster.MonsterPresentation.monsterPresentationDefinitions = wolf.MonsterPresentation
                        .MonsterPresentationDefinitions;

                    // changing beetle scale to suit replacement model
                    monster.MonsterPresentation.maleModelScale = 0.655f;
                    monster.MonsterPresentation.femaleModelScale = 0.655f;
                    break;
            }
        }
    }

    internal static void SwitchCrownOfTheMagister()
    {
        var crowns = new[]
        {
            CrownOfTheMagister, CrownOfTheMagister01, CrownOfTheMagister02, CrownOfTheMagister03,
            CrownOfTheMagister04, CrownOfTheMagister05, CrownOfTheMagister06, CrownOfTheMagister07,
            CrownOfTheMagister08, CrownOfTheMagister09, CrownOfTheMagister10, CrownOfTheMagister11,
            CrownOfTheMagister12
        };

        foreach (var itemPresentation in crowns.Select(x => x.ItemPresentation))
        {
            var maleBodyPartBehaviours = itemPresentation.GetBodyPartBehaviours(CreatureSex.Male);

            maleBodyPartBehaviours[0] = SettingsContext.GuiModManagerInstance.HideCrownOfMagister
                ? GraphicsCharacterDefinitions.BodyPartBehaviour.Shape
                : GraphicsCharacterDefinitions.BodyPartBehaviour.Armor;
        }
    }

    internal static void SwitchEmpressGarb()
    {
        EmpressGarbOriginalItemPresentation ??=
            Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.DeepCopy();

        ItemPresentation presentation;
        string armorAddressableName = null;

        switch (SettingsContext.GuiModManagerInstance.EmpressGarbAppearance)
        {
            case "Normal":
                presentation = EmpressGarbOriginalItemPresentation.DeepCopy();
                break;

            case "Barbarian":
                presentation = BarbarianClothes.ItemPresentation.DeepCopy();
                break;

            case "Druid":
                presentation = LeatherDruid.ItemPresentation.DeepCopy();
                armorAddressableName = LeatherDruid.Name;
                break;

            case "ElvenChain":
                presentation = ElvenChain.ItemPresentation.DeepCopy();
                break;

            case "SorcererOutfit":
                presentation = SorcererArmor.ItemPresentation.DeepCopy();
                break;

            case "StuddedLeather":
                presentation = StuddedLeather.ItemPresentation.DeepCopy();
                break;

            case "GreenMageArmor":
                presentation = GreenmageArmor.ItemPresentation.DeepCopy();
                break;

            case "WizardOutfit":
                presentation = WizardClothes_Alternate.ItemPresentation.DeepCopy();
                break;

            case "ScavengerOutfit1": // Ranger
                presentation = ClothesScavenger_A.ItemPresentation.DeepCopy();
                armorAddressableName = ClothesScavenger_A.Name;
                break;

            case "ScavengerOutfit2": // Rogue
                presentation = ClothesScavenger_B.ItemPresentation.DeepCopy();
                break;

            case "BardArmor":
                presentation = Bard_Armor.ItemPresentation.DeepCopy();
                armorAddressableName = Bard_Armor.Name;
                break;

            case "WarlockArmor":
                presentation = Warlock_Armor.ItemPresentation.DeepCopy();
                armorAddressableName = Warlock_Armor.Name;
                break;

            default:
                presentation = EmpressGarbOriginalItemPresentation.DeepCopy();
                break;
        }

        if (!string.IsNullOrEmpty(armorAddressableName))
        {
            presentation.useArmorAddressableName = true;
            presentation.armorAddressableName = armorAddressableName;
        }

        Enchanted_ChainShirt_Empress_war_garb.itemPresentation = presentation;
    }

    internal static FeatureDefinitionActionAffinity ActionAffinityFeatCrusherToggle { get; private set; }

    private static void LoadFeatCrusherToggle()
    {
        ActionAffinityFeatCrusherToggle = FeatureDefinitionActionAffinityBuilder
            .Create(DatabaseHelper.FeatureDefinitionActionAffinitys.ActionAffinitySorcererMetamagicToggle,
                "ActionAffinityFeatCrusherToggle")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((ActionDefinitions.Id)ExtraActionId.FeatCrusherToggle)
            .AddToDB();
    }

    internal static FeatureDefinitionActionAffinity ActionAffinityPaladinSmiteToggle { get; private set; }

    private static void LoadPaladinSmiteToggle()
    {
        ActionAffinityPaladinSmiteToggle = FeatureDefinitionActionAffinityBuilder
            .Create(DatabaseHelper.FeatureDefinitionActionAffinitys.ActionAffinitySorcererMetamagicToggle,
                "ActionAffinityPaladinSmiteToggle")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((ActionDefinitions.Id)ExtraActionId.PaladinSmiteToggle)
            .AddToDB();
    }

    internal static void ResetFormationGrid(int selectedSet)
    {
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                Main.Settings.FormationGridSets[selectedSet][y][x] = FormationGridSetTemplates[selectedSet][y][x];
            }
        }
    }

    internal static void ResetAllFormationGrids()
    {
        for (var i = 0; i < FormationGridSetTemplates.Length; i++)
        {
            ResetFormationGrid(i);
        }

        Main.Settings.FormationGridSelectedSet = 1;
    }

    private static void LoadFormationGrid()
    {
        if (Main.Settings.FormationGridSelectedSet < 0)
        {
            ResetAllFormationGrids();
        }
        else
        {
            FillDefinitionFromFormationGrid();
        }
    }

#if false
    private static void FillFormationGridFromDefinition(int selectedSet)
    {
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                Main.Settings.FormationGridSets[selectedSet][y][x] = 0;
            }
        }

        foreach (var position in DatabaseHelper.FormationDefinitions.Column2.FormationPositions)
        {
            Main.Settings.FormationGridSets[selectedSet][-position.z][position.x + 2] = 1;
        }
    }
#endif

    internal static void SetFormationGrid(int set)
    {
        Main.Settings.FormationGridSelectedSet = set;
        FillDefinitionFromFormationGrid();
    }

    internal static void FillDefinitionFromFormationGrid()
    {
        var position = 0;
        var selectedSet = Main.Settings.FormationGridSelectedSet;

        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                if (Main.Settings.FormationGridSets[selectedSet][y][x] == 1)
                {
                    DatabaseHelper.FormationDefinitions.Column2.FormationPositions[position++] = new int3(x - 2, 0, -y);
                }
            }
        }

        if (UnityModManagerUIPatcher.ModManagerUI.IsOpen)
        {
            return;
        }

        Gui.GuiService.ShowAlert(
            Gui.Format("ModUi/&FormationSelected", (Main.Settings.FormationGridSelectedSet + 1).ToString()),
            Gui.ColorAlert);
    }

    internal static void Load()
    {
        InventoryManagementContext.Load();
        SwitchCrownOfTheMagister();
        SwitchEmpressGarb();
        LoadRemoveBugVisualModels();
        LoadFeatCrusherToggle();
        LoadPaladinSmiteToggle();
        LoadFormationGrid();
    }

    internal static class GameHud
    {
        internal static void ShowAll([NotNull] GameLocationBaseScreen gameLocationBaseScreen)
        {
            var initiativeOrPartyPanel = GetInitiativeOrPartyPanel();
            var timeAndNavigationPanel = GetTimeAndNavigationPanel();
            var guiConsoleScreen = Gui.GuiService.GetScreen<GuiConsoleScreen>();
            var anyVisible = guiConsoleScreen.Visible || gameLocationBaseScreen.CharacterControlPanel.Visible;

            if (!anyVisible)
            {
                if (initiativeOrPartyPanel)
                {
                    anyVisible = initiativeOrPartyPanel.Visible;
                }
            }

            if (!anyVisible)
            {
                if (timeAndNavigationPanel)
                {
                    anyVisible = timeAndNavigationPanel.Visible;
                }
            }

            ShowCharacterControlPanel(gameLocationBaseScreen, anyVisible);
            TogglePanelVisibility(guiConsoleScreen, anyVisible);
            TogglePanelVisibility(initiativeOrPartyPanel);
            TogglePanelVisibility(timeAndNavigationPanel, anyVisible);

            return;

            [CanBeNull]
            GuiPanel GetInitiativeOrPartyPanel()
            {
                return gameLocationBaseScreen switch
                {
                    GameLocationScreenExploration gameLocationScreenExploration => gameLocationScreenExploration
                        .partyControlPanel,
                    GameLocationScreenBattle gameLocationScreenBattle => gameLocationScreenBattle.initiativeTable,
                    _ => null
                };
            }

            [CanBeNull]
            TimeAndNavigationPanel GetTimeAndNavigationPanel()
            {
                return gameLocationBaseScreen switch
                {
                    GameLocationScreenExploration gameLocationScreenExploration => gameLocationScreenExploration
                        .timeAndNavigationPanel,
                    GameLocationScreenBattle gameLocationScreenBattle =>
                        gameLocationScreenBattle.timeAndNavigationPanel,
                    _ => null
                };
            }
        }

        private static void ShowCharacterControlPanel([NotNull] GameLocationBaseScreen gameLocationBaseScreen,
            bool forceHide = false)
        {
            var characterControlPanel = gameLocationBaseScreen.CharacterControlPanel;

            if (characterControlPanel.Visible || forceHide)
            {
                characterControlPanel.Hide();
                characterControlPanel.Unbind();
            }
            else
            {
                var gameLocationSelectionService = ServiceRepository.GetService<IGameLocationSelectionService>();

                if (gameLocationSelectionService.SelectedCharacters.Count <= 0)
                {
                    return;
                }

                characterControlPanel.Bind(gameLocationSelectionService.SelectedCharacters[0],
                    gameLocationBaseScreen.ActionTooltipDock);
                characterControlPanel.Show();
            }
        }

        private static void TogglePanelVisibility(GuiPanel guiPanel, bool forceHide = false)
        {
            if (!guiPanel)
            {
                return;
            }

            if (guiPanel.Visible || forceHide)
            {
                guiPanel.Hide();
            }
            else
            {
                guiPanel.Show();
            }
        }

        internal static void RefreshCharacterControlPanel()
        {
            if (Gui.CurrentLocationScreen && Gui.CurrentLocationScreen is GameLocationBaseScreen location)
            {
                location.CharacterControlPanel.RefreshNow();
            }
        }
    }

    internal static class Teleporter
    {
        internal static void ConfirmTeleportParty(Func<int3> getPosition)
        {
            var position = getPosition();

            Gui.GuiService.ShowMessage(
                MessageModal.Severity.Attention2,
                "Message/&TeleportPartyTitle",
                Gui.Format("Message/&TeleportPartyDescription", position.x.ToString(), position.x.ToString()),
                "Message/&MessageYesTitle", "Message/&MessageNoTitle",
                () => TeleportParty(position),
                null);
        }

        internal static int3 GetEncounterPosition()
        {
            var gameLocationService = ServiceRepository.GetService<IGameLocationService>();
            var x = (int)gameLocationService.GameLocation.LastCameraPosition.x;
            var z = (int)gameLocationService.GameLocation.LastCameraPosition.z;

            return new int3(x, 0, z);
        }

        internal static int3 GetLeaderPosition()
        {
            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
            var position = characterService.PartyCharacters[0].LocationPosition;
            var currentCharacter = Global.CurrentCharacter ??
                                   characterService.PartyCharacters[0].RulesetCharacter;
            var locationCharacter = characterService.PartyCharacters
                .FirstOrDefault(x => x.RulesetCharacter == currentCharacter);

            return locationCharacter?.LocationPosition ?? position;
        }

        private static void TeleportParty(int3 position)
        {
            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
            var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();
            var boxInt = new BoxInt(position, int3.zero, int3.zero);

            // 20 to improve teleport behavior on campaigns with different heights
            boxInt.Inflate(1, 20, 1);

            var characters = characterService.PartyCharacters.Union(characterService.GuestCharacters);

            foreach (var gameLocationCharacter in characters)
            {
                foreach (var alternatePosition in boxInt.EnumerateAllPositionsWithin())
                {
                    if (!positioningService.CanPlaceCharacter(
                            gameLocationCharacter, alternatePosition, CellHelpers.PlacementMode.Station)
                        || !positioningService.CanCharacterStayAtPosition_Floor(
                            gameLocationCharacter, alternatePosition, true))
                    {
                        continue;
                    }

                    ServiceRepository.GetService<IGameLocationPositioningService>().TeleportCharacter(
                        gameLocationCharacter, alternatePosition, LocationDefinitions.Orientation.North);
                }
            }
        }
    }
}
