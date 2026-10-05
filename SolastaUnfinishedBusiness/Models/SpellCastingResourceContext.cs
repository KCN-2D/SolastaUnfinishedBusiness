using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// A casting choice keeps the spell's origin separate from the repertoire paying for it.
// Native reactions synchronize the ordinal choice, so order by definitions, never localized text.
internal static class SpellCastingResourceContext
{
    private static readonly ConditionalWeakTable<ReactionRequest, RequestResources> Requests = new();
    private static readonly ConditionalWeakTable<RulesetEffectSpell, ResourceOption> Selections = new();

    // CastSpell does not use IntParameter2. Existing markers encode only the resource kind.
    // Higher groups identify a payment repertoire by stable definition order. The native
    // repertoire field remains the spell's origin, including after an effect is saved/reloaded.
    private const int SelectionMarker = -202400;
    private const int ResourceKindCount = (int)ResourceKind.SignatureSpell + 1;
    [ThreadStatic] private static ResourceOption _currentSelection;

    internal static ResourceOption CurrentSelection => _currentSelection;

    internal static bool SupportsItemResourceSelection { get; set; }

    internal static void SetSelectedEffect(CharacterActionParams parameters, RulesetEffect effect)
    {
        parameters.RulesetEffect = effect;
        if (effect is not RulesetEffectSpell spell || !SupportsSelection(spell))
        {
            return;
        }

        var selected = CurrentSelection;
        if (selected != null && IsSameSpell(selected.Spell, spell.SpellDefinition))
        {
            ApplySelection(parameters, selected);
        }
        else
        {
            // A cancelled cast may leave the panel's reusable parameters carrying an old choice.
            ClearSelectionMarker(parameters);
        }
    }

    internal enum ResourceKind
    {
        SpellSlot,
        FreeRepertoire,
        SpellMastery,
        SignatureSpell
    }

