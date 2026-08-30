using SolastaUnfinishedBusiness.Api.GameExtensions;
using static ActionDefinitions;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors;

internal delegate bool IsPowerVisibleHandler(
    RulesetCharacter character,
    FeatureDefinitionPower power,
    ActionType actionType);

internal class ModifyPowerVisibility(IsPowerVisibleHandler handler, bool keepsVisibleWhenUnavailable = false)
{
    internal static readonly ModifyPowerVisibility Default = new(IsDefaultVisible);
    internal static readonly ModifyPowerVisibility Hidden = new((_, _, _) => false);
    internal static readonly ModifyPowerVisibility Visible = new((_, _, _) => true);
    internal static readonly ModifyPowerVisibility NotInCombat = new((_, _, _) => Gui.Battle == null);
    internal static readonly ModifyPowerVisibility NotInCombatWhenUnavailable =
        new((_, _, _) => Gui.Battle == null, true);

    internal bool KeepsVisibleWhenUnavailable { get; } = keepsVisibleWhenUnavailable;

    private static bool IsDefaultVisible(
        RulesetCharacter character,
        FeatureDefinitionPower power,
        ActionType actionType)
    {
        if (Gui.Battle == null)
        {
            return true;
        }

        var powerActivationTime = power.activationTime;

        CastingTimeToActionDefinition.TryGetValue(powerActivationTime, out var powerActionType);

        return powerActionType == actionType
               || (actionType == ActionType.Main &&
                   (powerActivationTime == ActivationTime.Reaction ||
                    (powerActivationTime != ActivationTime.NoCost &&
                     powerActionType == ActionType.NoCost)));
    }

    internal bool IsVisible(RulesetCharacter character, FeatureDefinitionPower power, ActionType actionType)
    {
        return handler(character, power, actionType);
    }

    internal static bool IsPowerHidden(RulesetCharacter character, RulesetUsablePower power, ActionType actionType)
    {
        return IsPowerHidden(character, power.PowerDefinition, actionType);
    }

    internal static bool IsPowerHidden(RulesetCharacter character, FeatureDefinitionPower power, ActionType actionType)
    {
        var validator = power.GetFirstSubFeatureOfType<ModifyPowerVisibility>();

        return validator != null && !validator.IsVisible(character, power, actionType);
    }

    internal static bool ShouldKeepVisibleWhenUnavailable(FeatureDefinitionPower power)
    {
        return power.GetFirstSubFeatureOfType<ModifyPowerVisibility>()?.KeepsVisibleWhenUnavailable == true;
    }
}
