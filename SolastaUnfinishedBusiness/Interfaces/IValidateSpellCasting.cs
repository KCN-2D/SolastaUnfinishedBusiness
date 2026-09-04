using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IValidateSpellCasting
{
    bool CanCastSpell(
        SpellCastingValidationContext context,
        out string failure);
}

internal readonly struct SpellCastingValidationContext(
    RulesetCharacter caster,
    RulesetSpellRepertoire repertoire,
    SpellDefinition spellDefinition,
    RulesetEffectSpell activeSpell,
    bool bypassComponentsAndCastingTime,
    bool bypassMaterialComponent,
    bool bypassSpellSlotLimit)
{
    internal RulesetCharacter Caster { get; } = caster;
    internal RulesetSpellRepertoire Repertoire { get; } = repertoire;
    internal SpellDefinition SpellDefinition { get; } = spellDefinition;
    internal RulesetEffectSpell ActiveSpell { get; } = activeSpell;
    internal bool BypassComponentsAndCastingTime { get; } = bypassComponentsAndCastingTime;
    internal bool BypassMaterialComponent { get; } = bypassMaterialComponent;
    internal bool BypassSpellSlotLimit { get; } = bypassSpellSlotLimit;
}

internal static class SpellCastingValidation
{
    [ThreadStatic]
    private static RulesetSpellRepertoire _selectedRepertoire;

    private static readonly ConditionalWeakTable<object, RepertoireBinding>
        TooltipRepertoires = new();
    private static readonly ConditionalWeakTable<RulesetEffectSpell, RepertoireBinding>
        EffectRepertoires = new();

    internal static IDisposable EnterSelectedRepertoire(RulesetSpellRepertoire repertoire)
    {
        return new RepertoireScope(repertoire);
    }

    internal static IDisposable EnterTooltipRepertoire(ITooltip tooltip)
    {
        return tooltip != null && TooltipRepertoires.TryGetValue(tooltip, out var binding)
            ? EnterSelectedRepertoire(binding.Repertoire)
            : null;
    }

    internal static void BindTooltipRepertoire(
        object tooltip,
        RulesetSpellRepertoire repertoire,
        bool bypassComponentsAndCastingTime = false,
        bool bypassMaterialComponent = false)
    {
        if (tooltip == null)
        {
            return;
        }

        TooltipRepertoires.Remove(tooltip);

        if (repertoire != null || bypassComponentsAndCastingTime || bypassMaterialComponent)
        {
            TooltipRepertoires.Add(
                tooltip,
                new RepertoireBinding(
                    repertoire,
                    bypassComponentsAndCastingTime,
                    bypassMaterialComponent));
        }
    }

    internal static bool TryGetTooltipRepertoire(
        object tooltip,
        out RulesetSpellRepertoire repertoire)
    {
        repertoire = null;

        if (tooltip == null || !TooltipRepertoires.TryGetValue(tooltip, out var binding))
        {
            return false;
        }

        repertoire = binding.Repertoire;

        return repertoire != null;
    }

    internal static bool TooltipBypassesComponentsAndCastingTime(object tooltip)
    {
        return tooltip != null &&
               TooltipRepertoires.TryGetValue(tooltip, out var binding) &&
               binding.BypassComponentsAndCastingTime;
    }

    internal static bool TooltipBypassesMaterialComponent(object tooltip)
    {
        return tooltip != null &&
               TooltipRepertoires.TryGetValue(tooltip, out var binding) &&
               binding.BypassMaterialComponent;
    }

    internal static bool TooltipBypassesMissingComponents(ITooltip tooltip)
    {
        if (TooltipBypassesComponentsAndCastingTime(tooltip))
        {
            return true;
        }

        if (!TooltipBypassesMaterialComponent(tooltip) ||
            tooltip?.DataProvider is not ISpellParametersProvider provider ||
            provider.SpellDefinition == null)
        {
            return false;
        }

        var caster = tooltip.Context switch
        {
            RulesetCharacter rulesetCharacter => rulesetCharacter,
            GameLocationCharacter locationCharacter => locationCharacter.RulesetCharacter,
            GuiCharacter guiCharacter => guiCharacter.RulesetCharacter,
            _ => null
        };

        return caster != null &&
               caster.IsComponentVerbalValid(provider.SpellDefinition, out _) &&
               caster.IsComponentSomaticValid(provider.SpellDefinition, out _);
    }

    internal static void BindEffectRepertoire(
        RulesetEffectSpell effect,
        RulesetSpellRepertoire repertoire)
    {
        if (effect == null)
        {
            return;
        }

        EffectRepertoires.Remove(effect);

        if (repertoire != null)
        {
            EffectRepertoires.Add(effect, new RepertoireBinding(repertoire));
        }
    }

    internal static bool TryGetSelectedRepertoire(
        RulesetCharacter caster,
        out RulesetSpellRepertoire repertoire)
    {
        repertoire = _selectedRepertoire;

        return caster != null &&
               repertoire != null &&
               caster.SpellRepertoires.Contains(repertoire);
    }

