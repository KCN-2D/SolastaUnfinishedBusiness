using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using Newtonsoft.Json;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using UnityEngine.AddressableAssets;
using static SolastaUnfinishedBusiness.Builders.DefinitionBuilder;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

internal static class DungeonMakerContext
{
    private const string BackupFolder = "../UserContentBackup";
    private const string FlatRoomsCategoryName = "FlatRooms";
    private const string ModdedSuffix = "~MOD";
    internal static readonly HashSet<string> OutdoorRooms = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<UserCampaignPoolManager, CampaignFileCache> CampaignFiles = new();

    // The native pool rebuilds whenever a Session exists. Keep its registration and
    // saved-campaign supplementation, while reusing unchanged disk content it owns.
    internal static List<UserCampaign> RetainCampaignFilesForEnumeration(UserCampaignPoolManager owner)
    {
        var retained = new List<UserCampaign>();

        if (!CampaignFiles.TryGetValue(owner, out var cache))
        {
            return retained;
        }

        foreach (var pair in cache.Files.ToArray())
        {
            if (pair.Value.Campaign.TryGetTarget(out var campaign) &&
                TryGetCampaignFileStamp(pair.Key, out var stamp) && stamp.Equals(pair.Value.Stamp))
            {
                retained.Add(campaign);
            }
            else
            {
                cache.Files.Remove(pair.Key);
            }
        }

        return retained;
    }

    internal static bool TryGetCachedCampaignFile(
        UserCampaignPoolManager owner,
        string path,
        bool fromGamingPlatform,
        bool fromExternalSource,
        List<string> removedContent,
        out UserCampaign campaign,
        out CampaignFileRead read)
    {
        campaign = null;
        read = null;

        if (!TryGetCampaignFileStamp(path, out var stamp))
        {
            return false;
        }

        var cache = CampaignFiles.GetValue(owner, _ => new CampaignFileCache());

        if (cache.Files.TryGetValue(path, out var entry) && stamp.Equals(entry.Stamp) &&
            entry.FromGamingPlatform == fromGamingPlatform && entry.FromExternalSource == fromExternalSource &&
            entry.Campaign.TryGetTarget(out campaign))
        {
            removedContent?.AddRange(entry.RemovedContent);

            return true;
        }

        cache.Files.Remove(path);
        read = new CampaignFileRead(path, stamp, fromGamingPlatform, fromExternalSource, removedContent?.Count ?? 0);

        return false;
    }

    internal static void CacheCampaignFile(
        UserCampaignPoolManager owner,
        CampaignFileRead read,
        UserCampaign campaign,
        bool someContentRemoved,
        List<string> removedContent,
        Dictionary<string, UserCampaign> pool)
    {
        // Native may return a partially read or unsupported campaign after failure.
        // Cache only registered results with unchanged file metadata and no removed-content flag.
        if (read == null || campaign == null || someContentRemoved ||
            !pool.TryGetValue(campaign.Title, out var registered) || !ReferenceEquals(campaign, registered) ||
            !TryGetCampaignFileStamp(read.Path, out var stamp) || !stamp.Equals(read.Stamp))
        {
            return;
        }

        var removed = removedContent != null && removedContent.Count > read.RemovedCount
            ? removedContent.Skip(read.RemovedCount).ToArray()
            : [];
        CampaignFiles.GetValue(owner, _ => new CampaignFileCache()).Files[read.Path] = new CachedCampaignFile(
            stamp, read.FromGamingPlatform, read.FromExternalSource, campaign, removed);
    }

    internal static void InvalidateCampaignFile(UserCampaignPoolManager owner, UserCampaign campaign)
    {
        if (owner == null || !CampaignFiles.TryGetValue(owner, out var cache))
        {
            return;
        }

        foreach (var pair in cache.Files.ToArray())
        {
            if (!pair.Value.Campaign.TryGetTarget(out var cached) || ReferenceEquals(campaign, cached))
            {
                cache.Files.Remove(pair.Key);
            }
        }
    }

    internal static void InvalidateCampaignFileForEditing(UserCampaign campaign)
    {
        InvalidateCampaignFile(ServiceRepository.GetService<IUserCampaignPoolService>() as UserCampaignPoolManager, campaign);
    }

