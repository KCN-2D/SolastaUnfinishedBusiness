using System.Collections.Generic;
using HarmonyLib;
using JetBrains.Annotations;
#if DEBUG
using SolastaUnfinishedBusiness.Api.Diagnostics;
#endif

namespace SolastaUnfinishedBusiness.Api;

internal static partial class DatabaseHelper
{
    [NotNull]
    internal static T GetDefinition<T>(string key) where T : BaseDefinition
    {
        var db = DatabaseRepository.GetDatabase<T>();

#if DEBUG
        if (db == null)
        {
            throw new SolastaUnfinishedBusinessException(
                $"{typeof(T).Name} not found.");
        }

        if (!db.TryGetElement(key, out var definition))
        {
            throw new SolastaUnfinishedBusinessException(
                $"{key} not found in database {typeof(T).Name}.");
        }

        return definition;
#else
        return db.GetElement(key);
#endif
    }

    internal static bool TryGetDefinition<T>(string key, out T definition) where T : BaseDefinition
    {
        var db = DatabaseRepository.GetDatabase<T>();

#if DEBUG
        if (key != null && db != null)
        {
            return TryGetDefinition(db, key, out definition);
        }

        definition = null;

        return false;
#else
        return TryGetDefinition(db, key, out definition);
#endif
    }

    internal static bool TryGetDefinition<T>(Database<T> database, string key, out T definition,
        bool includeUserContent = true) where T : BaseDefinition
    {
        // Native's out-parameter overload enumerates every key. Access the same live
        // dictionary so edits and user-content loading do not need cache invalidation.
        var values = DatabaseValues<T>.Values(database);
        definition = null;

        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (!UsesOrdinalDefinitionNames(values.Comparer))
        {
            return database.TryGetElement(key, out definition, includeUserContent);
        }

        if (!values.TryGetValue(key, out var found))
        {
            return false;
        }

        if (!includeUserContent && found.IsUserContent)
        {
            return false;
        }

        definition = found;

        return true;
    }

    internal static bool HasDefinition<T>(Database<T> database, string key, bool includeUserContent = true)
        where T : BaseDefinition
    {
        var values = DatabaseValues<T>.Values(database);

        if (!UsesOrdinalDefinitionNames(values.Comparer))
        {
            return database.HasElement(key, includeUserContent);
        }

        // HasElement permits an empty key, unlike the native out-parameter lookup.
        return key != null && values.TryGetValue(key, out var found) &&
               (includeUserContent || !found.IsUserContent);
    }

    private static bool UsesOrdinalDefinitionNames(IEqualityComparer<string> comparer)
    {
        // Native compares strings directly, even when another mod replaces the table.
        return ReferenceEquals(comparer, EqualityComparer<string>.Default) ||
               ReferenceEquals(comparer, System.StringComparer.Ordinal);
    }

    private static class DatabaseValues<T> where T : BaseDefinition
    {
        internal static readonly AccessTools.FieldRef<Database<T>, Dictionary<string, T>> Values =
            AccessTools.FieldRefAccess<Database<T>, Dictionary<string, T>>("valuesTable");
    }

    internal static class ArmorTypeDefinitions
    {
        internal static ArmorTypeDefinition ShieldType { get; } = GetDefinition<ArmorTypeDefinition>("ShieldType");
    }
}
