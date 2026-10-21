using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Runtime targeting permissions belong to the acting character and effect instance.
// In particular, changing a shared definition would also change NPC planning.
internal static class TargetingContext
{
    internal sealed class HostileTargetsOnly
    {
        internal static readonly HostileTargetsOnly Marker = new();
        private HostileTargetsOnly() { }
    }

    internal sealed class SelectiveTargets
    {
        internal static readonly SelectiveTargets Marker = new(false);
        internal static readonly SelectiveTargets VisibleTargets = new(true);
        internal bool RequiresSight { get; }
        private SelectiveTargets(bool requiresSight) { RequiresSight = requiresSight; }
    }

    // Some native control conditions use Neutral for removal rules. Targeting
    // semantics must not change those rules merely to identify a harmful effect.
    internal sealed class HarmfulCondition
    {
        internal static readonly HarmfulCondition Marker = new();
        private HarmfulCondition() { }
    }

    // Some reactions alter a pending roll through a callback rather than effect
    // forms. Mark the harmful choice, not its mixed supportive/offensive bundle.
    internal sealed class HarmfulInterrupt
    {
        internal static readonly HarmfulInterrupt Marker = new();
        private HarmfulInterrupt() { }
    }

    // Roll modifiers and other callback-based support may have no effect forms.
    internal sealed class BeneficialEffect
    {
        internal static readonly BeneficialEffect Marker = new();
        private BeneficialEffect() { }
    }

    private const string SelectionAttributeName = "FriendlyFireTargetSelection";
    private const string SelectionPrepared = SelectionAttributeName + ":Prepared";
    private const string SelectionExcluded = SelectionAttributeName + ":Excluded:";
    private static SmartAttributeDefinition _selectionAttribute;

    internal static void LateLoad()
    {
        _selectionAttribute = SmartAttributeDefinitionBuilder.Create(SelectionAttributeName)
            .SetGuiPresentationNoContent(true).AddToDB();
    }

    private static BaseDefinition TargetDefinition(BaseDefinition definition) =>
        definition is SpellDefinition spell ? PowerBundle.GetPower(spell) ?? definition : definition;

    private sealed class ExpandedEffect(EffectDescription original)
    {
        internal EffectDescription Original { get; } = original;
        internal EffectDescription Description { get; } = Copy(original);
        internal BaseDefinition Definition { get; set; }
        internal RulesetEffect Effect { get; set; }

        private static EffectDescription Copy(EffectDescription original)
        {
            var description = new EffectDescription();
            description.Copy(original);
            description.TargetSide = Side.All;
            return description;
        }
    }

    private static readonly ConditionalWeakTable<RulesetEffect, ExpandedEffect> ExpandedEffects = new();
    private static readonly ConditionalWeakTable<EffectDescription, ExpandedEffect> ExpandedDescriptions = new();

    internal static bool IsPlayerPartyActor(GameLocationCharacter actor)
    {
        if (actor?.RulesetCharacter == null || actor.ControllerId == PlayerControllerManager.DmControllerId ||
            PlayerControllerContext.GetBattleSide(actor) != Side.Ally)
        {
            return false;
        }
        var owner = actor.GetEffectControllerOrSelf();
        return owner?.RulesetCharacter is not null and not RulesetCharacterEffectProxy &&
               owner.ControllerId != PlayerControllerManager.DmControllerId &&
               PlayerControllerContext.GetBattleSide(owner) == Side.Ally && IsPartyMember(owner);
    }

