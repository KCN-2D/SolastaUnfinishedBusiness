using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class CustomizedSubFeatureDefinitions
{
    private static readonly Dictionary<BaseDefinition, List<object>> CustomSubFeatures = new();

    private static List<object> GetOrCreateForKey([NotNull] BaseDefinition definition)
    {
        if (CustomSubFeatures.TryGetValue(definition, out var value))
        {
            return value;
        }

        value = [];
        CustomSubFeatures.Add(definition, value);

        return value;
    }

    [CanBeNull]
    // ReSharper disable once ReturnTypeCanBeEnumerable.Local
    private static List<object> GetForKey([NotNull] BaseDefinition definition)
    {
        return !CustomSubFeatures.TryGetValue(definition, out var value) ? null : value;
    }

    [NotNull]
    internal static List<object> GetCustomSubFeatures([CanBeNull] this BaseDefinition definition)
    {
        if (!definition)
        {
            return [];
        }

        return GetForKey(definition) is { Count: > 0 } subFeatures
            ? [..subFeatures]
            : [];
    }

    internal static void AddCustomSubFeatures<T>(
        [NotNull] this T definition,
        [NotNull] params object[] subFeatures)
        where T : BaseDefinition
    {
        GetOrCreateForKey(definition).AddRange(subFeatures);
    }

    [NotNull]
    internal static List<T> GetAllSubFeaturesOfType<T>([CanBeNull] this BaseDefinition definition) where T : class
    {
        var results = new List<T>();

        if (!definition)
        {
            return results;
        }

        if (definition is T custom)
        {
            results.Add(custom);
        }

        var subFeatures = GetForKey(definition)?.OfType<T>();

        if (subFeatures != null)
        {
            results.AddRange(subFeatures);
        }

        return results;
    }

    // Frame-based readers only need to inspect declarations. Keep the copying
    // List API above for callers that modify their results, without copying it
    // for each equipment lookup or caching declarations past Add/Set changes.
    internal static SubFeaturesOfType<T> EnumerateSubFeaturesOfType<T>(
        [CanBeNull] this BaseDefinition definition) where T : class =>
        definition ? new SubFeaturesOfType<T>(definition as T, GetForKey(definition)) : default;

    internal readonly struct SubFeaturesOfType<T>(T first, List<object> others) where T : class
    {
        internal bool HasAny
        {
            get
            {
                var enumerator = GetEnumerator();
                return enumerator.MoveNext();
            }
        }

        public Enumerator GetEnumerator() => new(first, others);

        internal struct Enumerator(T first, List<object> others)
        {
            private bool _visitedFirst;
            private int _index;

            public T Current { get; private set; }

            public bool MoveNext()
            {
                if (!_visitedFirst)
                {
                    _visitedFirst = true;
                    if (first != null)
                    {
                        Current = first;
                        return true;
                    }
                }

                while (others != null && _index < others.Count)
                {
                    if (others[_index++] is not T match) continue;
                    Current = match;
                    return true;
                }

                Current = null;
                return false;
            }
        }
    }

    [CanBeNull]
    internal static T GetFirstSubFeatureOfType<T>([CanBeNull] this BaseDefinition definition) where T : class
    {
        if (!definition)
        {
            return null;
        }

        if (definition is T custom)
        {
            return custom;
        }

        var subFeatures = GetForKey(definition);
        if (subFeatures != null)
        {
            foreach (var feature in subFeatures)
            {
                if (feature is T match)
                {
                    return match;
                }
            }
        }

        return null;
    }

    internal static bool HasSubFeatureOfType<T>(this BaseDefinition definition) where T : class
    {
        return definition.GetFirstSubFeatureOfType<T>() != null;
    }

    internal static void SetSubFeatureOfType<T>(this BaseDefinition definition, [CanBeNull] object feature)
        where T : class
    {
        var features = feature == null ? GetForKey(definition) : GetOrCreateForKey(definition);

        //first try removing all existing features of this type
        features?.RemoveAll(x => x.GetType() == typeof(T));
        //try adding new one
        if (feature != null) { features?.Add(feature); }
    }
}
