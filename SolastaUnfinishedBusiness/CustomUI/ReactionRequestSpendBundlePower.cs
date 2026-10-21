using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class ReactionRequestSpendBundlePower : ReactionRequest, IReactionRequestWithResource,
    IReactionRequestWithCallbacks, IReactionRequestMultiSelection
{
    internal const string Name = "ReactionSpendPowerBundle";
    private readonly GuiCharacter _guiCharacter;
    private readonly FeatureDefinitionPower _masterPower;
    private readonly ActionModifier _modifier;
    private readonly GameLocationCharacter _target;
    private readonly Func<bool> _reactionStillValid;
    private readonly bool _selectionOnly;
    private readonly Func<FeatureDefinitionPower, bool> _powerFilter;

    internal ReactionRequestSpendBundlePower([NotNull] CharacterActionParams reactionParams,
        Action<ReactionRequestSpendBundlePower> reactionValidated = null,
        Action<ReactionRequestSpendBundlePower> reactionNotValidated = null,
        Func<bool> reactionStillValid = null,
        int maximumPowers = 0,
        Func<int> availableBudget = null,
        Func<FeatureDefinitionPower, bool> powerFilter = null)
        : base(Name, reactionParams)
    {
        _reactionStillValid = reactionStillValid;
        _selectionOnly = maximumPowers > 0;
        _powerFilter = powerFilter;
        ReactionValidated = ReactionRequestCallback.Transform(reactionValidated);
        ReactionNotValidated = ReactionRequestCallback.Transform(reactionNotValidated);

        _target = reactionParams.TargetCharacters[0];
        _modifier = reactionParams.ActionModifiers.ElementAtOrDefault(0) ?? new ActionModifier();
        _guiCharacter = new GuiCharacter(reactionParams.ActingCharacter);
        _masterPower = ((RulesetEffectPower)reactionParams.RulesetEffect).PowerDefinition;
        BuildSuboptions();
        if (_selectionOnly)
        {
            ReactionParams.SpellRepertoire ??= new RulesetSpellRepertoire();
            Selection = new ReactionSelection(ReactionParams.SpellRepertoire.KnownSpells.Count, maximumPowers, 1,
                option => IsPowerAvailable(PowerAt(option)), option => PowerAt(option).CostPerUse, availableBudget);
            ClearSelectedPower();
            ReactionParams.ActionDefinition = ServiceRepository.GetService<IGameLocationActionService>()
                .AllActionDefinitions[(ActionDefinitions.Id)ExtraActionId.DoNothingFree];
            // A selection request has no effect to execute or resource to spend. Its caller
            // applies the accepted powers through their existing native action paths.
            ReactionParams.UsablePower = null;
        }
    }

    public override int SelectedSubOption
    {
        get
        {
            var power = (ReactionParams.RulesetEffect as RulesetEffectPower)?.PowerDefinition;

            if (!power)
            {
                return -1;
            }

            // The UI lists only usable subpowers, so its index must use the same filtered list.
            return ReactionParams.SpellRepertoire?.KnownSpells.FindIndex(
                spell => PowerBundle.GetPower(spell) == power) ?? -1;
        }
    }

    public ReactionSelection Selection { get; }
    public bool CanContinueSelection => Selection?.HasAvailableOptions == true && IsTriggerStillValid();

    internal sealed class SelectionDisplaySettings(
        string resourceTitle,
        string optionCostFormat,
        string selectionTitleFormat)
    {
        internal string ResourceTitle { get; } = resourceTitle;
        internal string OptionCostFormat { get; } = optionCostFormat;
        internal string SelectionTitleFormat { get; } = selectionTitleFormat;
    }

    internal SelectionDisplaySettings SelectionDisplay => Selection == null
        ? null
        : _masterPower.GetFirstSubFeatureOfType<SelectionDisplaySettings>();

    internal IReadOnlyList<FeatureDefinitionPower> SelectedPowers => Selection?.SelectedOptions
        .Select(PowerAt).ToArray() ?? [];

    private FeatureDefinitionPower PowerAt(int option) =>
        PowerBundle.GetPower(ReactionParams.SpellRepertoire.KnownSpells[option]);


    [NotNull] public override string SuboptionTag => "PowerBundle";

    // Custom triggers may allow an unconscious target, but every bundle still has
    // to validate its selected grant before the native command spends its resource.
    public override bool IsStillValid =>
        (Selection?.IsValid ?? (ReactionParams.RulesetEffect is RulesetEffectPower selected &&
        IsPowerAvailable(selected.PowerDefinition))) &&
        IsTriggerStillValid();

    private bool IsTriggerStillValid() =>
        _reactionStillValid != null ? ReactionRequestCallback.Validate(ReactionParams, _reactionStillValid) :
         (ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters.Contains(_target) == true &&
          _target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false });

    public Action<ReactionRequest> ReactionValidated { get; }
    public Action<ReactionRequest> ReactionNotValidated { get; }

    public ICustomReactionResource Resource { get; set; }

    // Deferred choices can apply a different power or benefit the actor after the
    // triggering action. This describes their recipients without executing them.
    internal sealed class EffectContext(FeatureDefinitionPower definition = null, bool targetsSelf = false)
    {
        internal FeatureDefinitionPower Definition { get; } = definition;
        internal bool TargetsSelf { get; } = targetsSelf;
    }

    internal (FeatureDefinitionPower Definition, GameLocationCharacter Target) GetEffectContext(
        FeatureDefinitionPower power)
    {
        var context = power.GetFirstSubFeatureOfType<EffectContext>();
        var definition = context?.Definition ?? power;
        return (definition, context?.TargetsSelf == true ? Character : GetTargetForPower(definition));
    }

    internal GameLocationCharacter GetTargetForPower(FeatureDefinitionPower power)
    {
        var effectDescription = power?.EffectDescription;

        return effectDescription != null &&
               (effectDescription.RangeType == RangeType.Self || effectDescription.TargetType == TargetType.Self)
            ? ReactionParams.ActingCharacter
            : _target;
    }

    private void BuildSuboptions()
    {
        SubOptionsAvailability.Clear();

        var bundle = _masterPower.GetBundle();
        var selected = false;

        if (bundle == null)
        {
            ClearSelectedPower();
            return;
        }

        ReactionParams.SpellRepertoire = new RulesetSpellRepertoire();

        var i = 0;

        foreach (var power in bundle.SubPowers.Where(IsPowerAvailable))
        {
            reactionParams.SpellRepertoire.KnownSpells.Add(PowerBundle.GetSpell(power));
            SubOptionsAvailability.Add(i, true);

            if (!selected && !_selectionOnly)
            {
                SelectSubOption(i);
                selected = true;
            }

            i++;
        }

        if (!selected)
        {
            ClearSelectedPower();
        }
    }

    private bool IsPowerAvailable(FeatureDefinitionPower power)
    {
        if (!power) return false;
        var character = Character.RulesetCharacter;
        var effect = GetEffectContext(power);
        var automatedBenefitAllowed = !effect.Definition.HasSubFeatureOfType<TargetingContext.BeneficialEffect>() ||
            TargetingContext.IsManualPartyActor(Character) || Character.Side == effect.Target.Side;

        return _masterPower.GetBundle()?.SubPowers.Contains(power) == true &&
               (_powerFilter?.Invoke(power) ?? true) &&
               automatedBenefitAllowed &&
               TargetingContext.IsHarmfulInterruptTargetAllowed(Character, effect.Target,
                   effect.Definition, effect.Definition.EffectDescription) &&
               TargetingContext.IsEffectRecipientAllowed(Character, effect.Target,
                   effect.Definition, effect.Definition.EffectDescription) &&
               character.UsablePowers.Any(usable => usable.PowerDefinition == power) &&
               CanUsePower(character, power);
    }

    private static bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
    {
        var powerValidators = power.GetAllSubFeaturesOfType<IValidatePowerUse>();

        if (powerValidators.Any(v => !v.CanUsePower(character, power)))
        {
            return false;
        }

        // must use GetRemainingPowerUses as power could be a Shared Pool
        return character.GetRemainingPowerUses(power) > 0;
    }

    public override void SelectSubOption(int option)
    {
        if (Selection != null)
        {
            Selection.ApplyCommand(option);
            return;
        }

        if (option == -1)
        {
            ClearSelectedPower();
            return;
        }

        var spells = ReactionParams.SpellRepertoire?.KnownSpells;
        if (option < 0 || spells == null || option >= spells.Count)
        {
            return;
        }

        var spell = spells[option];
        var power = spell ? PowerBundle.GetPower(spell) : null;
        if (!IsPowerAvailable(power))
        {
            return;
        }

        var parameters = CreatePowerParameters(power);
        // Resolve the replacement before releasing the current selection. An expired
        // or stale UI index must leave a valid choice and its targets intact.
        ClearSelectedPower();
        ReactionParams.ActionDefinition = parameters.ActionDefinition;
        ReactionParams.RulesetEffect = parameters.RulesetEffect;
        ReactionParams.UsablePower = parameters.UsablePower;
        ReactionParams.TargetCharacters.AddRange(parameters.TargetCharacters);
        ReactionParams.ActionModifiers.AddRange(parameters.ActionModifiers);
    }

    internal IEnumerable<CharacterAction> CreateSelectedActions()
    {
        // Validate the complete allocation before instantiating any child effect.
        if (Selection?.IsValid != true || !IsStillValid) return [];
        // These parameters always use SpendPower. Like other native follow-ups,
        // instantiate its action directly instead of the dynamic name-based factory.
        return SelectedPowers.Select(power => new CharacterActionSpendPower(CreatePowerParameters(power))).ToArray();
    }

    private CharacterActionParams CreatePowerParameters(FeatureDefinitionPower power)
    {
        var actingCharacter = ReactionParams.ActingCharacter;
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();
        var rulesetCharacter = actingCharacter.RulesetCharacter;
        var usablePower = PowerProvider.Get(power, rulesetCharacter);
        var powerEffect = implementationService.InstantiateEffectPower(rulesetCharacter, usablePower, false);

        var parameters = new CharacterActionParams(actingCharacter, ActionDefinitions.Id.SpendPower)
        {
            RulesetEffect = powerEffect,
            UsablePower = usablePower
        };
        var targetCharacters = parameters.TargetCharacters;
        var modifiers = parameters.ActionModifiers;
        var effectDescription = power.EffectDescription;

        // We let targets collection be empty with RangeType MeleeHit on sub power so it can be handled manually in code
        // i.e.: Cunning / Devious Strike powers that need to be applied on attack finished not before hit
        if (effectDescription.RangeType == RangeType.MeleeHit)
        {
            return parameters;
        }

        if (effectDescription.RangeType == RangeType.Self
            || effectDescription.TargetType == TargetType.Self)
        {
            targetCharacters.Add(actingCharacter);
            modifiers.Add(_modifier);
        }
        else
        {
            targetCharacters.Add(_target);
            modifiers.Add(_modifier);

            var targets = powerEffect.ComputeTargetParameter();

            if (!effectDescription.IsSingleTarget || targets <= 1)
            {
                return parameters;
            }

            while (_target != null && _modifier != null && targetCharacters.Count < targets)
            {
                targetCharacters.Add(_target);
                modifiers.Add(_modifier);
            }
        }
        return parameters;
    }

    private void ClearSelectedPower()
    {
        ReactionParams.RulesetEffect?.Terminate(false);
        ReactionParams.RulesetEffect = null;
        ReactionParams.TargetCharacters.Clear();
        ReactionParams.ActionModifiers.Clear();
    }

    public override string FormatTitle()
    {
        return Gui.Localize($"Reaction/&ReactionSpendPowerBundle{ReactionParams.StringParameter}Title");
    }

    public override string FormatDescription()
    {
        var format = $"Reaction/&ReactionSpendPowerBundle{ReactionParams.StringParameter}Description";

        return string.IsNullOrEmpty(reactionParams.StringParameter2)
            ? Gui.Format(format, _guiCharacter.Name)
            : reactionParams.StringParameter2;
    }

    public override string FormatReactTitle()
    {
        var format = $"Reaction/&ReactionSpendPowerBundle{ReactionParams.StringParameter}ReactTitle";

        return Gui.Format(format, _guiCharacter.Name);
    }

    public override string FormatReactDescription()
    {
        var format = $"Reaction/&ReactionSpendPowerBundle{ReactionParams.StringParameter}ReactDescription";

        return Gui.Format(format, _guiCharacter.Name);
    }

    public override void OnSetInvalid()
    {
        base.OnSetInvalid();
        ClearSelectedPower();
    }
}
