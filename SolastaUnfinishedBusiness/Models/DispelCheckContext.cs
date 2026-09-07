using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// One check per ongoing spell, shared by all of that spell's conditions and lights.
// The scope belongs to ApplyCounterForm, including nested applications and failed casts.
internal sealed class DispelCheckContext : IDisposable
{
    // Most spells use TagEffect. Custom conditions can opt in from another category,
    // while ownership-only conditions can opt out without changing saved condition data.
    internal sealed class ConditionPolicy(bool canDispel)
    {
        internal static readonly ConditionPolicy Include = new(true);
        internal static readonly ConditionPolicy Ignore = new(false);
        internal bool CanDispel { get; } = canDispel;
    }

    [ThreadStatic] private static DispelCheckContext _current;
    private readonly DispelCheckContext _previous = _current;
    private readonly RulesetImplementationDefinitions.ApplyFormsParams _forms;
    private readonly ulong _targetGuid;
    private readonly CounterForm _counter;
    private readonly IDisposable _observer;
    private readonly Dictionary<object, bool> _results = new();
    private readonly Dictionary<object, (SpellDefinition Spell, RulesetCharacter Target, bool? Success)> _notifications = new();
    private readonly List<(RulesetCondition Condition, ulong Guid, RulesetEffectSpell Effect)> _conditions = new();
    private bool CanHandle => _forms.sourceCharacter != null && _forms.targetCharacter is RulesetCharacter;

    internal DispelCheckContext(RulesetImplementationDefinitions.ApplyFormsParams forms, CounterForm counter)
    {
        _forms = forms;
        _targetGuid = forms.targetCharacter?.Guid ?? 0;
        _counter = counter;
        if (forms.targetCharacter is RulesetCharacter character)
        {
            foreach (var category in character.ConditionsByCategory)
            {
                foreach (var condition in category.Value)
                {
                    var policy = condition.ConditionDefinition.GetFirstSubFeatureOfType<ConditionPolicy>();
                    if (!(policy?.CanDispel ?? category.Key == AttributeDefinitions.TagEffect))
                    {
                        continue;
                    }

                    var effect = EffectHelpers.GetCharacterByGuid(condition.SourceGuid)?.SpellsCastByMe
                        .FirstOrDefault(candidate => candidate.TrackedConditionGuids.Contains(condition.Guid));
                    _conditions.Add((condition, condition.Guid, effect));
                }
            }
        }

        _observer = SpellInterruptionContext.ObserveDispel(forms);
        _current = this;
    }

    public void Dispose()
    {
        _current = _previous;
        _observer?.Dispose();
    }

    internal static int RollFallbackDie(
        DieType die, AdvantageType advantage, out int first, out int second, float random)
    {
        // Native rolls before knowing whether a check is needed. Defer it until an
        // effect above the automatic dispelling level is actually encountered.
        if (_current?.CanHandle == true)
        {
            first = second = 0;
            return 0;
        }

        return RollDie(die, advantage, out first, out second, random);
    }

    internal static void ApplyToCharacter(
        RulesetActor target, RulesetActor source, int automaticLevel, int baseDC, int die, int bonus)
    {
        if (_current?.CanHandle != true || target is not RulesetCharacter character)
        {
            target.DissipateSpells(source, automaticLevel, baseDC, die, bonus);
            return;
        }

        var scope = _current;
        var remove = new List<(RulesetCondition Condition, ulong Guid, object Key)>();

        foreach (var entry in scope._conditions)
        {
            var condition = entry.Condition;
            if (!IsActiveCondition(condition, entry.Guid) || string.IsNullOrEmpty(condition.EffectDefinitionName) ||
                !DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(
                    condition.EffectDefinitionName, out var spell, true))
            {
                continue;
            }

            var key = (object)entry.Effect ?? condition;
            if (scope.Check(key, spell, entry.Effect?.SlotLevel ?? condition.EffectLevel,
                    automaticLevel, baseDC, bonus, character))
            {
                remove.Add((condition, entry.Guid, key));
            }
        }

        // Conditions and light sources can coexist. Native returned after the condition
        // branch and silently skipped all lights in that case.
        var personalLight = character.PersonalLightSource;
        if (personalLight != null && CheckLight(personalLight, out var personalLightKey))
        {
            character.PersonalLightSource = null;
            scope.RecordSuccess(personalLightKey);
        }

        var items = new List<RulesetItem>();
        character.CharacterInventory?.EnumerateAllItems(items, false, false);
        foreach (var item in items)
        {
            var light = item.RulesetLightSource;
            if (light == null || !CheckLight(light, out var lightKey))
            {
                continue;
            }

            light.LightSourceExtinguished?.Invoke(light);
            item.RulesetLightSource = null;
            scope.RecordSuccess(lightKey);
        }

        foreach (var entry in remove)
        {
            // Ending a summoning spell can remove the target and its other conditions.
            // Recheck every entry so an unregistered or pooled condition is never removed twice.
            if (!IsActiveCondition(entry.Condition, entry.Guid))
            {
                continue;
            }

            character.RemoveCondition(entry.Condition, false, true);
            if (!IsActiveCondition(entry.Condition, entry.Guid))
            {
                scope.RecordSuccess(entry.Key);
            }
        }

        scope.NotifyResults();
        return;

        bool IsActiveCondition(RulesetCondition condition, ulong guid)
        {
            return condition.Guid == guid && character.ConditionsByCategory.Values
                .Any(list => list.Contains(condition));
        }

        bool CheckLight(RulesetLightSource light, out object key)
        {
            key = null;
            if (!DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(light.SourceName, out var spell, true))
            {
                return false;
            }

            var effect = EffectHelpers.GetEffectByGuid(light.EffectGuid) as RulesetEffectSpell;
            key = (object)effect ?? light;
            return scope.Check(key, spell, effect?.SlotLevel ?? spell.SpellLevel,
                automaticLevel, baseDC, bonus, character);
        }
    }