    internal sealed class ResourceOption(
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        int slotLevel,
        ResourceKind kind,
        RulesetSpellRepertoire castingRepertoire = null)
    {
        internal RulesetSpellRepertoire Repertoire { get; } = repertoire;
        internal RulesetSpellRepertoire CastingRepertoire { get; } = castingRepertoire ?? repertoire;
        internal SpellDefinition Spell { get; } = spell;
        internal int SlotLevel { get; } = slotLevel;
        internal ResourceKind Kind { get; } = kind;
        internal bool IsFree => Kind != ResourceKind.SpellSlot;
        internal string SourceTitle => Kind switch
        {
            ResourceKind.SpellMastery => Level20Context.WizardSpellMastery.FeatureSpellMastery.FormatTitle(),
            ResourceKind.SignatureSpell => Level20Context.WizardSignatureSpells.PowerSignatureSpells.FormatTitle(),
            _ => CastingRepertoire == Repertoire
                ? Repertoire.FormatHeader()
                : $"{CastingRepertoire.FormatHeader()} · {Repertoire.FormatHeader()}"
        };

        internal ResourceOption AtLevel(int level) =>
            new(Repertoire, Spell, level, Kind, CastingRepertoire);

        internal string FormatChoiceTitle(RulesetCharacter caster)
        {
            if (!IsFree)
            {
                return CastingRepertoire == Repertoire
                    ? Gui.ToRoman(SlotLevel)
                    : $"{SourceTitle} {Gui.ToRoman(SlotLevel)}";
            }

            GetUses(caster, out var remaining, out var maximum);
            return maximum < 0 ? $"{SourceTitle}  ∞" : $"{SourceTitle}  {remaining}/{maximum}";
        }

        internal string FormatDescription(RulesetCharacter caster)
        {
            GetUses(caster, out var remaining, out var maximum);
            if (IsFree)
            {
                return maximum < 0
                    ? Gui.Format("Reaction/&SpellResourceFreeUnlimitedFormat", SourceTitle, SlotLevel.ToString())
                    : Gui.Format("Reaction/&SpellResourceFreeUsesFormat", SourceTitle, SlotLevel.ToString(),
                        remaining.ToString(), maximum.ToString());
            }

            var usesPactSlot = Repertoire.WouldSpendPactSlot(caster, SlotLevel);
            if (caster.IsSpellPointsEnabled() && Repertoire.UsesSharedSpellSlots() && !usesPactSlot)
            {
                return Gui.Format("Reaction/&SpellResourcePointsFormat", SourceTitle, SlotLevel.ToString(),
                    SpellPointsContext.SpellCostByLevel[SlotLevel].ToString());
            }

            if (SharedSpellsContext.IsMulticaster(caster))
            {
                Repertoire.GetSharedAndPactSlotNumbers(caster, SlotLevel, out var sharedRemaining, out var sharedMax,
                    out var pactRemaining, out var pactMax);
                remaining = usesPactSlot ? pactRemaining : sharedRemaining;
                maximum = usesPactSlot ? pactMax : sharedMax;
            }

            return Gui.Format("Reaction/&SpellResourceSlotFormat", SourceTitle, SlotLevel.ToString(),
                remaining.ToString(), maximum.ToString());
        }

        internal void GetUses(RulesetCharacter caster, out int remaining, out int maximum)
        {
            if (Repertoire == null)
            {
                remaining = maximum = 0;
                return;
            }

            switch (Kind)
            {
                case ResourceKind.SpellMastery:
                    remaining = maximum = -1;
                    return;
                case ResourceKind.SignatureSpell:
                    maximum = 1;
                    remaining = Level20Context.WizardSignatureSpells.HasFreeCast(
                        caster, Repertoire, Spell, SlotLevel) ? 1 : 0;
                    return;
                default:
                    Repertoire.GetDisplaySlotNumbers(caster, SlotLevel, out remaining, out maximum);
                    return;
            }
        }

        internal bool IsAvailable(RulesetCharacter caster)
        {
            if (caster == null || !caster.SpellRepertoires.Contains(Repertoire) ||
                !caster.SpellRepertoires.Contains(CastingRepertoire) ||
                !CanUseRepertoire(caster, CastingRepertoire, Spell) ||
                !IsSupportedLevel(caster, Repertoire, Spell, SlotLevel) ||
                Kind == ResourceKind.SpellSlot && SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(Repertoire) ||
                CastingRepertoire != Repertoire &&
                (Kind != ResourceKind.SpellSlot || !Repertoire.UsesSharedSpellSlots() ||
                 !IsSlotCastableFeatSpell(caster, CastingRepertoire, Spell)))
            {
                return false;
            }

            switch (Kind)
            {
                case ResourceKind.SpellMastery:
                    return Level20Context.WizardSpellMastery.HasFreeCast(Repertoire, Spell, SlotLevel);
                case ResourceKind.SignatureSpell:
                    return Level20Context.WizardSignatureSpells.HasFreeCast(caster, Repertoire, Spell, SlotLevel);
                default:
                    return Repertoire.TryGetAvailableSlotLevel(caster, SlotLevel, null, out var available) &&
                           (Repertoire.UsesSharedSpellSlots() && caster.IsSpellPointsEnabled()
                               ? SpellPointsContext.CanCastSpellOfLevel(caster, Repertoire, SlotLevel)
                               : available) &&
                           // Pass no spell here: a paid choice cannot borrow a Wizard free-use exemption.
                           SpellSlotCastingLimit2024Context.CanUseSpellSlotLevel(caster, Repertoire, null, SlotLevel);
            }
        }
    }

    private sealed class RequestResources(List<ResourceOption> options)
    {
        internal List<ResourceOption> Options { get; } = options;
        internal int Selected { get; set; } = -1;
    }

    private sealed class SelectionScope : IDisposable
    {
        private readonly ResourceOption _previous = _currentSelection;
        private bool _disposed;