    internal static void ReleaseCampaignFiles(UserCampaignPoolManager owner)
    {
        CampaignFiles.Remove(owner);
    }

    private static bool TryGetCampaignFileStamp(string path, out CampaignFileStamp stamp)
    {
        stamp = default;

        try
        {
            var file = new FileInfo(path);

            if (!file.Exists)
            {
                return false;
            }

            stamp = new CampaignFileStamp(file.Length, file.LastWriteTimeUtc);

            return true;
        }
        catch
        {
            // Keep the native reader responsible for reporting inaccessible files.
            return false;
        }
    }

    internal readonly struct CampaignFileStamp(long length, DateTime lastWriteUtc) : IEquatable<CampaignFileStamp>
    {
        public bool Equals(CampaignFileStamp other)
        {
            return Length == other.Length && LastWriteUtc == other.LastWriteUtc;
        }

        private long Length { get; } = length;
        private DateTime LastWriteUtc { get; } = lastWriteUtc;
    }

    internal sealed class CampaignFileRead(
        string path, CampaignFileStamp stamp, bool fromGamingPlatform, bool fromExternalSource, int removedCount)
    {
        internal string Path { get; } = path;
        internal CampaignFileStamp Stamp { get; } = stamp;
        internal bool FromGamingPlatform { get; } = fromGamingPlatform;
        internal bool FromExternalSource { get; } = fromExternalSource;
        internal int RemovedCount { get; } = removedCount;
    }

    private sealed class CampaignFileCache
    {
        internal Dictionary<string, CachedCampaignFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CachedCampaignFile(
        CampaignFileStamp stamp, bool fromGamingPlatform, bool fromExternalSource,
        UserCampaign campaign, string[] removedContent)
    {
        internal CampaignFileStamp Stamp { get; } = stamp;
        internal bool FromGamingPlatform { get; } = fromGamingPlatform;
        internal bool FromExternalSource { get; } = fromExternalSource;
        internal WeakReference<UserCampaign> Campaign { get; } = new(campaign);
        internal string[] RemovedContent { get; } = removedContent;
    }

    internal static UserContent TryParseUserContent<T>(string json, out T parsed) where T : UserContent
    {
        parsed = null;

        try
        {
            parsed = JsonUtility.FromJson<T>(json);
            return parsed;
        }
        catch (ArgumentException)
        {
            // An invalid typed payload must still pass the original common-header gate.
            // If that gate accepts it, the native typed call reports its error at the original point.
            return JsonUtility.FromJson<UserContent>(json);
        }
    }

    internal static T GetParsedUserContent<T>(string json, T parsed) where T : UserContent
    {
        return parsed ?? JsonUtility.FromJson<T>(json);
    }

    internal static IEnumerable<CodeInstruction> ReuseUserContentParsing<T>(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator, string reader) where T : UserContent
    {
        var code = instructions.ToList();
        var fromJson = typeof(JsonUtility).GetMethod(nameof(JsonUtility.FromJson), [typeof(string)]);

        if (fromJson is not { IsGenericMethodDefinition: true })
        {
            Main.Error($"Failed to locate native JSON parsing in {reader}.");
            return code;
        }

        var headerParser = fromJson.MakeGenericMethod(typeof(UserContent));
        var typedParser = fromJson.MakeGenericMethod(typeof(T));
        var headerIndex = code.FindIndex(instruction => instruction.Calls(headerParser));
        var typedIndex = code.FindIndex(instruction => instruction.Calls(typedParser));

        if (headerIndex <= 0 || typedIndex <= headerIndex || typedIndex + 1 >= code.Count ||
            code.Count(instruction => instruction.Calls(headerParser)) != 1 ||
            code.Count(instruction => instruction.Calls(typedParser)) != 1 ||
            code[headerIndex].opcode != OpCodes.Call || code[typedIndex].opcode != OpCodes.Call ||
            !code[headerIndex - 1].IsLdloc() || !code[typedIndex - 1].IsLdloc() ||
            code[headerIndex - 1].opcode != code[typedIndex - 1].opcode ||
            !Equals(code[headerIndex - 1].operand, code[typedIndex - 1].operand) ||
            !code[headerIndex + 1].IsStloc() || !code[typedIndex + 1].IsStloc())
        {
            Main.Error($"Failed to locate native header and typed JSON reads in {reader}.");
            return code;
        }

        var parsed = generator.DeclareLocal(typeof(T));
        var parse = AccessTools.Method(typeof(DungeonMakerContext), nameof(TryParseUserContent))
            .MakeGenericMethod(typeof(T));
        var reuse = AccessTools.Method(typeof(DungeonMakerContext), nameof(GetParsedUserContent))
            .MakeGenericMethod(typeof(T));
        var result = new List<CodeInstruction>(code.Count + 2);

        for (var index = 0; index < code.Count; index++)
        {
            var instruction = code[index];

            if (index == headerIndex || index == typedIndex)
            {
                var load = new CodeInstruction(index == headerIndex ? OpCodes.Ldloca : OpCodes.Ldloc, parsed);
                load.MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                result.Add(load);
                result.Add(new CodeInstruction(OpCodes.Call, index == headerIndex ? parse : reuse));
            }
            else
            {
                result.Add(instruction);
            }
        }

        return result;
    }

