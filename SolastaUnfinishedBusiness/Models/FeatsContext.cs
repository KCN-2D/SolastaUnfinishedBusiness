using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using UnityEngine;
using UnityEngine.UI;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionFeatureSets;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterRaceDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPointPools;

namespace SolastaUnfinishedBusiness.Models;

internal static class FeatsContext
{
    private struct FeatSubPanelUiState
    {
        internal readonly bool Active;
        internal readonly HeroDefinitions.PointsPoolType CurrentPoolType;
        internal readonly bool HasActiveFeatPointPool;

        internal FeatSubPanelUiState(
            bool active,
            HeroDefinitions.PointsPoolType currentPoolType,
            bool hasActiveFeatPointPool)
        {
            Active = active;
            CurrentPoolType = currentPoolType;
            HasActiveFeatPointPool = hasActiveFeatPointPool;
        }
    }

    private const int Columns = 3;
    internal const int Width = 300;
    internal const int Height = 44;
    internal const int Spacing = 5;
    internal const int MinInitialFeats = 0;
    internal const int MaxInitialFeats = 4; // don't increase this value to avoid issue reports on crazy scenarios

    internal static HashSet<FeatDefinition> Feats { get; private set; } = [];
    internal static HashSet<FeatDefinition> FeatGroups { get; private set; } = [];
    private static readonly Dictionary<int, FeatSubPanelUiState> PanelUiStates = [];
    private static readonly HashSet<string> LegacyStructuralRootGroupNames =
    [
        "FeatGroupRaceBound",
        "FeatGroupSkills",
        "FeatGroupGeneralAdept",
        "FeatGroupPlaneMagic",
        "FeatGroupSpellCombat",
        "FeatGroupMeleeCombat",
        "FeatGroupRangedCombat",
        "FeatGroupSupportCombat",
        "FeatGroupDefenseCombat",
        "FeatGroupAgilityCombat",
        "FeatGroupTwoHandedCombat",
        "FeatGroupTwoWeaponCombat",
        "FeatGroupUnarmoredCombat"
    ];
    private static int PreviousTotalFeatsGrantedFirstLevel { get; set; } = -1;
    private static bool PreviousAlternateHuman { get; set; }

    internal static void Load()
    {
        LoadFeatsPointPools();
    }

