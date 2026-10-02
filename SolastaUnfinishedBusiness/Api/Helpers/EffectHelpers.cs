using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Spells;

namespace SolastaUnfinishedBusiness.Api.Helpers;

internal static class EffectHelpers
{
    private static readonly AccessTools.FieldRef<RulesetEffectPower, FeatureDefinitionPower> PowerSourceDefinitionRef =
        AccessTools.FieldRefAccess<RulesetEffectPower, FeatureDefinitionPower>("sourceDefinition");

    /**DC and magic attack bonus will be calculated based on the stats of the user, not from device itself*/
    public const int BasedOnUser = -1;

    /**DC and magic attack bonus will be calculated based on the stats of character who summoned item, not from device itself*/
    public const int BasedOnItemSummoner = -2;

    // use this to start a custom visual effect during combat
    internal static void StartVisualEffect(
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        IMagicEffect magicEffect,
        EffectType effectType = EffectType.Impact)
    {
        StartVisualEffect(attacker, defender, magicEffect.EffectDescription.EffectParticleParameters, effectType);
    }

    internal static void StartVisualEffect(
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        EffectParticleParameters effectParticleParameters,
        EffectType effectType = EffectType.Impact)
    {
        // be safe on multiplayer sessions as depending on flow, SFX can break them
        if (Global.IsMultiplayer)
        {
            return;
        }

        var prefab = effectType switch
        {
            EffectType.Caster => effectParticleParameters.CasterParticle,
            EffectType.QuickCaster => effectParticleParameters.CasterQuickSpellParticle,
            EffectType.Condition => effectParticleParameters.ConditionParticle,
            EffectType.Effect => effectParticleParameters.EffectParticle,
            EffectType.Impact => effectParticleParameters.ImpactParticle,
            EffectType.Zone => effectParticleParameters.ZoneParticle,
            _ => throw new ArgumentOutOfRangeException(nameof(effectType), effectType, null)
        };

        if (!prefab)
        {
            return;
        }

        var sentParameters = new ParticleSentParameters(attacker, defender, "ChuckNorris");

        WorldLocationPoolManager
            .GetElement(prefab, true)
            .GetComponent<ParticleSetup>()
            .Setup(sentParameters);
    }

    internal static int CalculateSaveDc(RulesetCharacter character, EffectDescription effectDescription,
        CharacterClassDefinition classDefinition, int def = 10)
    {
        switch (effectDescription.DifficultyClassComputation)
        {
            case RuleDefinitions.EffectDifficultyClassComputation.SpellCastingFeature:
            {
                var rulesetSpellRepertoire = character.GetClassSpellRepertoire(classDefinition);

                if (rulesetSpellRepertoire != null)
                {
                    return rulesetSpellRepertoire.SaveDC;
                }

                break;
            }
            case RuleDefinitions.EffectDifficultyClassComputation.AbilityScoreAndProficiency:
                var attributeValue = character.TryGetAttributeValue(effectDescription.SavingThrowDifficultyAbility);
                var proficiencyBonus = character.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus);

                return RuleDefinitions.ComputeAbilityScoreBasedDC(attributeValue, proficiencyBonus);

            case RuleDefinitions.EffectDifficultyClassComputation.FixedValue:
                return effectDescription.FixedSavingThrowDifficultyClass;
            //TODO: implement missing computation methods (like Ki and Breath Weapon)
            case RuleDefinitions.EffectDifficultyClassComputation.Ki:
            case RuleDefinitions.EffectDifficultyClassComputation.BreathWeapon:
            case RuleDefinitions.EffectDifficultyClassComputation.CustomAbilityModifierAndProficiency:
            default:
                break;
            // ReSharper disable once RedundantEmptySwitchSection
        }