    internal static bool ShouldReadUserContentFile(string path, string contentType)
    {
        try
        {
            return !TryReadTopLevelString(path, "contentType", out var fileContentType, true) ||
                   string.Equals(fileContentType, contentType, StringComparison.Ordinal);
        }
        catch
        {
            // Preserve native handling for legacy, unreadable or malformed content.
            return true;
        }
    }

    internal static bool TryReadTopLevelString(
        [NotNull] string path,
        [NotNull] string propertyName,
        [CanBeNull] out string value,
        bool headerOnly = false)
    {
        value = null;

        using var stream = File.OpenRead(path);
        using var textReader = new StreamReader(stream);
        using var jsonReader = new JsonTextReader(textReader);

        if (!ReadNextNonComment(jsonReader) || jsonReader.TokenType != JsonToken.StartObject)
        {
            return false;
        }

        while (ReadNextNonComment(jsonReader))
        {
            if (jsonReader.TokenType != JsonToken.PropertyName)
            {
                return false;
            }

            var currentPropertyName = jsonReader.Value as string;

            if (!ReadNextNonComment(jsonReader))
            {
                return false;
            }

            if (string.Equals(currentPropertyName, propertyName, StringComparison.Ordinal))
            {
                value = jsonReader.TokenType == JsonToken.String ? jsonReader.Value as string : null;

                return !string.IsNullOrWhiteSpace(value);
            }

            // Do not scan a campaign's rooms just to decide which pool should read it.
            if (headerOnly && jsonReader.TokenType is JsonToken.StartObject or JsonToken.StartArray)
            {
                return false;
            }

            jsonReader.Skip();
        }

        return false;
    }

    private static bool ReadNextNonComment([NotNull] JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.Comment)
            {
                return true;
            }
        }

