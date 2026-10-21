using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Feats;

internal static class FeatHelpers
{
    private static readonly Dictionary<string, FormattedDefinitionText> SpellSelectionPresentations = [];

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
        bool allowSlotCasting = false,
        bool unlimitedFreeCasting = false)
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
            .AddCustomSubFeatures(new SpellTag(spellTag, forceFixedList: true,
                allowSlotCasting: allowSlotCasting, unlimitedFreeCasting: unlimitedFreeCasting))
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

    internal static SpellTag GetSpellSelectionTag(
        CharacterHeroBuildingData data, string selectionTag, FeatureDefinitionCastSpell castingFeature = null)
    {
        if (data?.HeroCharacter == null || string.IsNullOrEmpty(selectionTag))
        {
            return null;
        }

        // The pending feat owns its pools before Finalize creates its repertoire.
        // Resolve the longest actual suffix so an unrelated class pool cannot borrow its policy.
        return (data.LevelupTrainedFeats.Values.SelectMany(feats => feats)
                .SelectMany(feat => feat.Features).OfType<FeatureDefinitionCastSpell>())
            .Concat(data.HeroCharacter.SpellRepertoires.Select(repertoire => repertoire.SpellCastingFeature))
            .Append(castingFeature)
            .Where(feature => feature != null)
            .Select(feature => feature.GetFirstSubFeatureOfType<SpellTag>())
            .Where(tag => !string.IsNullOrEmpty(tag?.Name) &&
                          selectionTag.EndsWith(tag.Name, StringComparison.Ordinal))
            .OrderByDescending(tag => tag.Name.Length)
            .FirstOrDefault();
    }

    internal static FormattedDefinitionText GetSpellSelectionPresentation(string selectionTag) =>
        string.IsNullOrEmpty(selectionTag) ? null : SpellSelectionPresentations
            .Where(entry => selectionTag.EndsWith(entry.Key, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Key.Length)
            .Select(entry => entry.Value)
            .FirstOrDefault();

    internal sealed class SpellTag
    {
        internal SpellTag(string spellTag, bool forceFixedList = false, bool allowSlotCasting = false,
            bool unlimitedFreeCasting = false, bool dedicatedSelectionList = false,
            Func<RulesetCharacterHero, SpellDefinition, bool> selectionValidator = null,
            bool freeCastingIgnoresComponents = false, FormattedDefinitionText presentation = null,
            string selectionGroup = null, int selectionOrder = 0,
            FormattedDefinitionText sourceDescription = null)
        {
            Name = spellTag;
            ForceFixedList = forceFixedList;
            AllowSlotCasting = allowSlotCasting;
            UnlimitedFreeCasting = unlimitedFreeCasting;
            DedicatedSelectionList = dedicatedSelectionList;
            SelectionValidator = selectionValidator;
            FreeCastingIgnoresComponents = freeCastingIgnoresComponents;
            Presentation = presentation;
            SourceDescription = sourceDescription;
            SelectionGroup = selectionGroup;
            SelectionOrder = selectionOrder;
            if (presentation != null)
            {
                // Tags also label learned spells outside an active character-building session.
                SpellSelectionPresentations[spellTag] = presentation;
            }
        }

        internal string Name { get; }
        internal bool ForceFixedList { get; }
        internal bool AllowSlotCasting { get; }
        internal bool UnlimitedFreeCasting { get; }
        internal bool DedicatedSelectionList { get; }
        internal bool FreeCastingIgnoresComponents { get; }
        internal FormattedDefinitionText Presentation { get; }
        // A complete post-learning tooltip for a grant with one free casting level,
        // distinct from spell-selection instructions.
        // Numeric placeholders are {0}: free casting level and {1}: maximum uses.
        internal FormattedDefinitionText SourceDescription { get; }
        internal string SelectionGroup { get; }
        internal int SelectionOrder { get; }
        private Func<RulesetCharacterHero, SpellDefinition, bool> SelectionValidator { get; }

        internal bool CanSelectSpell(RulesetCharacterHero hero, SpellDefinition spell) =>
            SelectionValidator?.Invoke(hero, spell) ?? true;
    }
}
