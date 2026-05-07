using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;

namespace SolastaUnfinishedBusiness.Models;

internal static class CampaignTranslationRuntimeRepairContext
{
    private static UserCampaign _cachedUserCampaign;
    private static RepairIndex _cachedRepairIndex;
    private static RepairSource _cachedRepairSource;

    internal static void Unload()
    {
        _cachedUserCampaign = null;
        _cachedRepairIndex = null;
        _cachedRepairSource = null;
    }

    internal static void RepairCampaignMap([CanBeNull] GameCampaignMap campaignMap)
    {
        if (campaignMap == null || !TryGetCurrentUserCampaign(out var userCampaign))
        {
            return;
        }

        var repairIndex = GetRepairIndex(userCampaign);

        if (repairIndex.UserLocationNames.Count == 0)
        {
            return;
        }

        RepairSessionLocation(repairIndex);
        RepairCampaignNodes(campaignMap, repairIndex);
        RepairCampaignSegments(campaignMap, repairIndex);
        RepairUserLocationStatuses(repairIndex);
    }

    internal static void RepairUserCampaignReferences()
    {
        if (!TryGetCurrentUserCampaign(out var userCampaign))
        {
            return;
        }

        var repairIndex = GetRepairIndex(userCampaign);

        RepairUserBiomes(userCampaign, repairIndex);

        if (repairIndex.UserLocationNames.Count == 0)
        {
            return;
        }

        RepairSessionLocation(repairIndex);
        RepairCampaignSourceNodes(userCampaign, repairIndex);
        RepairUserLocationGadgets(userCampaign, repairIndex);
    }

    internal static void RepairTravelJournalSource()
    {
        if (!TryGetCurrentUserCampaign(out var userCampaign))
        {
            return;
        }

        var repairIndex = GetRepairIndex(userCampaign);

        RepairUserBiomes(userCampaign, repairIndex);
        RepairCampaignSegments(Gui.GameCampaign?.GameCampaignMap, repairIndex);
    }

    internal static bool TryRepairTravelJournalLine(
        [CanBeNull] string logLine,
        [CanBeNull] out string repairedLogLine)
    {
        repairedLogLine = null;

        return !string.IsNullOrWhiteSpace(logLine) &&
               TryGetCurrentUserCampaign(out var userCampaign) &&
               GetRepairIndex(userCampaign).TryRepairTravelJournalLine(logLine, out repairedLogLine);
    }

    internal static void RepairSessionLocation()
    {
        if (!TryGetCurrentUserCampaign(out var userCampaign))
        {
            return;
        }

        var repairIndex = GetRepairIndex(userCampaign);

        if (repairIndex.UserLocationNames.Count == 0)
        {
            return;
        }

        RepairSessionLocation(repairIndex);
    }

    internal static void RepairUserLocationName([CanBeNull] ref string userLocationName)
    {
        if (!TryGetCurrentUserCampaign(out var userCampaign))
        {
            return;
        }

        var repairIndex = GetRepairIndex(userCampaign);

        if (repairIndex.UserLocationNames.Count == 0 ||
            !TryRepairLocationName(userLocationName, repairIndex, out var repairedLocationName))
        {
            return;
        }

        userLocationName = repairedLocationName;
    }

    internal static void RepairUserLocationTransitions([CanBeNull] UserLocation userLocation)
    {
        if (userLocation == null || !TryGetCurrentUserCampaign(out var userCampaign))
        {
            return;
        }

        var repairIndex = GetRepairIndex(userCampaign);

        if (repairIndex.UserLocationNames.Count == 0)
        {
            return;
        }

        RepairUserLocationGadgets(userLocation, repairIndex);
    }

    internal static void RepairWorldLocationGadgets(
        [CanBeNull] WorldLocation worldLocation,
        [CanBeNull] UserLocation userLocation)
    {
        if (worldLocation == null || userLocation == null || !TryGetCurrentUserCampaign(out var userCampaign))
        {
            return;
        }

        var repairIndex = GetRepairIndex(userCampaign);

        if (repairIndex.UserLocationNames.Count == 0)
        {
            return;
        }

        RepairUserLocationGadgets(userLocation, repairIndex);

        if (!repairIndex.TryGetLocationIndex(userLocation.ContentItemTitle, out var sourceLocationIndex))
        {
            return;
        }

        foreach (var worldGadget in EnumerateWorldGadgets(worldLocation))
        {
            if (!TryGetWorldGadgetUniqueName(worldGadget, out var uniqueName) ||
                !sourceLocationIndex.TryGetGadgetByUniqueName(uniqueName, out var sourceGadgetEntry))
            {
                continue;
            }

            RepairWorldGadgetWaypointDefinition(worldGadget, sourceGadgetEntry.Gadget);

            if (worldGadget.GameGadget != null)
            {
                RepairGameGadgetText(worldGadget.GameGadget, sourceGadgetEntry.Gadget, repairIndex);
            }
        }
    }

    private static void RepairSessionLocation([NotNull] RepairIndex repairIndex)
    {
        var session = ServiceRepository.GetService<ISessionService>()?.Session;

        if (session == null ||
            !TryRepairLocationName(session.UserLocationName, repairIndex, out var repairedLocationName))
        {
            return;
        }

        session.UserLocationName = repairedLocationName;
    }

    private static void RepairCampaignSourceNodes(
        [NotNull] UserCampaign userCampaign,
        [NotNull] RepairIndex repairIndex)
    {
        var campaignMapNodes = userCampaign.CampaignMapNodes?.ToArray();

        if (campaignMapNodes == null)
        {
            return;
        }

        for (var index = 0; index < campaignMapNodes.Length; index++)
        {
            var sourceNode = campaignMapNodes[index];

            RepairCampaignMapNodeText(sourceNode, repairIndex.GetCampaignMapNode(index));

            if (sourceNode?.PartyStart == true ||
                !TryRepairLocationName(sourceNode?.UserLocationName, repairIndex, out var repairedLocationName))
            {
                continue;
            }

            sourceNode.UserLocationName = repairedLocationName;
        }
    }

    private static void RepairCampaignNodes(
        [NotNull] GameCampaignMap campaignMap,
        [NotNull] RepairIndex repairIndex)
    {
        var gameNodes = campaignMap.GameCampaignNodes?.ToArray();
        var sourceNodes = repairIndex.CampaignMapNodes;

        if (gameNodes == null || gameNodes.Length == 0 || sourceNodes == null || sourceNodes.Length == 0)
        {
            return;
        }

        var count = Math.Min(gameNodes.Length, sourceNodes.Length);

        for (var index = 0; index < count; index++)
        {
            RepairCampaignNode(gameNodes[index], sourceNodes[index], repairIndex);
        }
    }

