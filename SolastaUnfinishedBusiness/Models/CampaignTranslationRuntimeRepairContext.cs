using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Models;

internal static class CampaignTranslationRuntimeRepairContext
{
    internal static void RepairCampaignMap([CanBeNull] GameCampaignMap campaignMap)
    {
        if (campaignMap == null || Gui.GameCampaign?.campaignDefinition?.IsUserCampaign != true)
        {
            return;
        }

        var userCampaign = ServiceRepository.GetService<ISessionService>()?.Session?.UserCampaign;

        if (userCampaign == null)
        {
            return;
        }

        var userLocationNames = GetUserLocationNames(userCampaign);

        if (userLocationNames.Count == 0)
        {
            return;
        }

        RepairCampaignNodes(campaignMap, userCampaign, userLocationNames);
        RepairUserLocationStatuses(userLocationNames);
    }

    private static HashSet<string> GetUserLocationNames([NotNull] UserCampaign userCampaign)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var userLocation in userCampaign.UserLocations ?? Enumerable.Empty<UserLocation>())
        {
            if (!string.IsNullOrWhiteSpace(userLocation?.ContentItemTitle))
            {
                names.Add(userLocation.ContentItemTitle);
            }
        }

        return names;
    }

    private static void RepairCampaignNodes(
        [NotNull] GameCampaignMap campaignMap,
        [NotNull] UserCampaign userCampaign,
        [NotNull] HashSet<string> userLocationNames)
    {
        var gameNodes = campaignMap.GameCampaignNodes?.ToArray();
        var sourceNodes = userCampaign.CampaignMapNodes?.ToArray();

        if (gameNodes == null || gameNodes.Length == 0 || sourceNodes == null || sourceNodes.Length == 0)
        {
            return;
        }

        var count = Math.Min(gameNodes.Length, sourceNodes.Length);

        for (var index = 0; index < count; index++)
        {
            RepairCampaignNode(gameNodes[index], sourceNodes[index], userLocationNames);
        }
    }

    private static void RepairCampaignNode(
        [CanBeNull] GameCampaignNode gameNode,
        [CanBeNull] UserCampaignMapNode sourceNode,
        [NotNull] HashSet<string> userLocationNames)
    {
        if (gameNode == null || sourceNode?.PartyStart == true)
        {
            return;
        }

        var userLocationName = !string.IsNullOrWhiteSpace(sourceNode?.UserLocationName)
            ? sourceNode.UserLocationName
            : gameNode.UserLocationName;

        if (!string.IsNullOrWhiteSpace(userLocationName) && userLocationNames.Contains(userLocationName))
        {
            return;
        }

        if (gameNode.NodeKnowledge != GameCampaignDefinitions.NodeKnowledge.Unknown)
        {
            gameNode.NodeKnowledge = GameCampaignDefinitions.NodeKnowledge.Unknown;
        }
    }

    private static void RepairUserLocationStatuses([NotNull] HashSet<string> userLocationNames)
    {
        var statusByLocation = Gui.GameCampaign?.UserLocationsStatus;

        if (statusByLocation == null)
        {
            return;
        }

        foreach (var userLocationName in statusByLocation.Keys.ToArray())
        {
            if (!userLocationNames.Contains(userLocationName))
            {
                statusByLocation[userLocationName] = LocationDefinitions.UserLocationStatus.Hidden;
            }
        }
    }
}
