using System.Collections.Generic;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Behaviors;

internal static class ForceGlobalUniqueEffects
{
    private static readonly Dictionary<Group, HashSet<BaseDefinition>> Groups = [];

    private static HashSet<BaseDefinition> GetGroup(Group group)
    {
        if (Groups.TryGetValue(group, out var value))
        {
            return value;
        }

        var newGroup = new HashSet<BaseDefinition>();

        Groups.Add(group, newGroup);

        return newGroup;
    }

    /**Returns copies*/
    private static HashSet<BaseDefinition> GetSameGroupItems(BaseDefinition definition)
    {
        var result = new HashSet<BaseDefinition>();

        foreach (var group in Groups)
        {
            if (!group.Value.Contains(definition))
            {
                continue;
            }

            foreach (var p in group.Value)
            {
                result.Add(p);
            }
        }

        return result;
    }

    internal static void AddToGroup(Group group, [NotNull] params BaseDefinition[] definitions)
    {
        foreach (var definition in definitions)
        {
            GetGroup(group).Add(definition);
        }
    }

    internal static void EnforceLimitedInstancePower(CharacterActionUsePower action)
    {
        var power = action.ActionParams.RulesetEffect.GetSourceDefinitionSafe();

        if (!power)
        {
            return;
        }

        var limiter = power.GetFirstSubFeatureOfType<ILimitEffectInstances>();

        if (limiter == null)
        {
            return;
        }

        var character = action.ActingCharacter.RulesetCharacter;
        var effects = new List<RulesetEffectPower>();

        foreach (var effect in EffectHelpers.GetAllEffectsBySourceGuid(character.Guid))
        {
            if (effect is not RulesetEffectPower powerEffect)
            {
                continue;
            }

            var sourceDefinition = powerEffect.GetSourceDefinitionSafe();

            if (!sourceDefinition)
            {
                continue;
            }

            var tmp = sourceDefinition.GetFirstSubFeatureOfType<ILimitEffectInstances>();

            if (tmp == null || tmp.Name != limiter.Name)
            {
                continue;
            }

            effects.Add(powerEffect);
        }

        effects.Sort((x, y) => x.Guid.CompareTo(y.Guid));

        var limit = limiter.GetLimit(character);
        var remove = effects.Count - limit;

        for (var i = 0; i < remove; i++)
        {
            character.TerminatePower(effects[i]);
        }
    }

    /**
     * Used in the patch to terminate all matching powers and spells of same group
     */
    internal static void TerminateMatchingUniqueEffect(RulesetCharacter character, RulesetEffect uniqueEffect)
    {
        var sourceDefinition = uniqueEffect.GetSourceDefinitionSafe();

        if (!sourceDefinition)
        {
            return;
        }

        var group = GetSameGroupItems(sourceDefinition);

        if (sourceDefinition is
            FeatureDefinitionPower { UniqueInstance: true } or
            SpellDefinition { UniqueInstance: true })
        {
            //ensure we try to properly terminate unique effects not in groups
            group.Add(sourceDefinition);
        }

        var allSubDefinitions = new HashSet<BaseDefinition>();

        foreach (var definition in group)
        {
            allSubDefinitions.Add(definition);

            switch (definition)
            {
                case FeatureDefinitionPower power:
                {
                    var bundles = PowerBundle.GetMasterPowersBySubPower(power);

                    foreach (var masterPower in bundles)
                    {
                        var bundle = PowerBundle.GetBundle(masterPower);

                        if (bundle is not { TerminateAll: true })
                        {
                            continue;
                        }

                        foreach (var subPower in bundle.SubPowers)
                        {
                            allSubDefinitions.Add(subPower);
                        }
                    }

                    break;
                }
                case SpellDefinition spell:
                {
                    foreach (var allElement in DatabaseRepository.GetDatabase<SpellDefinition>())
                    {
                        if (!spell.IsSubSpellOf(allElement))
                        {
                            continue;
                        }

                        foreach (var subSpell in allElement.SubspellsList)
                        {
                            allSubDefinitions.Add(subSpell);
                        }
                    }

                    break;
                }
            }
        }

        foreach (var effect in EffectHelpers.GetAllEffectsBySourceGuid(character.Guid))
        {
            if (effect == uniqueEffect)
            {
                continue;
            }

            var effectSourceDefinition = effect.GetSourceDefinitionSafe();

            if (!effectSourceDefinition ||
                !allSubDefinitions.Contains(effectSourceDefinition))
            {
                continue;
            }

            effect.DoTerminate(character);
        }
    }

    internal enum Group
    {
        DomainSmithReinforceArmor,
        Familiar,
        GrenadierGrenadeMode,
        InventorSpellStoringItem,
        MoonlitNewAndFullMoon,
        ConstellationForm
    }
}
