using System;
using System.Linq;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Feats;

internal static class FeatHelpers
{
    internal sealed class SpellReplacementOnLevelUp(
        Func<bool> isEnabled,
        params FeatureDefinitionCastSpell[] castingFeatures)
    {
        internal bool IsEnabled => isEnabled();
        internal FeatureDefinitionCastSpell[] CastingFeatures { get; } = castingFeatures;
    }

    internal sealed class ModifyWeaponAttackModeTypeFilter(
        FeatDefinition source,
        params WeaponTypeDefinition[] weaponTypeDefinition) : IModifyWeaponAttackMode
    {
        private readonly TrendInfo _trendInfo = new(1, FeatureSourceType.Feat, source.Name, source);

        public void ModifyWeaponAttackMode(
            RulesetCharacter character,
            RulesetAttackMode attackMode,
            RulesetItem weapon,
            bool canAddAbilityDamageBonus)
        {
            if (attackMode.SourceDefinition is not ItemDefinition { IsWeapon: true } sourceDefinition ||
                !weaponTypeDefinition.Contains(sourceDefinition.WeaponDescription.WeaponTypeDefinition))
            {
                return;
            }

            attackMode.ToHitBonus += 1;
            attackMode.ToHitBonusTrends.Add(_trendInfo);
        }
    }

    internal sealed class SpellTag
    {
        internal SpellTag(string spellTag, bool forceFixedList = false, bool allowSlotCasting = false)
        {
            Name = spellTag;
            ForceFixedList = forceFixedList;
            AllowSlotCasting = allowSlotCasting;
        }

        internal string Name { get; }
        internal bool ForceFixedList { get; }
        internal bool AllowSlotCasting { get; }
    }
}
