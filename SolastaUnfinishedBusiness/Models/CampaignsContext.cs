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
using SolastaUnfinishedBusiness.Patches;
using TA;
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

    private static readonly List<RectTransform> SpellLineTables = [];
    private static readonly string[] LegacySpellSelectionRuntimeContainerNames =
    [
        "SpellSelection" + "Viewport",
        "SpellSelection" + "Scroll" + "Viewport",
        "SpellSelection" + "Line" + "Content"
    ];
    private static readonly Vector3[] SpellSelectionWorldCorners = new Vector3[4];
    private static SpellSelectionLinePager ActiveSpellSelectionLinePager { get; set; }
    private static ItemPresentation EmpressGarbOriginalItemPresentation { get; set; }

    internal static bool ShouldSuppressSpellSelectionBackgroundWheel()
    {
        return ActiveSpellSelectionLinePager && ActiveSpellSelectionLinePager.ShouldSuppressBackgroundWheel();
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

    internal static void SpellSelectionPanelMultilineUnbind()
    {
        if (Main.Settings.DisableMultilineSpellOffering)
        {
            return;
        }

        foreach (var spellTable in SpellLineTables
                     .Where(spellTable => spellTable && spellTable.childCount > 0))
        {
            SetSpellSelectionLineTableVisible(spellTable, true);
            Gui.ReleaseChildrenToPool(spellTable);
            spellTable.SetParent(null);
            Object.Destroy(spellTable.gameObject);
        }

        SpellLineTables.Clear();
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
            return;
        }

        var spellRepertoireLines = __instance.spellRepertoireLines;
        var spellRepertoireSecondaryLine = __instance.spellRepertoireSecondaryLine;
        var spellRepertoireLinesTable = __instance.spellRepertoireLinesTable;
        var slotAdvancementPanel = __instance.SlotAdvancementPanel;

        foreach (var spellRepertoireLine in spellRepertoireLines)
        {
            spellRepertoireLine.Unbind();
        }

        spellRepertoireLines.Clear();
        Gui.ReleaseChildrenToPool(spellRepertoireLinesTable);
        spellRepertoireSecondaryLine.Unbind();
        spellRepertoireSecondaryLine.gameObject.SetActive(false);

        var spellLineHolder = EnsureSpellSelectionLineHolder(spellRepertoireLinesTable) ?? spellRepertoireLinesTable;
        var spellRepertoires = __instance.Caster.RulesetCharacter.SpellRepertoires
            .Where(r => r.SpellCastingFeature.SpellListDefinition != SpellsContext.EmptySpellList)
            .ToArray();

        var needNewLine = true;
        var lineIndex = 0;
        var indexOfLine = 0;
        var spellLevelsOnLine = 0;
        var curTable = spellRepertoireLinesTable;

        foreach (var rulesetSpellRepertoire in spellRepertoires)
        {
            LevelUpHelper.AddSlotCastableExtraSpellsToAutoPreparedSpells(
                caster.RulesetCharacter,
                rulesetSpellRepertoire);

            var startLevel = 0;
            var maxLevel = rulesetSpellRepertoire.MaxSpellLevelOfSpellCastingLevel;

            SharedSpellsContext.FactorMysticArcanum(caster.RulesetCharacter, rulesetSpellRepertoire,
                ref maxLevel);

            for (var level = startLevel; level <= maxLevel; level++)
            {
                if (!IsLevelActive(rulesetSpellRepertoire, level, actionType))
                {
                    continue;
                }

                spellLevelsOnLine++;

                if (spellLevelsOnLine < 4) // Main.Settings.MaxSpellLevelsPerLine)
                {
                    continue;
                }

                curTable = AddActiveSpellsToLine(
                    __instance,
                    spellCastEngaged,
                    actionType,
                    cantripOnly,
                    spellRepertoireLines,
                    curTable,
                    slotAdvancementPanel,
                    spellRepertoires,
                    needNewLine,
                    lineIndex,
                    indexOfLine,
                    rulesetSpellRepertoire,
                    startLevel,
                    level);

                startLevel = level + 1;
                lineIndex++;
                spellLevelsOnLine = 0;
                needNewLine = true;
                indexOfLine = 0;
            }

            if (spellLevelsOnLine == 0)
            {
                continue;
            }

            curTable = AddActiveSpellsToLine(
                __instance,
                spellCastEngaged,
                actionType,
                cantripOnly,
                spellRepertoireLines,
                curTable,
                slotAdvancementPanel,
                spellRepertoires,
                needNewLine,
                lineIndex,
                indexOfLine,
                rulesetSpellRepertoire,
                startLevel,
                maxLevel);

            needNewLine = false;
            indexOfLine++;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(curTable);
        __instance.RectTransform.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Horizontal,
            spellRepertoireLinesTable.rect.width);

        var pagerMetrics = ConfigureSpellSelectionLinePager(__instance, spellLineHolder);

        __instance.RectTransform.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Horizontal,
            Mathf.Max(pagerMetrics.VisibleWidth, spellRepertoireLinesTable.rect.width, spellLineHolder.rect.width));
        __instance.RectTransform.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Vertical,
            Mathf.Max(pagerMetrics.VisibleHeight, spellLineHolder.rect.height));
        LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.RectTransform);

        FloatingPanelBounds.ClampToScreen(__instance.RectTransform);
        FloatingPanelBounds.ClampToScreenForNextFrames(__instance, __instance.RectTransform);
    }

    private static RectTransform EnsureSpellSelectionLineHolder(RectTransform spellRepertoireLinesTable)
    {
        RestoreSpellSelectionLineTableHierarchy(spellRepertoireLinesTable);

        var holder = spellRepertoireLinesTable.parent as RectTransform;

        if (holder && holder.GetComponent<VerticalLayoutGroup>())
        {
            RestoreSpellSelectionHolderLayout(holder);
            return holder;
        }

        holder = new GameObject("SpellSelectionLineHolder", typeof(RectTransform)).GetComponent<RectTransform>();

        var verticalLayoutGroup = holder.gameObject.AddComponent<VerticalLayoutGroup>();

        verticalLayoutGroup.spacing = 10;
        holder.gameObject.AddComponent<ContentSizeFitter>();
        holder.SetParent(spellRepertoireLinesTable.parent, true);
        holder.SetAsFirstSibling();
        holder.localScale = Vector3.one;
        spellRepertoireLinesTable.SetParent(holder, true);

        return holder;
    }

    private static SpellSelectionPagerMetrics ConfigureSpellSelectionLinePager(
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

            return new SpellSelectionPagerMetrics(
                0f,
                0f);
        }

        var visibleRows = CalculateVisibleSpellSelectionRows(rowHeights, spacing, availableHeight);
        var pagerEnabled = visibleRows < lineTables.Length;
        var visibleHeight = GetVisibleSpellSelectionRowsHeight(rowHeights, spacing, 0, visibleRows);
        var pager = holder.GetComponent<SpellSelectionLinePager>() ?? holder.gameObject.AddComponent<SpellSelectionLinePager>();

        if (pagerEnabled)
        {
            pager.Configure(panel, holder, lineTables, rowHeights, spacing, visibleRows, safeCanvasBounds, canvasRect);
        }
        else
        {
            pager.DisablePager();
            SetAllSpellSelectionLineTablesVisible(holder, true);
        }

        RefreshSpellSelectionPanelSize(panel, holder, contentWidth, visibleHeight, safeCanvasBounds, canvasRect);

        return new SpellSelectionPagerMetrics(
            contentWidth,
            visibleHeight);
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
        var safeBottom = canvasBounds.yMin + canvasBounds.height * SpellSelectionBottomFallbackCanvasRatio;
        var actionPanel = panel.GetComponentInParent<CharacterActionPanel>();

        if (actionPanel &&
            TryGetCanvasLocalBounds(actionPanel.RectTransform, canvasRect, out var actionPanelBounds) &&
            actionPanelBounds.height > 1f)
        {
            safeBottom = Mathf.Max(canvasBounds.yMin, actionPanelBounds.yMax + SpellSelectionMargin);
        }

        return Rect.MinMaxRect(canvasBounds.xMin, safeBottom, canvasBounds.xMax, canvasBounds.yMax);
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

        canvasGroup.alpha = visible ? 1f : 0f;
        canvasGroup.blocksRaycasts = visible;
        canvasGroup.interactable = visible;
        layoutElement.ignoreLayout = !visible;
        lineTable.gameObject.SetActive(true);
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
        var bounds = GetChildrenLocalBounds(lineTable);

        return Mathf.Max(GetPreferredHeight(lineTable), bounds.height, lineTable.rect.height);
    }

    private static float GetSpellSelectionLineTableWidth(RectTransform lineTable)
    {
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

        var panelWidth = Mathf.Max(1f, width);
        var panelHeight = Mathf.Max(1f, height);

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

        if (!rectTransform || !rectTransform.gameObject.activeInHierarchy)
        {
            return false;
        }

        var canvas = rectTransform.GetComponentInParent<Canvas>();

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

        if (!rectTransform || !canvasRect || !rectTransform.gameObject.activeInHierarchy)
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

    private readonly struct SpellSelectionPagerMetrics(
        float visibleWidth,
        float visibleHeight)
    {
        internal readonly float VisibleWidth = visibleWidth;
        internal readonly float VisibleHeight = visibleHeight;
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
            _holder = holder;
            _lineTables = lineTables;
            _rowHeights = rowHeights;
            _spacing = spacing;
            _visibleRows = Mathf.Clamp(visibleRows, 1, lineTables.Length);
            _firstVisibleRow = Mathf.Clamp(_firstVisibleRow, 0, GetMaxFirstVisibleRow());
            _safeCanvasBounds = safeCanvasBounds;
            _canvasRect = canvasRect;
            _rowDragThreshold = Mathf.Max(24f, GetAverageRowHeight() * SpellSelectionDragThresholdRatio);
            enabled = true;
            ActiveSpellSelectionLinePager = this;

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

            _dragAccumulator = 0f;
            _firstVisibleRow = 0;

            if (ActiveSpellSelectionLinePager == this)
            {
                ActiveSpellSelectionLinePager = null;
            }

            enabled = false;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragAccumulator = 0f;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!CanPage())
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
            if (!CanPage())
            {
                return;
            }

            if (FloatingPanelBounds.ShouldSuppressBackgroundWheel(this))
            {
                return;
            }

            CaptureWheelInput();

            var deltaRows = eventData.scrollDelta.y < 0f ? 1 : -1;

            MoveRows(deltaRows);
            eventData.Use();
        }

        private void OnDisable()
        {
            if (ActiveSpellSelectionLinePager == this)
            {
                ActiveSpellSelectionLinePager = null;
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
            return CanPage() && (IsPointerInsidePanel() || Time.frameCount == _lastWheelInputFrame);
        }

        private bool CanPage()
        {
            return isActiveAndEnabled &&
                   _panel && _panel.isActiveAndEnabled &&
                   _holder && _holder.gameObject.activeInHierarchy &&
                   _lineTables is { Length: > 0 } &&
                   _visibleRows > 0 &&
                   _lineTables.Length > _visibleRows;
        }

        private void CaptureWheelInput()
        {
            ActiveSpellSelectionLinePager = this;
            _lastWheelInputFrame = Time.frameCount;
        }

        private bool IsPointerInsidePanel()
        {
            var target = _panel ? _panel.RectTransform : _holder;

            if (!target)
            {
                return false;
            }

            var canvas = target.GetComponentInParent<Canvas>();
            var camera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

            return RectTransformUtility.RectangleContainsScreenPoint(
                target,
                UnityEngine.Input.mousePosition,
                camera);
        }

        private int GetMaxFirstVisibleRow()
        {
            return _lineTables == null ? 0 : Mathf.Max(0, _lineTables.Length - _visibleRows);
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

            var lastVisibleRow = Mathf.Min(_lineTables.Length, _firstVisibleRow + _visibleRows);

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
            var visibleWidth = 0f;

            for (var index = _firstVisibleRow; index < lastVisibleRow; index++)
            {
                visibleWidth = Mathf.Max(visibleWidth, GetSpellSelectionLineTableWidth(_lineTables[index]));
            }

            RefreshSpellSelectionPanelSize(
                _panel,
                _holder,
                visibleWidth,
                visibleHeight,
                _safeCanvasBounds,
                _canvasRect);
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
        RulesetSpellRepertoire[] spellRepertoires,
        bool needNewLine,
        int lineIndex,
        int indexOfLine,
        RulesetSpellRepertoire rulesetSpellRepertoire,
        int startLevel,
        int level)
    {
        if (needNewLine)
        {
            var previousTable = spellRepertoireLinesTable;

            LayoutRebuilder.ForceRebuildLayoutImmediate(previousTable);

            if (lineIndex > 0)
            {
                // instantiate new table
                spellRepertoireLinesTable =
                    Object.Instantiate(spellRepertoireLinesTable, previousTable.parent.transform);
                // clear it of children
                spellRepertoireLinesTable.DetachChildren();
                //spellRepertoireLinesTable.SetParent(previousTable.parent.transform, true);
                spellRepertoireLinesTable.localScale = previousTable.localScale;
                spellRepertoireLinesTable.transform.SetAsFirstSibling();
                SpellLineTables.Add(spellRepertoireLinesTable);
            }
        }

        var curLine = SetUpNewLine(indexOfLine, spellRepertoireLinesTable, spellRepertoireLines, __instance);

        curLine.Bind(
            __instance.Caster,
            rulesetSpellRepertoire,
            spellRepertoires.Length > 1,
            spellCastEngaged,
            slotAdvancementPanel,
            actionType,
            cantripOnly,
            startLevel,
            level,
            false);

        return spellRepertoireLinesTable;
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

    private static bool IsLevelActive(
        RulesetSpellRepertoire spellRepertoire, int level,
        ActionDefinitions.ActionType actionType)
    {
        var spellActivationTime = actionType switch
        {
            ActionDefinitions.ActionType.Bonus => ActivationTime.BonusAction,
            ActionDefinitions.ActionType.Main => ActivationTime.Action,
            ActionDefinitions.ActionType.Reaction => ActivationTime.Reaction,
            ActionDefinitions.ActionType.NoCost => ActivationTime.NoCost,
            _ => ActivationTime.Action
        };

        if (level == 0)
        {
            // changed to support game v1.3.44 and allow ancestry cantrips to display off battle
            return actionType == ActionDefinitions.ActionType.None ||
                   spellRepertoire.KnownCantrips.Any(cantrip => cantrip.ActivationTime == spellActivationTime) ||
                   (spellRepertoire.ExtraSpellsByTag.TryGetValue("BonusCantrips", out var bonusCantrips) &&
                    bonusCantrips.Any(cantrip => cantrip.ActivationTime == spellActivationTime));
        }

        if (LevelUpHelper.HasSlotCastableExtraSpellOfLevelAndActionType(
                spellRepertoire.GetCaster(),
                spellRepertoire,
                level,
                actionType))
        {
            return true;
        }

        switch (spellRepertoire.SpellCastingFeature.SpellReadyness)
        {
            case SpellReadyness.Prepared when spellRepertoire.PreparedSpells
                                                 .Any(spellDefinition =>
                                                     spellDefinition.SpellLevel == level
                                                     && IsSpellVisibleForActionType(spellDefinition, actionType)):
            case SpellReadyness.AllKnown
                when spellRepertoire.KnownSpells.Any(spellDefinition => spellDefinition.SpellLevel == level)
                     || spellRepertoire.ExtraSpellsByTag.Any(x => x.Value.Any(s => s.SpellLevel == level)):

                return true;

            default:
                return false;
        }
    }

    private static bool IsSpellVisibleForActionType(
        SpellDefinition spellDefinition,
        ActionDefinitions.ActionType actionType)
    {
        if (!spellDefinition)
        {
            return false;
        }

        if (actionType == ActionDefinitions.ActionType.None)
        {
            return spellDefinition.ActivationTime is not ActivationTime.Reaction and not ActivationTime.OnAttackHit;
        }

        return spellDefinition.ActivationTime == LevelUpHelper.GetSpellActivationTime(actionType);
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
