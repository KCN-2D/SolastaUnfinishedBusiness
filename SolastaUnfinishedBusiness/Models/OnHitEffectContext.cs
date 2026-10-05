using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal enum AdditionalEffectTrigger
{
    Hit,
    Damage
}

// Limited hit effects choose from resolved targets. Damage-triggered effects
// additionally require actual received damage; their uses are paid after choice.
internal static class OnHitEffectContext
{
    private static readonly ConditionalWeakTable<RulesetEffect, Scope> Scopes = new();
    private static readonly ConditionalWeakTable<DamageForm, DamageObserver> DamageObservers = new();
    private static readonly ConditionalWeakTable<GameLocationCharacter, Scope> ActionScopes = new();

    [ThreadStatic]
    private static DamageFormScope _damageFormScope;

    internal static void ObserveReceivedDamage(DamageForm form, Action<int> damageReceived)
    {
        DamageObservers.Remove(form);
        DamageObservers.Add(form, new DamageObserver(damageReceived));
    }

    internal static IDisposable BeginDamageForm(
        DamageForm form, RulesetImplementationDefinitions.ApplyFormsParams formsParams)
    {
        return new DamageFormScope(form, formsParams);
    }

    internal static void NotifyDamageReceived(
        RulesetCharacter target, int damage, string damageType, ulong sourceGuid)
    {
        _damageFormScope?.Notify(target, damage, damageType, sourceGuid);
    }

    internal static Scope TrackAttack(CharacterActionAttack action)
    {
        return new Scope(action);
    }

    internal static bool NeedsReactionConfirmation(CharacterAction action)
    {
        return action.ActionType == ActionDefinitions.ActionType.Reaction ||
               action.ActionId is ActionDefinitions.Id.AttackReadied or ActionDefinitions.Id.CastReadied;
    }

    internal static Scope Track(CharacterActionMagicEffect action)
    {
        return new Scope(action);
    }

    internal static bool CanSelectTarget(
        RulesetEffect effect, GameLocationCharacter attacker)
    {
        return TryGetScope(effect, attacker, out var scope) && !scope.IsResolving && scope.HasMultipleTargets;
    }

    internal static bool CanSelectTarget(
        RulesetEffect effect, GameLocationCharacter attacker, IAdditionalDamageProvider provider)
    {
        return TryGetScope(effect, attacker, out var scope) &&
               !scope.IsResolving &&
               (scope.HasMultipleTargets || NeedsReactionConfirmation(scope.Action)) && IsSelectableProvider(provider);
    }

    internal static List<EffectForm> GetApplyingForms(
        RulesetEffect effect, GameLocationCharacter attacker, GameLocationCharacter defender)
    {
        return TryGetScope(effect, attacker, out var scope) && scope.ApplyingTarget == defender
            ? scope.ApplyingForms
            : null;
    }

    internal static bool TryPrepareDamageReduction(
        RulesetEffect effect, GameLocationCharacter attacker, GameLocationCharacter defender,
        FeatureDefinitionReduceDamage feature)
    {
        return !TryGetScope(effect, attacker, out var scope) || scope.TryPrepareDamageReduction(defender, feature);
    }

    private static bool IsSelectableProvider(IAdditionalDamageProvider provider)
    {
        return provider != null && (provider.LimitedUsage is
            FeatureLimitedUsage.OncePerTurn or FeatureLimitedUsage.OnceInMyTurn ||
            provider.FirstTargetOnly && UsesFirstTargetRestriction(provider));
    }

    private static bool UsesFirstTargetRestriction(IAdditionalDamageProvider provider)
    {
        // Native definitions default FirstTargetOnly to true, but the native
        // rule applies it only to these spell damage triggers.
        return provider.TriggerCondition is AdditionalDamageTriggerCondition.SpellDamagesTarget or
            AdditionalDamageTriggerCondition.SpellDamageMatchesSourceAncestry or
            AdditionalDamageTriggerCondition.EvocationSpellDamage;
    }