    internal static bool IsManualPartyActor(GameLocationCharacter actor) =>
        IsPlayerPartyActor(actor) &&
        actor.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } &&
        actor.GetEffectControllerOrSelf().RulesetCharacter is
            { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false };

    private static bool IsPartyMember(GameLocationCharacter character)
    {
        var service = ServiceRepository.GetService<IGameLocationCharacterService>();
        var visited = new HashSet<RulesetCharacter>();
        var current = character?.RulesetCharacter;

        while (current != null && visited.Add(current))
        {
            if (service?.PartyCharacters.Any(member => member.RulesetCharacter == current) == true ||
                service?.GuestCharacters.Any(member => member.RulesetCharacter == current) == true)
            {
                return true;
            }

            current = current.OriginalFormCharacter ?? EffectHelpers.GetSummoner(current);
        }

        return false;
    }

    internal static bool IsPartyAllyTarget(GameLocationCharacter actor, GameLocationCharacter target,
        bool includeSelf = false) =>
        actor != null && target != null && (includeSelf || actor != target) &&
        target.RulesetCharacter is not null and not RulesetCharacterEffectProxy &&
        actor.Side == target.Side && PlayerControllerContext.GetBattleSide(target) == Side.Ally &&
        IsPartyMember(target);

    internal static string FormatPartyTargetWarning(GameLocationCharacter actor,
        IEnumerable<GameLocationCharacter> targets, bool hostileAid = false)
    {
        var recipients = targets?.Where(target => target != null).Distinct().Take(2).ToArray() ?? [];
        var key = hostileAid ? "Caption/&HostileAidWarning" :
            recipients.Length > 1 ? "Caption/&PartyTargetsIncluded" :
            recipients.Length == 1 && recipients[0] == actor ? "Caption/&SelfTargetWarning" :
            "Caption/&FriendlyFireWarning";
        return Gui.Colorize(Gui.Localize(key), Gui.ColorAlert);
    }

    internal static bool IsHarmfulEffect(BaseDefinition definition, EffectDescription description) =>
        TargetDefinition(definition).HasSubFeatureOfType<AttackAfterMagicEffect>() || IsHarmful(description);

    internal static bool IsHarmful(EffectDescription description) => HasHarmfulForms(description, new HashSet<EffectDescription>());

    // A summoned proxy or a granted follow-up power harms its later victims,
    // rather than the creature anchoring the summoning/buff at cast time.
    internal static bool IsDirectlyHarmfulEffect(BaseDefinition definition, EffectDescription description) =>
        TargetDefinition(definition).HasSubFeatureOfType<AttackAfterMagicEffect>() ||
        HasHarmfulForms(description, new HashSet<EffectDescription>(), false);

    // Incidental protection or cleansing does not turn an offensive effect into
    // deliberate aid to an opponent. Assess harm to the recipient first.
    internal static bool IsBeneficialEffect(BaseDefinition definition, EffectDescription description)
    {
        if (IsHarmfulTargetEffect(definition, description)) return false;
        return TargetDefinition(definition).HasSubFeatureOfType<BeneficialEffect>() ||
               description?.EffectForms.Any(form => form.FormType switch
        {
            EffectForm.EffectFormType.Healing or EffectForm.EffectFormType.Revive => true,
            EffectForm.EffectFormType.TemporaryHitPoints => form.TemporaryHitPointsForm is { ApplyToSelf: false },
            EffectForm.EffectFormType.Condition => form.ConditionForm is { ApplyToSelf: false, ForceOnSelf: false } condition &&
                (condition.Operation is ConditionForm.ConditionOperation.RemoveDetrimentalAll or
                     ConditionForm.ConditionOperation.RemoveDetrimentalRandom ||
                 condition.Operation == ConditionForm.ConditionOperation.Remove && IsHarmfulCondition(condition.ConditionDefinition) ||
                 new[] { form }.GetAppliedConditionDefinitions().Any(IsBeneficialCondition)),
            EffectForm.EffectFormType.Counter => form.CounterForm?.Type == CounterForm.CounterType.DissipateOneDetrimentalSpell,
            _ => false
        }) == true;
    }

    private static bool HasHarmfulForms(EffectDescription description, HashSet<EffectDescription> visited,
        bool includeIndirect = true) =>
        description != null && visited.Add(description) && description.EffectForms.Any(form => form.FormType switch
        {
            EffectForm.EffectFormType.Damage or EffectForm.EffectFormType.Kill => true,
            EffectForm.EffectFormType.Condition => form.ConditionForm is { ApplyToSelf: false, ForceOnSelf: false } &&
                new[] { form }.GetAppliedConditionDefinitions().Any(IsHarmfulCondition),
            EffectForm.EffectFormType.Motion => form.MotionForm.Type is
                MotionForm.MotionType.PushFromOrigin or MotionForm.MotionType.DragToOrigin or
                MotionForm.MotionType.FallProne or MotionForm.MotionType.PushFromWall or
                MotionForm.MotionType.PushRandomDirection or MotionForm.MotionType.Telekinesis,
            EffectForm.EffectFormType.Summon => includeIndirect &&
                form.SummonForm.SummonType == SummonForm.Type.EffectProxy &&
                description.EffectForms.Any(effect => effect.FormType == EffectForm.EffectFormType.Alteration &&
                    effect.AlterationForm.AlterationType == AlterationForm.Type.TriggerProxyPower) &&
                DatabaseHelper.TryGetDefinition<EffectProxyDefinition>(form.SummonForm.EffectProxyDefinitionName,
                    out var proxy) && HasHarmfulForms(proxy.AttackPower?.EffectDescription, visited),
            _ => false
        });

    private static bool IsHarmfulCondition(ConditionDefinition condition)
    {
        var visited = new HashSet<ConditionDefinition>();
        for (var current = condition; current != null && visited.Add(current); current = current.parentCondition)
        {
            if (current.ConditionType == ConditionType.Detrimental || current.HasSubFeatureOfType<HarmfulCondition>())
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsBeneficialCondition(ConditionDefinition condition)
    {
        if (IsHarmfulCondition(condition)) return false;
        var visited = new HashSet<ConditionDefinition>();
        for (var current = condition; current != null && visited.Add(current); current = current.parentCondition)
        {
            if (current.ConditionType == ConditionType.Beneficial) return true;
        }
        return false;
    }

    internal static bool CanTargetAlly(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition, EffectDescription description) =>
        IsManualPartyActor(actor) && CanAffectAlly(actor, target, definition, description);

    // An already-created effect can outlast its conscious caster. Action eligibility
    // is checked before spending; ongoing application retains the effect's ownership rules.
    internal static bool CanAffectAlly(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition, EffectDescription description) =>
        IsPartyInterferenceEnabled(actor, requireManual: false) && IsPartyAllyTarget(actor, target, true) &&
        !IsHostileOnly(actor, definition) &&
        (actor != target || description?.TargetExcludeCaster != true) &&
        (description == null || IsHarmfulEffect(definition, description) || IsHarmfulTargetEffect(definition, description));

    // This is the party-interference permission, not native range, side or
    // creature eligibility. Countering an allied cast has its own defensive
    // decision and must not be mistaken for directly harming its caster.
    internal static bool IsEffectRecipientAllowed(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition, EffectDescription description)
    {
        if (!Main.Settings.AllowPartyFriendlyFire || !IsPlayerPartyActor(actor)) return true;
        if (description?.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Counter &&
                form.CounterForm?.Type == CounterForm.CounterType.DissipateSpells) == true &&
            !DispelCheckContext.IsTargetAllowed(actor, target)) return false;
        if (IsPartyAllyTarget(actor, target, true) && IsHarmfulTargetEffect(definition, description))
        {
            return CanAffectAlly(actor, target, definition, GetOriginalDescription(description));
        }
        return !IsPartyAdverseInterruptTarget(actor, target, harmful: false) ||
               !IsBeneficialEffect(definition, description) ||
               IsPartyInterferenceEnabled(actor, requireManual: false);
    }

    internal static bool IsConditionRemovalAllowed(GameLocationCharacter actor, GameLocationCharacter target,
        ConditionDefinition condition)
    {
        if (!Main.Settings.AllowPartyFriendlyFire || !IsPlayerPartyActor(actor)) return true;
        var adverse = IsBeneficialCondition(condition) && IsPartyAllyTarget(actor, target, true) ||
                      IsHarmfulCondition(condition) && target != null && actor.IsOppositeSide(target.Side);
        return !adverse || IsPartyInterferenceEnabled(actor, requireManual: false);
    }

    private static bool IsHostileOnly(GameLocationCharacter actor, BaseDefinition definition)
    {
        if (TargetDefinition(definition).HasSubFeatureOfType<HostileTargetsOnly>()) return true;
        if (actor?.RulesetCharacter is not RulesetCharacterEffectProxy proxy) return false;
        return proxy.EffectProxyDefinition.HasSubFeatureOfType<HostileTargetsOnly>() ||
               TargetDefinition(EffectHelpers.GetEffectByGuid(proxy.EffectGuid)?.GetSourceDefinitionSafe())
                   .HasSubFeatureOfType<HostileTargetsOnly>();
    }

    internal static bool IsValidOffensiveTarget(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition = null, EffectDescription description = null)
    {
        if (actor == null || target == null) return false;
        var original = GetOriginalDescription(description ??
            (TargetDefinition(definition) as IMagicEffect)?.EffectDescription);
        if (!IsEffectRecipientAllowed(actor, target, definition, original)) return false;

        if (Main.Settings.AllowPartyFriendlyFire && IsPlayerPartyActor(actor) &&
            IsPartyAllyTarget(actor, target, true) &&
            (original == null || IsHarmfulTargetEffect(definition, original)))
        {
            return CanTargetAlly(actor, target, definition, original);
        }

        // The caller has already checked native range, visibility and creature
        // eligibility. Preserve native unrestricted candidates, without
        // treating an instance-expanded Enemy description as a native All effect.
        var nativeUnrestrictedTarget = original?.TargetSide == Side.All && IsManualPartyActor(actor) &&
            !IsHostileOnly(actor, definition) &&
            (actor != target || !original.TargetExcludeCaster);
        return actor.IsOppositeSide(target.Side) || nativeUnrestrictedTarget ||
               (actor != target || original?.TargetExcludeCaster != true) &&
               CanTargetAlly(actor, target, definition, GetOriginalDescription(description));
    }

    internal static bool CanReactToAllyAction(GameLocationCharacter reactor, GameLocationCharacter actor,
        RulesetEffect effect = null) =>
        Main.Settings.AllowPartyFriendlyFire && IsManualPartyActor(reactor) && IsPartyAllyTarget(reactor, actor, true) &&
        (effect == null || !effect.Terminated);

    internal static bool CanHarmPartyWithInterrupt(GameLocationCharacter reactor, GameLocationCharacter target) =>
        CanReactToAllyAction(reactor, target) && IsPartyInterferenceEnabled(reactor);

    internal static RulesetCharacter GetPartyInterferenceOwner(RulesetCharacter character)
    {
        var visited = new HashSet<RulesetCharacter>();
        var current = character;
        while (current != null && visited.Add(current))
        {
            var controller = current.GetEffectControllerOrSelf();
            var next = controller != current ? controller : current.OriginalFormCharacter;
            if (next == null || next == current || visited.Contains(next)) return current;
            current = next;
        }
        return current;
    }

    private static bool IsPartyInterferenceEnabled(GameLocationCharacter actor, bool requireManual = true) =>
        Main.Settings.AllowPartyFriendlyFire &&
        (requireManual ? IsManualPartyActor(actor) : IsPlayerPartyActor(actor)) &&
        GetPartyInterferenceOwner(actor.RulesetCharacter)
            .IsToggleEnabled((ActionDefinitions.Id)ExtraActionId.PartyHarmfulInterruptsToggle);

    internal static bool IsPartyAdverseInterruptTarget(GameLocationCharacter actor, GameLocationCharacter target,
        bool harmful) => actor != null && target != null &&
        (harmful ? IsPartyAllyTarget(actor, target, true) : actor.IsOppositeSide(target.Side));

    // This permission does not widen a power's native recipients. Callers first
    // apply its range, sight, creature and side restrictions, then assess the
    // recipients of the optional effect relative to the party. An enemy in the
    // same effect does not authorize a forbidden party recipient.
    private static bool IsPartyAdverseInterruptTargetsAllowed(GameLocationCharacter actor,
        IEnumerable<GameLocationCharacter> targets, bool harmful)
    {
        var selected = targets?.Where(target => target != null).Distinct().ToArray() ?? [];
        return !selected.Any(target => IsPartyAdverseInterruptTarget(actor, target, harmful)) ||
               IsPartyInterferenceEnabled(actor);
    }

    internal static bool IsBeneficialTargetsAllowed(GameLocationCharacter actor,
        IEnumerable<GameLocationCharacter> targets) =>
        actor == null || IsPartyAdverseInterruptTargetsAllowed(actor, targets, harmful: false);

    internal static bool IsBeneficialTargetAllowed(GameLocationCharacter actor,
        GameLocationCharacter target) =>
        IsBeneficialTargetsAllowed(actor, target == null ? [] : [target]);

    internal static bool CanCounterAllySpell(GameLocationCharacter reactor, GameLocationCharacter caster,
        CharacterActionCastSpell castAction, RulesetEffectSpell effect)
    {
        if (!CanReactToAllyAction(reactor, caster, effect) || effect is not { Terminated: false }) return false;
        if (CanHarmPartyWithInterrupt(reactor, caster)) return true;

        // Countering a companion's attack on the reactor is a defense. Use the
        // pending action's real recipients, including collected area targets;
        // a harmful follow-up granted by a beneficial spell is not an attack on
        // the creature receiving that spell.
        return caster != reactor && castAction is { Aborted: false } &&
               castAction.ActingCharacter == caster && castAction.ActionParams.RulesetEffect == effect &&
               castAction.ActionParams.TargetCharacters.Contains(reactor) &&
               CanAffectExpandedTarget(effect, caster, reactor) &&
               caster.RulesetCharacter.GetSubFeaturesByType<IFilterRulesetEffectTargets>()
                   .All(filter => filter.CanAffectTarget(effect, caster, reactor)) &&
               IsDirectlyHarmfulEffect(effect.SourceDefinition, effect.EffectDescription);
    }

    // Validate every adverse recipient, including a party creature sharing a
    // multi-target effect with enemies. The picker can retain its legal targets.
    internal static bool IsHarmfulInterruptTargetsAllowed(GameLocationCharacter actor,
        IEnumerable<GameLocationCharacter> targets, BaseDefinition definition = null,
        EffectDescription description = null)
    {
        if (!Main.Settings.AllowPartyFriendlyFire || actor == null) return true;
        if (description != null && !IsHarmfulInterruptEffect(definition, description)) return true;
        var selected = targets?.Where(target => target != null).Distinct().ToArray() ?? [];
        var allies = selected.Where(target => IsPartyAdverseInterruptTarget(actor, target, harmful: true)).ToArray();
        if (allies.Length == 0) return true;
        if (IsHostileOnly(actor, definition) ||
            description?.TargetExcludeCaster == true && allies.Contains(actor)) return false;
        return IsPartyAdverseInterruptTargetsAllowed(actor, selected, harmful: true);
    }

    internal static bool IsValidOffensiveReactionTarget(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition = null, EffectDescription description = null) =>
        IsValidOffensiveTarget(actor, target, definition, description) &&
        (!Main.Settings.AllowPartyFriendlyFire || !IsPartyAllyTarget(actor, target, true) ||
         CanHarmPartyWithInterrupt(actor, target));

    internal static bool IsHarmfulTargetEffect(BaseDefinition definition, EffectDescription description) =>
        TargetDefinition(definition).HasSubFeatureOfType<HarmfulInterrupt>() ||
        IsDirectlyHarmfulEffect(definition, description);

    internal static bool IsHarmfulInterruptEffect(BaseDefinition definition, EffectDescription description) =>
        IsHarmfulTargetEffect(definition, description) || IsSpellcastingInterruption(description);

    private static bool IsSpellcastingInterruption(EffectDescription description) =>
        description?.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Counter &&
            form.CounterForm?.Type == CounterForm.CounterType.InterruptSpellcasting) == true;

    internal static bool IsHarmfulInterruptTargetAllowed(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition, EffectDescription description) =>
        IsHarmfulInterruptTargetsAllowed(actor, target == null ? [] : [target], definition, description) ||
        !IsHarmfulInterruptEffect(definition, description);

    internal static bool IsExpandedFriendlyFireDescription(EffectDescription description) =>
        description != null && ExpandedDescriptions.TryGetValue(description, out _);

    internal static EffectDescription GetOriginalDescription(EffectDescription description) =>
        description != null && ExpandedDescriptions.TryGetValue(description, out var expanded)
            ? expanded.Original : description;

    internal static void TrackDerivedDescription(EffectDescription source, EffectDescription derived)
    {
        if (source != derived && ExpandedDescriptions.TryGetValue(source, out var expanded))
        {
            ExpandedDescriptions.Remove(derived);
            ExpandedDescriptions.Add(derived, expanded);
        }
    }

    internal static EffectDescription GetEffectDescription(RulesetEffect effect, EffectDescription description)
    {
        var actor = GameLocationCharacter.GetFromActor(effect switch
        {
            RulesetEffectSpell spell => spell.Caster,
            RulesetEffectPower power => power.User,
            _ => null
        });
        var original = GetOriginalDescription(description);

        if (!IsPartyInterferenceEnabled(actor, requireManual: false) ||
            original?.TargetSide != Side.Enemy || original.TargetType == TargetType.Self ||
            !IsHarmfulEffect(effect.SourceDefinition, original) ||
            IsHostileOnly(actor, effect.SourceDefinition))
        {
            return original;
        }

        if (!ExpandedEffects.TryGetValue(effect, out var expanded) || expanded.Original != original)
        {
            ExpandedEffects.Remove(effect);
            expanded = new ExpandedEffect(original)
            {
                Definition = TargetDefinition(effect.SourceDefinition), Effect = effect
            };
            ExpandedEffects.Add(effect, expanded);
            ExpandedDescriptions.Add(expanded.Description, expanded);
        }

        return expanded.Description;
    }

    // Used after native area collection as well as immediately before application.
    // Keep the native side check for every non-party actor, even with an expanded cast.
    internal static bool CanAffectExpandedTarget(RulesetEffect effect, GameLocationCharacter actor,
        GameLocationCharacter target)
    {
        // Application can be the first access to this effect. Establish its scoped
        // description before deciding whether the additional party targets are allowed.
        if (effect != null)
        {
            _ = effect.EffectDescription;
        }

        if (effect != null &&
            !IsEffectRecipientAllowed(actor, target, effect.SourceDefinition, effect.EffectDescription))
        {
            return false;
        }

        return effect == null || !ExpandedEffects.TryGetValue(effect, out var expanded) ||
               actor == null || target == null ||
               (actor.IsOppositeSide(target.Side) ||
                CanAffectAlly(actor, target, effect.SourceDefinition, expanded.Original)) &&
               IsSelectedAreaTarget(effect, actor, target);
    }

    internal static bool ValidateActionTargets(CharacterActionParams parameters)
    {
        var effect = parameters.RulesetEffect;
        var description = GetOriginalDescription(effect?.EffectDescription);
        if (description == null)
        {
            return true;
        }

        var actor = parameters.ActingCharacter;
        if (!parameters.TargetCharacters.All(target =>
                IsEffectRecipientAllowed(actor, target, effect.SourceDefinition, description)))
        {
            return false;
        }
        var proxyDelivery = parameters.ActionDefinition?.Id == ActionDefinitions.Id.SpendPower &&
                            actor?.RulesetCharacter is RulesetCharacterEffectProxy proxy &&
                            EffectHelpers.GetEffectByGuid(proxy.EffectGuid) is { Terminated: false };
        if (description.TargetSide != Side.Enemy)
        {
            return !Main.Settings.AllowPartyFriendlyFire || !IsPlayerPartyActor(actor) ||
                   !IsHarmfulTargetEffect(effect.SourceDefinition, description) ||
                   parameters.TargetCharacters.All(target => !IsPartyAllyTarget(actor, target, true) ||
                       (proxyDelivery ? CanAffectAlly(actor, target, effect.SourceDefinition, description) :
                           CanTargetAlly(actor, target, effect.SourceDefinition, description)));
        }
        if (!IsHarmfulEffect(effect.SourceDefinition, description)) return true;

        return parameters.TargetCharacters.All(target => target != null &&
            (target == actor && description.TargetType == TargetType.Self ||
            actor.IsOppositeSide(target.Side) || !IsPartyMember(target) &&
            !IsExpandedFriendlyFireDescription(effect.EffectDescription) ||
            (proxyDelivery ? CanAffectAlly(actor, target, effect.SourceDefinition, description) :
                CanTargetAlly(actor, target, effect.SourceDefinition, description))));
    }

    internal static int FilterExpandedTargets(GameLocationCharacter source, EffectDescription description,
        List<GameLocationCharacter> targets, List<ActionModifier> modifiers = null)
    {
        if (source == null || description == null)
        {
            return 0;
        }

        var isExpanded = ExpandedDescriptions.TryGetValue(description, out var expanded);
        var filterNativePartyTargets = Main.Settings.AllowPartyFriendlyFire && IsPlayerPartyActor(source) &&
            (IsHarmfulTargetEffect(null, description) || IsBeneficialEffect(null, description));
        if (!isExpanded && !filterNativePartyTargets) return 0;

        var removed = 0;
        for (var index = targets.Count - 1; index >= 0; index--)
        {
            var target = targets[index];
            var allowed = IsEffectRecipientAllowed(source, target, expanded?.Definition, description) &&
                (!isExpanded ||
                 (source.IsOppositeSide(target.Side) ||
                  CanAffectAlly(source, target, expanded.Definition, expanded.Original)) &&
                 (expanded.Effect == null || IsSelectedAreaTarget(expanded.Effect, source, target)));
            if (allowed)
            {
                continue;
            }

            targets.RemoveAt(index);
            if (modifiers != null && index < modifiers.Count)
            {
                modifiers.RemoveAt(index);
            }
            removed++;
        }

        return removed;
    }

    private static bool IsSelectedAreaTarget(RulesetEffect effect, GameLocationCharacter source,
        GameLocationCharacter target)
    {
        var selective = TargetDefinition(effect.SourceDefinition).GetFirstSubFeatureOfType<SelectiveTargets>();
        return selective == null || !effect.Attributes.ContainsKey(SelectionPrepared) ||
            !effect.Attributes.ContainsKey(SelectionExcluded + target.Guid) &&
            (source.IsOppositeSide(target.Side) ||
             effect.Attributes.ContainsKey(SelectionAttributeName + ":" + target.Guid)) &&
            (!selective.RequiresSight || effect.EffectDescription.RecurrentEffect == RecurrentEffect.No ||
             source.CanSeeTarget(target));
    }

    internal static IEnumerator SelectAreaTargets(CharacterActionMagicEffect action)
    {
        var parameters = action.ActionParams;
        var effect = parameters.RulesetEffect;
        var actor = parameters.ActingCharacter;
        var definition = TargetDefinition(effect.SourceDefinition);
        var description = GetOriginalDescription(effect.EffectDescription);
        var selective = definition.GetFirstSubFeatureOfType<SelectiveTargets>();
        if (!Main.Settings.AllowPartyFriendlyFire || !IsManualPartyActor(actor) ||
            selective == null || description.TargetSide != Side.Enemy ||
            effect.Attributes.ContainsKey(SelectionPrepared))
        {
            yield break;
        }

        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
        bool CandidateValid(GameLocationCharacter target) =>
            !effect.Terminated && !action.Aborted &&
            ReactionRequestSelectTarget.IsCandidateValid(actor, target) &&
            IsValidOffensiveTarget(actor, target, definition, description) &&
            (!selective.RequiresSight || actor.CanSeeTarget(target)) &&
            parameters.TargetCharacters.Contains(target);
        var candidates = characters.ValidCharacters.Where(CandidateValid).Distinct().ToList();
        List<GameLocationCharacter> selectedTargets = null;
        var cancelled = false;
        yield return SelectOffensiveTargets(actor, candidates, actor, definition, CandidateValid, -1,
            selected => selectedTargets = selected, isHarmfulInterruption: false, preselectHostiles: true,
            selectionCancelled: () => cancelled = true);
        if (cancelled)
        {
            action.Abort(CharacterAction.InterruptionType.Abort);
            effect.Terminate(false);
            yield break;
        }
        foreach (var selected in selectedTargets ?? [])
        {
            if (CandidateValid(selected))
            {
                effect.Attributes[SelectionAttributeName + ":" + selected.Guid] = SelectionAttribute();
            }
        }

        // Keep the native default for creatures entering a recurrent effect later,
        // and preserve legacy saved inclusions. Explicit exclusions apply to either side.
        foreach (var excluded in candidates.Except(selectedTargets ?? []))
        {
            effect.Attributes[SelectionExcluded + excluded.Guid] = SelectionAttribute();
        }

        effect.Attributes[SelectionPrepared] = SelectionAttribute();
        FilterExpandedTargets(actor, effect.EffectDescription, parameters.TargetCharacters, parameters.ActionModifiers);
    }

    internal static IEnumerator SelectOffensiveTargets(GameLocationCharacter actor,
        IEnumerable<GameLocationCharacter> candidates, GameLocationCharacter waiter, BaseDefinition definition,
        Func<GameLocationCharacter, bool> validator, int maximumTargets,
        Action<List<GameLocationCharacter>> completed, bool isHarmfulInterruption = true,
        bool preselectHostiles = false, Action selectionCancelled = null)
    {
        var description = (TargetDefinition(definition) as IMagicEffect)?.EffectDescription;
        bool CandidateValid(GameLocationCharacter target) => validator(target) &&
            IsValidOffensiveTarget(actor, target, definition, description);
        var remaining = candidates.Where(CandidateValid).Distinct().ToList();
        var maximum = maximumTargets < 0 ? remaining.Count : maximumTargets;
        if (!IsManualPartyActor(actor))
        {
            // Autonomous actors retain the caller's native ordering and hostile targets.
            completed(remaining.Where(target => actor.IsOppositeSide(target.Side)).Take(maximum).ToList());
            yield break;
        }

        if (isHarmfulInterruption && remaining.Count > 0 &&
            !IsHarmfulInterruptTargetsAllowed(actor, remaining, definition, description))
        {
            completed([]);
            yield break;
        }

        List<GameLocationCharacter> selected = null;
        yield return actor.MyReactToSelectTargets(remaining, waiter, "AdditionalEffectTarget",
            Gui.Format("Reaction/&CustomReactionAdditionalEffectTargetDescription", definition.FormatTitle()),
            maximum, choices => selected = choices, effectDefinition: definition,
            candidateValidator: CandidateValid, isOffensive: true, allowEmptySelection: true,
            isHarmfulInterruption: isHarmfulInterruption, preselectHostiles: preselectHostiles,
            reactionNotValidated: selectionCancelled);
        var validTargets = (selected ?? []).Where(CandidateValid).ToList();
        completed(!isHarmfulInterruption ||
                  IsHarmfulInterruptTargetsAllowed(actor, validTargets, definition, description)
            ? validTargets
            : []);
    }

    private static RulesetAttribute SelectionAttribute() => new()
    {
        AttributeDefinition = _selectionAttribute,
        BaseValue = 1,
        MinValue = 0,
        MaxValue = 1,
        MaxEditableValue = 1
    };
}