        internal SelectionScope(ResourceOption option)
        {
            _currentSelection = option;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _currentSelection = _previous;
        }
    }

    internal static IDisposable BeginSelection(ResourceOption option)
    {
        return new SelectionScope(option);
    }

    internal static bool IsSameSpell(SpellDefinition first, SpellDefinition second)
    {
        if (first == null || second == null)
        {
            return false;
        }

        var firstRoot = SpellsContext.SpellsChildMaster.TryGetValue(first, out var root) ? root : first;
        var secondRoot = SpellsContext.SpellsChildMaster.TryGetValue(second, out root) ? root : second;
        return firstRoot == secondRoot;
    }

    internal static bool SupportsSelection(RulesetEffectSpell effect)
    {
        return effect != null && effect is not RulesetEffectSpellWithOrigin && effect.OriginItem == null &&
               effect.SlotLevel >= 0 && effect.SpellDefinition.SpellLevel > 0 &&
               effect.RulesetInvocation?.InvocationDefinition is not { ConsumesSpellSlot: false };
    }

    internal static void BindEffectSelection(RulesetEffectSpell effect, ResourceOption option)
    {
        if (!SupportsSelection(effect) || option == null || !IsSameSpell(option.Spell, effect.SpellDefinition))
        {
            return;
        }

        effect.spellRepertoire = option.CastingRepertoire;
        effect.SlotLevel = option.SlotLevel;
        SpellCastingValidation.BindEffectRepertoire(effect, option.CastingRepertoire);
        Selections.Remove(effect);
        // Use the actual child spell so validation and consumption see the same definition.
        Selections.Add(effect, new ResourceOption(option.Repertoire, effect.SpellDefinition, option.SlotLevel,
            option.Kind, option.CastingRepertoire));
    }

    internal static void ApplySelection(CharacterActionParams parameters, ResourceOption option)
    {
        if (parameters == null || option == null)
        {
            return;
        }

        parameters.SpellRepertoire = option.CastingRepertoire;
        parameters.IntParameter = option.SlotLevel;
        var resourceIndex = -1;
        if (option.Repertoire != option.CastingRepertoire)
        {
            var repertoires = GetOrderedRepertoires(parameters.ActingCharacter?.RulesetCharacter);
            resourceIndex = Array.IndexOf(repertoires, option.Repertoire);
            if (resourceIndex < 0)
            {
                resourceIndex = repertoires.Length;
            }
        }

        parameters.IntParameter2 = SelectionMarker - (int)option.Kind - ResourceKindCount * (resourceIndex + 1);
        BindEffectSelection(parameters.RulesetEffect as RulesetEffectSpell, option);
    }

