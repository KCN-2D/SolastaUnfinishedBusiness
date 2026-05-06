using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Models;

internal static class CampaignTranslationRuntimeRepairContext
{
    private static UserCampaign _cachedUserCampaign;
    private static RepairIndex _cachedRepairIndex;

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
        RepairCampaignNodes(campaignMap, userCampaign, repairIndex);
        RepairUserLocationStatuses(repairIndex);
    }

    internal static void RepairUserCampaignReferences()
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
        RepairCampaignSourceNodes(userCampaign, repairIndex);
        RepairUserLocationGadgets(userCampaign, repairIndex);
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
        foreach (var sourceNode in userCampaign.CampaignMapNodes ?? Enumerable.Empty<UserCampaignMapNode>())
        {
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
        [NotNull] UserCampaign userCampaign,
        [NotNull] RepairIndex repairIndex)
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
            RepairCampaignNode(gameNodes[index], sourceNodes[index], repairIndex);
        }
    }

    private static void RepairCampaignNode(
        [CanBeNull] GameCampaignNode gameNode,
        [CanBeNull] UserCampaignMapNode sourceNode,
        [NotNull] RepairIndex repairIndex)
    {
        if (gameNode == null || sourceNode?.PartyStart == true)
        {
            return;
        }

        var userLocationName = !string.IsNullOrWhiteSpace(sourceNode?.UserLocationName)
            ? sourceNode.UserLocationName
            : gameNode.UserLocationName;

        if (TryRepairLocationName(userLocationName, repairIndex, out var repairedLocationName))
        {
            if (sourceNode != null)
            {
                sourceNode.UserLocationName = repairedLocationName;
            }
        }

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
        if (userLocation.GadgetsByName == null)
        {
            return;
        }

        foreach (var gadget in userLocation.GadgetsByName.Values)
        {
            if (gadget?.ParameterValues == null)
            {
                continue;
            }

            foreach (var parameterValue in gadget.ParameterValues)
            {
                RepairUserGadgetParameterValue(parameterValue, repairIndex);
            }
        }
    }

    private static void RepairUserGadgetParameterValue(
        [CanBeNull] UserGadgetParameterValue parameterValue,
        [NotNull] RepairIndex repairIndex)
    {
        switch (parameterValue?.GadgetParameterDescription?.Name)
        {
            case "DestinationLocation":
                if (TryRepairLocationName(parameterValue.StringValue, repairIndex, out var repairedLocationName))
                {
                    parameterValue.StringValue = repairedLocationName;
                }

                break;

            case "LocationsList":
                RepairDestinationList(parameterValue, repairIndex);
                break;
        }
    }

    private static void RepairDestinationList(
        [NotNull] UserGadgetParameterValue parameterValue,
        [NotNull] RepairIndex repairIndex)
    {
        if (parameterValue.DestinationsList == null)
        {
            return;
        }

        foreach (var destination in parameterValue.DestinationsList)
        {
            if (destination == null ||
                !TryRepairLocationName(destination.UserLocationName, repairIndex, out var repairedLocationName))
            {
                continue;
            }

            destination.UserLocationName = repairedLocationName;
        }
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
        var repairIndex = new RepairIndex(GetUserLocationNames(userCampaign));

        AddCampaignMapNodeAliases(userCampaign, repairIndex);
        AddUserLocationGadgetAliases(userCampaign, repairIndex);

        return repairIndex;
    }

    [NotNull]
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
        if (userLocation?.GadgetsByName == null)
        {
            return;
        }

        foreach (var gadget in userLocation.GadgetsByName.Values)
        {
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
        if (parameterValue?.GadgetParameterDescription?.Name != "LocationsList" ||
            parameterValue.DestinationsList == null)
        {
            return;
        }

        foreach (var destination in parameterValue.DestinationsList)
        {
            repairIndex.AddAliasPair(destination?.UserLocationName, destination?.DisplayedTitle);
        }
    }

    private sealed class RepairIndex
    {
        private readonly HashSet<string> _ambiguousAliases = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ambiguousNumberPrefixes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _locationsByNumberPrefix = new(StringComparer.Ordinal);

        internal RepairIndex([NotNull] HashSet<string> userLocationNames)
        {
            UserLocationNames = userLocationNames;

            foreach (var userLocationName in userLocationNames)
            {
                AddLocationNumberPrefix(userLocationName);
            }
        }

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
