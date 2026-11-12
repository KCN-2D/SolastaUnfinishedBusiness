using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors;

// A source contributes choices; a single handler combines character and current-weapon sources.
internal sealed class WeaponDamageTypeChoice(
    Func<bool> isEnabled,
    IsWeaponValidHandler isWeaponValid,
    params string[] damageTypes)
{
    internal static readonly IPhysicalAttackBeforeHitConfirmedOnEnemy Handler = new ChooseDamageType();

    private IEnumerable<string> GetDamageTypes(RulesetCharacter character, RulesetAttackMode mode, RulesetItem weapon)
    {
        return isEnabled() && isWeaponValid(mode, weapon, character) ? damageTypes : [];
    }

    private sealed class ChooseDamageType : IPhysicalAttackBeforeHitConfirmedOnEnemy
    {
        private const string ChoiceName = "WeaponDamageTypeChoice";
        private readonly FeatureDefinitionPower _pool;
        private readonly Dictionary<FeatureDefinitionPower, string> _choices = [];

        internal ChooseDamageType()
        {
            _pool = FeatureDefinitionPowerBuilder
                .Create($"Power{ChoiceName}")
                .SetGuiPresentation(ChoiceName, Category.Feature)
                .SetUsesFixed(ActivationTime.NoCost)
                .SetShowCasting(false)
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .Build())
                .AddToDB();

            foreach (var damageType in new[] { DamageTypeForce, DamageTypeNecrotic, DamageTypePsychic, DamageTypeRadiant })
            {
                var power = FeatureDefinitionPowerBuilder
                    .Create($"Power{ChoiceName}{damageType}")
                    .SetGuiPresentation($"Rules/&{damageType}Title", $"Rules/&{damageType}Description")
                    .SetUsesFixed(ActivationTime.NoCost)
                    .SetShowCasting(false)
                    .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
                    .SetEffectDescription(EffectDescriptionBuilder.Create()
                        .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                        .Build())
                    .AddToDB();

                _choices.Add(power, damageType);
            }

            PowerBundle.RegisterPowerBundle(_pool, false, _choices.Keys);
        }

        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnEnemy(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier actionModifier,
            RulesetAttackMode attackMode,
            bool rangedAttack,
            AdvantageType advantageType,
            List<EffectForm> actualEffectForms,
            bool firstTarget,
            bool criticalHit)
        {
            if (attackMode == null)
            {
                yield break;
            }

            var weapon = attackMode.SourceObject as RulesetItem;
            var damage = actualEffectForms.Find(form => form.FormType == EffectForm.EffectFormType.Damage)?.DamageForm;
            var character = attacker.RulesetCharacter;

            if (damage == null || character.IsDeadOrDyingOrUnconscious)
            {
                yield break;
            }

            var availableTypes = character.GetSubFeaturesByType<WeaponDamageTypeChoice>()
                .Concat(weapon?.GetSubFeaturesByType<WeaponDamageTypeChoice>() ?? [])
                .Distinct()
                .SelectMany(choice => choice.GetDamageTypes(character, attackMode, weapon))
                .Where(type => type != damage.DamageType)
                .ToHashSet();
            var powers = _choices.Where(choice => availableTypes.Contains(choice.Value))
                .Select(choice => PowerProvider.Get(choice.Key, character))
                .ToArray();

            if (powers.Length == 0)
            {
                yield break;
            }

            var added = powers.Where(power => !character.UsablePowers.Contains(power)).ToArray();
            character.UsablePowers.AddRange(added);

            try
            {
                yield return attacker.MyReactToSelectPowerBundle(
                    PowerProvider.Get(_pool, character), attacker, ChoiceName, 1, () => 1,
                    selected =>
                    {
                        if (_choices.TryGetValue(selected[0], out var type) && availableTypes.Contains(type))
                            damage.DamageType = type;
                    },
                    () => !character.IsDeadOrDyingOrUnconscious && availableTypes.Count != 0,
                    executeSelectedPowers: false,
                    powerFilter: power => _choices.TryGetValue(power, out var type) && availableTypes.Contains(type),
                    waiter: attacker);
            }
            finally
            {
                foreach (var power in added)
                {
                    character.UsablePowers.Remove(power);
                }
            }

        }
    }
}