        return false;
    }

    internal static void Load()
    {
        UpdateAvailableDungeonSizes();
        UpdateCategories();
        LoadFlatRooms();
        UnleashGadgetsOnAllEnvironments();
        UnleashPropsOnAllEnvironments();
        UnleashRoomsOnAllEnvironments();

        //BUGFIX: This NPC cannot fight without sense
        DatabaseHelper.MonsterDefinitions.DLC3_ElvenClans_Leralyn.Features.Add(
            DatabaseHelper.FeatureDefinitionSenses.SenseDarkvision24);
    }

    internal static void BackupAndDelete([NotNull] string path, [NotNull] UserContent userContent)
    {
        const int MaxBackupFilesPerLocationCampaign = 20;

        var backupDirectory = Path.Combine(Main.ModFolder, BackupFolder);

        Main.EnsureFolderExists(backupDirectory);

        var title = userContent.Title;
        var compliantTitle = IOHelper.GetOsCompliantFilename(title);
        var destinationPath =
            Path.Combine(backupDirectory, compliantTitle) + "." + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        var backupFiles = Directory
            .EnumerateFiles(backupDirectory, compliantTitle + "*")
            .OrderBy(f => f)
            .ToArray();

        for (var i = 0; i <= backupFiles.Length - MaxBackupFilesPerLocationCampaign; i++)
        {
            File.Delete(backupFiles[i]);
        }

        File.Copy(path, destinationPath);
        File.Delete(path);
    }

    internal static void AddCustomDungeonSizes(UserLocationSettingsModal __instance)
    {
        for (var size = ExtendedDungeonSize.Huge; size <= ExtendedDungeonSize.Gargantuan; size++)
        {
            var sizeString = UserLocationDefinitions.CellsBySize[(UserLocationDefinitions.Size)size].ToString();

            __instance.optionsListSize.Add(new GuiDropdown.OptionDataAdvanced
            {
                text = Gui.FormatLocationSize((UserLocationDefinitions.Size)size).Khaki() + " " +
                       Gui.Format("{0} x {1}", sizeString, sizeString),
                TooltipContent = string.Empty
            });
        }
    }

    internal static int Compare([NotNull] BaseBlueprint left, [NotNull] BaseBlueprint right)
    {
        var leftCategory = DatabaseHelper.GetDefinition<BlueprintCategory>(left.Category);
        var rightCategory = DatabaseHelper.GetDefinition<BlueprintCategory>(right.Category);
        var result = string.Compare(leftCategory.FormatTitle(), rightCategory.FormatTitle(),
            StringComparison.CurrentCultureIgnoreCase);

        if (result != 0)
        {
            return result;
        }

        result = string.Compare(left.name, right.name, StringComparison.CurrentCultureIgnoreCase);

        if (result == 0)
        {
            result = left.GuiPresentation.SortOrder.CompareTo(right.GuiPresentation.SortOrder);
        }

        return result;
    }

    private static void UpdateAvailableDungeonSizes()
    {
        var customDungeonSizes = new Dictionary<UserLocationDefinitions.Size, int>
        {
            { (UserLocationDefinitions.Size)ExtendedDungeonSize.Huge, 150 },
            { (UserLocationDefinitions.Size)ExtendedDungeonSize.Gargantuan, 200 }
        };

        UserLocationDefinitions.CellsBySize[UserLocationDefinitions.Size.Medium] = 75;

        foreach (var kvp in customDungeonSizes)
        {
            UserLocationDefinitions.CellsBySize[kvp.Key] = kvp.Value;
        }
    }

    private static void UpdateCategories()
    {
        var categories = new List<BlueprintCategory>();
        var dbBlueprintCategory = DatabaseRepository.GetDatabase<BlueprintCategory>();
        var dbEnvironmentDefinition = DatabaseRepository.GetDatabase<EnvironmentDefinition>();
        var emptyRoomsCategory = dbBlueprintCategory.GetElement("EmptyRooms");

        if (!dbBlueprintCategory.TryGetElement(FlatRoomsCategoryName, out _))
        {
            var flatRoomsCategory = Object.Instantiate(emptyRoomsCategory);

            flatRoomsCategory.name = FlatRoomsCategoryName;
            flatRoomsCategory.guid = GuidHelper.Create(CeNamespaceGuid, flatRoomsCategory.name).ToString();
            flatRoomsCategory.GuiPresentation.Title =
                Gui.Localize($"BlueprintCategory/&{flatRoomsCategory.name}Title").Khaki();
            dbBlueprintCategory.Add(flatRoomsCategory);
        }

        foreach (var blueprintCategory in dbBlueprintCategory)
        {
            if (IsModdedName(blueprintCategory.Name))
            {
                continue;
            }

            foreach (var environmentDefinition in dbEnvironmentDefinition)
            {
                var environmentName = environmentDefinition.Name;
                var categoryName = blueprintCategory.Name + "~" + environmentName + ModdedSuffix;

                if (dbBlueprintCategory.TryGetElement(categoryName, out _))
                {
                    continue;
                }

                var newBlueprintCategory = Object.Instantiate(blueprintCategory);

                newBlueprintCategory.name = categoryName;
                newBlueprintCategory.guid = GuidHelper.Create(CeNamespaceGuid, newBlueprintCategory.name).ToString();
                newBlueprintCategory.GuiPresentation.Title = Gui.Localize(blueprintCategory.GuiPresentation.Title) +
                                                             " " + Gui.Localize(environmentDefinition.GuiPresentation
                                                                 .Title) + " [MODDED]".Khaki();
                categories.Add(newBlueprintCategory);
            }
        }

        categories.ForEach(x => dbBlueprintCategory.Add(x));
    }

    private static void LoadFlatRooms()
    {
        CreateFlatRooms(12); // from 12x12 to 144x144
        CreateSpecialFlatRoom("Drain_Big_24C_A", 12 + 1);
        CreateSpecialFlatRoom("Drain_Big_24C_B", 12 + 2);
    }

    private static void CreateFlatRooms(int maxMultiplier)
    {
        const string TEMPLATE = "Crossroad_12C";
        var dbRoomBlueprint = DatabaseRepository.GetDatabase<RoomBlueprint>();

        for (var multiplier = 1; multiplier <= maxMultiplier; multiplier++)
        {
            var flatRoomName = $"Flat{multiplier:D2}Room";

            if (dbRoomBlueprint.TryGetElement(flatRoomName, out _))
            {
                continue;
            }

            var flatRoom = Object.Instantiate(dbRoomBlueprint.GetElement(TEMPLATE));

            flatRoom.name = flatRoomName;
            flatRoom.guid = GuidHelper.Create(CeNamespaceGuid, flatRoom.name).ToString();
            flatRoom.GuiPresentation.title = "Flat".Khaki() + " Room";
            flatRoom.GuiPresentation.sortOrder = multiplier;
            flatRoom.GuiPresentation.hidden = true;
            flatRoom.category = FlatRoomsCategoryName;
            flatRoom.dimensions = new Vector2Int(
                flatRoom.Dimensions.x * multiplier, flatRoom.Dimensions.y * multiplier);
            flatRoom.cellInfos = new int[flatRoom.Dimensions.x * flatRoom.Dimensions.y];
            flatRoom.wallSpriteReference = new AssetReferenceSprite("");
            flatRoom.wallAndOpeningSpriteReference = new AssetReferenceSprite("");

            for (var i = 0; i < flatRoom.CellInfos.Length; i++)
            {
                flatRoom.CellInfos[i] = 1;
            }

            dbRoomBlueprint.Add(flatRoom);
        }
    }

    private static void CreateSpecialFlatRoom(string template, int sortOrder)
    {
        var dbRoomBlueprint = DatabaseRepository.GetDatabase<RoomBlueprint>();
        var flatRoomName = "Flat" + template;

        if (dbRoomBlueprint.TryGetElement(flatRoomName, out _))
        {
            return;
        }

        var flatRoom = Object.Instantiate(dbRoomBlueprint.GetElement(template));

        flatRoom.name = flatRoomName;
        flatRoom.guid = GuidHelper.Create(CeNamespaceGuid, flatRoom.name).ToString();
        flatRoom.GuiPresentation.title = "Flat".Khaki() + " " + Gui.Localize(flatRoom.GuiPresentation.Title);
        flatRoom.GuiPresentation.sortOrder = sortOrder;
        flatRoom.GuiPresentation.hidden = true;
        flatRoom.category = FlatRoomsCategoryName;

        for (var i = 0; i < flatRoom.CellInfos.Length; i++)
        {
            if (flatRoom.CellInfos[i] == 2 || flatRoom.CellInfos[i] == 3)
            {
                flatRoom.CellInfos[i] = 1;
            }
        }

        dbRoomBlueprint.Add(flatRoom);
    }

    private static void UnleashGadgetsOnAllEnvironments()
    {
        var dbGadgetBlueprint = DatabaseRepository.GetDatabase<GadgetBlueprint>();
        var dbEnvironmentDefinition = DatabaseRepository.GetDatabase<EnvironmentDefinition>();
        var newGadgets = new List<GadgetBlueprint>();

        foreach (var gadgetBlueprint in dbGadgetBlueprint)
        {
            if (IsModdedName(gadgetBlueprint.Name))
            {
                continue;
            }

            foreach (var prefabByEnvironment in gadgetBlueprint.PrefabsByEnvironment)
            {
                if (string.IsNullOrEmpty(prefabByEnvironment.Environment))
                {
                    continue;
                }

                var environmentName = prefabByEnvironment.Environment;
                var prefabEnvironmentDefinition = dbEnvironmentDefinition.GetElement(environmentName);
                var newGadgetName = gadgetBlueprint.Name + "~" + environmentName + ModdedSuffix;
                var categoryName = gadgetBlueprint.Category + "~" + environmentName + ModdedSuffix;

                if (dbGadgetBlueprint.TryGetElement(newGadgetName, out _))
                {
                    continue;
                }

                var newGadgetBlueprint = Object.Instantiate(gadgetBlueprint);

                newGadgetBlueprint.name = newGadgetName;
                newGadgetBlueprint.guid = GuidHelper.Create(CeNamespaceGuid, newGadgetBlueprint.name).ToString();
                newGadgetBlueprint.GuiPresentation.Title = Gui.Localize(gadgetBlueprint.GuiPresentation.Title) + " " +
                                                           Gui.Localize(prefabEnvironmentDefinition.GuiPresentation
                                                               .Title).Khaki();
                newGadgetBlueprint.category = categoryName;
                newGadgetBlueprint.PrefabsByEnvironment.Clear();

                foreach (var environmentDefinition in dbEnvironmentDefinition)
                {
                    var myPrefabByEnvironment = new BaseBlueprint.PrefabByEnvironmentDescription
                    {
                        environment = environmentDefinition.name,
                        prefabReference = prefabByEnvironment.PrefabReference
                    };

                    newGadgetBlueprint.PrefabsByEnvironment.Add(myPrefabByEnvironment);
                }

                newGadgets.TryAdd(newGadgetBlueprint);
            }
        }

        newGadgets.ForEach(x => dbGadgetBlueprint.Add(x));
        SetModdedGadgetsHiddenStatus();
    }

    private static void UnleashPropsOnAllEnvironments()
    {
        var dbPropBlueprint = DatabaseRepository.GetDatabase<PropBlueprint>();
        var dbEnvironmentDefinition = DatabaseRepository.GetDatabase<EnvironmentDefinition>();
        var newProps = new List<PropBlueprint>();

        foreach (var propBlueprint in dbPropBlueprint)
        {
            if (IsModdedName(propBlueprint.Name))
            {
                continue;
            }

            foreach (var prefabByEnvironment in propBlueprint.PrefabsByEnvironment)
            {
                if (string.IsNullOrEmpty(prefabByEnvironment.Environment))
                {
                    continue;
                }

                var environmentName = prefabByEnvironment.Environment;
                var prefabEnvironmentDefinition = dbEnvironmentDefinition.GetElement(environmentName);
                var newPropName = propBlueprint.Name + "~" + environmentName + ModdedSuffix;
                var categoryName = propBlueprint.Category + "~" + environmentName + ModdedSuffix;

                if (dbPropBlueprint.TryGetElement(newPropName, out _))
                {
                    continue;
                }

                var newPropBlueprint = Object.Instantiate(propBlueprint);

                newPropBlueprint.name = newPropName;
                newPropBlueprint.guid = GuidHelper.Create(CeNamespaceGuid, newPropBlueprint.name).ToString();
                newPropBlueprint.GuiPresentation.Title = Gui.Localize(propBlueprint.GuiPresentation.Title) + " " +
                                                         Gui.Localize(prefabEnvironmentDefinition.GuiPresentation
                                                             .Title).Khaki();
                newPropBlueprint.category = categoryName;
                newPropBlueprint.PrefabsByEnvironment.Clear();

                foreach (var environmentDefinition in dbEnvironmentDefinition)
                {
                    var myPrefabByEnvironment = new BaseBlueprint.PrefabByEnvironmentDescription
                    {
                        environment = environmentDefinition.name,
                        prefabReference = prefabByEnvironment.PrefabReference
                    };

                    newPropBlueprint.PrefabsByEnvironment.Add(myPrefabByEnvironment);
                }

                newProps.TryAdd(newPropBlueprint);
            }
        }

        newProps.ForEach(x => dbPropBlueprint.Add(x));
        SetModdedPropsHiddenStatus();
    }

    private static void UnleashRoomsOnAllEnvironments()
    {
        var dbRoomBlueprint = DatabaseRepository.GetDatabase<RoomBlueprint>();
        var dbEnvironmentDefinition = DatabaseRepository.GetDatabase<EnvironmentDefinition>();
        var newRooms = new List<RoomBlueprint>();

        foreach (var roomBlueprint in dbRoomBlueprint)
        {
            if (roomBlueprint.Category != "EmptyRooms" && roomBlueprint.Category != FlatRoomsCategoryName)
            {
                continue;
            }

            foreach (var prefabByEnvironment in roomBlueprint.PrefabsByEnvironment)
            {
                if (string.IsNullOrEmpty(prefabByEnvironment.Environment))
                {
                    continue;
                }

                var environmentName = prefabByEnvironment.Environment;
                var prefabEnvironmentDefinition = dbEnvironmentDefinition.GetElement(environmentName);
                var newRoomName = roomBlueprint.Name + "~" + environmentName + ModdedSuffix;
                var categoryName = roomBlueprint.Category + "~" + environmentName + ModdedSuffix;

                if (prefabEnvironmentDefinition.Outdoor)
                {
                    OutdoorRooms.Add(newRoomName);
                    OutdoorRooms.Add(roomBlueprint.Name);
                }

                if (dbRoomBlueprint.TryGetElement(newRoomName, out _))
                {
                    continue;
                }

                var newRoomBlueprint = Object.Instantiate(roomBlueprint);

                newRoomBlueprint.name = newRoomName;
                newRoomBlueprint.guid = GuidHelper.Create(CeNamespaceGuid, newRoomBlueprint.name).ToString();
                newRoomBlueprint.GuiPresentation.Title = Gui.Localize(roomBlueprint.GuiPresentation.Title) + " " +
                                                         Gui.Localize(prefabEnvironmentDefinition.GuiPresentation
                                                             .Title).Khaki();
                newRoomBlueprint.category = categoryName;
                newRoomBlueprint.GuiPresentation.hidden = false;
                newRoomBlueprint.PrefabsByEnvironment.Clear();

                foreach (var environmentDefinition in dbEnvironmentDefinition)
                {
                    if (prefabEnvironmentDefinition.Outdoor && !environmentDefinition.Outdoor)
                    {
                        continue;
                    }

                    var myPrefabByEnvironment = new BaseBlueprint.PrefabByEnvironmentDescription
                    {
                        environment = environmentDefinition.name,
                        prefabReference = prefabByEnvironment.PrefabReference
                    };

                    newRoomBlueprint.PrefabsByEnvironment.Add(myPrefabByEnvironment);
                }

                newRooms.Add(newRoomBlueprint);
            }
        }

        newRooms.ForEach(x => dbRoomBlueprint.Add(x));
        SetModdedRoomsHiddenStatus();
    }

    private static void SetModdedGadgetsHiddenStatus()
    {
        var dbGadgetBlueprint = DatabaseRepository.GetDatabase<GadgetBlueprint>();

        foreach (var gadgetBlueprint in dbGadgetBlueprint)
        {
            if (IsModdedName(gadgetBlueprint.Name))
            {
                gadgetBlueprint.GuiPresentation.hidden = !Main.Settings.EnableDungeonMakerModdedContent;
            }
        }
    }

    private static void SetModdedPropsHiddenStatus()
    {
        var dbPropBlueprint = DatabaseRepository.GetDatabase<PropBlueprint>();

        foreach (var propBlueprint in dbPropBlueprint)
        {
            if (IsModdedName(propBlueprint.Name))
            {
                propBlueprint.GuiPresentation.hidden = !Main.Settings.EnableDungeonMakerModdedContent;
            }
        }
    }

    private static void SetModdedRoomsHiddenStatus()
    {
        var dbRoomBlueprint = DatabaseRepository.GetDatabase<RoomBlueprint>();

        foreach (var roomBlueprint in dbRoomBlueprint)
        {
            if (IsModdedName(roomBlueprint.Name) ||
                roomBlueprint.Name.StartsWith("Flat", StringComparison.Ordinal))
            {
                roomBlueprint.GuiPresentation.hidden = !Main.Settings.EnableDungeonMakerModdedContent;
            }
        }
    }

    private static bool IsModdedName([CanBeNull] string name)
    {
        return name != null && name.EndsWith(ModdedSuffix, StringComparison.Ordinal);
    }

    private enum ExtendedDungeonSize
    {
        Huge = UserLocationDefinitions.Size.Large + 1,
        Gargantuan
    }
}