    private static void RepairCampaignSegments(
        [CanBeNull] GameCampaignMap campaignMap,
        [NotNull] RepairIndex repairIndex)
    {
        var gameSegments = campaignMap?.GameCampaignSegments;

        if (gameSegments == null || gameSegments.Length == 0)
        {
            return;
        }

        foreach (var gameSegment in gameSegments)
        {
            RepairBiomeDefinition(gameSegment?.BiomeDefinition, repairIndex);
        }
    }

    private static void RepairBiomeDefinition(
        [CanBeNull] BiomeDefinition biomeDefinition,
        [NotNull] RepairIndex repairIndex)
    {
        var narrativeLines = biomeDefinition?.NarrativeEventBasicLines;

        if (narrativeLines == null || narrativeLines.Count == 0)
        {
            return;
        }

        for (var lineIndex = 0; lineIndex < narrativeLines.Count; lineIndex++)
        {
            if (repairIndex.TryRepairTravelJournalLine(narrativeLines[lineIndex], out var repairedLine))
            {
                narrativeLines[lineIndex] = repairedLine;
            }
        }
    }

    private static void RepairCampaignNode(
        [CanBeNull] GameCampaignNode gameNode,
        [CanBeNull] SourceCampaignMapNode sourceNode,
        [NotNull] RepairIndex repairIndex)
    {
        if (gameNode == null || sourceNode?.PartyStart == true)
        {
            return;
        }

        RepairGameCampaignNodeText(gameNode, sourceNode);

        var gameNodeLocationName = gameNode.UserLocationName;

        if (!string.IsNullOrWhiteSpace(gameNodeLocationName) &&
            repairIndex.UserLocationNames.Contains(gameNodeLocationName))
        {
            return;
        }

        if (gameNode.NodeKnowledge != GameCampaignDefinitions.NodeKnowledge.Unknown)
        {
            gameNode.NodeKnowledge = GameCampaignDefinitions.NodeKnowledge.Unknown;
        }
    }

    private static void RepairUserLocationStatuses([NotNull] RepairIndex repairIndex)
    {
        var statusByLocation = Gui.GameCampaign?.UserLocationsStatus;

        if (statusByLocation == null)
        {
            return;
        }

        foreach (var userLocationName in statusByLocation.Keys.ToArray())
        {
            if (repairIndex.UserLocationNames.Contains(userLocationName))
            {
                continue;
            }

            if (TryRepairLocationName(userLocationName, repairIndex, out var repairedLocationName))
            {
                statusByLocation[repairedLocationName] = statusByLocation[userLocationName];
            }

            if (!repairIndex.UserLocationNames.Contains(userLocationName))
            {
                statusByLocation[userLocationName] = LocationDefinitions.UserLocationStatus.Hidden;
            }
        }
    }

    private static void RepairUserBiomes([NotNull] UserCampaign userCampaign, [NotNull] RepairIndex repairIndex)
    {
        var targetBiomes = userCampaign.UserBiomes;
        var sourceBiomes = repairIndex.UserBiomes;

        if (targetBiomes == null || targetBiomes.Count == 0 || sourceBiomes.Length == 0)
        {
            return;
        }

        var count = Math.Min(targetBiomes.Count, sourceBiomes.Length);

        for (var biomeIndex = 0; biomeIndex < count; biomeIndex++)
        {
            RepairUserBiome(targetBiomes[biomeIndex], sourceBiomes[biomeIndex]);
        }
    }

    private static void RepairUserBiome([CanBeNull] UserBiome targetBiome, [CanBeNull] SourceBiome sourceBiome)
    {
        var targetLines = targetBiome?.NarrativeEventBasicLines;
        var sourceLines = sourceBiome?.NarrativeEventBasicLines;

        if (targetLines == null || targetLines.Count == 0 || sourceLines == null || sourceLines.Count == 0)
        {
            return;
        }

        var count = Math.Min(targetLines.Count, sourceLines.Count);

        for (var lineIndex = 0; lineIndex < count; lineIndex++)
        {
            if (!string.IsNullOrWhiteSpace(sourceLines[lineIndex]))
            {
                targetLines[lineIndex] = sourceLines[lineIndex];
            }
        }
    }

    private static void RepairUserLocationGadgets([NotNull] UserCampaign userCampaign, [NotNull] RepairIndex repairIndex)
    {
        foreach (var userLocation in userCampaign.UserLocations ?? Enumerable.Empty<UserLocation>())
        {
            if (userLocation != null)
            {
                RepairUserLocationGadgets(userLocation, repairIndex);
            }
        }
    }

    private static void RepairUserLocationGadgets([NotNull] UserLocation userLocation, [NotNull] RepairIndex repairIndex)
    {
        if (!repairIndex.TryGetLocationIndex(userLocation.ContentItemTitle, out var sourceLocationIndex))
        {
            return;
        }

        foreach (var gadgetEntry in EnumerateUserGadgetEntries(userLocation))
        {
            var gadget = gadgetEntry.Gadget;

            if (gadget?.ParameterValues == null)
            {
                continue;
            }

            sourceLocationIndex.TryGetGadget(gadgetEntry, out var sourceGadgetEntry);

            foreach (var parameterValue in gadget.ParameterValues)
            {
                RepairUserGadgetParameterValue(
                    parameterValue,
                    GetSourceParameterValue(sourceGadgetEntry?.Gadget, parameterValue),
                    repairIndex);
            }
        }
    }

    private static void RepairUserGadgetParameterValue(
        [CanBeNull] UserGadgetParameterValue parameterValue,
        [CanBeNull] SourceParameterValue sourceParameterValue,
        [NotNull] RepairIndex repairIndex)
    {
        switch (GetParameterName(parameterValue))
        {
            case "DestinationLocation":
                if (TryRepairLocationName(parameterValue.StringValue, repairIndex, out var repairedLocationName))
                {
                    parameterValue.StringValue = repairedLocationName;
                }

                break;

            case "LocationsList":
                RepairDestinationList(parameterValue, sourceParameterValue, repairIndex);
                break;

            case "WaypointTitle":
                if (!string.IsNullOrWhiteSpace(sourceParameterValue?.StringValue))
                {
                    parameterValue.StringValue = sourceParameterValue.StringValue;
                }

                break;
        }
    }

