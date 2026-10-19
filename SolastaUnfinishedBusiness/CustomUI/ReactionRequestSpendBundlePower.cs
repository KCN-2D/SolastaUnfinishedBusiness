using System;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class ReactionRequestSpendBundlePower : ReactionRequest, IReactionRequestWithResource,
    IReactionRequestWithCallbacks
{
    internal const string Name = "ReactionSpendPowerBundle";
    private readonly GuiCharacter _guiCharacter;
    private readonly FeatureDefinitionPower _masterPower;
    private readonly ActionModifier _modifier;
    private readonly GameLocationCharacter _target;
    private readonly Func<bool> _reactionStillValid;

    internal ReactionRequestSpendBundlePower([NotNull] CharacterActionParams reactionParams,
        Action<ReactionRequestSpendBundlePower> reactionValidated = null,
        Action<ReactionRequestSpendBundlePower> reactionNotValidated = null,
        Func<bool> reactionStillValid = null)
        : base(Name, reactionParams)
    {
        _reactionStillValid = reactionStillValid;
        ReactionValidated = ReactionRequestCallback.Transform(reactionValidated);
        ReactionNotValidated = ReactionRequestCallback.Transform(reactionNotValidated);

        _target = reactionParams.TargetCharacters[0];
        _modifier = reactionParams.ActionModifiers.ElementAtOrDefault(0) ?? new ActionModifier();
        _guiCharacter = new GuiCharacter(reactionParams.ActingCharacter);
        _masterPower = ((RulesetEffectPower)reactionParams.RulesetEffect).PowerDefinition;
        BuildSuboptions();
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


    [NotNull] public override string SuboptionTag => "PowerBundle";

    // Custom triggers may allow an unconscious target, but every bundle still has
    // to validate its selected grant before the native command spends its resource.
    public override bool IsStillValid =>
        ReactionParams.RulesetEffect is RulesetEffectPower selected &&
        IsPowerAvailable(selected.PowerDefinition) &&
        (_reactionStillValid?.Invoke() ??
         (ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters.Contains(_target) == true &&
          _target?.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false }));

    public Action<ReactionRequest> ReactionValidated { get; }
    public Action<ReactionRequest> ReactionNotValidated { get; }

    public ICustomReactionResource Resource { get; set; }

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

            if (!selected)
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
        var character = Character.RulesetCharacter;

        return power && _masterPower.GetBundle()?.SubPowers.Contains(power) == true &&
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

        var actingCharacter = ReactionParams.ActingCharacter;
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();
        var rulesetCharacter = actingCharacter.RulesetCharacter;
        var usablePower = PowerProvider.Get(power, rulesetCharacter);
        var powerEffect = implementationService.InstantiateEffectPower(rulesetCharacter, usablePower, false);

        // Resolve the replacement before releasing the current selection. An expired
        // or stale UI index must leave a valid choice and its targets intact.
        ClearSelectedPower();
        ReactionParams.ActionDefinition = ServiceRepository.GetService<IGameLocationActionService>()
            .AllActionDefinitions[ActionDefinitions.Id.SpendPower];
        ReactionParams.RulesetEffect = powerEffect;

        var targetCharacters = ReactionParams.TargetCharacters;
        var modifiers = ReactionParams.ActionModifiers;
        var effectDescription = power.EffectDescription;

        // We let targets collection be empty with RangeType MeleeHit on sub power so it can be handled manually in code
        // i.e.: Cunning / Devious Strike powers that need to be applied on attack finished not before hit
        if (effectDescription.RangeType == RangeType.MeleeHit)
        {
            return;
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
                return;
            }

            while (_target != null && _modifier != null && targetCharacters.Count < targets)
            {
                targetCharacters.Add(_target);
                modifiers.Add(_modifier);
            }
        }
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