    internal static void RefreshAfterDispel(RulesetActor actor)
    {
        // ApplyCounterForm owns the final refresh. A dispelled summoning spell can
        // unregister its target, so retain that native boundary only for a live actor.
        var scope = _current;
        if (scope?.CanHandle == true && ReferenceEquals(scope._forms.targetCharacter, actor) &&
            EffectHelpers.GetCharacterByGuid(scope._targetGuid) != actor)
        {
            return;
        }

        actor.RefreshAll();
    }

    internal static void ApplyToProxy(
        RulesetCharacterEffectProxy target, RulesetActor source, int automaticLevel, int baseDC,
        int die, int bonus, RulesetEffectSpell effect)
    {
        if (_current?.CanHandle != true)
        {
            target.DissipateSpell(source, automaticLevel, baseDC, die, bonus, effect);
            return;
        }

        if (_current.Check(effect, effect.SpellDefinition, effect.SlotLevel, automaticLevel, baseDC, bonus, null))
        {
            effect.Terminate(true);
            if (effect.Terminated)
            {
                _current.RecordSuccess(effect);
            }
        }

        _current.NotifyResults();
    }

    private void NotifyResults()
    {
        var notifications = _notifications.Values.ToArray();
        _notifications.Clear();
        foreach (var result in notifications)
        {
            if (!result.Success.HasValue)
            {
                // Another removal can end the target or reuse a pooled condition before
                // this entry is applied. It is neither a dispelling success nor a failed check.
                continue;
            }

            _forms.sourceCharacter.SpellDissipated?.Invoke(
                _forms.sourceCharacter, result.Target, result.Spell, result.Success.Value);
        }
    }

    private void RecordSuccess(object key)
    {
        if (_notifications.TryGetValue(key, out var result))
        {
            _notifications[key] = (result.Spell, result.Target, true);
        }
    }

    private bool Check(object key, SpellDefinition spell, int level, int automaticLevel,
        int baseDC, int nativeBonus, RulesetCharacter target)
    {
        var source = _forms.sourceCharacter;
        if (!_results.TryGetValue(key, out var success))
        {
            success = level <= automaticLevel;
            if (!success)
            {
                var repertoire = (_forms.activeEffect as RulesetEffectSpell)?.SpellRepertoire;
                var ability = _counter.AddSpellCastingAbility && repertoire != null
                    ? repertoire.SpellCastingAbility
                    : _counter.AbilityToAdd;
                var proficiency = _counter.AddProficiencyBonus ? "ForcedProficiency" : string.Empty;
                var modifier = new ActionModifier();
                var baseBonus = nativeBonus;
                if (!string.IsNullOrEmpty(ability))
                {
                    baseBonus = source.ComputeBaseAbilityCheckBonus(
                        ability, modifier.AbilityCheckModifierTrends, proficiency);
                    // Retain additional CounterForm bonuses without counting the casting
                    // ability or the explicit proficiency twice. Other check features still apply.
                    var abilityBonus = AttributeDefinitions.ComputeAbilityScoreModifier(
                        source.GetAttribute(ability).CurrentValue);
                    var proficiencyBonus = _counter.AddProficiencyBonus
                        ? source.GetAttribute(AttributeDefinitions.ProficiencyBonus).CurrentValue
                        : 0;
                    modifier.AbilityCheckModifier = nativeBonus - abilityBonus - proficiencyBonus;
                    var location = GameLocationCharacter.GetFromActor(source);
                    var context = source.IsWearingHeavyArmor() ? AbilityCheckContext.None : AbilityCheckContext.NotWearingHeavyArmor;
                    location?.PrepareActionModifier(ability, proficiency, AdvantageType.None, modifier, (int)context);
                }

                var dc = baseDC + level;
                source.RollAbilityCheck(baseBonus, ability ?? string.Empty, proficiency,
                    modifier.AbilityCheckModifierTrends, modifier.AbilityCheckAdvantageTrends,
                    modifier.AbilityCheckModifier, dc, false, 0, out _, out _, out _,
                    out var outcome, out _, true, true, true);
                success = outcome is RollOutcome.Success or RollOutcome.CriticalSuccess;
            }

            _results.Add(key, success);
        }

        // A passed check only permits removal. Report success after this application has
        // actually removed a condition/light or terminated a proxy, for Spell Breaker too.
        if (!_notifications.ContainsKey(key))
        {
            _notifications[key] = (spell, target, success ? null : false);
        }
        return success;
    }
}