    private static void RepairDestinationList(
        [NotNull] UserGadgetParameterValue parameterValue,
        [CanBeNull] SourceParameterValue sourceParameterValue,
        [NotNull] RepairIndex repairIndex)
    {
        if (parameterValue.DestinationsList == null)
        {
            return;
        }

        for (var index = 0; index < parameterValue.DestinationsList.Count; index++)
        {
            var destination = parameterValue.DestinationsList[index];

            if (destination == null ||
                string.IsNullOrWhiteSpace(destination.UserLocationName))
            {
                continue;
            }

            if (TryRepairLocationName(destination.UserLocationName, repairIndex, out var repairedLocationName))
            {
                destination.UserLocationName = repairedLocationName;
            }

            var sourceDestination = GetSourceDestination(sourceParameterValue, index, destination.UserLocationName);

            if (!string.IsNullOrWhiteSpace(sourceDestination?.DisplayedTitle))
            {
                destination.DisplayedTitle = sourceDestination.DisplayedTitle;
            }
        }
    }

    [CanBeNull]
    private static SourceParameterValue GetSourceParameterValue(
        [CanBeNull] SourceGadget sourceGadget,
        [CanBeNull] UserGadgetParameterValue parameterValue)
    {
        var parameterName = GetParameterName(parameterValue);

        return string.IsNullOrWhiteSpace(parameterName)
            ? null
            : sourceGadget?.ParameterValues?.FirstOrDefault(x => x.ParameterName == parameterName);
    }

    [CanBeNull]
    private static string GetParameterName([CanBeNull] UserGadgetParameterValue parameterValue)
    {
        return parameterValue?.GadgetParameterDescription?.Name ?? parameterValue?.GadgetParameterDescriptionName;
    }

    [CanBeNull]
    private static string GetParameterStringValue([CanBeNull] SourceGadget sourceGadget, [NotNull] string parameterName)
    {
        return sourceGadget?.ParameterValues?
            .FirstOrDefault(x => x.ParameterName == parameterName)
            ?.StringValue;
    }

    [CanBeNull]
    private static SourceParameterValue GetParameterValue(
        [CanBeNull] SourceGadget sourceGadget,
        [NotNull] string parameterName)
    {
        return sourceGadget?.ParameterValues?.FirstOrDefault(x => x.ParameterName == parameterName);
    }

    private static void RepairGameGadgetText(
        [NotNull] GameGadget gameGadget,
        [NotNull] SourceGadget sourceGadget,
        [NotNull] RepairIndex repairIndex)
    {
        var sourceDestinations = GetParameterValue(sourceGadget, "LocationsList");

        foreach (var functorParameters in EnumerateFunctorParameters(gameGadget))
        {
            RepairFunctorDestinations(functorParameters, sourceDestinations, repairIndex);
        }
    }

    private static void RepairWorldGadgetWaypointDefinition(
        [NotNull] WorldGadget worldGadget,
        [NotNull] SourceGadget sourceGadget)
    {
        var waypointTitle = GetParameterStringValue(sourceGadget, "WaypointTitle");

        if (string.IsNullOrWhiteSpace(waypointTitle))
        {
            return;
        }

        var worldNode = worldGadget.GetComponentInChildren<WorldNode>();
        var guiPresentation = worldNode?.MapWaypointDefinition?.GuiPresentation;

        if (guiPresentation == null || guiPresentation.Title == waypointTitle)
        {
            return;
        }

        guiPresentation.Title = waypointTitle;
    }

    private static void RepairFunctorDestinations(
        [NotNull] FunctorParametersDescription functorParameters,
        [CanBeNull] SourceParameterValue sourceParameterValue,
        [NotNull] RepairIndex repairIndex)
    {
        var destinations = functorParameters.Destinations;

        if (destinations == null)
        {
            return;
        }

        for (var index = 0; index < destinations.Count; index++)
        {
            var destination = destinations[index];

            if (destination == null || string.IsNullOrWhiteSpace(destination.UserLocationName))
            {
                continue;
            }

            if (TryRepairLocationName(destination.UserLocationName, repairIndex, out var repairedLocationName))
            {
                destination.UserLocationName = repairedLocationName;
            }

            var sourceDestination = GetSourceDestination(sourceParameterValue, index, destination.UserLocationName);

            if (!string.IsNullOrWhiteSpace(sourceDestination?.DisplayedTitle) &&
                destination.DisplayedTitle != sourceDestination.DisplayedTitle)
            {
                destination.DisplayedTitle = sourceDestination.DisplayedTitle;
            }
        }
    }

    [NotNull]
    private static IEnumerable<FunctorParametersDescription> EnumerateFunctorParameters([NotNull] GameGadget gameGadget)
    {
        if (gameGadget.ActiveListeners == null)
        {
            yield break;
        }

        foreach (var activeListener in gameGadget.ActiveListeners)
        {
            if (activeListener?.FunctorParams == null)
            {
                continue;
            }

            foreach (var functorParameters in activeListener.FunctorParams.OfType<FunctorParametersDescription>())
            {
                yield return functorParameters;
            }
        }
    }

    private static bool TryGetWorldGadgetUniqueName(
        [CanBeNull] WorldGadget worldGadget,
        [CanBeNull] out string uniqueName)
    {
        uniqueName = worldGadget?.UserGadget?.UniqueName ?? worldGadget?.GameGadget?.UniqueNameId;

        return !string.IsNullOrWhiteSpace(uniqueName);
    }

    [NotNull]
    private static IEnumerable<WorldGadget> EnumerateWorldGadgets([NotNull] WorldLocation worldLocation)
    {
        if (worldLocation.WorldSectors == null)
        {
            yield break;
        }

        foreach (var worldSector in worldLocation.WorldSectors)
        {
            if (worldSector?.WorldGadgets == null)
            {
                continue;
            }

            foreach (var worldGadget in worldSector.WorldGadgets)
            {
                if (worldGadget != null)
                {
                    yield return worldGadget;
                }
            }
        }
    }

    [CanBeNull]
    private static SourceDestination GetSourceDestination(
        [CanBeNull] SourceParameterValue sourceParameterValue,
        int index,
        [CanBeNull] string userLocationName)
    {
        var sourceDestinations = sourceParameterValue?.DestinationsList;

        if (sourceDestinations == null)
        {
            return null;
        }

        if (index >= 0 &&
            index < sourceDestinations.Count &&
            sourceDestinations[index]?.UserLocationName == userLocationName)
        {
            return sourceDestinations[index];
        }

        return sourceDestinations.FirstOrDefault(x => x?.UserLocationName == userLocationName);
    }

