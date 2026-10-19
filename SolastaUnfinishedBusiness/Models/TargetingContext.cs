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

    private const string SelectionAttributeName = "FriendlyFireTargetSelection";
    private const string SelectionPrepared = SelectionAttributeName + ":Prepared";
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

    internal static bool IsHarmfulEffect(BaseDefinition definition, EffectDescription description) =>
        TargetDefinition(definition).HasSubFeatureOfType<AttackAfterMagicEffect>() || IsHarmful(description);

    internal static bool IsHarmful(EffectDescription description) => HasHarmfulForms(description, new HashSet<EffectDescription>());

    private static bool HasHarmfulForms(EffectDescription description, HashSet<EffectDescription> visited) =>
        description != null && visited.Add(description) && description.EffectForms.Any(form => form.FormType switch
        {
            EffectForm.EffectFormType.Damage or EffectForm.EffectFormType.Kill => true,
            EffectForm.EffectFormType.Condition => form.ConditionForm is { ApplyToSelf: false, ForceOnSelf: false } &&
                new[] { form }.GetAppliedConditionDefinitions().Any(IsHarmfulCondition),
            EffectForm.EffectFormType.Motion => form.MotionForm.Type is
                MotionForm.MotionType.PushFromOrigin or MotionForm.MotionType.DragToOrigin or
                MotionForm.MotionType.FallProne or MotionForm.MotionType.PushFromWall or
                MotionForm.MotionType.PushRandomDirection or MotionForm.MotionType.Telekinesis,
            EffectForm.EffectFormType.Summon => form.SummonForm.SummonType == SummonForm.Type.EffectProxy &&
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

    internal static bool CanTargetAlly(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition, EffectDescription description) =>
        IsManualPartyActor(actor) && CanAffectAlly(actor, target, definition, description);

    // An already-created effect can outlast its conscious caster. Action eligibility
    // is checked before spending; ongoing application retains the effect's ownership rules.
    internal static bool CanAffectAlly(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition, EffectDescription description) =>
        Main.Settings.AllowPartyFriendlyFire && IsPlayerPartyActor(actor) && IsPartyAllyTarget(actor, target, true) &&
        !IsHostileOnly(actor, definition) &&
        (actor != target || description?.TargetExcludeCaster != true) &&
        (description == null || IsHarmfulEffect(definition, description));

    private static bool IsHostileOnly(GameLocationCharacter actor, BaseDefinition definition)
    {
        if (TargetDefinition(definition).HasSubFeatureOfType<HostileTargetsOnly>()) return true;
        if (actor?.RulesetCharacter is not RulesetCharacterEffectProxy proxy) return false;
        return proxy.EffectProxyDefinition.HasSubFeatureOfType<HostileTargetsOnly>() ||
               TargetDefinition(EffectHelpers.GetEffectByGuid(proxy.EffectGuid)?.GetSourceDefinitionSafe())
                   .HasSubFeatureOfType<HostileTargetsOnly>();
    }

    internal static bool IsValidOffensiveTarget(GameLocationCharacter actor, GameLocationCharacter target,
        BaseDefinition definition = null, EffectDescription description = null) =>
        actor != null && target != null &&
        (actor.IsOppositeSide(target.Side) || CanTargetAlly(actor, target, definition, description));

    internal static bool CanReactToAllyAction(GameLocationCharacter reactor, GameLocationCharacter actor,
        RulesetEffect effect = null) =>
        Main.Settings.AllowPartyFriendlyFire && IsManualPartyActor(reactor) && IsPartyAllyTarget(reactor, actor, true) &&
        (effect == null || !effect.Terminated);

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

        if (!Main.Settings.AllowPartyFriendlyFire || !IsPlayerPartyActor(actor) ||
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

        return effect == null || !ExpandedEffects.TryGetValue(effect, out var expanded) ||
               actor == null || target == null || actor.IsOppositeSide(target.Side) ||
               CanAffectAlly(actor, target, effect.SourceDefinition, expanded.Original) &&
               IsSelectedAreaTarget(effect, actor, target);
    }

    internal static bool ValidateActionTargets(CharacterActionParams parameters)
    {
        var effect = parameters.RulesetEffect;
        var description = GetOriginalDescription(effect?.EffectDescription);
        if (description?.TargetSide != Side.Enemy || !IsHarmfulEffect(effect.SourceDefinition, description))
        {
            return true;
        }

        var actor = parameters.ActingCharacter;
        var proxyDelivery = parameters.ActionDefinition?.Id == ActionDefinitions.Id.SpendPower &&
                            actor?.RulesetCharacter is RulesetCharacterEffectProxy proxy &&
                            EffectHelpers.GetEffectByGuid(proxy.EffectGuid) is { Terminated: false };
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
        if (source == null || description == null ||
            !ExpandedDescriptions.TryGetValue(description, out var expanded))
        {
            return 0;
        }

        var removed = 0;
        for (var index = targets.Count - 1; index >= 0; index--)
        {
            var target = targets[index];
            if (source.IsOppositeSide(target.Side) ||
                CanAffectAlly(source, target, expanded.Definition, expanded.Original) &&
                (!expanded.Definition.HasSubFeatureOfType<SelectiveTargets>() ||
                 expanded.Effect == null || !expanded.Effect.Attributes.ContainsKey(SelectionPrepared) ||
                 IsSelectedAreaTarget(expanded.Effect, source, target)))
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
        return selective == null || effect.Attributes.ContainsKey(SelectionAttributeName + ":" + target.Guid) &&
            (!selective.RequiresSight || effect.EffectDescription.RecurrentEffect == RecurrentEffect.No ||
             source.CanSeeTarget(target));
    }

    internal static IEnumerator SelectPartyAreaTargets(CharacterActionMagicEffect action)
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
            characters.ValidCharacters.Contains(target) && CanTargetAlly(actor, target, definition, description) &&
            (!selective.RequiresSight || actor.CanSeeTarget(target)) &&
            (description.RecurrentEffect != RecurrentEffect.No || parameters.TargetCharacters.Contains(target));
        var candidates = characters.ValidCharacters.Where(CandidateValid).Distinct().ToList();
        List<GameLocationCharacter> selectedTargets = null;
        yield return SelectOffensiveTargets(actor, candidates, actor, definition, CandidateValid, -1,
            selected => selectedTargets = selected, "FriendlyAreaTarget");
        foreach (var selected in selectedTargets ?? [])
        {
            if (CandidateValid(selected))
            {
                effect.Attributes[SelectionAttributeName + ":" + selected.Guid] = SelectionAttribute();
            }
        }

        effect.Attributes[SelectionPrepared] = SelectionAttribute();
        FilterExpandedTargets(actor, effect.EffectDescription, parameters.TargetCharacters, parameters.ActionModifiers);
    }

    internal static IEnumerator SelectOffensiveTargets(GameLocationCharacter actor,
        IEnumerable<GameLocationCharacter> candidates, GameLocationCharacter waiter, BaseDefinition definition,
        Func<GameLocationCharacter, bool> validator, int maximumTargets,
        Action<List<GameLocationCharacter>> completed, string selectionType = "AdditionalEffectTarget")
    {
        var selected = new List<GameLocationCharacter>();
        var description = (TargetDefinition(definition) as IMagicEffect)?.EffectDescription;
        bool CandidateValid(GameLocationCharacter target) => !selected.Contains(target) && validator(target) &&
            IsValidOffensiveTarget(actor, target, definition, description);
        var remaining = candidates.Where(CandidateValid).Distinct().ToList();
        var maximum = maximumTargets < 0 ? remaining.Count : maximumTargets;
        if (!Main.Settings.AllowPartyFriendlyFire || !IsManualPartyActor(actor))
        {
            // Autonomous actors retain the caller's native ordering and hostile targets.
            completed(remaining.Where(target => actor.IsOppositeSide(target.Side)).Take(maximum).ToList());
            yield break;
        }

        while (selected.Count < maximum && remaining.Any(CandidateValid))
        {
            GameLocationCharacter choice = null;
            yield return actor.MyReactToSelectTarget(remaining, waiter, selectionType,
                selectionType == "FriendlyAreaTarget"
                    ? Gui.Localize("Reaction/&CustomReactionFriendlyAreaTargetDescription")
                    : Gui.Format("Reaction/&CustomReactionAdditionalEffectTargetDescription", definition.FormatTitle()),
                target => choice = target, effectDefinition: definition,
                candidateValidator: CandidateValid, isOffensive: true);
            if (choice == null)
            {
                break;
            }

            if (CandidateValid(choice))
            {
                selected.Add(choice);
            }
            remaining.Remove(choice);
        }

        completed(selected.Where(target => validator(target) &&
            IsValidOffensiveTarget(actor, target, definition, description)).ToList());
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
