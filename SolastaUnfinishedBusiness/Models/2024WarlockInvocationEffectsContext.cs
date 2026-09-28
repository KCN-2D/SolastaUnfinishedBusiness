using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Validators;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.InvocationDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionFeatureSets;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly FeatureDefinitionFeatureSet FeatureSetInvocationWitchSight =
        GetDefinition<FeatureDefinitionFeatureSet>("FeatureSetInvocationWitchSight");

    private static readonly FeatureDefinition[] WitchSightFeatures2014 =
        FeatureSetInvocationWitchSight.FeatureSet.ToArray();

    private static readonly FeatureDefinitionSense SenseWarlockWitchSight2024 = FeatureDefinitionSenseBuilder
        .Create("SenseWarlockWitchSight2024")
        .SetGuiPresentationNoContent(true)
        .SetSense(SenseMode.Type.Truesight, 6)
        .AddToDB();

    private static CustomBehaviorLifedrinker _lifedrinker2024;

    private static void SwitchWarlockInvocationEffects()
    {
        var enabled = Main.Settings.EnableWarlockInvocationProgression2024;
        FeatureSetInvocationWitchSight.FeatureSet.Clear();
        FeatureSetInvocationWitchSight.FeatureSet.AddRange(enabled ? [SenseWarlockWitchSight2024] : WitchSightFeatures2014);
        GetDefinition<InvocationDefinition>("WitchSight").GuiPresentation.description = enabled
            ? "Invocation/&WitchSight2024Description"
            : "Invocation/&WitchSightDescription";
        InvocationLifedrinker.GuiPresentation.description = enabled
            ? "Invocation/&Lifedrinker2024Description"
            : "Invocation/&LifedrinkerDescription";

        if (_lifedrinker2024 == null)
        {
            _lifedrinker2024 = new CustomBehaviorLifedrinker();
            InvocationLifedrinker.AddCustomSubFeatures(_lifedrinker2024);
            FeatureDefinitionAdditionalDamages.AdditionalDamageLifedrinker.AddCustomSubFeatures(
                new ValidateContextInsteadOfRestrictedProperty((_, _, _, _, _, _, _) =>
                    (OperationType.Set, !Main.Settings.EnableWarlockInvocationProgression2024)));
        }

        // The old damage provider remains registered for existing saves, but the replacement
        // owns the optional damage and healing while the revised invocation is enabled.
        FeatureDefinitionAdditionalDamages.AdditionalDamageLifedrinker.requiredProperty = enabled
            ? RestrictedContextRequiredProperty.Weapon
            : RestrictedContextRequiredProperty.None;
    }

    private static void SpendLifedrinkerHitDie(RulesetCharacterHero hero, DieType die)
    {
        hero.spentHitDice.TryGetValue(die, out var spent);

        if (spent >= hero.ClassesHistory.Count(klass => klass.HitDice == die) || !hero.IsMissingHitPoints)
        {
            return;
        }

        var advantage = hero.FeaturesByType<IHealingModificationProvider>()
            .Any(provider => provider.AdvantageOnHitDieSpending)
            ? AdvantageType.Advantage
            : AdvantageType.None;
        var roll = RollDie(die, advantage, out var firstRoll, out var secondRoll);
        var modifier = AttributeDefinitions.ComputeAbilityScoreModifier(
            hero.TryGetAttributeValue(AttributeDefinitions.Constitution));
        var amount = System.Math.Max(1, roll + modifier);
        hero.spentHitDice.TryGetValue(die, out var spentDice);
        hero.spentHitDice[die] = spentDice + 1;
        hero.HitDieRolled?.Invoke(hero, die, amount, advantage, firstRoll, secondRoll, modifier, false);
        hero.ShowDieRoll(die, firstRoll, secondRoll, advantage: advantage,
            title: InvocationLifedrinker.GuiPresentation.Title);
        hero.ReceiveHealing(amount, true, hero.Guid);
    }

    private sealed class CustomBehaviorLifedrinker : IPhysicalAttackBeforeHitConfirmedOnEnemy
    {
        private const string Name = "Lifedrinker2024";
        private readonly FeatureDefinitionPower _pool;
        private readonly Dictionary<FeatureDefinitionPower, string> _choices = [];
        private readonly FeatureDefinitionPower _hitDiePool;
        private readonly Dictionary<FeatureDefinitionPower, DieType> _hitDice = [];

        internal CustomBehaviorLifedrinker()
        {
            _pool = FeatureDefinitionPowerBuilder.Create($"Power{Name}")
                .SetGuiPresentation(InvocationLifedrinker.GuiPresentation.Title, "Invocation/&Lifedrinker2024Description")
                .SetUsesFixed(ActivationTime.NoCost)
                .SetShowCasting(false)
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self).Build())
                .AddToDB();

            foreach (var type in new[] { DamageTypeNecrotic, DamageTypePsychic, DamageTypeRadiant })
            {
                var power = FeatureDefinitionPowerBuilder.Create($"Power{Name}{type}")
                    .SetGuiPresentation($"Rules/&{type}Title", $"Rules/&{type}Description")
                    .SetUsesFixed(ActivationTime.NoCost)
                    .SetShowCasting(false)
                    .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
                    .SetEffectDescription(EffectDescriptionBuilder.Create()
                        .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self).Build())
                    .AddToDB();
                _choices.Add(power, type);
            }

            PowerBundle.RegisterPowerBundle(_pool, false, _choices.Keys);

            _hitDiePool = FeatureDefinitionPowerBuilder.Create($"Power{Name}HitDie")
                .SetGuiPresentation(InvocationLifedrinker.GuiPresentation.Title, "Invocation/&Lifedrinker2024Description")
                .SetUsesFixed(ActivationTime.NoCost)
                .SetShowCasting(false)
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self).Build())
                .AddToDB();

            foreach (var die in new[] { DieType.D6, DieType.D8, DieType.D10, DieType.D12 })
            {
                var power = FeatureDefinitionPowerBuilder.Create($"Power{Name}HitDie{die}")
                    .SetGuiPresentation($"Rules/&Die{die}Title", "Invocation/&Lifedrinker2024Description")
                    .SetUsesFixed(ActivationTime.NoCost)
                    .SetShowCasting(false)
                    .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
                    .SetEffectDescription(EffectDescriptionBuilder.Create()
                        .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self).Build())
                    .AddToDB();
                _hitDice.Add(power, die);
            }

            PowerBundle.RegisterPowerBundle(_hitDiePool, false, _hitDice.Keys);
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
            var character = attacker.RulesetCharacter;

            if (!Main.Settings.EnableWarlockInvocationProgression2024 ||
                !IsPactBladeWeapon(attackMode, null, character) || !attacker.OncePerTurnIsValid(Name))
            {
                yield break;
            }

            var applied = false;
            var choices = _choices.Keys.Select(power => PowerProvider.Get(power, character))
                .Where(power => !character.UsablePowers.Contains(power)).ToArray();
            character.UsablePowers.AddRange(choices);

            try
            {
                yield return attacker.MyReactToSpendPowerBundle(
                    PowerProvider.Get(_pool, character), [attacker], attacker, Name,
                    reactionValidated: ReactionValidated, battleManager: battleManager);
            }
            finally
            {
                foreach (var choice in choices)
                {
                    character.UsablePowers.Remove(choice);
                }
            }

            var hero = character.GetOriginalHero();

            if (applied && hero != null && hero.RemainingHitDiceCount() > 0 &&
                hero.IsMissingHitPoints)
            {
                var availableDice = _hitDice.Where(entry =>
                        hero.ClassesHistory.Count(klass => klass.HitDice == entry.Value) >
                        (hero.spentHitDice.TryGetValue(entry.Value, out var spent) ? spent : 0))
                    .ToArray();
                var hitDiePowers = availableDice.Select(entry => PowerProvider.Get(entry.Key, character))
                    .Where(power => !character.UsablePowers.Contains(power)).ToArray();
                var remainingDice = string.Join(", ", availableDice.Select(entry =>
                    Gui.Localize($"Rules/&Die{entry.Value}Title") + ": " +
                    (hero.ClassesHistory.Count(klass => klass.HitDice == entry.Value) -
                     (hero.spentHitDice.TryGetValue(entry.Value, out var spent) ? spent : 0))));
                character.UsablePowers.AddRange(hitDiePowers);

                try
                {
                    yield return attacker.MyReactToSpendPowerBundle(
                        PowerProvider.Get(_hitDiePool, character), [attacker], attacker, "LifedrinkerHitDie2024",
                        Gui.Format("Reaction/&ReactionSpendPowerBundleLifedrinkerHitDie2024Description", remainingDice),
                        request =>
                        {
                            if (request.ReactionParams.RulesetEffect is RulesetEffectPower effect &&
                                _hitDice.TryGetValue(effect.PowerDefinition, out var die))
                            {
                                SpendLifedrinkerHitDie(hero, die);
                            }
                        }, battleManager: battleManager);
                }
                finally
                {
                    foreach (var power in hitDiePowers)
                    {
                        character.UsablePowers.Remove(power);
                    }
                }
            }

            yield break;

            void ReactionValidated(ReactionRequestSpendBundlePower request)
            {
                if (request.ReactionParams.RulesetEffect is not RulesetEffectPower effect ||
                    !_choices.TryGetValue(effect.PowerDefinition, out var type))
                {
                    return;
                }

                actualEffectForms.Add(EffectFormBuilder.DamageForm(type, 1, DieType.D6));
                attacker.SetSpecialFeatureUses(Name, 1);
                character.LogCharacterUsedFeature(InvocationLifedrinker);
                applied = true;
            }
        }
    }
}