    private static void RepairCampaignMapNodeText(
        [CanBeNull] UserCampaignMapNode targetNode,
        [CanBeNull] SourceCampaignMapNode sourceNode)
    {
        if (targetNode == null || sourceNode == null)
        {
            return;
        }

        targetNode.overriddenTitle = sourceNode.OverriddenTitle;
        targetNode.overriddenDescription = sourceNode.OverriddenDescription;
        targetNode.unchartedTitle = sourceNode.UnchartedTitle;
        targetNode.unchartedDescription = sourceNode.UnchartedDescription;
    }

    private static void RepairGameCampaignNodeText(
        [CanBeNull] GameCampaignNode gameNode,
        [CanBeNull] SourceCampaignMapNode sourceNode)
    {
        if (gameNode == null || sourceNode == null)
        {
            return;
        }

        var description = gameNode.campaignNodeDescription;

        description.nodeTitle = sourceNode.OverriddenTitle;
        description.nodeDescription = sourceNode.OverriddenDescription;
        description.nodeUnchartedTitle = sourceNode.UnchartedTitle;
        description.nodeUnchartedDescription = sourceNode.UnchartedDescription;

        gameNode.campaignNodeDescription = description;
    }

    private static bool TryRepairLocationName(
        [CanBeNull] string userLocationName,
        [NotNull] RepairIndex repairIndex,
        [CanBeNull] out string repairedLocationName)
    {
        repairedLocationName = null;

        return !string.IsNullOrWhiteSpace(userLocationName) &&
               !repairIndex.UserLocationNames.Contains(userLocationName) &&
               repairIndex.TryResolve(userLocationName, out repairedLocationName);
    }

    private static bool TryGetCurrentUserCampaign([CanBeNull] out UserCampaign userCampaign)
    {
        userCampaign = ServiceRepository.GetService<ISessionService>()?.Session?.UserCampaign;

        if (userCampaign == null)
        {
            return false;
        }

        var campaignDefinition = Gui.GameCampaign?.campaignDefinition;

        return campaignDefinition == null || campaignDefinition.IsUserCampaign;
    }

    [NotNull]
    private static RepairIndex GetRepairIndex([NotNull] UserCampaign userCampaign)
    {
        if (ReferenceEquals(_cachedUserCampaign, userCampaign) && _cachedRepairIndex != null)
        {
            return _cachedRepairIndex;
        }

        _cachedUserCampaign = userCampaign;
        _cachedRepairIndex = BuildRepairIndex(userCampaign);

        return _cachedRepairIndex;
    }

    [NotNull]
    private static RepairIndex BuildRepairIndex([NotNull] UserCampaign userCampaign)
    {
        var repairSource = GetRepairSource(userCampaign);
        var sourceCampaign = repairSource.Campaign;
        var repairIndex = new RepairIndex(sourceCampaign, GetUserLocationNames(sourceCampaign));

        AddCampaignMapNodeAliases(userCampaign, repairIndex);
        AddUserLocationGadgetAliases(userCampaign, repairIndex);
        AddUserBiomeLineTranslations(userCampaign, repairIndex);

        return repairIndex;
    }

