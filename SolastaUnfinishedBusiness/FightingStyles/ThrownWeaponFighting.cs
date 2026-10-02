using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionFightingStyleChoices;

namespace SolastaUnfinishedBusiness.FightingStyles;

internal sealed class ThrownWeaponFighting : AbstractFightingStyle
{
    internal const string Name = "ThrownWeaponFighting";

    internal override FightingStyleDefinition FightingStyle { get; } = FightingStyleBuilder
        .Create(Name)
        .SetGuiPresentation(Category.FightingStyle, ItemDefinitions.Dagger.GuiPresentation.SpriteReference)
        .SetFeatures(
            FeatureDefinitionAdditionalDamageBuilder
                .Create($"AdditionalDamage{Name}")
                .SetGuiPresentation(Name, Category.FightingStyle)
                .SetNotificationTag(Name)
                .SetDamageValueDetermination(AdditionalDamageValueDetermination.FlatBonus)
                .SetFlatDamageBonus(2)
                .SetAttackModeOnly()
                .SetRequiredProperty(RestrictedContextRequiredProperty.Weapon)
                .AddCustomSubFeatures(new ValidateContextInsteadOfRestrictedProperty(
                    // Melee and thrown attacks share an attack mode; only the hit context distinguishes them.
                    (_, _, _, item, rangedAttack, mode, _) =>
                    {
                        // Use the actual item for spell effects represented by a synthetic weapon attack mode.
                        var weapon = (mode?.SourceObject as RulesetItem)?.ItemDefinition ?? item;

                        return (OperationType.Set, rangedAttack && mode != null && weapon is { IsWeapon: true } &&
                                                   (mode.AttackTags.Contains(TagsDefinitions.WeaponTagThrown) ||
                                                    ValidatorsWeapon.HasAnyWeaponTag(weapon, TagsDefinitions.WeaponTagThrown)));
                    }))
                .AddToDB())
        .AddToDB();

    internal override List<FeatureDefinitionFightingStyleChoice> FightingStyleChoice =>
    [
        ClassesContext.FightingStyleChoiceBarbarian,
        ClassesContext.FightingStyleChoiceMonk,
        ClassesContext.FightingStyleChoiceRogue,
        FightingStyleChampionAdditional,
        FightingStyleFighter,
        FightingStylePaladin,
        FightingStyleRanger
    ];
}