    internal sealed class FollowUpEffect(BaseDefinition parentFeature)
    {
        internal BaseDefinition ParentFeature { get; } = parentFeature;
    }

    internal static bool TryDeferProvider(
        GameLocationBattleManager battleManager,
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        IAdditionalDamageProvider provider,
        List<EffectForm> actualEffectForms,
        CharacterActionParams reactionParams,
        RulesetAttackMode attackMode,
        RulesetEffect effect,
        bool criticalHit)
    {
        if (!TryGetScope(effect, attacker, out var scope) || !IsSelectableProvider(provider) ||
            (!scope.HasMultipleTargets && !NeedsReactionConfirmation(scope.Action)))
        {
            return false;
        }

        // The native provider's trigger and restrictions were already validated
        // at HitConfirmed. Its payload does not add a received-damage requirement;
        // providers can grant conditions or damage even when base damage is zero.
        // Features that require actual damage queue that trigger explicitly.
        return TryDeferEffect(
            effect, attacker, defender, (BaseDefinition)provider, actualEffectForms,
            () => IsProviderAvailable(battleManager, attacker, provider),
            forms => GLBM.ComputeAndNotifyAdditionalDamage(
                battleManager, attacker, defender, provider, forms, reactionParams, attackMode, criticalHit),
            AdditionalEffectTrigger.Hit, confirmReaction: true);
    }

    internal static bool TryDeferEffect(
        RulesetEffect effect,
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        BaseDefinition feature,
        List<EffectForm> actualEffectForms,
        Func<bool> isAvailable,
        Action<List<EffectForm>> addEffects,
        AdditionalEffectTrigger trigger,
        bool negatedBySpellSave = false,
        bool confirmReaction = false)
    {
        if (!TryGetScope(effect, attacker, out var scope) ||
            scope.IsResolving || attacker != scope.Action.ActingCharacter.GetEffectControllerOrSelf())
        {
            return false;
        }

        scope.Add(attacker, defender, feature, actualEffectForms, isAvailable, addEffects, trigger,
            negatedBySpellSave, confirmReaction);
        return true;
    }

    internal static bool TryQueueEffect(
        RulesetEffect effect,
        GameLocationCharacter attacker,
        GameLocationCharacter target,
        BaseDefinition feature,
        Func<bool> isAvailable,
        Func<RulesetImplementationDefinitions.ApplyFormsParams, List<EffectForm>> createEffects,
        AdditionalEffectTrigger trigger,
        Action effectApplied = null,
        Func<RulesetImplementationDefinitions.ApplyFormsParams, bool> isEligible = null,
        bool confirmReaction = false)
    {
        if (!TryGetScope(effect, attacker, out var scope) || scope.IsResolving ||
            attacker != scope.Action.ActingCharacter.GetEffectControllerOrSelf())
        {
            return false;
        }

        scope.Add(attacker, target, feature, isAvailable, createEffects, trigger, effectApplied, isEligible,
            confirmReaction);
        return true;
    }

    private static bool TryGetScope(RulesetEffect effect, GameLocationCharacter attacker, out Scope scope)
    {
        if (effect != null)
        {
            return Scopes.TryGetValue(effect, out scope);
        }

        return ActionScopes.TryGetValue(attacker, out scope) && scope.Action is CharacterActionAttack;
    }

    internal static void RecordApplication(
        CharacterActionMagicEffect action,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        int damageReceived,
        bool damageAbsorbedByTemporaryHitPoints)
    {
        if (formsParams.activeEffect == null ||
            !Scopes.TryGetValue(formsParams.activeEffect, out var scope) ||
            scope.Action != action || scope.IsResolving)
        {
            return;
        }

        scope.RecordApplication(formsParams, damageReceived, damageAbsorbedByTemporaryHitPoints);
    }

