using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Feats;

internal static class FeatHelpers
{
    internal static List<FeatureDefinitionCastSpell.SlotsByLevelDuplet> BuildFixedFreeCastingSlots(params int[] spellLevels)
    {
        if (spellLevels == null || spellLevels.Length == 0 || spellLevels.Any(level => level is < 1 or > 9))
        {
            throw new ArgumentOutOfRangeException(nameof(spellLevels));
        }

        return Enumerable.Range(1, 20)
            .Select(level => new FeatureDefinitionCastSpell.SlotsByLevelDuplet
            {
                Level = level,
                Slots = Enumerable.Range(1, spellLevels.Max())
                    .Select(slot => spellLevels.Contains(slot) ? 1 : 0).ToList()
            })
            .ToList();
    }

    internal static FeatureDefinitionCastSpell BuildFixedFreeSpellCasting(
        string name,
        SpellListDefinition spellList,
        string castingAttribute,
        string spellTag,
        int spellLevel,
        RechargeRate rechargeRate,
        bool allowSlotCasting = false)
    {
        return FeatureDefinitionCastSpellBuilder
            .Create(name)
            .SetGuiPresentationNoContent(true)
            .SetFocusType(EquipmentDefinitions.FocusType.None)
            .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Race)
            .SetSpellKnowledge(SpellKnowledge.FixedList)
            .SetSpellReadyness(SpellReadyness.AllKnown)
            .SetSlotsRecharge(rechargeRate)
            .SetSlotsPerLevel(BuildFixedFreeCastingSlots(spellLevel))
            .SetUniqueLevelSlots(false)
            .SetSpellList(spellList)
            .SetSpellCastingAbility(castingAttribute)
            .AddCustomSubFeatures(new SpellTag(spellTag, forceFixedList: true, allowSlotCasting: allowSlotCasting))
            .AddToDB();
    }

    internal sealed class LegacyInvocationFreeCast(string invocationName, int spellLevel)
    {
        internal string InvocationName { get; } = invocationName;
        internal int SpellLevel { get; } = spellLevel;
    }

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