        return def;
    }

    internal static RulesetCharacter GetSummoner(RulesetCharacter summon)
    {
        return summon.TryGetConditionOfCategoryAndType(
            AttributeDefinitions.TagConjure, RuleDefinitions.ConditionConjuredCreature,
            out var activeConditionConjuredCreature)
            ? GetCharacterByGuid(activeConditionConjuredCreature.SourceGuid)
            : summon.TryGetConditionOfCategoryAndType(
                AttributeDefinitions.TagConjure, RuleDefinitions.ConditionWildShapeSubstituteForm,
                out var activeConditionWildShapeSubstituteForm)
                ? GetCharacterByGuid(activeConditionWildShapeSubstituteForm.SourceGuid)
                : null;
    }

    internal static bool IsFamiliarTouchSpell(
        SpellDefinition spellDefinition, EffectDescription effectDescription)
    {
        return spellDefinition != null && effectDescription != null &&
               effectDescription.TargetType is RuleDefinitions.TargetType.Individuals or RuleDefinitions.TargetType.IndividualsUnique &&
               !spellDefinition.HasSubFeatureOfType<FixesContext.NoDistanced>() &&
               (effectDescription.RangeType == RuleDefinitions.RangeType.Touch ||
                effectDescription.RangeType == RuleDefinitions.RangeType.MeleeHit &&
                effectDescription.RangeParameter <= 1);
    }

    private const string FamiliarTouchDeliveryPrefix = "FamiliarTouchDelivery:";

    private sealed class FamiliarTouchDeliveryState(ulong familiarGuid)
    {
        internal ulong FamiliarGuid { get; } = familiarGuid;
        internal bool ReactionSpent { get; set; }
        internal EffectDescription SourceDescription { get; set; }
        internal EffectDescription DeliveryDescription { get; set; }
    }

    private static readonly ConditionalWeakTable<RulesetEffectSpell, FamiliarTouchDeliveryState>
        FamiliarTouchEffects = new();
    private static readonly ConditionalWeakTable<EffectDescription, FamiliarTouchDeliveryState>
        FamiliarTouchDescriptions = new();

    internal static void SelectFamiliarTouchDelivery(
        CharacterActionParams actionParams, GameLocationCharacter familiar)
    {
        // Action parameters survive native cloning and network serialization.
        actionParams.StringParameter2 = FamiliarTouchDeliveryPrefix + familiar.Guid;
    }

    internal static void BindFamiliarTouchDelivery(CharacterActionParams actionParams)
    {
        var selection = actionParams?.StringParameter2;
        if (actionParams?.RulesetEffect is not RulesetEffectSpell spell ||
            selection == null || !selection.StartsWith(FamiliarTouchDeliveryPrefix, StringComparison.Ordinal) ||
            !ulong.TryParse(selection.Substring(FamiliarTouchDeliveryPrefix.Length), out var familiarGuid) ||
            FamiliarTouchEffects.TryGetValue(spell, out _))
        {
            return;
        }

        FamiliarTouchEffects.Add(spell, new FamiliarTouchDeliveryState(familiarGuid));
    }

    internal static EffectDescription GetFamiliarTouchDescription(
        RulesetEffectSpell spell, EffectDescription description)
    {
        if (!FamiliarTouchEffects.TryGetValue(spell, out var state))
        {
            return description;
        }

        if (state.SourceDescription != description)
        {
            state.SourceDescription = description;
            state.DeliveryDescription = new EffectDescription();
            state.DeliveryDescription.Copy(description);
            FamiliarTouchDescriptions.Add(state.DeliveryDescription, state);
        }

        // Battle targeting only receives an effect description. Give this cast its own identity,
        // without marking the shared spell definition or other simultaneous casts.
        return state.DeliveryDescription;
    }

    internal static bool IsFamiliarTouchDelivery(EffectDescription description)
    {
        return description != null && FamiliarTouchDescriptions.TryGetValue(description, out _);
    }

    internal static GameLocationCharacter GetFamiliarTouchDelivery(
        GameLocationCharacter caster,
        SpellDefinition spellDefinition,
        EffectDescription effectDescription,
        GameLocationCharacter target,
        MetamagicOptionDefinition metamagicOption = null)
    {
        if (caster?.RulesetCharacter == null || target?.RulesetCharacter == null ||
            !IsFamiliarTouchSpell(spellDefinition, effectDescription) ||
            !FamiliarTouchDescriptions.TryGetValue(effectDescription, out var state))
        {
            return null;
        }

        var familiar = SpellBuilders.GetFamiliars(caster.RulesetCharacter)
            .FirstOrDefault(character => character.Guid == state.FamiliarGuid);

        return familiar != null && caster.IsWithinRange(familiar, 20) &&
               (state.ReactionSpent || familiar.CanReact()) && familiar.IsWithinRange(target, 1)
            ? familiar
            : null;
    }

    internal static bool ValidateFamiliarTouchDelivery(CharacterActionParams actionParams, bool spendReaction)
    {
        BindFamiliarTouchDelivery(actionParams);
        if (actionParams?.RulesetEffect is not RulesetEffectSpell spell ||
            !FamiliarTouchEffects.TryGetValue(spell, out var state))
        {
            return true;
        }

        // Native action ranks are spent after Execute completes. Recheck the selected
        // action before starting a queued delivery or paying either character's resources.
        if (!state.ReactionSpent && !ActionPanelContext.IsFamiliarTouchActionAvailable(
                actionParams.ActingCharacter, actionParams.ActionDefinition.Id))
        {
            foreach (var modifier in actionParams.ActionModifiers)
            {
                modifier.FailureFlags.Add("Failure/&FailureFlagFamiliarTouchCasterActionUnavailable");
            }

            return false;
        }

        GameLocationCharacter deliveryFamiliar = null;
        for (var index = 0; index < actionParams.TargetCharacters.Count; index++)
        {
            var familiar = GetFamiliarTouchDelivery(actionParams.ActingCharacter, spell.SpellDefinition,
                spell.EffectDescription, actionParams.TargetCharacters[index]);

            if (familiar == null)
            {
                if (index < actionParams.ActionModifiers.Count)
                {
                    actionParams.ActionModifiers[index].FailureFlags.Add("Failure/&FailureFlagNoReachForTargetDescription");
                }

                return false;
            }

            deliveryFamiliar = familiar;
        }

        if (deliveryFamiliar == null)
        {
            return false;
        }

        // Validate every target before paying once. Later attack checks retain this cast's origin
        // even after its reaction has been spent.
        if (spendReaction && !state.ReactionSpent)
        {
            deliveryFamiliar.SpendActionType(ActionDefinitions.ActionType.Reaction);
            state.ReactionSpent = true;
        }

        return true;
    }

    internal static RulesetCharacter GetCharacterByGuid(ulong guid)
    {
        if (guid == 0)
        {
            return null;
        }

        if (!RulesetEntity.TryGetEntity<RulesetEntity>(guid, out var entity))
        {
            return null;
        }

        return entity as RulesetCharacter;
    }

    internal static RulesetEffect GetEffectByGuid(ulong guid)
    {
        if (guid == 0)
        {
            return null;
        }

        return !RulesetEntity.TryGetEntity<RulesetEffect>(guid, out var entity) ? null : entity;
    }

    internal static RulesetItem GetItemByGuid(ulong guid)
    {
        if (guid == 0)
        {
            return null;
        }

        return !RulesetEntity.TryGetEntity<RulesetItem>(guid, out var item) ? null : item;
    }

    internal static List<RulesetCharacter> GetSummonedCreatures(RulesetEffect effect)
    {
        var summons = new List<RulesetCharacter>();

        if (effect == null)
        {
            return summons;
        }

        foreach (var conditionGuid in effect.trackedConditionGuids)
        {
            if (!RulesetEntity.TryGetEntity<RulesetCondition>(conditionGuid, out var condition)
                || condition.Name != RuleDefinitions.ConditionConjuredCreature)
            {
                continue;
            }

            if (RulesetEntity.TryGetEntity<RulesetCharacter>(condition.TargetGuid, out var creature)
                && creature != null)
            {
                summons.TryAdd(creature);
            }
        }

        return summons;
    }

    internal static RulesetCharacter GetCharacterByEffectGuid(ulong guid)
    {
        return GetEffectByGuid(guid) switch
        {
            RulesetEffectSpell spell => spell.Caster,
            RulesetEffectPower power => power.User,
            _ => null
        };
    }

    internal static BaseDefinition GetSourceDefinitionSafe(this RulesetEffect effect)
    {
        return effect switch
        {
            RulesetEffectPower power => GetPowerSourceDefinitionSafe(power),
            RulesetEffectSpell spell => spell.SpellDefinition,
            _ => null
        };
    }

    internal static FeatureDefinitionPower GetPowerSourceDefinitionSafe(RulesetEffectPower power)
    {
        if (power == null)
        {
            return null;
        }

        ref var sourceDefinition = ref PowerSourceDefinitionRef(power);

        return sourceDefinition ?? power.UsablePower?.PowerDefinition;
    }

    internal static FeatureDefinitionPower EnsurePowerSourceDefinition(RulesetEffectPower power)
    {
        if (power == null)
        {
            return null;
        }

        ref var sourceDefinition = ref PowerSourceDefinitionRef(power);

        sourceDefinition ??= power.UsablePower?.PowerDefinition;

        return sourceDefinition;
    }

    internal static List<RulesetEffect> GetAllEffectsBySourceGuid(ulong guid)
    {
        return ServiceRepository.GetService<IRulesetEntityService>().RulesetEntities.Values
            .OfType<RulesetEffect>()
            .Where(e => e.SourceGuid == guid)
            .ToList();
    }

    internal static List<RulesetCondition> GetAllConditionsBySourceGuid(ulong guid)
    {
        return ServiceRepository.GetService<IRulesetEntityService>().RulesetEntities.Values
            .OfType<RulesetCondition>()
            .Where(e => e.SourceGuid == guid)
            .ToList();
    }

    internal static void DoTerminate(this RulesetEffect effect, RulesetCharacter source = null)
    {
        source ??= GetCharacterByGuid(effect.SourceGuid);

        if (source != null)
        {
            switch (effect)
            {
                case RulesetEffectPower power:
                    source.TerminatePower(power);
                    return;
                case RulesetEffectSpell spell:
                    source.TerminateSpell(spell);
                    return;
            }
        }

        effect.Terminate(true);
    }

    internal static (RulesetCharacter, BaseDefinition) GetCharacterAndSourceDefinitionByEffectGuid(ulong guid)
    {
        if (guid == 0)
        {
            return (null, null);
        }

        if (!RulesetEntity.TryGetEntity<RulesetEffect>(guid, out var effect))
        {
            return (null, null);
        }

        return effect switch
        {
            RulesetEffectSpell spell => (spell.Caster, spell.SpellDefinition),
            RulesetEffectPower power => (power.User, GetPowerSourceDefinitionSafe(power)),
            _ => (null, null)
        };
    }

    internal static void SetGuid(this RulesetEffect effect, ulong guid)
    {
        switch (effect)
        {
            case RulesetEffectPower power:
                power.userId = guid;
                break;
            case RulesetEffectSpell spell:
                spell.casterId = guid;
                break;
        }
    }

    internal enum EffectType
    {
        Caster,
        QuickCaster,
        Condition,
        Effect,
        Impact,
        Zone
    }
}
