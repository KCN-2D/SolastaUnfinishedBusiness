using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal sealed class AttackAfterMagicEffect(AttackAfterMagicEffect.AttackType attackType, bool firstTargetOnly)
    : IFilterTargetingCharacter
{
    private static readonly ConditionalWeakTable<RulesetAttackMode, RulesetEffect> OriginatingEffects = new();

    internal const string AttackAfterMagicEffectTag = "AttackAfterMagicEffectTag";

    private const RollOutcome MinOutcomeToAttack = RollOutcome.Success;
    private const RollOutcome MinSaveOutcomeToAttack = RollOutcome.Failure;

    internal static readonly AttackAfterMagicEffect MarkerAnyWeaponAttack =
        new(AttackType.Melee | AttackType.Ranged | AttackType.Thrown, true);

    internal static readonly AttackAfterMagicEffect MarkerMeleeWeaponAttack = new(AttackType.Melee, true);
    internal static readonly AttackAfterMagicEffect MarkerRangedWeaponAttack = new(AttackType.Ranged, false);

    internal readonly bool AllowMelee = attackType.HasFlag(AttackType.Melee);
    internal readonly bool AllowRanged = attackType.HasFlag(AttackType.Ranged);
    internal readonly bool AllowThrown = attackType.HasFlag(AttackType.Thrown);

    public bool EnforceFullSelection => false;

    public bool IsValid(CursorLocationSelectTarget __instance, GameLocationCharacter target)
    {
        if (!firstTargetOnly && __instance.SelectionService.SelectedTargets.Count != 0)
        {
            return true;
        }

        if (CanAttack(__instance.ActionParams.ActingCharacter, target, AllowMelee, AllowRanged, AllowThrown))
        {
            return true;
        }

        __instance.actionModifier.FailureFlags.Add(Gui.Localize("Failure/&CannotAttackTarget"));

        return false;
    }

    internal static bool CanAttack(
        [NotNull] GameLocationCharacter attacker,
        GameLocationCharacter defender,
        bool allowMelee,
        bool allowRanged,
        bool allowThrown,
        RulesetAttackMode attackMode = null)
    {
        attackMode ??= attacker.FindActionAttackMode(ActionDefinitions.Id.AttackMain);
        return CanAttack(attacker, defender, allowMelee, allowRanged, allowThrown, attackMode, out _);
    }

    private static bool CanAttack(
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        bool allowMelee,
        bool allowRanged,
        bool allowThrown,
        RulesetAttackMode attackMode,
        out bool rangedAttack)
    {
        rangedAttack = attackMode?.Ranged ?? false;
        if (attackMode == null)
        {
            return false;
        }

        var battleService = ServiceRepository.GetService<IGameLocationBattleService>();
        var attackModifier = new ActionModifier();
        var evalParams = new BattleDefinitions.AttackEvaluationParams();
        var attackerPosition = attacker.LocationPosition;
        var defenderPosition = defender.LocationPosition;
        var canAttack = false;

        switch (attackMode.Ranged)
        {
            case false when allowMelee:
            {
                evalParams.FillForPhysicalReachAttack(
                    attacker, attackerPosition, attackMode, defender, defenderPosition, attackModifier);

                var reach = Main.Settings.AllowBladeCantripsToUseReach ? attackMode.ReachRange : 1;

                canAttack = battleService.CanAttack(evalParams) && attacker.IsWithinRange(defender, reach);

                if (!canAttack && allowThrown && attackMode.Thrown)
                {
                    var wasRanged = attackMode.Ranged;
                    try
                    {
                        attackMode.Ranged = true;
                        evalParams.FillForPhysicalRangeAttack(
                            attacker, attackerPosition, attackMode, defender, defenderPosition, attackModifier);

                        canAttack = battleService.CanAttack(evalParams);
                        rangedAttack = canAttack;
                    }
                    finally
                    {
                        // Target previews also use the character's reusable attack mode.
                        attackMode.Ranged = wasRanged;
                    }
                }

                break;
            }
            case true when allowRanged:
                evalParams.FillForPhysicalRangeAttack(
                    attacker, attackerPosition, attackMode, defender, defenderPosition, attackModifier);

                canAttack = battleService.CanAttack(evalParams);
                break;
        }

        return canAttack;
    }

    internal List<CharacterActionParams> PerformAttackAfterUse(CharacterActionMagicEffect actionMagicEffect)
    {
        var attacks = new List<CharacterActionParams>();
        var actionParams = actionMagicEffect?.ActionParams;

        if (actionParams == null)
        {
            return attacks;
        }

        //Spell got countered or it failed
        if (actionMagicEffect.Countered || actionMagicEffect.ExecutionFailed)
        {
            return attacks;
        }

        //Attack outcome is worse than required
        if (actionMagicEffect.AttackRollOutcome > MinOutcomeToAttack)
        {
            return attacks;
        }

        //Target rolled saving throw and got better result
        if (actionMagicEffect.RolledSaveThrow && actionMagicEffect.SaveOutcome < MinSaveOutcomeToAttack)
        {
            return attacks;
        }

        var caster = actionParams.ActingCharacter;
        var originalAttackMode = caster.FindActionAttackMode(ActionDefinitions.Id.AttackMain);

        if (originalAttackMode == null)
        {
            return attacks;
        }

        var targets = actionParams.IsReactionEffect
            ? actionParams.TargetCharacters
            : actionParams.TargetCharacters
                .Where(t => CanAttack(caster, t, AllowMelee, AllowRanged, AllowThrown, originalAttackMode))
                .ToList();

        if (targets.Count == 0)
        {
            return attacks;
        }

        var maxTargets = firstTargetOnly ? 1 : targets.Count;

        for (var i = 0; i < maxTargets; i++)
        {
            //get copy to be sure we don't break existing mode
            var attackMode = RulesetAttackMode.AttackModesPool.Get();
            attackMode.Copy(originalAttackMode);
            CanAttack(caster, targets[i], AllowMelee, AllowRanged, AllowThrown, attackMode, out var rangedAttack);
            attackMode.Ranged = rangedAttack;

            //set action type to be same as the one used for the magic effect
            attackMode.ActionType = actionMagicEffect.ActionType;

            //mark this attack for proper integration with polearm, and follow-up strike
            if (!actionParams.ActingCharacter.RulesetCharacter.HasSubFeatureOfType<IAttackReplaceWithCantrip>())
            {
                attackMode.AddAttackTagAsNeeded(AttackAfterMagicEffectTag);
            }

            //handle interaction with Potent Spell Caster feat and blade cantrips
            ClassFeats.CustomBehaviorFeatPotentSpellcaster.HandleBladeCantrips(caster, actionMagicEffect, attackMode);

            OriginatingEffects.Remove(attackMode);

            if (actionParams.RulesetEffect != null)
            {
                OriginatingEffects.Add(attackMode, actionParams.RulesetEffect);

                foreach (var modifier in actionParams.RulesetEffect.SourceDefinition
                             .GetAllSubFeaturesOfType<IModifyAttackAfterMagicEffect>())
                {
                    modifier.ModifyAttack(actionParams.RulesetEffect, caster.RulesetCharacter, attackMode);
                }
            }

            // always use free attack
            var attackActionParams =
                new CharacterActionParams(caster, ActionDefinitions.Id.AttackFree) { AttackMode = attackMode };

            attackActionParams.TargetCharacters.Add(targets[i]);
            attackActionParams.ActionModifiers.Add(new ActionModifier());
            attacks.Add(attackActionParams);
        }

        return attacks;
    }

    internal static RulesetEffect ConsumeOriginatingEffect(RulesetAttackMode attackMode)
    {
        if (attackMode == null || !OriginatingEffects.TryGetValue(attackMode, out var rulesetEffect))
        {
            return null;
        }

        OriginatingEffects.Remove(attackMode);

        return rulesetEffect;
    }

    [Flags]
    internal enum AttackType
    {
        Melee = 1,
        Ranged = 2,
        Thrown = 4
    }
}
