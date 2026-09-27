using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly ConditionDefinition ConditionHalfOrcAdrenalineRush = ConditionDefinitionBuilder
        .Create(ConditionDefinitions.ConditionDashingBonus, "ConditionHalfOrcAdrenalineRush")
        // The Dash ends this turn; temporary hit points last until the next long rest.
        .SetSpecialDuration(DurationType.Round, 0, TurnOccurenceType.EndOfTurn)
        .AddToDB();

    internal static readonly FeatureDefinitionPower PowerHalfOrcAdrenalineRush = FeatureDefinitionPowerBuilder
        .Create("PowerHalfOrcAdrenalineRush")
        .SetGuiPresentation(Category.Feature, FeatureDefinitionPowers.PowerMonkStepOfTheWindDash)
        .SetUsesProficiencyBonus(ActivationTime.BonusAction, RechargeRate.ShortRest)
        .SetShowCasting(false)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.UntilLongRest)
                .SetEffectForms(
                    EffectFormBuilder.ConditionForm(ConditionHalfOrcAdrenalineRush),
                    EffectFormBuilder.Create()
                        .SetTempHpForm()
                        .SetBonusMode(AddBonusMode.Proficiency)
                        .Build())
                .Build())
        .AddToDB();

    private static readonly RaceFeatureReplacement HalfOrcAdrenalineRushReplacement = new(
        CharacterRaceDefinitions.HalfOrc,
        FeatureDefinitionAdditionalDamages.AdditionalDamageHalfOrcSavageAttacks,
        PowerHalfOrcAdrenalineRush,
        () => Main.Settings.EnableHalfOrcAdrenalineRush2024);

    internal static void SwitchHalfOrcAdrenalineRush()
    {
        HalfOrcAdrenalineRushReplacement.Apply();
    }

    internal static void SynchronizeSpeciesFeatures(RulesetCharacterHero hero)
    {
        HalfOrcAdrenalineRushReplacement.Synchronize(hero);
    }
}