    internal static void RecordAttackApplication(
        CharacterActionAttack action,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        int damageReceived,
        bool damageAbsorbedByTemporaryHitPoints)
    {
        var attacker = action.ActingCharacter.GetEffectControllerOrSelf();

        if (ActionScopes.TryGetValue(attacker, out var scope) && scope.Action == action && !scope.IsResolving)
        {
            scope.RecordApplication(formsParams, damageReceived, damageAbsorbedByTemporaryHitPoints);
        }
    }

    private static bool IsProviderAvailable(
        GameLocationBattleManager battleManager,
        GameLocationCharacter attacker,
        IAdditionalDamageProvider provider)
    {
        if (attacker.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false })
        {
            return false;
        }

        var used = attacker.UsedSpecialFeatures;

        if (provider.LimitedUsage is FeatureLimitedUsage.OncePerTurn or FeatureLimitedUsage.OnceInMyTurn &&
            used.ContainsKey(provider.Name))
        {
            return false;
        }

        if (provider.LimitedUsage == FeatureLimitedUsage.OnceInMyTurn &&
            battleManager.Battle != null && battleManager.Battle.ActiveContender != attacker)
        {
            return false;
        }

        if (provider is not FeatureDefinitionAdditionalDamage { OtherSimilarAdditionalDamages.Count: > 0 } damage)
        {
            return true;
        }

        var database = DatabaseRepository.GetDatabase<FeatureDefinitionAdditionalDamage>();

