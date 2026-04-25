using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using TA;
using static SolastaUnfinishedBusiness.Subclasses.Builders.MetamagicBuilders;

namespace SolastaUnfinishedBusiness.Models;

internal static class MetamagicContext
{
    internal const string FeatMetamagicAdeptPointPoolTag = "PointPoolFeatMetamagicAdept";

    internal static HashSet<MetamagicOptionDefinition> Metamagic { get; private set; } = [];

    internal static void LateLoad()
    {
        LoadMetamagic(BuildMetamagicAltruisticSpell());
        LoadMetamagic(BuildMetamagicFocusedSpell());
        LoadMetamagic(BuildMetamagicPowerfulSpell());
        LoadMetamagic(BuildMetamagicSeekingSpell());
        LoadMetamagic(BuildMetamagicTransmutedSpell());
        LoadMetamagic(BuildMetamagicWidenedSpell());

        // sorting
        Metamagic = Metamagic.OrderBy(x => x.FormatTitle()).ToHashSet();

        // settings paring
        foreach (var name in Main.Settings.MetamagicEnabled
                     .Where(name => Metamagic.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.MetamagicEnabled.Remove(name);
        }
    }

    private static void LoadMetamagic([NotNull] MetamagicOptionDefinition metamagicDefinition)
    {
        Metamagic.Add(metamagicDefinition);
        UpdateMetamagicVisibility(metamagicDefinition);
    }

    private static void UpdateMetamagicVisibility([NotNull] BaseDefinition metamagicDefinition)
    {
        metamagicDefinition.GuiPresentation.hidden =
            !Main.Settings.MetamagicEnabled.Contains(metamagicDefinition.Name);
    }

    internal static void SwitchMetamagic(MetamagicOptionDefinition metamagicDefinition, bool active)
    {
        if (!Metamagic.Contains(metamagicDefinition))
        {
            return;
        }

        var name = metamagicDefinition.Name;

        if (active)
        {
            Main.Settings.MetamagicEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.MetamagicEnabled.Remove(name);
        }

        UpdateMetamagicVisibility(metamagicDefinition);
    }

    internal static int CompareMetamagic(MetamagicOptionDefinition a, MetamagicOptionDefinition b)
    {
        var compare = Math.Max(a.SorceryPointsCost, 1) - Math.Max(b.SorceryPointsCost, 1);

        return compare == 0
            ? string.Compare(a.FormatTitle(), b.FormatTitle(), StringComparison.CurrentCultureIgnoreCase)
            : compare;
    }

    internal static List<MetamagicOptionDefinition> GetVisibleMetamagicOptions()
    {
        var metamagicDatabase = DatabaseRepository.GetDatabase<MetamagicOptionDefinition>();

        if (metamagicDatabase == null)
        {
            return [];
        }

        return metamagicDatabase
            .GetAllElements()
            .Where(x => !x.GuiPresentation.Hidden)
            .OrderBy(x => x, Comparer<MetamagicOptionDefinition>.Create(CompareMetamagic))
            .ToList();
    }

    internal static List<MetamagicOptionDefinition> GetRestrictedVisibleMetamagicOptions(
        IReadOnlyCollection<string> restrictedChoices)
    {
        var metamagicOptions = GetVisibleMetamagicOptions();

        if (restrictedChoices is not { Count: > 0 })
        {
            return metamagicOptions;
        }

        var restrictedChoiceNames = restrictedChoices.ToHashSet(StringComparer.Ordinal);

        return metamagicOptions
            .Where(option => option != null && restrictedChoiceNames.Contains(option.Name))
            .ToList();
    }
}