    internal static void LateLoad()
    {
        var feats = new List<FeatDefinition>();

        // generate feats here and fill the list
        ArmorFeats.CreateFeats(feats);
        CasterFeats.CreateFeats(feats);
        OtherFeats.CreateFeats(feats); // must come before Class Feats
        ClassFeats.CreateFeats(feats);
        CraftyFeats.CreateFeats(feats);
        CriticalVirtuosoFeats.CreateFeats(feats);
        DefenseExpertFeats.CreateFeats(feats);
        MeleeCombatFeats.CreateFeats(feats);
        PrecisionFocusedFeats.CreateFeats(feats);
        RaceFeats.CreateFeats(feats);
        RangedCombatFeats.CreateFeats(feats);
        TwoWeaponCombatFeats.CreateFeats(feats);
        Tabletop2024Context.Load2024TabletopFeats();

        // load them in mod UI
        feats.ForEach(LoadFeat);
        foreach (var feat in Tabletop2024Context.GetManagedStandaloneTabletopFeats())
        {
            LoadFeat(feat);
        }

        GroupFeats.Load(LoadFeatGroup);
        var groupedLeafVariants = BuildGroupedLeafVariantSet();

        // tweak the groups to make display simpler on mod UI
        Feats.RemoveWhere(groupedLeafVariants.Contains);
        Feats.RemoveWhere(x => x.HasSubFeatureOfType<HideFromFeats>());

        foreach (var child in AttributeDefinitions.AbilityScoreNames
                     .Select(attribute => DatabaseRepository.GetDatabase<FeatDefinition>()
                         .GetElement($"FeatGroupHalf{attribute}")))
        {
            FeatGroups.Remove(child);
        }

        FeatGroups.Remove(GroupFeats.FeatGroupOrigin);

        foreach (var featGroup in FeatGroups
                     .Where(featGroup =>
                         !string.IsNullOrEmpty(featGroup.FamilyTag) &&
                         featGroup.Name != "FeatGroupElementalTouch")
                     .ToArray())
        {
            FeatGroups.Remove(featGroup);

            if (!CasterFeats.MagicTouchedData.ContainsKey(featGroup.Name.Replace("FeatGroup", string.Empty)))
            {
                LoadFeat(featGroup);
            }
        }

        // sorting
        Feats = [.. Feats.OrderBy(x => x.FormatTitle())];
        FeatGroups = [.. FeatGroups.OrderBy(x => x.FormatTitle())];

        foreach (var groupedFeat in GroupFeats.Groups
                     .Select(groupDefinition => groupDefinition.GetFirstSubFeatureOfType<GroupedFeat>()))
        {
            groupedFeat?.Feats.Sort(Sorting.CompareTitle);
        }

        // settings paring feats
        foreach (var name in Main.Settings.FeatEnabled
                     .Where(name => Feats.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.FeatEnabled.Remove(name);
        }

        // settings paring groups
        foreach (var name in Main.Settings.FeatGroupEnabled
                     .Where(name => FeatGroups.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.FeatGroupEnabled.Remove(name);
        }

        if (!Main.Settings.EnableTabletopFeatRules2024)
        {
            RepairFeatGroupEnabledAfter2024ContainerBugV2();
        }

        // handle Half Attributes subgroups special case
        SwitchHalfAttributes(Main.Settings.FeatGroupEnabled.Contains("FeatGroupHalfAttributes"));

        // avoids restart on level up UI
        GuiWrapperContext.RecacheFeats();

        SwitchAsiAndFeat();
        SwitchFirstLevelTotalFeats();
        SwitchEveryFourLevelsFeats();
        SwitchEveryFourLevelsFeats(true);
    }

    private static void LoadFeat([NotNull] FeatDefinition featDefinition)
    {
        Feats.Add(featDefinition);
        UpdateFeatsVisibility(featDefinition, !Main.Settings.FeatEnabled.Contains(featDefinition.Name));
    }

    private static void LoadFeatGroup([NotNull] FeatDefinition featDefinition)
    {
        FeatGroups.Add(featDefinition);
        UpdateFeatGroupsVisibility(featDefinition);
    }

    private static void UpdateFeatsVisibility([NotNull] BaseDefinition featDefinition, bool hidden)
    {
        featDefinition.GuiPresentation.hidden = hidden;

        var groupedFeat = featDefinition.GetFirstSubFeatureOfType<GroupedFeat>();

        if (groupedFeat == null)
        {
            return;
        }

        if (!hidden && featDefinition == GroupFeats.FeatGroupFightingStyle)
        {
            groupedFeat?.GetSubFeats(true, true)
                .ForEach(x => UpdateFeatsVisibility(
                    x,
                    !Main.Settings.EnableTabletopFeatRules2024 && Tabletop2024Context.Is2024TabletopFeat(x)
                        ? true
                        : FightingStyleContext.HideFightingStyle(x)));
        }
        else
        {
            groupedFeat.GetSubFeats(true, true)
                .ForEach(x => UpdateFeatsVisibility(
                    x,
                    !hidden &&
                    !Main.Settings.EnableTabletopFeatRules2024 &&
                    Tabletop2024Context.Is2024TabletopFeat(x)
                        ? true
                        : hidden));
        }
    }

    private static void UpdateFeatGroupsVisibility([NotNull] BaseDefinition featDefinition)
    {
        featDefinition.GuiPresentation.hidden = !Main.Settings.FeatGroupEnabled.Contains(featDefinition.Name);
    }

    internal static void SwitchFeat(FeatDefinition featDefinition, bool active)
    {
        if (!Feats.Contains(featDefinition))
        {
            return;
        }

        var name = featDefinition.Name;

        if (active)
        {
            Main.Settings.FeatEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.FeatEnabled.Remove(name);
        }

        UpdateFeatsVisibility(featDefinition, !Main.Settings.FeatEnabled.Contains(featDefinition.Name));
        Tabletop2024Context.SwitchTabletopFeatRules2024();
    }

    private static void SwitchHalfAttributes(bool active)
    {
        foreach (var child in AttributeDefinitions.AbilityScoreNames
                     .Select(attribute =>
                         DatabaseRepository.GetDatabase<FeatDefinition>().GetElement($"FeatGroupHalf{attribute}")))
        {
            child.GuiPresentation.hidden = !active;
        }
    }

    internal static void SwitchFeatGroup(FeatDefinition featDefinition, bool active)
    {
        if (!FeatGroups.Contains(featDefinition))
        {
            return;
        }

        var name = featDefinition.Name;

        if (name == "FeatGroupHalfAttributes")
        {
            SwitchHalfAttributes(active);
        }

        if (active)
        {
            Main.Settings.FeatGroupEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.FeatGroupEnabled.Remove(name);
        }

        UpdateFeatGroupsVisibility(featDefinition);
        Tabletop2024Context.SwitchTabletopFeatRules2024();
    }

    internal static void RefreshFeatGroupVisibilityFromSettings()
    {
        RefreshFeatVisibilityFromSettings();
    }

    internal static void RefreshFeatVisibilityFromSettings()
    {
        var use2024 = Main.Settings.EnableTabletopFeatRules2024;

        foreach (var feat in Feats)
        {
            if (!use2024 && Tabletop2024Context.Is2024TabletopFeat(feat))
            {
                UpdateFeatsVisibility(feat, true);

                continue;
            }

            UpdateFeatsVisibility(feat, !Main.Settings.FeatEnabled.Contains(feat.Name));
        }

        foreach (var featGroup in FeatGroups)
        {
            if (!use2024 && Tabletop2024Context.Is2024TabletopFeat(featGroup))
            {
                featGroup.GuiPresentation.hidden = true;

                continue;
            }

            UpdateFeatGroupsVisibility(featGroup);
        }

        SwitchHalfAttributes(Main.Settings.FeatGroupEnabled.Contains("FeatGroupHalfAttributes"));
    }

    internal static void ClearFeatSubPanel2024UiState()
    {
        PanelUiStates.Clear();
    }

    internal static void RepairFeatGroupEnabledAfter2024ContainerBugV2()
    {
        Tabletop2024Context.RepairLegacyFeatGroupSettingsAfter2024BugV2();
    }

    internal static void UpdatePanelChildren(FeatSubPanel panel)
    {
        if (panel?.table == null ||
            panel.relevantFeats == null)
        {
            return;
        }

        // get missing children from pool
        while (panel.table.childCount < panel.relevantFeats.Count)
        {
            Gui.GetPrefabFromPool(panel.itemPrefab, panel.table);
        }

        // release extra children to pool
        while (panel.table.childCount > panel.relevantFeats.Count)
        {
            Gui.ReleaseInstanceToPool(panel.table.GetChild(panel.table.childCount - 1).gameObject);
        }
    }

    internal static void SortFeats(FeatSubPanel panel)
    {
        if (panel?.relevantFeats == null)
        {
            return;
        }

        panel.relevantFeats.Sort(CompareFeats);
    }

    internal static int CompareFeats(FeatDefinition a, FeatDefinition b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a == null)
        {
            return 1;
        }

        if (b == null)
        {
            return -1;
        }

        return string.Compare(a.FormatTitle(), b.FormatTitle(),
            StringComparison.CurrentCultureIgnoreCase);
    }

    internal static void UpdateRelevantFeatList(FeatSubPanel panel)
    {
        if (Main.Settings.EnableTabletopFeatRules2024)
        {
            UpdateRelevantFeatListFor2024Selection(panel);
        }
        else
        {
            UpdateRelevantFeatListForLegacySelection(panel);
        }
    }

    internal static bool IsFeatSelectionCandidateContext(
        FeatSubPanel panel,
        bool active,
        string stageTag,
        HeroDefinitions.PointsPoolType currentPoolType)
    {
        if (panel == null ||
            !active ||
            currentPoolType != HeroDefinitions.PointsPoolType.Feat ||
            string.IsNullOrEmpty(stageTag) ||
            !IsCharacterBuildingProficiencyPanel(panel))
        {
            RememberPanelUiState(panel, active, currentPoolType, false);

            return false;
        }

        var hasActiveFeatPointPool = TryGetActiveFeatPointPoolContext(
            panel,
            stageTag,
            currentPoolType,
            out _,
            out _,
            out _);

        RememberPanelUiState(panel, active, currentPoolType, hasActiveFeatPointPool);

        return hasActiveFeatPointPool;
    }

    internal static bool IsCharacterBuildingFeatSummaryContext(
        FeatSubPanel panel,
        bool active,
        string stageTag,
        HeroDefinitions.PointsPoolType currentPoolType)
    {
        _ = stageTag;
        _ = currentPoolType;

        return panel?.InspectedCharacter != null &&
               !active &&
               IsCharacterBuildingProficiencyPanel(panel);
    }

    internal static bool IsPassiveFeatDisplayContext(
        FeatSubPanel panel,
        bool active,
        string stageTag,
        HeroDefinitions.PointsPoolType currentPoolType)
    {
        _ = active;
        _ = stageTag;
        _ = currentPoolType;

        var hero = panel?.InspectedCharacter;

        return hero != null &&
               panel.GetComponentInParent<CharacterStageProficiencySelectionPanel>() == null &&
               !LevelUpHelper.IsLevelingUp(hero);
    }

    internal static void PrepareRelevantFeatsFor2024Selection(
        FeatSubPanel panel,
        string stageTag,
        string previousStageTag,
        HeroDefinitions.PointsPoolType currentPoolType,
        ref List<string> restrictedChoices)
    {
        if (!Main.Settings.EnableTabletopFeatRules2024 ||
            panel?.relevantFeats == null ||
            !TryGetActiveFeatPointPoolContext(
                panel,
                stageTag,
                currentPoolType,
                out var buildingData,
                out var service,
                out var pointPool))
        {
            return;
        }

        var modeAwareRestrictedChoices = Tabletop2024Context.GetModeAwareRestrictedChoiceNames(pointPool)
            .Where(choice => !string.IsNullOrEmpty(choice))
            .Distinct()
            .ToHashSet();

        var relevantFeats = Tabletop2024Context.GetGameFeatSelectionCatalogRoots()
            .Where(feat => feat != null &&
                           Tabletop2024Context.IsVisibleInGameFeatSelection(feat) &&
                           HasAllowedSelectionDescendant(
                               feat,
                               null,
                               service,
                               buildingData,
                               stageTag))
            .ToList();

        panel.relevantFeats.SetRange(relevantFeats);
        restrictedChoices = [.. modeAwareRestrictedChoices];

        SortFeats(panel);
        UpdatePanelChildren(panel);
        RebindPanelChildren(panel, stageTag, previousStageTag, currentPoolType, true);
    }

    internal static void UpdateRelevantFeatListFor2024Selection(FeatSubPanel panel)
    {
        if (!Main.Settings.EnableTabletopFeatRules2024 ||
            panel?.relevantFeats == null)
        {
            return;
        }

        var visibleFeats = Tabletop2024Context.GetGameFeatSelectionCatalogRoots()
            .Where(feat => feat != null && Tabletop2024Context.IsVisibleInGameFeatSelection(feat))
            .ToHashSet();

        panel.relevantFeats.SetRange(visibleFeats
            .Where(f =>
            {
                if (f.GetFirstSubFeatureOfType<IGroupedFeat>() == null)
                {
                    return true;
                }

                if (Tabletop2024Context.IsNonSelectableTabletopGroup(f))
                {
                    return false;
                }

                var allowedChildren = Tabletop2024Context.GetAllowedGameFeatChildren(f).ToArray();

                if (Tabletop2024Context.IsTabletopContainerGroup(f))
                {
                    return allowedChildren.Length > 0;
                }

                return allowedChildren.Length > 0;
            })
        );
    }

    internal static void UpdateRelevantFeatListForLegacySelection(FeatSubPanel panel)
    {
        if (Main.Settings.EnableTabletopFeatRules2024 ||
            panel?.relevantFeats == null)
        {
            return;
        }

        var allGroupedDescendants = BuildLegacyNestedGroupedDescendantSet();
        var visibleGroups = FeatGroups
            .Where(group => !allGroupedDescendants.Contains(group))
            .Where(IsVisibleLegacySelectionRoot)
            .Where(HasVisibleLegacyGroupedFeatDescendant)
            .ToList();
        var visibleStandaloneFeats = Feats
            .Where(feat => !allGroupedDescendants.Contains(feat))
            .Where(IsVisibleLegacySelectionRoot)
            .ToList();

        panel.relevantFeats.SetRange(visibleStandaloneFeats
            .Concat(visibleGroups)
            .Distinct());
    }

    internal static void MergeRelevantFeatsForPassiveDisplay(
        FeatSubPanel panel,
        bool active,
        string stageTag,
        string previousStageTag,
        HeroDefinitions.PointsPoolType currentPoolType)
    {
        _ = active;
        _ = stageTag;
        _ = previousStageTag;
        _ = currentPoolType;

        RefreshPassiveFeatDisplayPanel(panel);
    }

    internal static void MergeRelevantFeatsForCharacterBuildingSummary(
        FeatSubPanel panel,
        string stageTag,
        string previousStageTag,
        HeroDefinitions.PointsPoolType currentPoolType)
    {
        if (panel?.InspectedCharacter == null ||
            panel.relevantFeats == null ||
            !IsCharacterBuildingProficiencyPanel(panel))
        {
            return;
        }

        var displayFeats = BuildActualDisplayFeats(panel.InspectedCharacter, true);

        panel.relevantFeats.SetRange(displayFeats);
        SortFeats(panel);
        UpdatePanelChildren(panel);
        RebindPanelChildren(panel, stageTag, previousStageTag, currentPoolType, false);
    }

    internal static void RefreshPassiveFeatDisplayPanel(FeatSubPanel panel)
    {
        if (panel?.InspectedCharacter == null ||
            panel.relevantFeats == null ||
            !IsPassiveFeatDisplayContext(
                panel,
                false,
                null,
                HeroDefinitions.PointsPoolType.Irrelevant))
        {
            return;
        }

        var displayFeats = BuildActualDisplayFeats(panel.InspectedCharacter, false);

        panel.relevantFeats.SetRange(displayFeats);
        SortFeats(panel);
        UpdatePanelChildren(panel);
        RebindPanelChildren(
            panel,
            string.Empty,
            string.Empty,
              HeroDefinitions.PointsPoolType.Feat,
              false);
    }

    internal static bool TryBuildFeatGroupContentsDescription(FeatDefinition feat, out string description)
    {
        description = null;

        if (feat == null ||
            feat.GetFirstSubFeatureOfType<IGroupedFeat>() == null &&
            !Tabletop2024Context.IsTabletopContainerGroup(feat))
        {
            return false;
        }

        const int maxDisplayedChildren = 8;

        var childTitles = EnumerateTooltipFeatGroupChildren(feat)
            .Select(child => child?.FormatTitle())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct()
            .ToList();

        if (childTitles.Count == 0)
        {
            description = Gui.Localize("Tooltip/&FeatGroupEmptyDescription");

            return true;
        }

        var displayedTitles = childTitles
            .Take(maxDisplayedChildren)
            .ToList();
        var remainingCount = childTitles.Count - displayedTitles.Count;

        if (remainingCount > 0)
        {
            displayedTitles.Add(Gui.Format("Tooltip/&FeatGroupMoreItemsFormat", remainingCount.ToString()));
        }

        description = Gui.Format("Tooltip/&FeatGroupContainsFormat", string.Join(Gui.ListSeparator(), displayedTitles));

        return true;
    }

    internal static bool IsCharacterBuildingProficiencyPanel(FeatSubPanel panel)
    {
        var hero = panel?.InspectedCharacter;

        return panel &&
               (panel.GetComponentInParent<CharacterStageProficiencySelectionPanel>() != null ||
                hero != null &&
                LevelUpHelper.IsLevelingUp(hero));
    }

    internal static void ForceSameWidth(RectTransform table, bool active, FeatSubPanel panel)
    {
        if (table == null)
        {
            return;
        }

        if (panel?.table == null ||
            table != panel.table)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);

            return;
        }