        return !used.Keys.Any(name => database.TryGetElement(name, out var previous) &&
                                     damage.OtherSimilarAdditionalDamages.Contains(previous));
    }

    private static RulesetImplementationDefinitions.ApplyFormsParams CopyApplication(
        RulesetImplementationDefinitions.ApplyFormsParams original)
    {
        // Native ApplyFormsParams is a value type. Keep its final save, hit,
        // critical, origin and level state without recomputing them after a prompt.
        var copy = original;

        copy.addDice = 0;
        copy.addHP = 0;
        copy.addTempHP = 0;
        copy.actionModifier = original.actionModifier?.Clone();
        // Native damage reduction consumes and replenishes this mutable list.
        // Keep it shared so a later packet sees the remaining reduction rather
        // than restoring an old allowance or losing a newly paid reaction.

        return copy;
    }

    private static List<EffectForm> BuildAdditionalForms(Candidate candidate)
    {
        var forms = candidate.EffectForms.Select(EffectForm.GetCopy).ToList();
        var originalBonuses = forms.Select(form => form.DamageForm?.BonusDamage ?? 0).ToArray();
        var originalForms = forms.ToArray();

        candidate.AddEffects(forms);

        var addedForms = forms.Where(form => !originalForms.Any(original => ReferenceEquals(original, form))).ToList();

        // An ancestry bonus is merged into an existing damage form. Apply only
        // that increment, with the original form's saving throw affinity.
        for (var index = 0; index < originalForms.Length; index++)
        {
            var form = originalForms[index];

            if (form.FormType != EffectForm.EffectFormType.Damage ||
                form.DamageForm.BonusDamage <= originalBonuses[index])
            {
                continue;
            }

            var increment = EffectForm.GetCopy(form);

            increment.DamageForm.BonusDamage -= originalBonuses[index];
            increment.DamageForm.DiceNumber = 0;
            increment.DamageForm.DieType = DieType.D1;
            increment.DamageForm.IgnoreSpellAdvancementDamageDice = true;
            addedForms.Insert(0, increment);
        }

        return addedForms;
    }

    private static string FormatFeatureTitle(BaseDefinition feature)
    {
        var title = feature.FormatTitle();

        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        if (feature is IAdditionalDamageProvider provider)
        {
            var key = $"Feedback/&AdditionalDamage{provider.NotificationTag}Format";
            var feedbackTitle = Gui.Localize(key);

            if (!string.IsNullOrWhiteSpace(feedbackTitle) && feedbackTitle != key)
            {
                return feedbackTitle;
            }
        }

        return Gui.Localize("Reaction/&CustomReactionAdditionalEffectTargetTitle");
    }

    internal sealed class Scope : IDisposable
    {
        private readonly RulesetEffect _effect;
        private readonly Scope _previous;
        private readonly GameLocationCharacter _attacker;
        private readonly Scope _previousAction;
        private readonly Dictionary<BaseDefinition, List<Candidate>> _candidates = [];
        private readonly Dictionary<BaseDefinition, GameLocationCharacter> _selectedTargets = [];
        private readonly Dictionary<GameLocationCharacter, HashSet<FeatureDefinitionReduceDamage>> _damageReductions = [];

        internal Scope(CharacterAction action)
        {
            Action = action;
            _attacker = action.ActingCharacter.GetEffectControllerOrSelf();
            ActionScopes.TryGetValue(_attacker, out _previousAction);
            ActionScopes.Remove(_attacker);
            ActionScopes.Add(_attacker, this);
            _effect = action is CharacterActionMagicEffect ? action.ActionParams.RulesetEffect : null;

            if (_effect != null)
            {
                Scopes.TryGetValue(_effect, out _previous);
                Scopes.Remove(_effect);
                Scopes.Add(_effect, this);
            }
        }

        internal CharacterAction Action { get; }

        internal bool HasMultipleTargets => Action.ActionParams.TargetCharacters
            .Where(target => target?.RulesetCharacter != null)
            .Select(target => target.Guid).Distinct().Skip(1).Any();

        internal bool IsResolving { get; private set; }

        internal GameLocationCharacter ApplyingTarget { get; private set; }

        internal List<EffectForm> ApplyingForms { get; private set; }

        internal bool TryPrepareDamageReduction(
            GameLocationCharacter defender, FeatureDefinitionReduceDamage feature)
        {
            if (!_damageReductions.TryGetValue(defender, out var handled))
            {
                handled = [];
                _damageReductions.Add(defender, handled);
            }

            // Ordinary hits keep their native allowance. The selected additional
            // forms are part of the same hit and must not offer or grant it again.
            var first = handled.Add(feature);
            return ApplyingTarget != defender || first;
        }

        internal void Add(
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            BaseDefinition feature,
            List<EffectForm> forms,
            Func<bool> isAvailable,
            Action<List<EffectForm>> addEffects,
            AdditionalEffectTrigger trigger,
            bool negatedBySpellSave,
            bool confirmReaction)
        {
            if (!_candidates.TryGetValue(feature, out var candidates))
            {
                candidates = [];
                _candidates.Add(feature, candidates);
            }

            candidates.Add(new Candidate(
                attacker, defender, forms, isAvailable, addEffects, trigger, negatedBySpellSave, confirmReaction));
        }

        internal void Add(
            GameLocationCharacter attacker,
            GameLocationCharacter target,
            BaseDefinition feature,
            Func<bool> isAvailable,
            Func<RulesetImplementationDefinitions.ApplyFormsParams, List<EffectForm>> createEffects,
            AdditionalEffectTrigger trigger,
            Action effectApplied,
            Func<RulesetImplementationDefinitions.ApplyFormsParams, bool> isEligible,
            bool confirmReaction)
        {
            if (!_candidates.TryGetValue(feature, out var candidates))
            {
                candidates = [];
                _candidates.Add(feature, candidates);
            }

            candidates.Add(new Candidate(attacker, target, isAvailable, createEffects, trigger, effectApplied, isEligible,
                confirmReaction));
        }

        internal void RecordApplication(
            RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            int damageReceived,
            bool damageAbsorbedByTemporaryHitPoints)
        {
            foreach (var candidates in _candidates.Values)
            {
                var candidate = candidates.LastOrDefault(item => item.Application == null &&
                    item.Target.RulesetActor == formsParams.targetCharacter);

                if (candidate == null)
                {
                    continue;
                }

                candidate.Application = CopyApplication(formsParams);
                candidate.DealtDamage = damageReceived > 0 || damageAbsorbedByTemporaryHitPoints;
            }
        }

        internal IEnumerator Resolve(GameLocationBattleManager battleManager, Action<int> additionalDamageReceived = null)
        {
            if (Action.Countered || Action is CharacterActionMagicEffect { ExecutionFailed: true })
            {
                yield break;
            }

            IsResolving = true;

            var resolved = new HashSet<BaseDefinition>();

            foreach (var entry in _candidates)
            {
                yield return ResolveFeature(entry.Key, battleManager, additionalDamageReceived, resolved);
            }
        }

        private IEnumerator ResolveFeature(
            BaseDefinition feature, GameLocationBattleManager battleManager, Action<int> additionalDamageReceived,
            HashSet<BaseDefinition> resolved)
        {
            // Mark before following the parent so erroneous cycles cannot recurse.
            if (!resolved.Add(feature) || !_candidates.TryGetValue(feature, out var entries))
            {
                yield break;
            }

            var followUp = feature.GetFirstSubFeatureOfType<FollowUpEffect>();
            GameLocationCharacter parentTarget = null;

            if (followUp != null)
            {
                yield return ResolveFeature(followUp.ParentFeature, battleManager, additionalDamageReceived, resolved);

                if (!_selectedTargets.TryGetValue(followUp.ParentFeature, out parentTarget))
                {
                    yield break;
                }
            }

            var candidates = entries.Where(candidate => candidate.IsValid &&
                    (parentTarget == null || candidate.Target == parentTarget))
                .GroupBy(candidate => candidate.Target.Guid)
                .Select(group => group.First()).ToArray();

            if (candidates.Length == 0)
            {
                yield break;
            }

            var attacker = candidates[0].Attacker;
            Candidate selected = candidates[0];

            // Remote players still own their reaction request. AI controlled
            // casters retain the first eligible target without a player prompt.
            if (followUp == null && (candidates.Length > 1 ||
                                    selected.ConfirmReaction && NeedsReactionConfirmation(Action)) &&
                attacker.ControllerId != PlayerControllerManager.DmControllerId)
            {
                selected = null;
                yield return attacker.MyReactToSelectTarget(
                    candidates.Select(candidate => candidate.Target), Action.ActingCharacter,
                    "AdditionalEffectTarget",
                    Gui.Format("Reaction/&CustomReactionAdditionalEffectTargetDescription",
                        FormatFeatureTitle(feature)),
                    target => selected = candidates.FirstOrDefault(candidate => candidate.Target == target),
                    battleManager: battleManager,
                    effectDefinition: feature);
            }

            // Availability is rechecked after the suspended request. Cancelling,
            // or losing the selected creature, does not spend the feature.
            if (selected == null || !selected.IsValid)
            {
                yield break;
            }

            yield return Apply(battleManager, selected, additionalDamageReceived,
                () => _selectedTargets[feature] = selected.Target);

            if (_selectedTargets.ContainsKey(feature))
            {
                yield return GLBM.QueueLinkedAutoPowersAfterProvider(
                    battleManager, Action, selected.Attacker, selected.Target, feature as IAdditionalDamageProvider);
            }
        }

        private IEnumerator Apply(
            GameLocationBattleManager battleManager, Candidate candidate, Action<int> additionalDamageReceived,
            Action applied)
        {
            var target = candidate.Target;
            var formsParams = candidate.Application.Value;
            var originalTargetIndex = formsParams.targetIndex;
            // This choice applies to one creature. A zone's damage-roll cache is
            // indexed by form position and belongs to the original zone forms;
            // the selected feature must roll its own forms independently.
            formsParams.targetType = TargetType.Individuals;
            formsParams.targetIndex = 0;
            formsParams.totalTargetsNumber = 1;
            var service = ServiceRepository.GetService<IRulesetImplementationService>();

            if (service == null)
            {
                yield break;
            }

            if (!candidate.IsValid)
            {
                yield break;
            }

            // Native provider computation includes payment, condition operations,
            // light and notifications. Run it once, only after the choice is final.
            var forms = candidate.CreateEffects != null
                ? candidate.CreateEffects(formsParams)
                : BuildAdditionalForms(candidate);

            if (forms == null || forms.Count == 0)
            {
                // Native providers can apply light and other direct side effects
                // without adding a form. Their chosen target still resolves.
                if (candidate.CreateEffects == null)
                {
                    applied();
                }

                yield break;
            }

            yield return GLBM.RollAdditionalEffectSavingThrow(
                battleManager, Action, candidate.Attacker, target, forms, formsParams,
                updated => formsParams = updated);

            if (!candidate.CanApply)
            {
                yield break;
            }

            if (forms.Any(form => form.FormType == EffectForm.EffectFormType.Damage))
            {
                ApplyingTarget = target;
                ApplyingForms = forms;

                try
                {
                    yield return battleManager.HandleDefenderBeforeDamageReceived(
                        Action.ActingCharacter, target, formsParams.attackMode, _effect, formsParams.actionModifier,
                        formsParams.rolledSaveThrow,
                        formsParams.saveOutcome is RollOutcome.Success or RollOutcome.CriticalSuccess);
                }
                finally
                {
                    ApplyingTarget = null;
                    ApplyingForms = null;
                }
            }

            // Native provider computation can already have paid the use. Pure
            // queued effects remain unpaid through suspended damage reactions.
            if (!candidate.CanApply || candidate.CreateEffects != null && !candidate.IsValid)
            {
                yield break;
            }

            candidate.EffectApplied?.Invoke();
            applied();

            var wasDeadOrDying = target.RulesetActor.IsDeadOrDyingOrUnconscious;
            var damageTypes = new List<string>();
            var damageReceived = service.ApplyEffectForms(
                forms, formsParams, damageTypes, out var absorbedByTemporaryHitPoints, out _);

            if (damageReceived > 0)
            {
                if (Action is CharacterActionMagicEffect magicAction)
                {
                    magicAction.damagePerTargetIndexCache.TryGetValue(originalTargetIndex, out var previousDamage);
                    magicAction.damagePerTargetIndexCache[originalTargetIndex] = previousDamage + damageReceived;
                    magicAction.hitTargets.Add(target);
                }

                additionalDamageReceived?.Invoke(damageReceived);
            }

            // The original spell already ran its own notifications. Only the
            // additional damage is reported here; attack-finished uses the sum.
            if (damageReceived > 0 || absorbedByTemporaryHitPoints)
            {
                yield return battleManager.HandleDefenderOnDamageReceived(
                    Action.ActingCharacter, target, damageReceived, _effect, damageTypes);
                yield return battleManager.HandleAttackerOnDefenderDamageReceived(
                    Action.ActingCharacter, target, damageReceived, _effect, damageTypes);

                if (!absorbedByTemporaryHitPoints)
                {
                    yield return battleManager.HandleReactionToDamageShare(target, damageReceived);
                }
            }

            if ((damageReceived > 0 || absorbedByTemporaryHitPoints) && !wasDeadOrDying &&
                target.RulesetActor is { IsDeadOrDyingOrUnconscious: true })
            {
                yield return battleManager.HandleTargetReducedToZeroHP(
                    Action.ActingCharacter, target, formsParams.attackMode, _effect);
            }
        }

        public void Dispose()
        {
            if (ActionScopes.TryGetValue(_attacker, out var currentAction) && currentAction == this)
            {
                ActionScopes.Remove(_attacker);

                if (_previousAction != null)
                {
                    ActionScopes.Add(_attacker, _previousAction);
                }
            }

            if (_effect != null && Scopes.TryGetValue(_effect, out var current) && current == this)
            {
                Scopes.Remove(_effect);

                if (_previous != null)
                {
                    Scopes.Add(_effect, _previous);
                }
            }

            _candidates.Clear();
            _selectedTargets.Clear();
            _damageReductions.Clear();
        }
    }

    private sealed class Candidate
    {
        private readonly Func<bool> _isAvailable;
        private readonly AdditionalEffectTrigger _trigger;
        private readonly bool _negatedBySpellSave;
        private readonly Func<RulesetImplementationDefinitions.ApplyFormsParams, bool> _isEligible;

        internal Candidate(
            GameLocationCharacter attacker,
            GameLocationCharacter target,
            List<EffectForm> forms,
            Func<bool> isAvailable,
            Action<List<EffectForm>> addEffects,
            AdditionalEffectTrigger trigger,
            bool negatedBySpellSave,
            bool confirmReaction)
            : this(attacker, target, isAvailable, null, trigger, null, null, confirmReaction)
        {
            EffectForms = forms.Select(EffectForm.GetCopy).ToList();
            AddEffects = addEffects;
            _negatedBySpellSave = negatedBySpellSave;
        }

        internal Candidate(
            GameLocationCharacter attacker,
            GameLocationCharacter target,
            Func<bool> isAvailable,
            Func<RulesetImplementationDefinitions.ApplyFormsParams, List<EffectForm>> createEffects,
            AdditionalEffectTrigger trigger,
            Action effectApplied,
            Func<RulesetImplementationDefinitions.ApplyFormsParams, bool> isEligible,
            bool confirmReaction)
        {
            Attacker = attacker;
            Target = target;
            _isAvailable = isAvailable;
            _trigger = trigger;
            _isEligible = isEligible;
            CreateEffects = createEffects;
            EffectApplied = effectApplied;
            ConfirmReaction = confirmReaction;
        }

        internal GameLocationCharacter Attacker { get; }

        internal GameLocationCharacter Target { get; }

        internal List<EffectForm> EffectForms { get; }

        internal Action<List<EffectForm>> AddEffects { get; }

        internal Func<RulesetImplementationDefinitions.ApplyFormsParams, List<EffectForm>> CreateEffects { get; }

        internal Action EffectApplied { get; }

        internal bool ConfirmReaction { get; }

        internal RulesetImplementationDefinitions.ApplyFormsParams? Application { get; set; }

        internal bool DealtDamage { get; set; }

        internal bool CanApply => Application is { } application &&
                                  application.activeEffect is not { Terminated: true } &&
                                  ReactionRequestSelectTarget.IsCandidateValid(Target);

        internal bool IsValid => CanApply &&
                                 (_trigger == AdditionalEffectTrigger.Damage
                                     ? DealtDamage
                                     : Application.Value.attackOutcome is not
                                         (RollOutcome.Failure or RollOutcome.CriticalFailure)) &&
                                 _isAvailable() && (_isEligible?.Invoke(Application.Value) ?? true) &&
                                 (!_negatedBySpellSave || !Application.Value.rolledSaveThrow ||
                                  Application.Value.saveOutcome is RollOutcome.Failure or RollOutcome.CriticalFailure);
    }

    private sealed class DamageObserver(Action<int> damageReceived)
    {
        internal Action<int> DamageReceived { get; } = damageReceived;
    }

    private sealed class DamageFormScope : IDisposable
    {
        private readonly DamageFormScope _previous;
        private readonly DamageObserver _observer;
        private readonly RulesetActor _target;
        private readonly ulong _sourceGuid;
        private readonly string _damageType;
        private int _damageReceived;
        private bool _disposed;

        internal DamageFormScope(DamageForm form, RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            _previous = _damageFormScope;
            _damageFormScope = this;
            _target = formsParams.targetCharacter;
            _sourceGuid = formsParams.sourceCharacter?.Guid ?? 0;
            _damageType = form.DamageType;
            DamageObservers.TryGetValue(form, out _observer);
            DamageObservers.Remove(form);
        }

        internal void Notify(RulesetCharacter target, int damage, string damageType, ulong sourceGuid)
        {
            if (_observer != null && target == _target && sourceGuid == _sourceGuid && damageType == _damageType)
            {
                _damageReceived += Math.Max(0, damage);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _damageFormScope = _previous;
            _observer?.DamageReceived(_damageReceived);
        }
    }
}