    [NotNull]
    private static HashSet<string> GetUserLocationNames([NotNull] SourceCampaign userCampaign)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var userLocation in userCampaign.UserLocations)
        {
            if (!string.IsNullOrWhiteSpace(userLocation?.ContentItemTitle))
            {
                names.Add(userLocation.ContentItemTitle);
            }
        }

        return names;
    }

    [NotNull]
    private static RepairSource GetRepairSource([NotNull] UserCampaign userCampaign)
    {
        if (TryGetCachedDiskRepairSource(userCampaign.Title, out var cachedRepairSource))
        {
            return cachedRepairSource;
        }

        if (TryLoadDiskRepairSource(userCampaign.Title, out var repairSource))
        {
            _cachedRepairSource = repairSource;

            return repairSource;
        }

        return new RepairSource(CreateSourceCampaign(userCampaign), null, 0, default);
    }

    private static bool TryGetCachedDiskRepairSource(
        [CanBeNull] string campaignTitle,
        [CanBeNull] out RepairSource repairSource)
    {
        repairSource = null;

        var cachedRepairSource = _cachedRepairSource;

        if (cachedRepairSource?.Campaign?.Title != campaignTitle ||
            string.IsNullOrWhiteSpace(cachedRepairSource.SourcePath))
        {
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(cachedRepairSource.SourcePath);

            if (!fileInfo.Exists ||
                fileInfo.Length != cachedRepairSource.Length ||
                fileInfo.LastWriteTimeUtc != cachedRepairSource.LastWriteTimeUtc)
            {
                return false;
            }

            repairSource = cachedRepairSource;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLoadDiskRepairSource(
        [CanBeNull] string campaignTitle,
        [CanBeNull] out RepairSource repairSource)
    {
        repairSource = null;

        if (string.IsNullOrWhiteSpace(campaignTitle))
        {
            return false;
        }

        var directory = TacticalAdventuresApplication.UserCampaignsDirectory;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (TryLoadDiskRepairSource(path, campaignTitle, out repairSource))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryLoadDiskRepairSource(
        [NotNull] string path,
        [NotNull] string campaignTitle,
        [CanBeNull] out RepairSource repairSource)
    {
        repairSource = null;

        try
        {
            var fileInfo = new FileInfo(path);
            var payload = File.ReadAllText(path);
            var json = JObject.Parse(payload);
            var title = json["title"]?.Value<string>();

            if (title != campaignTitle)
            {
                return false;
            }

            repairSource = new RepairSource(
                CreateSourceCampaign(json),
                path,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc);

            return true;
        }
        catch
        {
            return false;
        }
    }

    [NotNull]
    private static SourceCampaign CreateSourceCampaign([NotNull] JObject campaign)
    {
        return new SourceCampaign(
            GetString(campaign, "title"),
            EnumerateSourceCampaignMapNodes(campaign["campaignMapNodes"]).ToArray(),
            EnumerateSourceLocations(campaign["userLocations"]).ToArray(),
            EnumerateSourceBiomes(campaign["userBiomes"]).ToArray());
    }

    [NotNull]
    private static SourceCampaign CreateSourceCampaign([NotNull] UserCampaign userCampaign)
    {
        return new SourceCampaign(
            userCampaign.Title,
            (userCampaign.CampaignMapNodes ?? Enumerable.Empty<UserCampaignMapNode>())
            .Select(CreateSourceCampaignMapNode)
            .ToArray(),
            (userCampaign.UserLocations ?? Enumerable.Empty<UserLocation>())
            .Where(x => x != null)
            .Select(CreateSourceLocation)
            .ToArray(),
            (userCampaign.UserBiomes ?? Enumerable.Empty<UserBiome>())
            .Where(x => x != null)
            .Select(CreateSourceBiome)
            .ToArray());
    }

    [NotNull]
    private static SourceCampaignMapNode CreateSourceCampaignMapNode([CanBeNull] UserCampaignMapNode mapNode)
    {
        return new SourceCampaignMapNode(
            mapNode?.PartyStart == true,
            mapNode?.UserLocationName,
            mapNode?.overriddenTitle,
            mapNode?.overriddenDescription,
            mapNode?.unchartedTitle,
            mapNode?.unchartedDescription);
    }

    [NotNull]
    private static SourceLocation CreateSourceLocation([NotNull] UserLocation userLocation)
    {
        return new SourceLocation(
            userLocation.ContentItemTitle,
            EnumerateUserGadgetEntries(userLocation)
            .Select(CreateSourceGadgetEntry)
            .ToArray());
    }

    [NotNull]
    private static SourceBiome CreateSourceBiome([NotNull] UserBiome userBiome)
    {
        return new SourceBiome((userBiome.NarrativeEventBasicLines ?? Enumerable.Empty<string>()).ToArray());
    }

    [NotNull]
    private static SourceGadgetEntry CreateSourceGadgetEntry([NotNull] UserGadgetEntry gadgetEntry)
    {
        return new SourceGadgetEntry(
            CreateSourceGadget(gadgetEntry.Gadget),
            gadgetEntry.Name,
            gadgetEntry.RoomIndex,
            gadgetEntry.GadgetIndex);
    }

    [NotNull]
    private static SourceGadget CreateSourceGadget([NotNull] UserGadget gadget)
    {
        return new SourceGadget(
            gadget.UniqueName,
            (gadget.ParameterValues ?? Enumerable.Empty<UserGadgetParameterValue>())
            .Where(x => x != null)
            .Select(CreateSourceParameterValue)
            .ToArray());
    }

    [NotNull]
    private static SourceParameterValue CreateSourceParameterValue([NotNull] UserGadgetParameterValue parameterValue)
    {
        return new SourceParameterValue(
            GetParameterName(parameterValue),
            parameterValue.StringValue,
            (parameterValue.DestinationsList ?? Enumerable.Empty<UserDestinationLocationDescription>())
            .Where(x => x != null)
            .Select(x => new SourceDestination(x.UserLocationName, x.DisplayedTitle))
            .ToArray());
    }

    [NotNull]
    private static IEnumerable<SourceCampaignMapNode> EnumerateSourceCampaignMapNodes([CanBeNull] JToken nodesToken)
    {
        foreach (var node in nodesToken?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            yield return new SourceCampaignMapNode(
                GetBool(node, "partyStart"),
                GetString(node, "userLocationName"),
                GetString(node, "overriddenTitle"),
                GetString(node, "overriddenDescription"),
                GetString(node, "unchartedTitle"),
                GetString(node, "unchartedDescription"));
        }
    }

    [NotNull]
    private static IEnumerable<SourceLocation> EnumerateSourceLocations([CanBeNull] JToken locationsToken)
    {
        foreach (var location in locationsToken?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            yield return new SourceLocation(
                GetString(location, "title"),
                EnumerateSourceGadgetEntries(location).ToArray());
        }
    }

    [NotNull]
    private static IEnumerable<SourceBiome> EnumerateSourceBiomes([CanBeNull] JToken biomesToken)
    {
        foreach (var biome in biomesToken?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            yield return new SourceBiome(EnumerateSourceStringArray(biome["narrativeEventBasicLines"]).ToArray());
        }
    }

    [NotNull]
    private static IEnumerable<SourceGadgetEntry> EnumerateSourceGadgetEntries([NotNull] JObject location)
    {
        var roomIndex = 0;

        foreach (var room in location["userRooms"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var gadgetIndex = 0;

            foreach (var gadget in room["userGadgets"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                yield return new SourceGadgetEntry(
                    CreateSourceGadget(gadget),
                    GetString(gadget, "uniqueName"),
                    roomIndex,
                    gadgetIndex);

                gadgetIndex++;
            }

            roomIndex++;
        }
    }

    [NotNull]
    private static SourceGadget CreateSourceGadget([NotNull] JObject gadget)
    {
        return new SourceGadget(
            GetString(gadget, "uniqueName"),
            EnumerateSourceParameterValues(gadget["parameterValues"]).ToArray());
    }

    [NotNull]
    private static IEnumerable<SourceParameterValue> EnumerateSourceParameterValues([CanBeNull] JToken parameterValuesToken)
    {
        foreach (var parameterValue in parameterValuesToken?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            yield return new SourceParameterValue(
                GetString(parameterValue, "gadgetParameterDescriptionName"),
                GetString(parameterValue, "stringValue"),
                EnumerateSourceDestinations(parameterValue["destinationsList"]).ToArray());
        }
    }

    [NotNull]
    private static IEnumerable<SourceDestination> EnumerateSourceDestinations([CanBeNull] JToken destinationsToken)
    {
        foreach (var destination in destinationsToken?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            yield return new SourceDestination(
                GetString(destination, "userLocationName"),
                GetString(destination, "displayedTitle"));
        }
    }

    [NotNull]
    private static IEnumerable<string> EnumerateSourceStringArray([CanBeNull] JToken stringsToken)
    {
        foreach (var value in stringsToken?.Values<string>() ?? Enumerable.Empty<string>())
        {
            yield return value;
        }
    }

    [CanBeNull]
    private static string GetString([CanBeNull] JObject source, [NotNull] string propertyName)
    {
        return source?[propertyName]?.Value<string>();
    }

    private static bool GetBool([CanBeNull] JObject source, [NotNull] string propertyName)
    {
        return source?[propertyName]?.Value<bool>() == true;
    }

    private static void AddCampaignMapNodeAliases(
        [NotNull] UserCampaign userCampaign,
        [NotNull] RepairIndex repairIndex)
    {
        foreach (var campaignMapNode in userCampaign.CampaignMapNodes ?? Enumerable.Empty<UserCampaignMapNode>())
        {
            repairIndex.AddAliasPair(campaignMapNode?.UserLocationName, campaignMapNode?.overriddenTitle);
        }
    }

    private static void AddUserLocationGadgetAliases(
        [NotNull] UserCampaign userCampaign,
        [NotNull] RepairIndex repairIndex)
    {
        foreach (var userLocation in userCampaign.UserLocations ?? Enumerable.Empty<UserLocation>())
        {
            AddUserLocationGadgetAliases(userLocation, repairIndex);
        }
    }

    private static void AddUserLocationGadgetAliases(
        [CanBeNull] UserLocation userLocation,
        [NotNull] RepairIndex repairIndex)
    {
        if (userLocation == null)
        {
            return;
        }

        foreach (var gadgetEntry in EnumerateUserGadgetEntries(userLocation))
        {
            var gadget = gadgetEntry.Gadget;

            if (gadget?.ParameterValues == null)
            {
                continue;
            }

            foreach (var parameterValue in gadget.ParameterValues)
            {
                AddUserGadgetParameterAliases(parameterValue, repairIndex);
            }
        }
    }

    private static void AddUserGadgetParameterAliases(
        [CanBeNull] UserGadgetParameterValue parameterValue,
        [NotNull] RepairIndex repairIndex)
    {
        if (GetParameterName(parameterValue) != "LocationsList" ||
            parameterValue.DestinationsList == null)
        {
            return;
        }

        foreach (var destination in parameterValue.DestinationsList)
        {
            repairIndex.AddAliasPair(destination?.UserLocationName, destination?.DisplayedTitle);
        }
    }

    private static void AddUserBiomeLineTranslations(
        [NotNull] UserCampaign userCampaign,
        [NotNull] RepairIndex repairIndex)
    {
        var targetBiomes = userCampaign.UserBiomes;
        var sourceBiomes = repairIndex.UserBiomes;

        if (targetBiomes == null || targetBiomes.Count == 0 || sourceBiomes.Length == 0)
        {
            return;
        }

        var biomeCount = Math.Min(targetBiomes.Count, sourceBiomes.Length);

        for (var biomeIndex = 0; biomeIndex < biomeCount; biomeIndex++)
        {
            AddUserBiomeLineTranslations(targetBiomes[biomeIndex], sourceBiomes[biomeIndex], repairIndex);
        }
    }

    private static void AddUserBiomeLineTranslations(
        [CanBeNull] UserBiome targetBiome,
        [CanBeNull] SourceBiome sourceBiome,
        [NotNull] RepairIndex repairIndex)
    {
        var targetLines = targetBiome?.NarrativeEventBasicLines;
        var sourceLines = sourceBiome?.NarrativeEventBasicLines;

        if (targetLines == null || targetLines.Count == 0 || sourceLines == null || sourceLines.Count == 0)
        {
            return;
        }

        var lineCount = Math.Min(targetLines.Count, sourceLines.Count);

        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            repairIndex.AddTravelJournalLineTranslation(targetLines[lineIndex], sourceLines[lineIndex]);
        }
    }

    [NotNull]
    private static IEnumerable<UserGadgetEntry> EnumerateUserGadgetEntries([NotNull] UserLocation userLocation)
    {
        var yieldedUniqueNames = new HashSet<string>(StringComparer.Ordinal);

        if (userLocation.UserRooms != null)
        {
            var roomIndex = 0;

            foreach (var userRoom in userLocation.UserRooms)
            {
                if (userRoom?.UserGadgets != null)
                {
                    var gadgetIndex = 0;

                    foreach (var userGadget in userRoom.UserGadgets)
                    {
                        if (userGadget != null)
                        {
                            AddYieldedUniqueName(yieldedUniqueNames, userGadget.UniqueName);
                            yield return new UserGadgetEntry(userGadget, userGadget.UniqueName, roomIndex, gadgetIndex);
                        }

                        gadgetIndex++;
                    }
                }

                roomIndex++;
            }
        }

        if (userLocation.GadgetsByName == null)
        {
            yield break;
        }

        foreach (var gadgetKvp in userLocation.GadgetsByName)
        {
            var userGadget = gadgetKvp.Value;

            if (userGadget == null ||
                (!string.IsNullOrWhiteSpace(userGadget.UniqueName) &&
                 yieldedUniqueNames.Contains(userGadget.UniqueName)))
            {
                continue;
            }

            yield return new UserGadgetEntry(userGadget, gadgetKvp.Key, -1, -1);
        }
    }

    private static void AddYieldedUniqueName([NotNull] HashSet<string> yieldedUniqueNames, [CanBeNull] string uniqueName)
    {
        if (!string.IsNullOrWhiteSpace(uniqueName))
        {
            yieldedUniqueNames.Add(uniqueName);
        }
    }

    private sealed class SourceCampaign
    {
        internal SourceCampaign(
            [CanBeNull] string title,
            [NotNull] SourceCampaignMapNode[] campaignMapNodes,
            [NotNull] SourceLocation[] userLocations,
            [NotNull] SourceBiome[] userBiomes)
        {
            Title = title;
            CampaignMapNodes = campaignMapNodes;
            UserLocations = userLocations;
            UserBiomes = userBiomes;
        }

        [CanBeNull]
        internal string Title { get; }

        [NotNull]
        internal SourceCampaignMapNode[] CampaignMapNodes { get; }

        [NotNull]
        internal SourceLocation[] UserLocations { get; }

        [NotNull]
        internal SourceBiome[] UserBiomes { get; }
    }

    private sealed class SourceCampaignMapNode
    {
        internal SourceCampaignMapNode(
            bool partyStart,
            [CanBeNull] string userLocationName,
            [CanBeNull] string overriddenTitle,
            [CanBeNull] string overriddenDescription,
            [CanBeNull] string unchartedTitle,
            [CanBeNull] string unchartedDescription)
        {
            PartyStart = partyStart;
            UserLocationName = userLocationName;
            OverriddenTitle = overriddenTitle;
            OverriddenDescription = overriddenDescription;
            UnchartedTitle = unchartedTitle;
            UnchartedDescription = unchartedDescription;
        }

        internal bool PartyStart { get; }

        [CanBeNull]
        internal string UserLocationName { get; }

        [CanBeNull]
        internal string OverriddenTitle { get; }

        [CanBeNull]
        internal string OverriddenDescription { get; }

        [CanBeNull]
        internal string UnchartedTitle { get; }

        [CanBeNull]
        internal string UnchartedDescription { get; }
    }

    private sealed class SourceLocation
    {
        internal SourceLocation(
            [CanBeNull] string contentItemTitle,
            [NotNull] SourceGadgetEntry[] gadgetEntries)
        {
            ContentItemTitle = contentItemTitle;
            GadgetEntries = gadgetEntries;
        }

        [CanBeNull]
        internal string ContentItemTitle { get; }

        [NotNull]
        internal SourceGadgetEntry[] GadgetEntries { get; }
    }

    private sealed class SourceBiome
    {
        internal SourceBiome([NotNull] IReadOnlyList<string> narrativeEventBasicLines)
        {
            NarrativeEventBasicLines = narrativeEventBasicLines;
        }

        [NotNull]
        internal IReadOnlyList<string> NarrativeEventBasicLines { get; }
    }

    private sealed class SourceGadget
    {
        internal SourceGadget(
            [CanBeNull] string uniqueName,
            [NotNull] SourceParameterValue[] parameterValues)
        {
            UniqueName = uniqueName;
            ParameterValues = parameterValues;
        }

        [CanBeNull]
        internal string UniqueName { get; }

        [NotNull]
        internal SourceParameterValue[] ParameterValues { get; }
    }

    private sealed class SourceParameterValue
    {
        internal SourceParameterValue(
            [CanBeNull] string parameterName,
            [CanBeNull] string stringValue,
            [NotNull] IReadOnlyList<SourceDestination> destinationsList)
        {
            ParameterName = parameterName;
            StringValue = stringValue;
            DestinationsList = destinationsList;
        }

        [CanBeNull]
        internal string ParameterName { get; }

        [CanBeNull]
        internal string StringValue { get; }

        [NotNull]
        internal IReadOnlyList<SourceDestination> DestinationsList { get; }
    }

    private sealed class SourceDestination
    {
        internal SourceDestination([CanBeNull] string userLocationName, [CanBeNull] string displayedTitle)
        {
            UserLocationName = userLocationName;
            DisplayedTitle = displayedTitle;
        }

        [CanBeNull]
        internal string UserLocationName { get; }

        [CanBeNull]
        internal string DisplayedTitle { get; }
    }

    private sealed class SourceGadgetEntry
    {
        internal SourceGadgetEntry(
            [NotNull] SourceGadget gadget,
            [CanBeNull] string name,
            int roomIndex,
            int gadgetIndex)
        {
            Gadget = gadget;
            Name = name;
            RoomIndex = roomIndex;
            GadgetIndex = gadgetIndex;
        }

        [NotNull]
        internal SourceGadget Gadget { get; }

        [CanBeNull]
        internal string Name { get; }

        internal int RoomIndex { get; }

        internal int GadgetIndex { get; }

        [CanBeNull]
        internal string UniqueName => Gadget.UniqueName;

        [NotNull]
        internal string RoomGadgetKey => $"{RoomIndex}:{GadgetIndex}";
    }

    private sealed class UserGadgetEntry
    {
        internal UserGadgetEntry(
            [NotNull] UserGadget gadget,
            [CanBeNull] string name,
            int roomIndex,
            int gadgetIndex)
        {
            Gadget = gadget;
            Name = name;
            RoomIndex = roomIndex;
            GadgetIndex = gadgetIndex;
        }

        [NotNull]
        internal UserGadget Gadget { get; }

        [CanBeNull]
        internal string Name { get; }

        internal int RoomIndex { get; }

        internal int GadgetIndex { get; }

        [CanBeNull]
        internal string UniqueName => Gadget.UniqueName;

        [NotNull]
        internal string RoomGadgetKey => $"{RoomIndex}:{GadgetIndex}";
    }

    private sealed class LocationRepairIndex
    {
        private readonly Dictionary<string, SourceGadgetEntry> _gadgetsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceGadgetEntry> _gadgetsByRoomGadgetKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceGadgetEntry> _gadgetsByUniqueName = new(StringComparer.Ordinal);

        internal LocationRepairIndex([NotNull] SourceLocation userLocation)
        {
            UserLocation = userLocation;
            GadgetEntries = userLocation.GadgetEntries;

            foreach (var gadgetEntry in GadgetEntries)
            {
                AddEntry(_gadgetsByUniqueName, gadgetEntry.UniqueName, gadgetEntry);
                AddEntry(_gadgetsByName, gadgetEntry.Name, gadgetEntry);

                if (gadgetEntry.RoomIndex >= 0 && gadgetEntry.GadgetIndex >= 0)
                {
                    AddEntry(_gadgetsByRoomGadgetKey, gadgetEntry.RoomGadgetKey, gadgetEntry);
                }
            }
        }

        [NotNull]
        internal SourceLocation UserLocation { get; }

        [NotNull]
        internal SourceGadgetEntry[] GadgetEntries { get; }

        internal bool TryGetGadget([NotNull] UserGadgetEntry targetGadgetEntry, [CanBeNull] out SourceGadgetEntry gadgetEntry)
        {
            gadgetEntry = null;

            return TryGetGadgetByUniqueName(targetGadgetEntry.UniqueName, out gadgetEntry) ||
                   TryGetEntry(_gadgetsByRoomGadgetKey, targetGadgetEntry.RoomGadgetKey, out gadgetEntry) ||
                   TryGetEntry(_gadgetsByName, targetGadgetEntry.Name, out gadgetEntry);
        }

        internal bool TryGetGadgetByUniqueName([CanBeNull] string uniqueName, [CanBeNull] out SourceGadgetEntry gadgetEntry)
        {
            return TryGetEntry(_gadgetsByUniqueName, uniqueName, out gadgetEntry);
        }

        private static void AddEntry(
            [NotNull] Dictionary<string, SourceGadgetEntry> entries,
            [CanBeNull] string key,
            [NotNull] SourceGadgetEntry gadgetEntry)
        {
            if (!string.IsNullOrWhiteSpace(key) && !entries.ContainsKey(key))
            {
                entries.Add(key, gadgetEntry);
            }
        }

        private static bool TryGetEntry(
            [NotNull] Dictionary<string, SourceGadgetEntry> entries,
            [CanBeNull] string key,
            [CanBeNull] out SourceGadgetEntry gadgetEntry)
        {
            gadgetEntry = null;

            return !string.IsNullOrWhiteSpace(key) && entries.TryGetValue(key, out gadgetEntry);
        }
    }

    private sealed class RepairSource
    {
        internal RepairSource(
            [NotNull] SourceCampaign campaign,
            [CanBeNull] string sourcePath,
            long length,
            DateTime lastWriteTimeUtc)
        {
            Campaign = campaign;
            SourcePath = sourcePath;
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        [NotNull]
        internal SourceCampaign Campaign { get; }

        [CanBeNull]
        internal string SourcePath { get; }

        internal long Length { get; }

        internal DateTime LastWriteTimeUtc { get; }
    }

    private sealed class RepairIndex
    {
        private readonly HashSet<string> _ambiguousAliases = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ambiguousNumberPrefixes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ambiguousTravelJournalLines = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LocationRepairIndex> _locationIndexesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _locationsByNumberPrefix = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _travelJournalLineTranslations = new(StringComparer.Ordinal);

        internal RepairIndex(
            [NotNull] SourceCampaign userCampaign,
            [NotNull] HashSet<string> userLocationNames)
        {
            CampaignMapNodes = userCampaign.CampaignMapNodes;
            UserBiomes = userCampaign.UserBiomes;
            UserLocationNames = userLocationNames;

            foreach (var userLocation in userCampaign.UserLocations)
            {
                var userLocationName = userLocation?.ContentItemTitle;

                if (string.IsNullOrWhiteSpace(userLocationName) || !UserLocationNames.Contains(userLocationName))
                {
                    continue;
                }

                _locationIndexesByName[userLocationName] = new LocationRepairIndex(userLocation);
                AddLocationNumberPrefix(userLocationName);
            }
        }

        [NotNull]
        internal SourceCampaignMapNode[] CampaignMapNodes { get; }

        [NotNull]
        internal SourceBiome[] UserBiomes { get; }

        [NotNull]
        internal HashSet<string> UserLocationNames { get; }

        internal void AddAliasPair([CanBeNull] string first, [CanBeNull] string second)
        {
            var firstIsValidLocation = IsValidUserLocationName(first);
            var secondIsValidLocation = IsValidUserLocationName(second);

            if (firstIsValidLocation == secondIsValidLocation)
            {
                return;
            }

            AddAlias(
                firstIsValidLocation ? second : first,
                firstIsValidLocation ? first : second);
        }

        internal bool TryResolve([CanBeNull] string alias, [CanBeNull] out string userLocationName)
        {
            userLocationName = null;

            return !string.IsNullOrWhiteSpace(alias) &&
                   TryResolveAlias(alias, out userLocationName);
        }

        internal void AddTravelJournalLineTranslation(
            [CanBeNull] string sourceLine,
            [CanBeNull] string translatedLine)
        {
            if (string.IsNullOrWhiteSpace(sourceLine) ||
                string.IsNullOrWhiteSpace(translatedLine) ||
                sourceLine == translatedLine)
            {
                return;
            }

            if (!_travelJournalLineTranslations.TryGetValue(sourceLine, out var existingTranslation))
            {
                _travelJournalLineTranslations.Add(sourceLine, translatedLine);
                return;
            }

            if (existingTranslation != translatedLine)
            {
                _ambiguousTravelJournalLines.Add(sourceLine);
            }
        }

        internal bool TryRepairTravelJournalLine(
            [CanBeNull] string sourceLine,
            [CanBeNull] out string translatedLine)
        {
            translatedLine = null;

            return !string.IsNullOrWhiteSpace(sourceLine) &&
                   !_ambiguousTravelJournalLines.Contains(sourceLine) &&
                   _travelJournalLineTranslations.TryGetValue(sourceLine, out translatedLine);
        }

        [CanBeNull]
        internal SourceCampaignMapNode GetCampaignMapNode(int index)
        {
            return index >= 0 && index < CampaignMapNodes.Length ? CampaignMapNodes[index] : null;
        }

        internal bool TryGetLocationIndex(
            [CanBeNull] string userLocationName,
            [CanBeNull] out LocationRepairIndex locationRepairIndex)
        {
            locationRepairIndex = null;

            return !string.IsNullOrWhiteSpace(userLocationName) &&
                   _locationIndexesByName.TryGetValue(userLocationName, out locationRepairIndex);
        }

        private bool IsValidUserLocationName([CanBeNull] string name)
        {
            return !string.IsNullOrWhiteSpace(name) && UserLocationNames.Contains(name);
        }

        private void AddAlias([CanBeNull] string alias, [CanBeNull] string userLocationName)
        {
            if (string.IsNullOrWhiteSpace(alias) ||
                string.IsNullOrWhiteSpace(userLocationName) ||
                alias == userLocationName ||
                UserLocationNames.Contains(alias))
            {
                return;
            }

            if (!_aliases.TryGetValue(alias, out var existingUserLocationName))
            {
                _aliases.Add(alias, userLocationName);
                return;
            }

            if (existingUserLocationName != userLocationName)
            {
                _ambiguousAliases.Add(alias);
            }
        }

        private void AddLocationNumberPrefix([CanBeNull] string userLocationName)
        {
            if (!TryGetLocationNumberPrefix(userLocationName, out var prefix))
            {
                return;
            }

            if (!_locationsByNumberPrefix.TryGetValue(prefix, out var existingUserLocationName))
            {
                _locationsByNumberPrefix.Add(prefix, userLocationName);
                return;
            }

            if (existingUserLocationName != userLocationName)
            {
                _ambiguousNumberPrefixes.Add(prefix);
            }
        }

        private bool TryResolveAlias([NotNull] string alias, [CanBeNull] out string userLocationName)
        {
            if (!_ambiguousAliases.Contains(alias) &&
                _aliases.TryGetValue(alias, out userLocationName))
            {
                return true;
            }

            userLocationName = null;

            return TryGetLocationNumberPrefix(alias, out var prefix) &&
                   !_ambiguousNumberPrefixes.Contains(prefix) &&
                   _locationsByNumberPrefix.TryGetValue(prefix, out userLocationName);
        }

        private static bool TryGetLocationNumberPrefix([CanBeNull] string userLocationName, [CanBeNull] out string prefix)
        {
            prefix = null;

            if (string.IsNullOrWhiteSpace(userLocationName))
            {
                return false;
            }

            var index = 0;

            while (index < userLocationName.Length && char.IsDigit(userLocationName[index]))
            {
                index++;
            }

            if (index == 0)
            {
                return false;
            }

            if (index < userLocationName.Length && IsAsciiLetter(userLocationName[index]))
            {
                index++;
            }

            if (index >= userLocationName.Length || userLocationName[index] != '.')
            {
                return false;
            }

            prefix = userLocationName.Substring(0, index + 1);

            return true;
        }

        private static bool IsAsciiLetter(char character)
        {
            return character >= 'A' && character <= 'Z' ||
                   character >= 'a' && character <= 'z';
        }
    }
}
