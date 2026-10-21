using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using static SolastaUnfinishedBusiness.Models.SpellCastingResourceContext;

namespace SolastaUnfinishedBusiness.Models;

// Native spell displays share source titles and independent free Wizard columns.
// Display-only repertoires are never attached to a character or serialized;
// casts resolve to real source and resource owners.
internal static class SpellSelectionContext
{
    private static readonly ConditionalWeakTable<RulesetSpellRepertoire, ResourceOption> Views = new();

    private static readonly string[] SpellSourceTitleFormats =
    [
        "Screen/&{0}ExtraSpellTitle",
        "Screen/&{0}SpellTitle",
        "Tag/&{0}SpellSpecialTagTitle",
        "Tag/&{0}CantripSpecialTagTitle",
        "Tag/&{0}CantripOrSpellSpecialTagTitle",
        "Feat/&Feat{0}Title",
        "Feat/&{0}Title",
        "FightingStyle/&{0}Title",
        "Feature/&{0}Title"
    ];

    internal static string NormalizeSpellSourceTag(string tag)
    {
        if (string.IsNullOrEmpty(tag) ||
            tag.StartsWith(SpellPreparationContext.FeatureTag + "|", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith(LevelUpHelper.ExtraClassTag + "|", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith(LevelUpHelper.ExtraSubclassTag + "|", StringComparison.OrdinalIgnoreCase))
        {
            return tag;
        }

        tag = Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(tag);
        return tag == "DOMAIN" ? "Domain" : tag;
    }

    internal static bool TryResolveGrantedSpellSource(
        string tag,
        SpellDefinition spell,
        RulesetCharacter caster,
        RulesetSpellRepertoire castingSource,
        out string title,
        out string description)
    {
        title = string.Empty;
        description = string.Empty;
        if (string.IsNullOrEmpty(tag) || caster == null || !spell)
        {
            return false;
        }

        tag = NormalizeSpellSourceTag(tag);

        // Preparation belongs to its grant even when an independently learned class
        // copy supplies the spell's casting origin. Never change that casting origin.
        var sources = caster.SpellRepertoires.Where(repertoire =>
        {
            var spellTag = repertoire.SpellCastingFeature?.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();
            var matches = spellTag != null &&
                          string.Equals(NormalizeSpellSourceTag(spellTag.Name), tag, StringComparison.OrdinalIgnoreCase) ||
                          tag.Equals("Race", StringComparison.OrdinalIgnoreCase) && repertoire.SpellCastingRace;

            return matches && CharacterInspectionScreenEnhancement.GetInspectionLearnedSpells(repertoire).Contains(spell);
        }).ToArray();
        if (sources.Length == 0)
        {
            return false;
        }

        var source = sources.Contains(castingSource) ? castingSource : SelectFeatCastingRepertoire(caster, sources);
        title = source.FormatHeader();
        var alwaysPrepared = source.SpellCastingFeature.SpellReadyness == RuleDefinitions.SpellReadyness.AllKnown ||
                             source.AutoPreparedSpells.Contains(spell);
        if (TryFormatSpellSourceDescription(caster, source, spell, title, alwaysPrepared, out description))
        {
            return true;
        }

        // A recognized grant must not borrow a family's fixed daily-use text when
        // its actual resource policy differs. Native counters retain those details.
        description = alwaysPrepared ? Gui.Localize("Screen/&AutoPreparedSpellSourceDescription") : null;
        return alwaysPrepared;
    }

    private static bool TryFormatSpellSourceDescription(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        string sourceTitle,
        bool alwaysPrepared,
        out string description)
    {
        description = null;
        if (caster == null || repertoire?.SpellCastingFeature == null || !spell)
        {
            return false;
        }

        if (spell.SpellLevel == 0)
        {
            description = Gui.Format("Screen/&FeatGrantedCantripDescriptionFormat", sourceTitle);
            return true;
        }

        var feature = repertoire.SpellCastingFeature;
        var tag = feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();
        var resources = EnumerateResources(caster, spell, repertoire)
            .Where(option => option.Kind == ResourceKind.FreeRepertoire && option.CastingRepertoire == repertoire)
            .Select(option =>
            {
                option.GetUses(caster, out _, out var maximum);
                return (Option: option, Maximum: maximum);
            }).ToArray();

        if (resources.Length == 0)
        {
            return false;
        }

        // Exceptional grants own a complete localized rules body. The common source
        // resolver supplies only the actual casting level and maximum number of uses.
        if (resources.Length == 1 && tag?.SourceDescription is { } sourceDescription)
        {
            var template = sourceDescription.FormatCharacterDescription(
                sourceDescription.FormatDescription(null), caster);
            if (!CustomTooltipProvider.IsUnavailableContent(template))
            {
                description = Gui.Format(template, resources[0].Option.SlotLevel.ToString(),
                    resources[0].Maximum.ToString());
                return true;
            }
        }

        if (!alwaysPrepared || tag?.FreeCastingIgnoresComponents == true ||
            resources.Any(resource => resource.Maximum <= 0))
        {
            return false;
        }

        var slotCastable = LevelUpHelper.EnumerateSlotCastableFeatSpells(caster)
            .Any(entry => entry.Repertoire == repertoire && entry.Spell == spell);
        var first = resources[0];
        if (resources.Length == 1)
        {
            var shared = HasSharedSpellSourceUses(caster, repertoire, spell, first.Option.SlotLevel);
            if (slotCastable && first.Maximum == 1)
            {
                if (feature.SlotsRecharge == RuleDefinitions.RechargeRate.LongRest &&
                    first.Option.SlotLevel == spell.SpellLevel)
                {
                    description = Gui.Localize(shared
                        ? "Screen/&FeatGrantedSpellSharedUseDescription"
                        : "Screen/&FeatGrantedSpellSingleUseDescription");
                    return true;
                }

                if (!shared && feature.SlotsRecharge == RuleDefinitions.RechargeRate.ShortRest)
                {
                    description = Gui.Format("Screen/&GrantedSpellShortRestSourceDescriptionFormat",
                        first.Option.SlotLevel.ToString());
                    return true;
                }
            }

            if (!slotCastable && feature.SlotsRecharge == RuleDefinitions.RechargeRate.LongRest)
            {
                description = Gui.Format(shared
                        ? "Screen/&GrantedSpellSharedFreeLongRestSourceDescriptionFormat"
                        : "Screen/&GrantedSpellFreeLongRestSourceDescriptionFormat",
                    first.Option.SlotLevel.ToString(), first.Maximum.ToString());
                return true;
            }
        }
        else if (!slotCastable && feature.SlotsRecharge == RuleDefinitions.RechargeRate.LongRest &&
                 resources.All(resource => resource.Maximum == first.Maximum) &&
                 resources.Select(resource => resource.Option.SlotLevel)
                     .SequenceEqual(Enumerable.Range(first.Option.SlotLevel, resources.Length)) &&
                 resources.Any(resource => HasSharedSpellSourceUses(caster, repertoire, spell, resource.Option.SlotLevel)))
        {
            description = Gui.Format("Screen/&GrantedSpellFreeLevelRangeSourceDescriptionFormat",
                first.Option.SlotLevel.ToString(), resources[resources.Length - 1].Option.SlotLevel.ToString(),
                first.Maximum.ToString());
            return true;
        }

        // Other policies keep their existing source description instead of borrowing
        // an unrelated grant's preparation, recharge or paid-slot permissions.
        return false;
    }

    private static bool HasSharedSpellSourceUses(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        int slotLevel) => CharacterInspectionScreenEnhancement.GetInspectionLearnedSpells(repertoire)
        .Where(candidate => candidate != spell && candidate.SpellLevel > 0)
        .Any(candidate => EnumerateResources(caster, candidate, repertoire).Any(option =>
            option.Kind == ResourceKind.FreeRepertoire && option.CastingRepertoire == repertoire &&
            option.SlotLevel == slotLevel));

    internal static bool TryFormatSpellTagSourceTitle(
        RulesetSpellRepertoire repertoire,
        bool shortTitle,
        out string title,
        out string source)
    {
        title = null;
        source = null;

        var spellCastingFeature = repertoire?.SpellCastingFeature;
        var spellTag = spellCastingFeature?
            .GetFirstSubFeatureOfType<FeatHelpers.SpellTag>()?.Name;
        if (string.IsNullOrEmpty(spellTag))
        {
            return false;
        }

        var presentation = spellCastingFeature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>()?.Presentation;
        title = presentation?.FormatTitle(null);
        if (IsUsableSpellSourceTitle(title))
        {
            source = spellTag;
            return true;
        }

        if (shortTitle)
        {
            if (Tabletop2024Context.TryGetTabletop2024FeatSpellSourceShortTitle(spellTag, out title) ||
                TryFormatClassHolderTitle(spellCastingFeature, out title))
            {
                source = "shortClass";

                return true;
            }
        }

        var sourceTag = Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(spellTag);
        var owner = repertoire.GetCastingClass();
        if (owner && TryLocalizeSpellSourceVariantTitle(sourceTag + owner.Name, out title, out source) ||
            TryLocalizeSpellSourceVariantTitle(spellTag, out title, out source))
        {
            return true;
        }

        var titleTerm = $"Screen/&{sourceTag}ExtraSpellTitle";
        if (TryLocalizeSpellSourceTitleTerm(titleTerm, out title))
        {
            source = titleTerm;
            if (owner)
            {
                title = Gui.Format("Feat/&GeneralFeat2024VariantTitle", title, owner.FormatTitle());
            }

            return true;
        }

        if (TryFormatGrantedSpellSourceTitle(repertoire, out title, out source))
        {
            return true;
        }

        if (TryLocalizeSpellSourceTitle(sourceTag, out title, out source))
        {
            if (owner)
            {
                title = Gui.Format("Feat/&GeneralFeat2024VariantTitle", title, owner.FormatTitle());
            }

            return true;
        }

        if (TryFormatClassHolderTitle(spellCastingFeature, out title))
        {
            source = "classHolder";

            return true;
        }

        if (TryFormatSpellCastingFeatureTitle(spellCastingFeature, out title))
        {
            source = "featureTitle";

            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSpellSourceTitleTags(
        string spellTag,
        string selectionTag,
        string sourceTag)
    {
        return new[] { spellTag, selectionTag, sourceTag }
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal);
    }

    internal static bool TryLocalizeSpellSourceTitle(string spellTag, out string title, out string source)
    {
        title = null;
        source = null;

        // Encoded feature/class sources are resolved by SpellBox's native source lookup;
        // their payload must not be interpreted as a plain feat-selection suffix.
        if (string.IsNullOrEmpty(spellTag) || spellTag.IndexOf('|') >= 0)
        {
            return false;
        }

        title = FeatHelpers.GetSpellSelectionPresentation(spellTag)?.FormatTitle(null);
        if (IsUsableSpellSourceTitle(title))
        {
            source = spellTag;
            return true;
        }

        var selectionTag = Tabletop2024Context.GetTabletop2024FeatSpellSelectionTag(spellTag);
        var sourceTag = Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(spellTag);
        var candidates = EnumerateSpellSourceTitleTags(spellTag, selectionTag, sourceTag).ToArray();

        if (TryLocalizeSpellSourceVariantTitle(spellTag, out title, out source))
        {
            return true;
        }

        // A feat family owns its source name; a UI category is not a spell grant.
        // Native tags such as Origin can share a category's translation suffix.
        foreach (var candidate in candidates.Reverse())
        {
            if (!TryGetSpellSourceGroup(candidate, sourceTag, out var group))
            {
                continue;
            }

            // Managed groups can store a title localized at construction time.
            // Keep their dedicated term live across language changes once ownership is proven.
            var titleTerm = $"Feat/&FeatGroup{candidate}Title";
            if (TryLocalizeSpellSourceTitleTerm(titleTerm, out title))
            {
                source = titleTerm;
                return true;
            }

            var groupTitle = group.FormatTitle();
            if (IsUsableSpellSourceTitle(groupTitle))
            {
                title = groupTitle;
                source = group.GuiPresentation.Title;
                return true;
            }
        }

        foreach (var candidate in candidates)
        {
            foreach (var format in SpellSourceTitleFormats)
            {
                var titleTerm = string.Format(format, candidate);
                if (TryLocalizeSpellSourceTitleTerm(titleTerm, out title))
                {
                    source = titleTerm;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryLocalizeSpellSourceVariantTitle(string spellTag, out string title, out string source)
    {
        title = null;
        source = null;
        if (string.IsNullOrEmpty(spellTag) || spellTag.IndexOf('|') >= 0)
        {
            return false;
        }

        var selectionTag = Tabletop2024Context.GetTabletop2024FeatSpellSelectionTag(spellTag);
        var sourceTag = Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(spellTag);
        // An acquisition variant is more precise than its family's group title.
        // Spell/cantrip labels and per-level tags do not name an acquisition row.
        foreach (var candidate in EnumerateSpellSourceTitleTags(spellTag, selectionTag, sourceTag)
                     .Where(candidate => candidate != sourceTag))
        {
            var term = $"Screen/&{candidate}ExtraSpellTitle";
            if (!TryLocalizeSpellSourceTitleTerm(term, out title))
            {
                continue;
            }

            source = term;
            return true;
        }

        return false;
    }

    private static bool TryGetSpellSourceGroup(string tag, string sourceTag, out FeatDefinition group)
    {
        if (!DatabaseHelper.TryGetDefinition($"FeatGroup{tag}", out group) ||
            group.GetFirstSubFeatureOfType<IGroupedFeat>() == null ||
            Tabletop2024Context.IsTabletopContainerGroup(group))
        {
            return false;
        }

        var pending = new Stack<BaseDefinition>();
        var visited = new HashSet<BaseDefinition>();
        pending.Push(group);
        while (pending.Count > 0)
        {
            var definition = pending.Pop();
            if (definition == null || !visited.Add(definition))
            {
                continue;
            }

            switch (definition)
            {
                case FeatDefinition feat when !Tabletop2024Context.IsTabletopContainerGroup(feat):
                    foreach (var feature in feat.Features)
                    {
                        pending.Push(feature);
                    }

                    if (feat.GetFirstSubFeatureOfType<IGroupedFeat>() is { } grouped)
                    {
                        foreach (var child in grouped.GetSubFeats(true))
                        {
                            pending.Push(child);
                        }
                    }

                    break;
                case FeatureDefinitionFeatureSet set:
                    foreach (var feature in set.FeatureSet)
                    {
                        pending.Push(feature);
                    }

                    break;
                case FeatureDefinitionCastSpell casting:
                    var spellTag = casting.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>();
                    if (spellTag != null &&
                        Tabletop2024Context.GetTabletop2024FeatSpellSourceTag(spellTag.Name) == sourceTag)
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    internal static bool IsFeatSpellRepertoire(RulesetSpellRepertoire repertoire)
    {
        return repertoire?.SpellCastingFeature is { SpellReadyness: RuleDefinitions.SpellReadyness.AllKnown } feature &&
               feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null &&
               !repertoire.SpellCastingClass && !repertoire.SpellCastingSubclass && !repertoire.SpellCastingRace;
    }

    internal static FeatDefinition GetSpellSourceFeat(RulesetCharacter character, RulesetSpellRepertoire repertoire)
    {
        var feature = repertoire?.SpellCastingFeature;
        return feature != null && character is RulesetCharacterHero hero
            ? hero.TrainedFeats.FirstOrDefault(feat =>
                RulesetActorExtensions.FlattenFeatureList(feat.Features).Contains(feature))
            : null;
    }

    internal static BaseDefinition GetSpellSourceGrant(RulesetCharacter character, RulesetSpellRepertoire repertoire)
    {
        var feature = repertoire?.SpellCastingFeature;
        if (feature == null || character is not RulesetCharacterHero hero)
        {
            return null;
        }

        return (BaseDefinition)GetSpellSourceFeat(hero, repertoire) ??
               hero.TrainedFightingStyles.FirstOrDefault(style =>
                   RulesetActorExtensions.FlattenFeatureList(style.Features).Contains(feature));
    }

    private static bool TryFormatGrantedSpellSourceTitle(
        RulesetSpellRepertoire repertoire,
        out string title,
        out string source)
    {
        title = null;
        source = null;
        var hero = repertoire.GetCaster()?.GetOriginalHero();
        var grant = GetSpellSourceGrant(hero, repertoire);
        if (grant == null)
        {
            return false;
        }

        title = grant.FormatTitle();
        source = grant.GuiPresentation.Title;
        return IsUsableSpellSourceTitle(title);
    }

    private static bool TryLocalizeSpellSourceTitleTerm(string titleTerm, out string title)
    {
        title = null;

        if (string.IsNullOrEmpty(titleTerm) ||
            !TranslatorContext.HasTranslation(titleTerm))
        {
            return false;
        }

        var localizedTitle = Gui.Localize(titleTerm);

        if (!IsUsableSpellSourceTitle(localizedTitle, titleTerm))
        {
            return false;
        }

        title = localizedTitle;

        return true;
    }

    private static bool TryFormatClassHolderTitle(FeatureDefinitionCastSpell spellCastingFeature, out string title)
    {
        title = spellCastingFeature?
            .GetFirstSubFeatureOfType<ClassHolder>()?.Class?
            .FormatTitle();

        return IsUsableSpellSourceTitle(title);
    }

    private static bool TryFormatSpellCastingFeatureTitle(
        FeatureDefinitionCastSpell spellCastingFeature,
        out string title)
    {
        title = null;

        var titleTerm = spellCastingFeature?.GuiPresentation?.Title;

        if (string.IsNullOrEmpty(titleTerm) ||
            titleTerm == Gui.NoLocalization ||
            titleTerm == Gui.EmptyContent)
        {
            return false;
        }

        title = spellCastingFeature.FormatTitle();

        return IsUsableSpellSourceTitle(title, titleTerm);
    }

    private static bool IsUsableSpellSourceTitle(string title, string titleTerm = null)
    {
        return !string.IsNullOrWhiteSpace(title) &&
               title != titleTerm &&
               !title.Contains("/&") &&
               !title.Contains("{0}");
    }

    internal static bool TryBeginSelection(RulesetCharacter caster, ResourceOption option, out IDisposable state)
    {
        state = null;
        if (!option.IsAvailable(caster))
        {
            return false;
        }

        state = BeginSelection(option);
        return true;
    }

    internal static bool TryGetOption(RulesetSpellRepertoire repertoire, out ResourceOption option)
    {
        option = null;
        return repertoire != null && Views.TryGetValue(repertoire, out option);
    }

    internal static RulesetSpellRepertoire Resolve(RulesetSpellRepertoire repertoire)
    {
        return TryGetOption(repertoire, out var option) ? option.CastingRepertoire : repertoire;
    }

    internal static List<RulesetSpellRepertoire> GetRepertoires(RulesetCharacter caster)
    {
        var repertoires = caster.SpellRepertoires.Where(IsDisplayedRepertoire).ToList();
        var spells = repertoires.SelectMany(repertoire => repertoire.ExtraSpellsByTag
                .Where(entry => entry.Key is "SpellMastery" or "SignatureSpells")
                .SelectMany(entry => entry.Value)).Distinct().ToArray();

        foreach (var spell in spells)
        {
            foreach (var option in EnumerateResources(caster, spell).Where(option =>
                         option.Kind is ResourceKind.SpellMastery or ResourceKind.SignatureSpell))
            {
                repertoires.Add(CreateView(option));
            }
        }

        return repertoires;
    }

    internal static bool IsDisplayedRepertoire(RulesetSpellRepertoire repertoire)
    {
        var feature = repertoire?.SpellCastingFeature;
        if (feature?.SpellListDefinition == null || feature.SpellListDefinition == SpellsContext.EmptySpellList)
        {
            return false;
        }

        // Feat spellcasting features hide their redundant character-sheet description, while their
        // tagged repertoires still own selectable spells and uses. Invocation-only helpers stay hidden.
        return !feature.GuiPresentation.Hidden || feature.GetFirstSubFeatureOfType<FeatHelpers.SpellTag>() != null;
    }

    internal static RulesetSpellRepertoire CreateView(ResourceOption option)
    {
        var owner = option.Repertoire;
        var view = new RulesetSpellRepertoire
        {
            spellCastingFeature = owner.SpellCastingFeature,
            spellCastingClass = owner.SpellCastingClass,
            spellCastingSubclass = owner.SpellCastingSubclass,
            spellCastingRace = owner.SpellCastingRace,
            spellCastingLevel = owner.SpellCastingLevel,
            CharacterInventory = owner.CharacterInventory,
            CharacterName = owner.CharacterName,
            spellAttackBonus = option.CastingRepertoire.SpellAttackBonus,
            saveDC = option.CastingRepertoire.SaveDC,
            formAbilityBonus = option.CastingRepertoire.FormAbilityBonus
        };

        view.KnownSpells.Add(option.Spell);
        view.PreparedSpells.Add(option.Spell);
        Views.Add(view, option);
        return view;
    }

    internal static ResourceOption GetBaseSelection(
        RulesetCharacter caster, RulesetSpellRepertoire repertoire, SpellDefinition spell)
    {
        if (TryGetOption(repertoire, out var view))
        {
            return view.IsFree
                ? view
                : view.AtLevel(GetBaseSelection(caster, view.Repertoire, spell).SlotLevel);
        }

        var level = spell.SpellLevel;
        if (SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire) &&
            repertoire.SpellCastingFeature.CannotUpcast)
        {
            for (var candidate = level; candidate <= 9; candidate++)
            {
                repertoire.GetDisplaySlotNumbers(caster, candidate, out _, out var capacity);
                if (capacity <= 0)
                {
                    continue;
                }

                // Capacity, not remaining uses, identifies a fixed innate casting level.
                level = candidate;
                break;
            }
        }
        else if (repertoire.SpellCastingFeature.UniqueLevelSlots &&
                 (!repertoire.UsesSharedSpellSlots() || !SharedSpellsContext.IsMulticaster(caster)))
        {
            level = repertoire.GetLowestAvailableSlotLevel();
        }

        return GetSelection(repertoire, spell, level, caster);
    }

    internal static ResourceOption GetSelection(
        RulesetSpellRepertoire repertoire, SpellDefinition spell, int level, RulesetCharacter caster = null)
    {
        if (TryGetOption(repertoire, out var option))
        {
            return option.IsFree ? option : option.AtLevel(level);
        }

        var kind = SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire)
            ? ResourceKind.FreeRepertoire
            : ResourceKind.SpellSlot;
        return kind == ResourceKind.SpellSlot
            ? GetSlotSelection(repertoire, spell, level, caster)
            : new ResourceOption(repertoire, spell, level, kind);
    }

    internal static void GetViewSlots(ResourceOption option, int level, out int remaining, out int maximum)
    {
        remaining = maximum = 0;
        if (option.IsFree && option.SlotLevel != level || level < option.Spell.SpellLevel)
        {
            return;
        }

        option = option.IsFree ? option : option.AtLevel(level);
        var caster = option.CastingRepertoire.GetCaster();
        option.GetUses(caster, out remaining, out maximum);
        if (option.IsFree && !option.IsAvailable(caster))
        {
            remaining = 0;
        }

        // Native counters need finite capacities. The native infinity indicator is bound separately.
        if (maximum < 0)
        {
            maximum = 1;
            remaining = remaining < 0 ? 1 : remaining;
        }
    }
}