        if (!IsSelectionLayoutContext(panel, active))
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);

            return;
        }

        if (Main.Settings.EnableSameWidthFeatSelection)
        {
            var hero = panel.InspectedCharacter;
            var buildingData = hero?.GetHeroBuildingData();
            var trainedFeats = buildingData?.LevelupTrainedFeats?
                .SelectMany(x => x.Value ?? [])
                .Concat(hero?.TrainedFeats ?? [])
                .Where(feat => feat != null)
                .ToArray() ?? [];

            var j = 0;
            RectTransform rect;

            for (var i = 0; i < table.childCount; i++)
            {
                var child = table.GetChild(i);

                if (!child.gameObject.activeSelf ||
                    !TryGetBoundFeatDefinition(child, out _, out var featDefinition) ||
                    trainedFeats.Any(trainedFeat =>
                        Tabletop2024Context.AreEquivalentTabletopFeatNames(trainedFeat.Name, featDefinition.Name)))
                {
                    continue;
                }

                var x = j % Columns;
                var y = j / Columns;
                var posX = x * (Width + (Spacing * 2));
                var posY = -y * (Height + Spacing);

                rect = child.GetComponent<RectTransform>();
                if (rect == null)
                {
                    continue;
                }

                rect.anchoredPosition = new Vector2(posX, posY);
                rect.sizeDelta = new Vector2(Width, Height);

                j++;
            }

            rect = table.GetComponent<RectTransform>();

            if (rect != null)
            {
                // ReSharper disable once PossibleLossOfFraction
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, ((j / Columns) + 1) * (Height + Spacing));
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(table);
    }

    private static bool TryGetBoundFeatDefinition(
        Component child,
        out FeatItem featItem,
        out FeatDefinition featDefinition)
    {
        featItem = null;
        featDefinition = null;

        if (child == null)
        {
            return false;
        }

        featItem = child.GetComponent<FeatItem>();
        featDefinition = featItem?.GuiFeatDefinition?.FeatDefinition;

        return featDefinition != null;
    }

    private static bool IsNonDisplayableManagedFeatName(string featName)
    {
        return !string.IsNullOrEmpty(featName) &&
               Tabletop2024Context.TryResolveModeAwareFeatDefinition(featName, out var featDefinition) &&
               Tabletop2024Context.IsManagedTabletopFeat(featDefinition) &&
               !Tabletop2024Context.IsDisplayableManagedTabletopLeaf(featDefinition);
    }

    private static void RememberPanelUiState(
        FeatSubPanel panel,
        bool active,
        HeroDefinitions.PointsPoolType currentPoolType,
        bool hasActiveFeatPointPool)
    {
        if (!panel)
        {
            return;
        }

        PanelUiStates[panel.GetInstanceID()] =
            new FeatSubPanelUiState(active, currentPoolType, hasActiveFeatPointPool);
    }

    private static bool IsKnownSelectionLayoutContext(FeatSubPanel panel)
    {
        return panel &&
               PanelUiStates.TryGetValue(panel.GetInstanceID(), out var uiState) &&
               uiState.Active &&
               uiState.CurrentPoolType == HeroDefinitions.PointsPoolType.Feat &&
               uiState.HasActiveFeatPointPool;
    }

    private static bool IsSelectionLayoutContext(FeatSubPanel panel, bool active)
    {
        return active &&
               IsKnownSelectionLayoutContext(panel);
    }

    internal static bool IsSafeFeatSelectionHoverContext(FeatSubPanel panel, ProficiencyBaseItem item)
    {
        return panel?.InspectedCharacter != null &&
               panel.table != null &&
               item is FeatItem featItem &&
               featItem.GuiFeatDefinition?.FeatDefinition != null &&
               !string.IsNullOrEmpty(featItem.StageTag) &&
               featItem.CurrentPoolType == HeroDefinitions.PointsPoolType.Feat &&
               IsKnownSelectionLayoutContext(panel);
    }

    private static bool TryGetActiveFeatPointPoolContext(
        FeatSubPanel panel,
        string stageTag,
        HeroDefinitions.PointsPoolType currentPoolType,
        out CharacterHeroBuildingData buildingData,
        out ICharacterBuildingService service,
        out PointPool pointPool)
    {
        buildingData = null;
        service = null;
        pointPool = null;

        if (panel?.InspectedCharacter?.GetHeroBuildingData() is not { } localBuildingData ||
            currentPoolType != HeroDefinitions.PointsPoolType.Feat ||
            string.IsNullOrEmpty(stageTag))
        {
            return false;
        }

        service = ServiceRepository.GetService<ICharacterBuildingService>();
        pointPool = service?.GetPointPoolOfTypeAndTag(localBuildingData, currentPoolType, stageTag);

        if (pointPool == null)
        {
            return false;
        }

        buildingData = localBuildingData;

        return true;
    }

    private static bool HasAllowedSelectionDescendant(
        FeatDefinition feat,
        FeatDefinition parentGroupedFeat,
        ICharacterBuildingService service,
        CharacterHeroBuildingData buildingData,
        string tag)
    {
        if (feat == null ||
            !Tabletop2024Context.IsAllowedInGameFeatSelectionByConfiguration(feat) ||
            !Tabletop2024Context.IsVisibleInGameFeatSelection(feat) ||
            Tabletop2024Context.IsNonSelectableTabletopGroup(feat))
        {
            return false;
        }

        var groupedFeat = feat.GetFirstSubFeatureOfType<IGroupedFeat>();

        if (groupedFeat == null)
        {
            return Tabletop2024Context.CanSelectFeatForCurrentPointPool(
                buildingData?.HeroCharacter,
                tag,
                feat,
                service);
        }

        var nextParentGroupedFeat = Tabletop2024Context.IsTabletopContainerGroup(feat)
            ? parentGroupedFeat
            : feat;

        var childFeats = Tabletop2024Context.GetAllowedGameFeatChildren(feat)
            .Where(child => child != null)
            .ToArray();

        if (childFeats.Length == 0 && !Tabletop2024Context.IsTabletopContainerGroup(feat))
        {
            childFeats = groupedFeat.GetSubFeats(true)
                ?.Where(child => child != null && Tabletop2024Context.IsVisibleInGameFeatSelection(child))
                .ToArray() ?? [];
        }

        return childFeats.Any(childFeat =>
            HasAllowedSelectionDescendant(
                childFeat,
                nextParentGroupedFeat,
                service,
                buildingData,
                tag));
    }

    internal static List<FeatDefinition> BuildActualDisplayFeats(
        RulesetCharacterHero hero,
        bool includeBuildingData)
    {
        var displayFeats = new List<FeatDefinition>();
        CharacterHeroBuildingData buildingData = null;

        if (includeBuildingData)
        {
            buildingData = hero?.GetHeroBuildingData();

            foreach (var feat in buildingData?.LevelupTrainedFeats?.Values
                         .Where(feats => feats != null)
                         .SelectMany(feats => feats) ?? [])
            {
                TryAddDisplayRelevantFeat(displayFeats, feat, true);
            }
        }

        foreach (var feat in hero?.TrainedFeats ?? [])
        {
            TryAddDisplayRelevantFeat(displayFeats, feat, true);
        }

        foreach (var featName in hero?.FeatProficiencies ?? [])
        {
            if (!TryResolveDisplayFeatDefinition(featName, out var featDefinition))
            {
                continue;
            }

            TryAddDisplayRelevantFeat(displayFeats, featDefinition, true);
        }

        if (includeBuildingData)
        {
            if (Tabletop2024Context.TryGetBackgroundBonusFeatForDisplay(hero, buildingData, out var backgroundFeat))
            {
                TryAddDisplayRelevantFeat(displayFeats, backgroundFeat, true);
            }

            if (Tabletop2024Context.TryGetHumanOriginFeatForCharacterBuildingDisplay(
                    hero,
                    buildingData,
                    out var humanOriginFeat))
            {
                TryAddDisplayRelevantFeat(displayFeats, humanOriginFeat, true);
            }
        }
        else
        {
            AddDisplayOnlyFallbackFeatsForExistingHero(hero, displayFeats);
        }

#if DEBUG
        LogActualDisplayFeats(hero, includeBuildingData, displayFeats);
#endif

        return displayFeats;
    }

    internal static bool TryResolveDisplayFeatDefinition(string featName, out FeatDefinition featDefinition)
    {
        featDefinition = null;

        if (string.IsNullOrEmpty(featName))
        {
            return false;
        }

        if (TryGetDefinition(featName, out featDefinition))
        {
            return true;
        }

        var canonicalName = Tabletop2024Context.GetCanonicalTabletopFeatName(featName);

        if (canonicalName != featName &&
            TryGetDefinition(canonicalName, out featDefinition))
        {
            return true;
        }

        return Tabletop2024Context.TryResolveModeAwareFeatDefinition(featName, out featDefinition);
    }

    private static void TryAddDisplayRelevantFeat(
        List<FeatDefinition> displayFeats,
        FeatDefinition feat,
        bool allowHideFromFeatsLeaf = false)
    {
        if (displayFeats == null ||
            feat == null ||
            !IsDisplayRelevantFeat(feat, allowHideFromFeatsLeaf))
        {
            return;
        }

        feat.GuiPresentation.hidden = false;

        if (displayFeats.Any(existingFeat =>
                existingFeat != null &&
                existingFeat.Name == feat.Name))
        {
            return;
        }

        var equivalentIndex = displayFeats.FindIndex(existingFeat =>
            existingFeat != null &&
            Tabletop2024Context.AreEquivalentTabletopFeatNames(existingFeat.Name, feat.Name));

        if (equivalentIndex >= 0)
        {
            var existingFeat = displayFeats[equivalentIndex];

            if (ShouldPreferDisplayFeat(feat, existingFeat))
            {
                displayFeats[equivalentIndex] = feat;
            }

            return;
        }

        displayFeats.Add(feat);
    }

    private static void AddDisplayOnlyFallbackFeatsForExistingHero(
        RulesetCharacterHero hero,
        List<FeatDefinition> displayFeats)
    {
        if (hero == null || displayFeats == null)
        {
            return;
        }

        if (Tabletop2024Context.TryGetBackgroundBonusFeatForDisplay(hero, out var backgroundFeat))
        {
            TryAddDisplayRelevantFeat(displayFeats, backgroundFeat, true);
        }

        if (Tabletop2024Context.TryGetHumanOriginFeatForExistingHeroMarker(hero, out var humanOriginFeat))
        {
            TryAddDisplayRelevantFeat(displayFeats, humanOriginFeat, true);
        }
    }

    private static bool ShouldPreferDisplayFeat(FeatDefinition candidateFeat, FeatDefinition existingFeat)
    {
        if (candidateFeat == null)
        {
            return false;
        }

        if (existingFeat == null)
        {
            return true;
        }

        return Tabletop2024Context.IsManagedTabletopFeat(candidateFeat) &&
               !Tabletop2024Context.IsManagedTabletopFeat(existingFeat);
    }

    private static bool IsDisplayRelevantFeat(FeatDefinition feat, bool allowHideFromFeatsLeaf)
    {
        if (feat == null)
        {
            return false;
        }

        if (Tabletop2024Context.IsManagedTabletopFeat(feat))
        {
            return Tabletop2024Context.IsDisplayableManagedTabletopLeaf(feat);
        }

        return feat.GetFirstSubFeatureOfType<IGroupedFeat>() == null &&
               !Tabletop2024Context.IsTabletopContainerGroup(feat) &&
               !Tabletop2024Context.IsNonSelectableTabletopGroup(feat) &&
               (allowHideFromFeatsLeaf || !feat.HasSubFeatureOfType<HideFromFeats>()) &&
               Tabletop2024Context.GetCanonicalTabletopFeatName(feat.Name) != "FeatSkilled";
    }

    [Conditional("DEBUG")]
    private static void LogActualDisplayFeats(
        RulesetCharacterHero hero,
        bool includeBuildingData,
        List<FeatDefinition> displayFeats)
    {
        var trainedNames = hero?.TrainedFeats?.Where(feat => feat != null).Select(feat => feat.Name) ?? [];
        var proficiencyNames = hero?.FeatProficiencies?.Where(name => !string.IsNullOrEmpty(name)) ?? [];
        var buildingDataNames = includeBuildingData
            ? hero?.GetHeroBuildingData()?.LevelupTrainedFeats?.Values
                .Where(feats => feats != null)
                .SelectMany(feats => feats)
                .Where(feat => feat != null)
                .Select(feat => feat.Name) ?? []
            : [];
        var finalNames = displayFeats?.Where(feat => feat != null).Select(feat => feat.Name) ?? [];

        if (finalNames.Any() ||
            (!trainedNames.Any() &&
             !proficiencyNames.Any() &&
             !buildingDataNames.Any()))
        {
            return;
        }

        Main.Log(
            $"Actual display feats: hero={hero?.Name ?? "null"} guid={(hero != null ? hero.Guid.ToString() : "null")} " +
            $"use2024={Main.Settings.EnableTabletopFeatRules2024} includeBuildingData={includeBuildingData} " +
            $"trained=[{string.Join(", ", trainedNames)}] " +
            $"proficiencies=[{string.Join(", ", proficiencyNames)}] " +
            $"levelup=[{string.Join(", ", buildingDataNames)}] " +
            $"display=[{string.Join(", ", finalNames)}]");
    }

    private static IEnumerable<FeatDefinition> EnumerateTooltipFeatGroupChildren(FeatDefinition feat)
    {
        if (feat == null)
        {
            return [];
        }

        if (Main.Settings.EnableTabletopFeatRules2024)
        {
            return Tabletop2024Context.GetAllowedGameFeatChildren(feat)
                .Where(child => child != null)
                .Distinct();
        }

        if (feat.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeat)
        {
            return [];
        }

        return groupedFeat
            .GetSubFeats(true, false)
            .Where(child => child != null && IsVisibleLegacyGroupedFeatChild(child, feat))
            .Distinct();
    }

    internal static bool IsVisibleLegacySelectionRoot(FeatDefinition feat)
    {
        if (feat == null ||
            Tabletop2024Context.IsManagedTabletopFeat(feat) ||
            Tabletop2024Context.IsTabletopContainerGroup(feat) ||
            Tabletop2024Context.IsNonSelectableTabletopGroup(feat) ||
            Tabletop2024Context.GetCanonicalTabletopFeatName(feat.Name) == "FeatSkilled" ||
            FightingStyleContext.HideFightingStyle(feat) ||
            feat.HasSubFeatureOfType<HideFromFeats>())
        {
            return false;
        }

        if (FeatGroups.Contains(feat))
        {
            return Main.Settings.FeatGroupEnabled.Contains(feat.Name) ||
                   ShouldForceShowLegacyStructuralRootGroup(feat);
        }

        return Feats.Contains(feat) &&
               Main.Settings.FeatEnabled.Contains(feat.Name);
    }

    private static HashSet<FeatDefinition> BuildGroupedLeafVariantSet()
    {
        var groupedLeafVariants = new HashSet<FeatDefinition>();
        var visitedGroups = new HashSet<string>();

        foreach (var group in GroupFeats.Groups.Where(group => group != null))
        {
            CollectGroupedLeafVariants(group, groupedLeafVariants, visitedGroups);
        }

        return groupedLeafVariants;
    }

    private static void CollectGroupedLeafVariants(
        FeatDefinition group,
        HashSet<FeatDefinition> groupedLeafVariants,
        HashSet<string> visitedGroups)
    {
        if (group?.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeat ||
            !visitedGroups.Add(group.Name))
        {
            return;
        }

        foreach (var child in groupedFeat.GetSubFeats(true, false) ?? [])
        {
            if (child == null)
            {
                continue;
            }

            if (child.GetFirstSubFeatureOfType<IGroupedFeat>() != null)
            {
                CollectGroupedLeafVariants(child, groupedLeafVariants, visitedGroups);

                continue;
            }

            if (IsGroupedLeafVariantOf(group, child))
            {
                groupedLeafVariants.Add(child);
            }
        }
    }

    internal static bool IsVisibleLegacyGroupedFeatChild(FeatDefinition feat, FeatDefinition parentGroup)
    {
        if (feat == null)
        {
            return false;
        }

        if (Tabletop2024Context.IsManagedTabletopFeat(feat) ||
            Tabletop2024Context.IsTabletopContainerGroup(feat) ||
            Tabletop2024Context.IsNonSelectableTabletopGroup(feat) ||
            Tabletop2024Context.GetCanonicalTabletopFeatName(feat.Name) == "FeatSkilled" ||
            FightingStyleContext.HideFightingStyle(feat))
        {
            LogLegacyGroupedChildRejected(parentGroup, feat, "common-filter");

            return false;
        }

        var hasRegisteredRootState = TryGetLegacyRegisteredRootEnabledState(feat, out var isEnabledRegisteredRoot);

        if (feat.GetFirstSubFeatureOfType<IGroupedFeat>() != null)
        {
            if (hasRegisteredRootState && !isEnabledRegisteredRoot)
            {
                LogLegacyGroupedChildRejected(parentGroup, feat, "registered-root-disabled");

                return false;
            }

            var hasVisibleDescendant = HasVisibleLegacyGroupedFeatDescendant(feat);

            if (!hasVisibleDescendant)
            {
                LogLegacyGroupedChildRejected(parentGroup, feat, "no-visible-descendant");
            }

            return hasVisibleDescendant;
        }

        if (IsGroupedLeafVariantOf(parentGroup, feat))
        {
            return true;
        }

        if (hasRegisteredRootState)
        {
            if (!isEnabledRegisteredRoot)
            {
                LogLegacyGroupedChildRejected(parentGroup, feat, "registered-leaf-disabled");
            }

            return isEnabledRegisteredRoot;
        }

        var visible = !feat.GuiPresentation.hidden;

        if (!visible)
        {
            LogLegacyGroupedChildRejected(parentGroup, feat, "hidden-unregistered-child");
        }

        return visible;
    }

    internal static bool HasVisibleLegacyGroupedFeatDescendant(FeatDefinition feat)
    {
        if (feat?.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeat)
        {
            return false;
        }

        var subFeats = groupedFeat.GetSubFeats(true, false);

        if (subFeats == null)
        {
            return false;
        }

        return subFeats
            .Where(subFeat => subFeat != null)
            .Any(subFeat => IsVisibleLegacyGroupedFeatChild(subFeat, feat));
    }

    private static bool ShouldForceShowLegacyStructuralRootGroup(FeatDefinition group)
    {
        return !Main.Settings.EnableTabletopFeatRules2024 &&
               group != null &&
               FeatGroups.Contains(group) &&
               LegacyStructuralRootGroupNames.Contains(group.Name) &&
               HasVisibleLegacyGroupedFeatDescendant(group);
    }

    private static HashSet<FeatDefinition> BuildLegacyNestedGroupedDescendantSet()
    {
        var descendants = new HashSet<FeatDefinition>();
        var visitedGroups = new HashSet<FeatDefinition>();
        var allGroups = GroupFeats.Groups
            .Concat(FeatGroups)
            .Concat(Feats)
            .Where(group =>
                group?.GetFirstSubFeatureOfType<IGroupedFeat>() != null &&
                !Tabletop2024Context.IsManagedTabletopFeat(group))
            .Distinct()
            .ToArray();
        var childGroups = new HashSet<FeatDefinition>();

        foreach (var group in allGroups)
        {
            var groupedFeat = group.GetFirstSubFeatureOfType<IGroupedFeat>();
            var subFeats = groupedFeat?.GetSubFeats(true, false);

            if (subFeats == null)
            {
                continue;
            }

            foreach (var childGroup in subFeats
                         .Where(child =>
                             child?.GetFirstSubFeatureOfType<IGroupedFeat>() != null &&
                             !Tabletop2024Context.IsManagedTabletopFeat(child)))
            {
                childGroups.Add(childGroup);
            }
        }

        foreach (var group in allGroups.Where(group => !childGroups.Contains(group)))
        {
            CollectAllLegacyGroupedDescendants(group, descendants, visitedGroups);
        }

        return descendants;
    }

    private static void CollectAllLegacyGroupedDescendants(
        FeatDefinition group,
        HashSet<FeatDefinition> descendants,
        HashSet<FeatDefinition> visitedGroups)
    {
        if (group?.GetFirstSubFeatureOfType<IGroupedFeat>() is not { } groupedFeat ||
            !visitedGroups.Add(group))
        {
            return;
        }

        var subFeats = groupedFeat.GetSubFeats(true, false);

        if (subFeats == null)
        {
            return;
        }

        foreach (var subFeat in subFeats.Where(subFeat =>
                     subFeat != null &&
                     !Tabletop2024Context.IsManagedTabletopFeat(subFeat)))
        {
            descendants.Add(subFeat);

            if (subFeat.GetFirstSubFeatureOfType<IGroupedFeat>() != null)
            {
                CollectAllLegacyGroupedDescendants(subFeat, descendants, visitedGroups);
            }
        }
    }

    internal static bool IsEnabledLegacyRegisteredRoot(FeatDefinition feat)
    {
        return TryGetLegacyRegisteredRootEnabledState(feat, out var enabled) && enabled;
    }

    private static bool IsGroupedLeafVariantOf(FeatDefinition parentGroup, FeatDefinition child)
    {
        return parentGroup != null &&
               child != null &&
               child.GetFirstSubFeatureOfType<IGroupedFeat>() == null &&
               !string.IsNullOrEmpty(parentGroup.FamilyTag) &&
               parentGroup.FamilyTag == child.FamilyTag;
    }

    private static bool TryGetLegacyRegisteredRootEnabledState(FeatDefinition feat, out bool enabled)
    {
        enabled = false;

        if (feat == null)
        {
            return false;
        }

        if (Feats.Contains(feat))
        {
            enabled = Main.Settings.FeatEnabled.Contains(feat.Name);

            return true;
        }

        if (FeatGroups.Contains(feat))
        {
            enabled = Main.Settings.FeatGroupEnabled.Contains(feat.Name);

            return true;
        }

        return false;
    }

    private static void LogLegacyGroupedChildRejected(FeatDefinition parentGroup, FeatDefinition child, string reason)
    {
#if DEBUG
        Main.Log(
            $"Legacy grouped child rejected: parent={parentGroup?.Name ?? "null"}, child={child?.Name ?? "null"}, reason={reason}");
#endif
    }

    internal static void RebindPanelChildren(
        FeatSubPanel panel,
        string stageTag,
        string previousStageTag,
        HeroDefinitions.PointsPoolType currentPoolType,
        bool selectionMode)
    {
        if (panel?.table == null ||
            panel.relevantFeats == null)
        {
            return;
        }

        for (var i = 0; i < panel.relevantFeats.Count && i < panel.table.childCount; i++)
        {
            var child = panel.table.GetChild(i);
            var featItem = child.GetComponent<FeatItem>();
            var featDefinition = panel.relevantFeats[i];

            if (!featItem || featDefinition == null)
            {
                continue;
            }

            featItem.StageTag = selectionMode ? stageTag : string.Empty;
            featItem.PreviousStageTag = selectionMode ? previousStageTag : string.Empty;
            featItem.CurrentPoolType = selectionMode ? currentPoolType : HeroDefinitions.PointsPoolType.Feat;
            featItem.Bind(
                panel.InspectedCharacter,
                featDefinition,
                selectionMode ? panel.OnItemClicked : null,
                selectionMode ? panel.OnItemHoverChanged : null,
                true);

            if (!selectionMode)
            {
                featItem.Refresh(ProficiencyBaseItem.InteractiveMode.Static, HeroDefinitions.PointsPoolType.Feat);
                featItem.OnItemClicked = null;
                featItem.OnItemHoverChanged = null;
                featItem.StageTag = string.Empty;
                featItem.PreviousStageTag = string.Empty;
                featItem.CurrentPoolType = HeroDefinitions.PointsPoolType.Feat;
            }

            if (featItem.Tooltip != null)
            {
                featItem.Tooltip.Anchor = panel.table;
                featItem.Tooltip.AnchorMode = TooltipDefinitions.AnchorMode.LEFT_CENTER;
            }
        }

        panel.DispatchItems();
    }

    private static void LoadFeatsPointPools()
    {
        // create feats point pools
        // +1 here as need to count the Alternate Human Feat
        for (var i = 1; i <= MaxInitialFeats + 1; i++)
        {
            var s = i.ToString();

            _ = FeatureDefinitionPointPoolBuilder
                .Create($"PointPool{i}BonusFeats")
                .SetGuiPresentation(
                    Gui.Format("Feature/&PointPoolSelectBonusFeatsTitle", s),
                    Gui.Format("Feature/&PointPoolSelectBonusFeatsDescription", s))
                .SetPool(HeroDefinitions.PointsPoolType.Feat, i)
                .AddToDB();
        }
    }

    internal static void SwitchFirstLevelTotalFeats()
    {
        var effectiveAlternateHuman = Tabletop2024Context.IsAlternateHumanEffectivelyEnabled();

        if (PreviousTotalFeatsGrantedFirstLevel > -1)
        {
            UnloadRacesLevel1Feats(PreviousTotalFeatsGrantedFirstLevel, PreviousAlternateHuman);
        }

        PreviousTotalFeatsGrantedFirstLevel = Main.Settings.TotalFeatsGrantedFirstLevel;
        PreviousAlternateHuman = effectiveAlternateHuman;
        LoadRacesLevel1Feats(Main.Settings.TotalFeatsGrantedFirstLevel, effectiveAlternateHuman);
    }

    private static void LoadRacesLevel1Feats(int initialFeats, bool alternateHuman)
    {
        var human = Human;

        BuildFeatureUnlocks(initialFeats, alternateHuman, out var featureUnlockByLevelNonHuman,
            out var featureUnlockByLevelHuman);

        foreach (var characterRaceDefinition in DatabaseRepository.GetDatabase<CharacterRaceDefinition>())
        {
            if (IsSubRace(characterRaceDefinition))
            {
                continue;
            }

            if (alternateHuman && characterRaceDefinition == human)
            {
                if (featureUnlockByLevelHuman != null)
                {
                    human.FeatureUnlocks.Add(featureUnlockByLevelHuman);
                }

                var pointPoolAbilityScoreImprovement =
                    new FeatureUnlockByLevel(PointPoolAbilityScoreImprovement, 1);
                human.FeatureUnlocks.Add(pointPoolAbilityScoreImprovement);

                var pointPoolHumanSkillPool = new FeatureUnlockByLevel(PointPoolHumanSkillPool, 1);
                human.FeatureUnlocks.Add(pointPoolHumanSkillPool);

                Remove(human,
                    FeatureDefinitionAttributeModifiers
                        .AttributeModifierHumanAbilityScoreIncrease);
            }
            else
            {
                if (featureUnlockByLevelNonHuman != null)
                {
                    characterRaceDefinition.FeatureUnlocks.Add(featureUnlockByLevelNonHuman);
                }
            }
        }
    }

    private static void UnloadRacesLevel1Feats(int initialFeats, bool alternateHuman)
    {
        var human = Human;

        BuildFeatureUnlocks(initialFeats, alternateHuman,
            out var featureUnlockByLevelNonHuman,
            out var featureUnlockByLevelHuman);

        foreach (var characterRaceDefinition in DatabaseRepository.GetDatabase<CharacterRaceDefinition>())
        {
            if (IsSubRace(characterRaceDefinition))
            {
                continue;
            }

            if (alternateHuman && characterRaceDefinition == human)
            {
                if (featureUnlockByLevelHuman != null)
                {
                    Remove(human, featureUnlockByLevelHuman);
                }

                Remove(human, PointPoolAbilityScoreImprovement);
                Remove(human, PointPoolHumanSkillPool);

                var humanAttributeIncrease = new FeatureUnlockByLevel(
                    FeatureDefinitionAttributeModifiers.AttributeModifierHumanAbilityScoreIncrease, 1);

                human.FeatureUnlocks.Add(humanAttributeIncrease);
            }
            else
            {
                if (featureUnlockByLevelNonHuman != null)
                {
                    Remove(characterRaceDefinition, featureUnlockByLevelNonHuman);
                }
            }
        }
    }

    private static void Remove(
        [NotNull] CharacterRaceDefinition characterRaceDefinition,
        BaseDefinition toRemove)
    {
        var ndx = -1;

        for (var i = 0; i < characterRaceDefinition.FeatureUnlocks.Count; i++)
        {
            if (characterRaceDefinition.FeatureUnlocks[i].Level == 1 &&
                characterRaceDefinition.FeatureUnlocks[i].FeatureDefinition == toRemove)
            {
                ndx = i;
            }
        }

        if (ndx >= 0)
        {
            characterRaceDefinition.FeatureUnlocks.RemoveAt(ndx);
        }
    }

    private static void Remove(
        [NotNull] CharacterRaceDefinition characterRaceDefinition,
        [NotNull] FeatureUnlockByLevel featureUnlockByLevel)
    {
        Remove(characterRaceDefinition, featureUnlockByLevel.FeatureDefinition);
    }

    private static bool IsSubRace(CharacterRaceDefinition raceDefinition)
    {
        return DatabaseRepository.GetDatabase<CharacterRaceDefinition>()
            .Any(crd => crd.SubRaces.Contains(raceDefinition));
    }

    internal static void SwitchAsiAndFeat()
    {
        FeatureSetAbilityScoreChoice.mode = Main.Settings.EnablesAsiAndFeat
            ? FeatureDefinitionFeatureSet.FeatureSetMode.Union
            : FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion;
    }


    internal static void SwitchEveryFourLevelsFeats(bool isMiddle = false)
    {
        var levels = isMiddle ? new[] { 6, 14 } : [2, 10, 18];
        var dbCharacterClassDefinition = DatabaseRepository.GetDatabase<CharacterClassDefinition>();
        var pointPool1BonusFeats = GetDefinition<FeatureDefinitionPointPool>("PointPool1BonusFeats");
        var pointPool2BonusFeats = GetDefinition<FeatureDefinitionPointPool>("PointPool2BonusFeats");
        var enable = isMiddle
            ? Main.Settings.EnableFeatsAtEveryFourLevelsMiddle
            : Main.Settings.EnableFeatsAtEveryFourLevels;

        foreach (var characterClassDefinition in dbCharacterClassDefinition)
        {
            foreach (var level in levels)
            {
                var featureUnlockPointPool1 = new FeatureUnlockByLevel(pointPool1BonusFeats, level);
                var featureUnlockPointPool2 = new FeatureUnlockByLevel(pointPool2BonusFeats, level);

                if (enable)
                {
                    characterClassDefinition.FeatureUnlocks.Add(ShouldBe2Points()
                        ? featureUnlockPointPool2
                        : featureUnlockPointPool1);
                }
                else
                {
                    if (ShouldBe2Points())
                    {
                        characterClassDefinition.FeatureUnlocks.RemoveAll(x =>
                            x.FeatureDefinition == pointPool2BonusFeats && x.level == level);
                    }
                    else
                    {
                        characterClassDefinition.FeatureUnlocks.RemoveAll(x =>
                            x.FeatureDefinition == pointPool1BonusFeats && x.level == level);
                    }
                }

                continue;

                bool ShouldBe2Points()
                {
                    return (characterClassDefinition == Rogue && level is 10 && !isMiddle) ||
                           (characterClassDefinition == Fighter && level is 6 or 14 && isMiddle);
                }
            }

            characterClassDefinition.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }
    }

    private static void BuildFeatureUnlocks(
        int initialFeats,
        bool alternateHuman,
        [CanBeNull] out FeatureUnlockByLevel featureUnlockByLevelNonHuman,
        [CanBeNull] out FeatureUnlockByLevel featureUnlockByLevelHuman)
    {
        string name;

        featureUnlockByLevelNonHuman = null;
        featureUnlockByLevelHuman = null;

        switch (initialFeats)
        {
            case 0:
            {
                if (alternateHuman)
                {
                    featureUnlockByLevelHuman = new FeatureUnlockByLevel(PointPoolBonusFeat, 1);
                }

                break;
            }
            case 1:
            {
                featureUnlockByLevelNonHuman = new FeatureUnlockByLevel(PointPoolBonusFeat, 1);

                name = "PointPool2BonusFeats";
                if (alternateHuman && TryGetDefinition<FeatureDefinitionPointPool>(name, out var pointPool2BonusFeats))
                {
                    featureUnlockByLevelHuman = new FeatureUnlockByLevel(pointPool2BonusFeats, 1);
                }

                break;
            }
            case > 1:
            {
                name = $"PointPool{initialFeats}BonusFeats";
                if (TryGetDefinition<FeatureDefinitionPointPool>(name, out var featureDefinitionPointPool))
                {
                    featureUnlockByLevelNonHuman = new FeatureUnlockByLevel(featureDefinitionPointPool, 1);
                }

                name = $"PointPool{initialFeats + 1}BonusFeats";
                if (alternateHuman && TryGetDefinition<FeatureDefinitionPointPool>(name, out var pointPoolXBonusFeats))
                {
                    featureUnlockByLevelHuman = new FeatureUnlockByLevel(pointPoolXBonusFeats, 1);
                }

                break;
            }
        }
    }

    internal sealed class HideFromFeats
    {
        internal static readonly HideFromFeats Marker = new();
    }
}