    internal static RulesetSpellRepertoire ResolveRepertoire(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        RulesetEffectSpell activeSpell = null)
    {
        if (caster == null)
        {
            return null;
        }

        if (repertoire == null &&
            activeSpell != null &&
            EffectRepertoires.TryGetValue(activeSpell, out var effectBinding))
        {
            repertoire = effectBinding.Repertoire;
        }

        if (repertoire != null && caster.SpellRepertoires.Contains(repertoire))
        {
            return repertoire;
        }

        if (TryGetSelectedRepertoire(caster, out var selectedRepertoire) &&
            (spellDefinition == null || KnowsSpell(selectedRepertoire, spellDefinition)))
        {
            return selectedRepertoire;
        }

        if (spellDefinition == null)
        {
            return null;
        }

        return caster.SpellRepertoires
            .Where(candidate => candidate?.SpellCastingFeature != null &&
                                KnowsSpell(candidate, spellDefinition))
            .OrderBy(GetRepertoirePriority)
            .ThenBy(candidate => candidate.SpellCastingFeature.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static bool IsValid(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        RulesetEffectSpell activeSpell,
        out string failure,
        bool bypassComponentsAndCastingTime = false,
        bool bypassMaterialComponent = false,
        bool bypassSpellSlotLimit = false)
    {
        failure = string.Empty;

        if (caster == null || spellDefinition == null)
        {
            return false;
        }

        bypassMaterialComponent |=
            activeSpell?.RulesetInvocation?.InvocationDefinition is
            {
                OverrideMaterialComponent: true
            };
        bypassComponentsAndCastingTime |=
            activeSpell is RulesetEffectSpellWithOrigin
            {
                BypassComponentsAndCastingTime: true
            } ||
            RulesetEffectSpellWithOrigin.IsPendingOrigin(caster, spellDefinition);
        repertoire = ResolveRepertoire(
            caster,
            repertoire ?? activeSpell?.SpellRepertoire,
            spellDefinition,
            activeSpell);
        var context = new SpellCastingValidationContext(
            caster,
            repertoire,
            spellDefinition,
            activeSpell,
            bypassComponentsAndCastingTime,
            bypassMaterialComponent,
            bypassSpellSlotLimit);

        if (!MetamagicContext.CanCastSpellWithQuickenedSpell2024Rules(context, out failure) ||
            !SpellSlotCastingLimit2024Context.CanCastSpell(context, out failure))
        {
            return false;
        }

        var validators = new List<IValidateSpellCasting>();

        if (caster.GetFeatureOwnerOrSelf() is RulesetCharacterSimulacrum &&
            SimulacrumBehavior.RuntimeRestrictionsMarker is IValidateSpellCasting runtimeRestrictions)
        {
            validators.Add(runtimeRestrictions);
        }

        validators.AddRange(caster.GetSubFeaturesByType<IValidateSpellCasting>());
        validators.AddRange(spellDefinition.GetAllSubFeaturesOfType<IValidateSpellCasting>());

        foreach (var validator in validators.Distinct())
        {
            if (validator.CanCastSpell(context, out failure))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    internal static bool KnowsSpell(
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition)
    {
        if (repertoire == null || spellDefinition == null)
        {
            return false;
        }

        var rootSpell = SpellsContext.SpellsChildMaster.TryGetValue(
            spellDefinition,
            out var master)
            ? master
            : spellDefinition;

        return repertoire.HasKnowledgeOfSpell(rootSpell) ||
               repertoire.KnownSpells.Contains(rootSpell) ||
               repertoire.PreparedSpells.Contains(rootSpell) ||
               repertoire.AutoPreparedSpells.Contains(rootSpell) ||
               repertoire.ExtraSpellsByTag.Values.Any(spells => spells.Contains(rootSpell));
    }

    private static int GetRepertoirePriority(RulesetSpellRepertoire repertoire)
    {
        return repertoire.SpellCastingFeature.SpellCastingOrigin switch
        {
            FeatureDefinitionCastSpell.CastingOrigin.Class => 0,
            FeatureDefinitionCastSpell.CastingOrigin.Subclass => 1,
            FeatureDefinitionCastSpell.CastingOrigin.Race => 2,
            FeatureDefinitionCastSpell.CastingOrigin.Monster => 3,
            _ => 4
        };
    }

    private sealed class RepertoireBinding(
        RulesetSpellRepertoire repertoire,
        bool bypassComponentsAndCastingTime = false,
        bool bypassMaterialComponent = false)
    {
        internal RulesetSpellRepertoire Repertoire { get; } = repertoire;
        internal bool BypassComponentsAndCastingTime { get; } =
            bypassComponentsAndCastingTime;
        internal bool BypassMaterialComponent { get; } = bypassMaterialComponent;
    }

    private sealed class RepertoireScope : IDisposable
    {
        private readonly RulesetSpellRepertoire _previous = _selectedRepertoire;
        private bool _disposed;

        internal RepertoireScope(RulesetSpellRepertoire repertoire)
        {
            _selectedRepertoire = repertoire;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _selectedRepertoire = _previous;
        }
    }
}