    private static RulesetSpellRepertoire[] GetOrderedRepertoires(RulesetCharacter caster)
    {
        return caster?.SpellRepertoires
            .OrderBy(repertoire => repertoire.SpellCastingFeature.Name, StringComparer.Ordinal)
            .ThenBy(repertoire => repertoire.SpellCastingClass?.Name, StringComparer.Ordinal)
            .ThenBy(repertoire => repertoire.SpellCastingSubclass?.Name, StringComparer.Ordinal)
            .ThenBy(repertoire => repertoire.SpellCastingRace?.Name, StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    internal static void ClearSelectionMarker(CharacterActionParams parameters)
    {
        if (parameters.IntParameter2 <= SelectionMarker)
        {
            parameters.IntParameter2 = 0;
        }
    }

    internal static void RestoreSelection(CharacterActionParams parameters)
    {
        if (parameters?.RulesetEffect is not RulesetEffectSpell effect || !SupportsSelection(effect))
        {
            return;
        }

        if (parameters.IntParameter2 > SelectionMarker)
        {
            BindImplicitFeatSelection(effect);
            if (Selections.TryGetValue(effect, out var implicitSelection))
            {
                ApplySelection(parameters, implicitSelection);
            }

            return;
        }

        var selection = (long)SelectionMarker - parameters.IntParameter2;
        var kind = (ResourceKind)(selection % ResourceKindCount);
        var resourceIndex = selection / ResourceKindCount - 1;
        var castingRepertoire = parameters.SpellRepertoire ?? effect.SpellRepertoire;
        var repertoires = GetOrderedRepertoires(parameters.ActingCharacter?.RulesetCharacter);
        var resourceRepertoire = resourceIndex < 0
            ? castingRepertoire
            : resourceIndex < repertoires.Length ? repertoires[(int)resourceIndex] : null;
        if (castingRepertoire != null)
        {
            // An invalid synchronized owner must fail validation, never become a free feat use.
            BindEffectSelection(effect, new ResourceOption(resourceRepertoire, effect.SpellDefinition,
                effect.SlotLevel, kind, castingRepertoire));
        }
    }

    internal static RulesetSpellRepertoire GetResourceRepertoire(RulesetEffectSpell effect)
    {
        return effect != null && Selections.TryGetValue(effect, out var option)
            ? option.Repertoire
            : effect?.SpellRepertoire;
    }

    internal static RulesetSpellRepertoire ResolveCastingRepertoire(
        RulesetSpellRepertoire repertoire, SpellDefinition spell, RulesetCharacter caster = null)
    {
        if (SpellSelectionContext.TryGetOption(repertoire, out var view))
        {
            return view.CastingRepertoire;
        }

        var selected = CurrentSelection;
        return selected != null && IsSameSpell(selected.Spell, spell) &&
               (selected.Repertoire == repertoire || selected.CastingRepertoire == repertoire)
            ? selected.CastingRepertoire
            : ResolveSlotCastingRepertoire(repertoire, spell, caster);
    }

    internal static ResourceOption GetSlotSelection(
        RulesetSpellRepertoire repertoire, SpellDefinition spell, int slotLevel, RulesetCharacter caster = null)
    {
        return new ResourceOption(repertoire, spell, slotLevel, ResourceKind.SpellSlot,
            ResolveSlotCastingRepertoire(repertoire, spell, caster));
    }

    private static RulesetSpellRepertoire ResolveSlotCastingRepertoire(
        RulesetSpellRepertoire repertoire, SpellDefinition spell, RulesetCharacter caster)
    {
        if (spell == null || !repertoire.UsesSharedSpellSlots())
        {
            return repertoire;
        }

        caster ??= repertoire.GetCaster();
        if (caster == null)
        {
            return repertoire;
        }

        var rootSpell = SpellsContext.SpellsChildMaster.TryGetValue(spell, out var master) ? master : spell;
        var grants = LevelUpHelper.EnumerateSlotCastableFeatSpells(caster)
            .Where(entry => IsSameSpell(entry.Spell, rootSpell)).ToArray();
        if (grants.Length == 0 || HasIndependentClassSpell(caster, repertoire, rootSpell,
                grants.Select(entry => entry.DisplayTag)))
        {
            return repertoire;
        }

        return SelectFeatCastingRepertoire(caster, grants.Select(entry => entry.Repertoire));
    }

    internal static RulesetSpellRepertoire SelectFeatCastingRepertoire(
        RulesetCharacter caster, IEnumerable<RulesetSpellRepertoire> repertoires)
    {
        // Several feats can grant the same spell. Keep the effective ability first, then
        // preserve useful repertoire bonuses and use definition names for a stable tie.
        return repertoires.Distinct()
            .OrderByDescending(source => AttributeDefinitions.ComputeAbilityScoreModifier(
                caster.TryGetAttributeValue(Tabletop2024Context.TryGetTabletop2024FeatSpellcastingAbility(
                    source, out var ability, caster) ? ability : source.SpellCastingAbility)))
            .ThenByDescending(source => source.SaveDC)
            .ThenByDescending(source => source.SpellAttackBonus)
            .ThenBy(source => source.SpellCastingFeature.Name, StringComparer.Ordinal)
            .First();
    }

    private static bool HasIndependentClassSpell(
        RulesetCharacter caster, RulesetSpellRepertoire repertoire, SpellDefinition spell,
        IEnumerable<string> featTags)
    {
        var feature = repertoire.SpellCastingFeature;
        var prepared = feature.SpellReadyness == SpellReadyness.Prepared;
        if (prepared ? repertoire.PreparedSpells.Contains(spell) :
            repertoire.KnownSpells.Contains(spell) ||
            feature.SpellKnowledge is SpellKnowledge.FixedList or SpellKnowledge.WholeList &&
            feature.SpellListDefinition?.SpellsByLevel.Any(level => level.Spells.Contains(spell)) == true)
        {
            return true;
        }

        // AutoPreparedSpells also contains projected feat grants. Check their actual feature
        // owners instead; HasKnowledgeOfSpell would recurse through the same projection.
        if (caster.FeaturesByType<FeatureDefinitionAutoPreparedSpells>()
                .Any(source => SpellPreparationContext.EnumerateFeatureSpells(caster, repertoire, source)
                    .Contains(spell)) ||
            caster.GetSubFeaturesByType<IModifyAutoPreparedSpells>()
                .Any(source => source.SourceFeature != null &&
                               source.GetAutoPreparedSpells(caster, repertoire).Contains(spell)))
        {
            return true;
        }

        var projectedTags = new HashSet<string>(featTags, StringComparer.Ordinal);
        return repertoire.ExtraSpellsByTag.Any(entry => !projectedTags.Contains(entry.Key) &&
                                                        entry.Value.Contains(spell));
    }

    private static bool IsSlotCastableFeatSpell(
        RulesetCharacter caster, RulesetSpellRepertoire repertoire, SpellDefinition spell)
    {
        return repertoire != null && LevelUpHelper.EnumerateSlotCastableFeatSpells(caster)
            .Any(entry => entry.Repertoire == repertoire && IsSameSpell(entry.Spell, spell));
    }

    internal static bool IsManaged(ReactionRequest request)
    {
        return request != null && Requests.TryGetValue(request, out _);
    }

    internal static bool TryGetOption(ReactionRequest request, int optionKey, out ResourceOption option)
    {
        option = null;
        if (request == null || !Requests.TryGetValue(request, out var resources) ||
            optionKey < 0 || optionKey >= resources.Options.Count)
        {
            return false;
        }

        option = resources.Options[optionKey];
        return true;
    }

    internal static bool TryGetSelectedOption(ReactionRequest request, out ResourceOption option)
    {
        return TryGetOption(request, GetSelectedOption(request), out option);
    }

    internal static int GetSelectedOption(ReactionRequest request)
    {
        return request != null && Requests.TryGetValue(request, out var resources) ? resources.Selected : -1;
    }

    internal static bool BuildOptions(ReactionRequest request)
    {
        var parameters = request?.ReactionParams;
        if (parameters?.ActionDefinition?.Id != ActionDefinitions.Id.CastReaction ||
            parameters.RulesetEffect is not RulesetEffectSpell effect || !SupportsSelection(effect))
        {
            return false;
        }

        var caster = parameters.ActingCharacter?.RulesetCharacter;
        if (caster == null)
        {
            return false;
        }

        var options = EnumerateResources(caster, effect.SpellDefinition,
            parameters.SpellRepertoire ?? effect.SpellRepertoire);
        Requests.Remove(request);
        Requests.Add(request, new RequestResources(options));
        Selections.Remove(effect);
        request.SubOptionsAvailability.Clear();

        for (var index = 0; index < options.Count; index++)
        {
            request.SubOptionsAvailability.Add(index, options[index].IsAvailable(caster));
        }

        var selected = options.FindIndex(option => option.IsAvailable(caster));
        if (selected >= 0)
        {
            SelectOption(request, selected);
        }
        else if (options.Count > 0)
        {
            // Keep a depleted request invalid even if an automatic reaction handler accepts it.
            // There is still no selected/available UI row and no fallback to an implicit free use.
            ApplySelection(parameters, options[0]);
        }

        return true;
    }

    internal static bool SelectOption(ReactionRequest request, int index)
    {
        if (request == null || !Requests.TryGetValue(request, out var resources))
        {
            return false;
        }

        if (!TryGetOption(request, index, out var option) ||
            request.ReactionParams.RulesetEffect is not RulesetEffectSpell)
        {
            return true;
        }

        var parameters = request.ReactionParams;
        resources.Selected = index;
        ApplySelection(parameters, option);
        return true;
    }

    private static void BindImplicitFeatSelection(RulesetEffectSpell effect)
    {
        if (!SupportsSelection(effect) || Selections.TryGetValue(effect, out _) ||
            !IsSlotCastableFeatSpell(effect.Caster, effect.SpellRepertoire, effect.SpellDefinition))
        {
            return;
        }

        // Native automatic casting finds a spell origin and level before it creates the action.
        // Freeze its payment choice too, rather than treating an exhausted feat counter as a slot.
        var options = EnumerateResources(effect.Caster, effect.SpellDefinition, effect.SpellRepertoire)
            .Where(option => option.SlotLevel == effect.SlotLevel).ToArray();
        var originOptions = options.Where(option => option.CastingRepertoire == effect.SpellRepertoire).ToArray();
        // An automatic cast can fall back to another learned origin after its free use is
        // exhausted. Explicit selections returned above must retain their chosen payment.
        var option = originOptions.FirstOrDefault(candidate => candidate.IsAvailable(effect.Caster)) ??
                     options.FirstOrDefault(candidate => candidate.IsAvailable(effect.Caster)) ??
                     originOptions.FirstOrDefault() ?? new ResourceOption(effect.SpellRepertoire,
                         effect.SpellDefinition, effect.SlotLevel, ResourceKind.FreeRepertoire);
        BindEffectSelection(effect, option);
    }

    internal static bool HasExplicitSelection(RulesetEffectSpell effect)
    {
        BindImplicitFeatSelection(effect);
        return effect != null && Selections.TryGetValue(effect, out _);
    }

    internal static bool TryGetSelectionKind(RulesetEffectSpell effect, out ResourceKind kind)
    {
        BindImplicitFeatSelection(effect);
        if (effect != null && Selections.TryGetValue(effect, out var option))
        {
            kind = option.Kind;
            return true;
        }

        kind = default;
        return false;
    }

    internal static bool IsExplicitSlotSelection(RulesetEffectSpell effect)
    {
        BindImplicitFeatSelection(effect);
        return effect != null && Selections.TryGetValue(effect, out var option) && !option.IsFree;
    }

    internal static bool IsSelectionAvailable(RulesetCharacter caster, RulesetEffectSpell effect)
    {
        BindImplicitFeatSelection(effect);
        return effect == null || !Selections.TryGetValue(effect, out var option) ||
               option.CastingRepertoire == effect.SpellRepertoire && option.SlotLevel == effect.SlotLevel &&
               option.IsAvailable(caster);
    }

    internal static bool TryGetPreferredResource(
        RulesetCharacter caster,
        SpellDefinition spell,
        out RulesetSpellRepertoire repertoire,
        out int slotLevel,
        RulesetSpellRepertoire preferredRepertoire = null)
    {
        var selected = EnumerateResources(caster, spell, preferredRepertoire)
            .FirstOrDefault(option => option.IsAvailable(caster));
        repertoire = selected?.CastingRepertoire;
        slotLevel = selected?.SlotLevel ?? 0;
        return selected != null;
    }

    internal static List<ResourceOption> EnumerateResources(
        RulesetCharacter caster,
        SpellDefinition spell,
        RulesetSpellRepertoire preferredRepertoire = null)
    {
        List<ResourceOption> options = [];
        if (caster == null || spell == null || spell.SpellLevel <= 0)
        {
            return options;
        }

        var repertoires = caster.SpellRepertoires
            .Where(repertoire => CanUseRepertoire(caster, repertoire, spell))
            .OrderBy(repertoire => repertoire.SpellCastingFeature.Name, StringComparer.Ordinal)
            .ToArray();
        var paid = new Dictionary<(string Pool, int Level), ResourceOption>();

        foreach (var repertoire in repertoires)
        {
            var freeRepertoire = SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire);
            var baseSelection = freeRepertoire
                ? new ResourceOption(repertoire, spell, spell.SpellLevel, ResourceKind.FreeRepertoire)
                : GetSlotSelection(repertoire, spell, spell.SpellLevel, caster);
            var maximumLevel = 9;
            for (var level = spell.SpellLevel; level <= maximumLevel; level++)
            {
                // Do not include Wizard's automatic free cast when measuring paid capacity.
                if (!repertoire.TryGetAvailableSlotLevel(caster, level, null, out _))
                {
                    continue;
                }

                if (repertoire.SpellCastingFeature.CannotUpcast)
                {
                    // Fixed-level innate casting can start above the spell's base level. Never
                    // move it to another level because its own daily use was already consumed.
                    maximumLevel = level;
                }

                var option = baseSelection.AtLevel(level);
                if (freeRepertoire)
                {
                    options.Add(option);
                    continue;
                }

                // Class repertoires share a pool. Preserve the native casting source when it is eligible.
                var pool = repertoire.UsesSharedSpellSlots() ? "Shared" : repertoire.SpellCastingFeature.Name;
                var key = (pool, level);
                if (!paid.TryGetValue(key, out var previous) ||
                    repertoire == preferredRepertoire ||
                    !previous.IsAvailable(caster) && option.IsAvailable(caster))
                {
                    paid[key] = option;
                }
            }

            if (freeRepertoire)
            {
                continue;
            }

            var rootSpell = SpellsContext.SpellsChildMaster.TryGetValue(spell, out var master) ? master : spell;
            foreach (var (tag, kind) in new[]
                     {
                         ("SpellMastery", ResourceKind.SpellMastery),
                         ("SignatureSpells", ResourceKind.SignatureSpell)
                     })
            {
                if (repertoire.ExtraSpellsByTag.TryGetValue(tag, out var spells) && spells.Contains(rootSpell))
                {
                    options.Add(new ResourceOption(repertoire, spell, spell.SpellLevel, kind));
                }
            }
        }

        options.AddRange(paid.Values);
        return options.OrderByDescending(option => option.IsFree)
            .ThenBy(option => option.SlotLevel)
            .ThenBy(option => option.Repertoire.SpellCastingFeature.Name, StringComparer.Ordinal)
            .ThenBy(option => option.CastingRepertoire.SpellCastingFeature.Name, StringComparer.Ordinal)
            .ThenBy(option => option.Kind)
            .ToList();
    }

    internal static List<ResourceOption> EnumerateUpcastSlots(
        RulesetCharacter caster, RulesetSpellRepertoire repertoire, SpellDefinition spell)
    {
        List<ResourceOption> options = [];
        if (caster == null || repertoire?.SpellCastingFeature is not { CannotUpcast: false } ||
            spell is not { SpellLevel: > 0 } ||
            SpellSlotCastingLimit2024Context.IsFreeUseRepertoire(repertoire))
        {
            return options;
        }

        // A class column chooses its own payment pool. Scaling changes the effect,
        // not whether that pool may pay a higher-level slot for the spell.
        for (var level = spell.SpellLevel + 1; level <= 9; level++)
        {
            var option = GetSlotSelection(repertoire, spell, level, caster);
            if (option.IsAvailable(caster))
            {
                options.Add(option);
            }
        }

        return options;
    }

    private static bool IsSupportedLevel(RulesetCharacter caster, RulesetSpellRepertoire repertoire,
        SpellDefinition spell, int slotLevel)
    {
        if (slotLevel < spell.SpellLevel || slotLevel > 9)
        {
            return false;
        }

        if (!repertoire.SpellCastingFeature.CannotUpcast)
        {
            return true;
        }

        for (var level = spell.SpellLevel; level < slotLevel; level++)
        {
            if (repertoire.TryGetAvailableSlotLevel(caster, level, null, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanUseRepertoire(
        RulesetCharacter caster, RulesetSpellRepertoire repertoire, SpellDefinition spell)
    {
        if (repertoire?.SpellCastingFeature == null)
        {
            return false;
        }

        var rootSpell = SpellsContext.SpellsChildMaster.TryGetValue(spell, out var master) ? master : spell;
        var prepared = repertoire.SpellCastingFeature.SpellReadyness == SpellReadyness.Prepared;
        return IsSlotCastableFeatSpell(caster, repertoire, rootSpell) || (prepared
                   ? repertoire.PreparedSpells.Contains(rootSpell) || repertoire.AutoPreparedSpells.Contains(rootSpell) ||
                     repertoire.ExtraSpellsByTag.Values.Any(spells => spells.Contains(rootSpell))
                   : SpellCastingValidation.KnowsSpell(repertoire, rootSpell)) ||
               repertoire.UsesSharedSpellSlots() &&
               LevelUpHelper.IsSlotCastableExtraSpellForRepertoire(caster, repertoire, rootSpell);
    }

    internal static bool CanCastFeatSpellOfActionType(
        RulesetCharacter caster, ActionDefinitions.ActionType actionType, bool canOnlyUseCantrips)
    {
        if (canOnlyUseCantrips)
        {
            return false;
        }

        foreach (var (repertoire, spell, _) in LevelUpHelper.EnumerateSlotCastableFeatSpells(caster))
        {
            if (!SpellActionTypeContext.MatchesCastingActionType(spell, actionType) &&
                !caster.GetSubFeaturesByType<IAllowSpellActionType>()
                    .Any(provider => provider.IsAllowed(caster, repertoire, spell, actionType)))
            {
                continue;
            }

            using var scope = SpellCastingValidation.EnterSelectedRepertoire(repertoire);
            if (caster.AreSpellComponentsValid(spell) &&
                SpellCastingValidation.IsValid(caster, repertoire, spell, null, out _) &&
                EnumerateResources(caster, spell).Any(option => option.CastingRepertoire == repertoire &&
                                                              option.IsAvailable(caster)))
            {
                return true;
            }
        }

        return false;
    }

    internal static void AddAdditionalUsableSpells(RulesetCharacter caster)
    {
        foreach (var spell in LevelUpHelper.EnumerateSlotCastableFeatSpells(caster)
                     .Select(entry => entry.Spell).Distinct())
        {
            if (!caster.UsableSpells.Contains(spell) &&
                EnumerateResources(caster, spell).Any(option => option.IsAvailable(caster)))
            {
                caster.UsableSpells.Add(spell);
            }
        }

        // Native enumeration only counts remaining slots. Wizard free uses have their own availability.
        foreach (var repertoire in caster.SpellRepertoires)
        {
            foreach (var entry in repertoire.ExtraSpellsByTag)
            {
                if (entry.Key is not ("SpellMastery" or "SignatureSpells"))
                {
                    continue;
                }

                foreach (var spell in entry.Value)
                {
                    if (!caster.UsableSpells.Contains(spell) && CanUseRepertoire(caster, repertoire, spell) &&
                        Level20Context.HasFreeWizardCast(caster, repertoire, spell, spell.SpellLevel))
                    {
                        caster.UsableSpells.Add(spell);
                    }
                }
            }
        }
    }
}
