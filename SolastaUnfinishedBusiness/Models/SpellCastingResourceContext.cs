using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// A casting choice identifies both its casting level and its resource owner.
// Native reactions synchronize the ordinal choice, so order by definitions, never localized text.
internal static class SpellCastingResourceContext
{
    private static readonly ConditionalWeakTable<ReactionRequest, RequestResources> Requests = new();
    private static readonly ConditionalWeakTable<RulesetEffectSpell, ResourceOption> Selections = new();

    // CastSpell does not use IntParameter2. Store the kind in this serialized action field;
    // the repertoire and actual level already have native serialization and clone support.
    private const int SelectionMarker = -202400;
    [ThreadStatic] private static ResourceOption _currentSelection;

    internal static ResourceOption CurrentSelection => _currentSelection;

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
        ResourceKind kind)
    {
        internal RulesetSpellRepertoire Repertoire { get; } = repertoire;
        internal SpellDefinition Spell { get; } = spell;
        internal int SlotLevel { get; } = slotLevel;
        internal ResourceKind Kind { get; } = kind;
        internal bool IsFree => Kind != ResourceKind.SpellSlot;
        internal string SourceTitle => Kind switch
        {
            ResourceKind.SpellMastery => Level20Context.WizardSpellMastery.FeatureSpellMastery.FormatTitle(),
            ResourceKind.SignatureSpell => Level20Context.WizardSignatureSpells.PowerSignatureSpells.FormatTitle(),
            _ => Repertoire.FormatHeader()
        };

        internal string FormatChoiceTitle(RulesetCharacter caster)
        {
            if (!IsFree)
            {
                return Gui.ToRoman(SlotLevel);
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

            return caster.IsSpellPointsEnabled() && Repertoire.UsesSharedSpellSlots()
                ? Gui.Format("Reaction/&SpellResourcePointsFormat", SourceTitle, SlotLevel.ToString(),
                    SpellPointsContext.SpellCostByLevel[SlotLevel].ToString())
                : Gui.Format("Reaction/&SpellResourceSlotFormat", SourceTitle, SlotLevel.ToString(),
                    remaining.ToString(), maximum.ToString());
        }

        internal void GetUses(RulesetCharacter caster, out int remaining, out int maximum)
        {
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
                !CanUseRepertoire(caster, Repertoire, Spell) || !IsSupportedLevel(caster, Repertoire, Spell, SlotLevel))
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

        effect.spellRepertoire = option.Repertoire;
        effect.SlotLevel = option.SlotLevel;
        SpellCastingValidation.BindEffectRepertoire(effect, option.Repertoire);
        Selections.Remove(effect);
        // Use the actual child spell so validation and consumption see the same definition.
        Selections.Add(effect, new ResourceOption(option.Repertoire, effect.SpellDefinition, option.SlotLevel, option.Kind));
    }

    internal static void ApplySelection(CharacterActionParams parameters, ResourceOption option)
    {
        if (parameters == null || option == null)
        {
            return;
        }

        parameters.SpellRepertoire = option.Repertoire;
        parameters.IntParameter = option.SlotLevel;
        parameters.IntParameter2 = SelectionMarker - (int)option.Kind;
        BindEffectSelection(parameters.RulesetEffect as RulesetEffectSpell, option);
    }

    internal static void ClearSelectionMarker(CharacterActionParams parameters)
    {
        if (parameters.IntParameter2 <= SelectionMarker &&
            parameters.IntParameter2 >= SelectionMarker - (int)ResourceKind.SignatureSpell)
        {
            parameters.IntParameter2 = 0;
        }
    }

    internal static void RestoreSelection(CharacterActionParams parameters)
    {
        if (parameters?.RulesetEffect is not RulesetEffectSpell effect || !SupportsSelection(effect) ||
            parameters.IntParameter2 > SelectionMarker ||
            parameters.IntParameter2 < SelectionMarker - (int)ResourceKind.SignatureSpell)
        {
            return;
        }

        var kind = (ResourceKind)(SelectionMarker - parameters.IntParameter2);
        var repertoire = parameters.SpellRepertoire ?? effect.SpellRepertoire;
        if (repertoire != null && !Selections.TryGetValue(effect, out _))
        {
            BindEffectSelection(effect, new ResourceOption(repertoire, effect.SpellDefinition, effect.SlotLevel, kind));
        }
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

    internal static bool HasExplicitSelection(RulesetEffectSpell effect)
    {
        return effect != null && Selections.TryGetValue(effect, out _);
    }

    internal static bool TryGetSelectionKind(RulesetEffectSpell effect, out ResourceKind kind)
    {
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
        return effect != null && Selections.TryGetValue(effect, out var option) && !option.IsFree;
    }

    internal static bool IsSelectionAvailable(RulesetCharacter caster, RulesetEffectSpell effect)
    {
        return effect == null || !Selections.TryGetValue(effect, out var option) ||
               option.Repertoire == effect.SpellRepertoire && option.SlotLevel == effect.SlotLevel &&
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
        repertoire = selected?.Repertoire;
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

                var option = new ResourceOption(repertoire, spell, level,
                    freeRepertoire ? ResourceKind.FreeRepertoire : ResourceKind.SpellSlot);
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
            .ThenBy(option => option.Kind)
            .ToList();
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
        return (prepared
                   ? repertoire.PreparedSpells.Contains(rootSpell) || repertoire.AutoPreparedSpells.Contains(rootSpell) ||
                     repertoire.ExtraSpellsByTag.Values.Any(spells => spells.Contains(rootSpell))
                   : SpellCastingValidation.KnowsSpell(repertoire, rootSpell)) ||
               repertoire.UsesSharedSpellSlots() &&
               LevelUpHelper.IsSlotCastableExtraSpellForRepertoire(caster, repertoire, rootSpell);
    }

    internal static void AddFreeWizardUsableSpells(RulesetCharacter caster)
    {
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
