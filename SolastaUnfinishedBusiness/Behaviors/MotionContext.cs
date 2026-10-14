using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using TA;
using UnityEngine;
using static ActionDefinitions;
using static CellFlags;

namespace SolastaUnfinishedBusiness.Behaviors;

internal static class MotionContext
{
    //If this is used as pull distance, it will pull not next to caster, but on top of caster
    public const int PullOntoCaster = -100;
    private const double Epsilon = 0.015;
    private const double StepHigh = 0.4 - Epsilon;
    private const double StepLow = 0.1 - Epsilon;

    internal sealed class TeleportMovementCost : IValidatePowerUseWithFailure, IValidateMagicEffectBeforeSpend
    {
        private readonly ConditionalWeakTable<RulesetEffect, TeleportUse> _uses = new();

        internal TeleportMovementCost(int cost, bool oncePerTurn = false)
        {
            if (cost <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cost));
            }

            Cost = cost;
            OncePerTurn = oncePerTurn;
        }

        internal int Cost { get; }
        internal bool OncePerTurn { get; }

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
        {
            return CanUsePower(character, power, out _);
        }

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power, out string failure)
        {
            if (character == null || character.IsDeadOrDyingOrUnconscious || character.IsIncapacitated || power == null)
            {
                failure = "Failure/&FailureFlagCannotUseAnyPower";
                return false;
            }

            var actor = GameLocationCharacter.GetFromActor(character);
            if (actor == null)
            {
                failure = "Failure/&FailureFlagCannotUseAnyPower";
                return false;
            }

            var inBattle = IsBattleInProgress();
            if (inBattle && !actor.IsMyTurn())
            {
                failure = "Failure/&FailureFlagMovementOnlyOnOwnTurn";
                return false;
            }

            if (inBattle && OncePerTurn && !actor.OncePerTurnIsValid(power.Name))
            {
                failure = "Failure/&FailureFlagOncePerTurnOnly";
                return false;
            }

            // Exploration does not retain a tactical movement budget between actions.
            if ((inBattle ? actor.RemainingTacticalMoves : actor.MaxTacticalMoves) < Cost)
            {
                failure = "Failure/&FailureFlagNotEnoughMove";
                return false;
            }

            failure = string.Empty;
            return true;
        }

        public bool IsValid(
            CharacterActionMagicEffect action,
            GameLocationCharacter actingCharacter,
            IReadOnlyList<GameLocationCharacter> targets,
            out string failure)
        {
            var effect = action?.ActionParams.RulesetEffect;
            if (effect?.SourceDefinition is not FeatureDefinitionPower power)
            {
                failure = "Failure/&FailureFlagCannotUseAnyPower";
                return false;
            }

            if (!CanUsePower(actingCharacter?.RulesetCharacter, power, out failure))
            {
                return false;
            }

            var description = effect.EffectDescription;
            var positions = action.ActionParams.Positions;
            var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
            var visibility = ServiceRepository.GetService<IGameLocationVisibilityService>();
            if (description.RangeType != RuleDefinitions.RangeType.Distance ||
                description.TargetType != RuleDefinitions.TargetType.Position || positions.Count != 1 ||
                positioning == null || visibility == null)
            {
                failure = "Failure/&FailureInvalidPosition";
                return false;
            }

            var destination = positions[0];
            var valid = destination != actingCharacter.LocationPosition &&
                        int3.Distance(actingCharacter.LocationPosition, destination) <= description.RangeParameter &&
                        positioning.CanPlaceCharacter(actingCharacter, destination, CellHelpers.PlacementMode.Station) &&
                        positioning.CanCharacterStayAtPosition_Floor(actingCharacter, destination,
                            onlyCheckCellsWithRealGround: !Main.Settings.EnableTeleportAllowsMidAirPositions) &&
                        visibility.MyIsCellPerceivedByCharacter(destination, actingCharacter,
                            requireLineOfSight: true, requireSight: true);
            failure = valid ? string.Empty : "Failure/&FailureInvalidPosition";
            return valid;
        }

        internal static PendingTeleport Capture(
            EffectForm effectForm, RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            var effect = formsParams.activeEffect;
            if (effectForm.MotionForm?.Type != MotionForm.MotionType.TeleportToDestination ||
                effect?.SourceDefinition.GetFirstSubFeatureOfType<TeleportMovementCost>() is not { } cost)
            {
                return null;
            }

            var actor = GameLocationCharacter.GetFromActor(formsParams.sourceCharacter);
            // This movement cost belongs to a personal teleport, not displacement of another creature.
            if (actor == null || formsParams.targetCharacter != null &&
                formsParams.targetCharacter != formsParams.sourceCharacter)
            {
                return null;
            }

            return new PendingTeleport(cost, cost._uses.GetValue(effect, _ => new TeleportUse()), actor,
                actor.LocationPosition, formsParams.position, effect.SourceDefinition.Name, IsBattleInProgress());
        }

        private static bool IsBattleInProgress()
        {
            return ServiceRepository.GetService<IGameLocationBattleService>()?.IsBattleInProgress ?? Gui.Battle != null;
        }

        internal sealed class TeleportUse
        {
            internal bool Committed;
        }

        internal sealed class PendingTeleport
        {
            private readonly TeleportMovementCost _cost;
            private readonly TeleportUse _use;
            private readonly GameLocationCharacter _actor;
            private readonly int3 _source;
            private readonly int3 _destination;
            private readonly string _powerName;
            private readonly bool _inBattle;

            internal PendingTeleport(TeleportMovementCost cost, TeleportUse use, GameLocationCharacter actor,
                int3 source, int3 destination, string powerName, bool inBattle)
            {
                _cost = cost;
                _use = use;
                _actor = actor;
                _source = source;
                _destination = destination;
                _powerName = powerName;
                _inBattle = inBattle;
            }

            internal void Complete()
            {
                if (_use.Committed || _source == _destination || _actor.LocationPosition != _destination)
                {
                    return;
                }

                _use.Committed = true;
                if (!_inBattle || !IsBattleInProgress())
                {
                    return;
                }

                if (_cost.OncePerTurn)
                {
                    _actor.UsedSpecialFeatures.TryAdd(_powerName, 1);
                }

                // The native setter sends UsedTacticalMovesChanged once and refreshes its consumers.
                _actor.UsedTacticalMoves += _cost.Cost;
            }
        }
    }

    internal static bool IsAttackPositionStable(GameLocationCharacter character)
    {
        // Grid coordinates are committed by FinishMoveTo/FinishFallingTo, after the motion.
        // A charge can keep IsCharging set after arrival, so it is not a pending position.
        return character is { MoveStepInProgress: false, Falling: false, Pushed: false,
            IgnoreForPlacementDuringPushAnimation: false, WillBePushedByMagicalEffect: false };
    }

    internal static bool CanEvaluateAttackPositions(BattleDefinitions.AttackEvaluationParams attackParams)
    {
        var attackMode = attackParams.attackMode;

        // Triggered attacks retain their native evaluation at the reaction boundary.
        return attackMode == null || attackParams.opportunityAttack || attackParams.readiedAttack ||
               attackMode.ActionType == ActionType.Reaction || attackMode.ReturnProjectileOnly ||
               (IsAttackPositionStable(attackParams.attacker) && IsAttackPositionStable(attackParams.defender));
    }

    internal static bool HasAttackPositionFailure(ActionModifier attackModifier)
    {
        return attackModifier?.FailureFlags.Any(failure => failure is
            "Failure/&FailureFlagNoReachForTargetDescription" or
            "Failure/&FailureFlagTargetOutOfRange" or
            "Failure/&FailureFlagTargetOutOfRangeStrength") == true;
    }

    internal static bool IsWithinThrowingStrengthRange(BattleDefinitions.AttackEvaluationParams attackParams)
    {
        return !Main.Settings.ModifyThrowingRulesForStrength || attackParams.attackMode is not { Thrown: true } ||
               (int)int3.Distance(attackParams.attacker.LocationPosition, attackParams.defender.LocationPosition) <=
               attackParams.attacker.RulesetCharacter.GetAttribute(AttributeDefinitions.Strength).CurrentValue;
    }

    internal static bool CanAttackFromCurrentPositions(
        IGameLocationBattleService battleService,
        BattleDefinitions.AttackEvaluationParams attackParams)
    {
        if (!IsAttackPositionStable(attackParams.attacker) || !IsAttackPositionStable(attackParams.defender) ||
            !IsWithinThrowingStrengthRange(attackParams))
        {
            return false;
        }

        attackParams.attackPosition = attackParams.attacker.LocationPosition;
        attackParams.defensePosition = attackParams.defender.LocationPosition;

        if (!attackParams.attackMode.Ranged)
        {
            return battleService.IsWithinXCells(attackParams.attacker, attackParams.attackPosition,
                attackParams.defender, attackParams.defensePosition, (int)attackParams.maxRange);
        }

        // Reuse the native evaluation's range and the shared size-aware distance, without
        // rerunning initiation callbacks or appending attack modifiers after the roll.
        attackParams.ComputeDistance();
        return attackParams.distance <= attackParams.maxRange + 0.01f;
    }

    public static bool ComputePushDestination(
        Vector3 sourceCenter,
        GameLocationCharacter target,
        int distance,
        bool reverse,
        IGameLocationPositioningService positioningService,
        ref int3 destination,
        ref Vector3 direction)
    {
        var targetCenter = new Vector3();
        positioningService.ComputeGravityCenterPosition(target, ref targetCenter);
        direction = targetCenter - sourceCenter;
        if (reverse)
        {
            direction = -direction;
            var b = (int)direction.ChessboardLength() - 1;
            if (distance == PullOntoCaster) { b += 1; } //if we have magic number - pull right on top of source

            distance = distance <= 0 ? b : Mathf.Min(distance, b);
        }
        else
        {
            distance = Mathf.Max(1, distance);
        }

        direction = direction.normalized;
        destination = target.LocationPosition;
        var position = target.LocationPosition;
        var delta = new Vector3();
        var result = false;

        for (var index = 0; index < distance; ++index)
        {
            delta += direction;

            var sides = Step(delta, StepHigh);
            if (sides == int3.zero)
            {
                sides = Step(delta, StepLow);
            }

            var flag = sides != int3.zero;

            if (flag)
            {
                var canMoveThroughWalls = target.CanMoveInSituation(RulesetCharacter.MotionRange.ThroughWalls);
                var size = target.SizeParameters;

                var slide = Slide(sides, position, canMoveThroughWalls, size, positioningService);

                flag = slide != int3.zero;
                position += slide;

                //zero delta based on full motion
                delta.x = sides.x == 0 ? delta.x : 0;
                delta.y = sides.y == 0 ? delta.y : 0;
                delta.z = sides.z == 0 ? delta.z : 0;
            }

            if (flag && positioningService.CanPlaceCharacter(target, position, CellHelpers.PlacementMode.Station))
            {
                destination = position;
                result = true;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    public static void ProneTarget(GameLocationCharacter target)
    {
        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        actionService.StopCharacterActions(target, CharacterAction.InterruptionType.ForcedMovement);
        actionService.ExecuteAction(new CharacterActionParams(target, Id.DropProne)
        {
            CanBeCancelled = false,
            Positions = { target.LocationPosition }
        }, null, true);
    }

    private static int3 Slide(int3 sides, int3 position, bool canMoveThroughWalls,
        RulesetActor.SizeParameters size, IGameLocationPositioningService positioningService)
    {
        if (CheckDirections(sides, position, canMoveThroughWalls, size, positioningService))
        {
            return sides;
        }

        // Every candidate checks the same cell surfaces. If the full move fails,
        // at most one single-axis omission can pass: two would cover all original sides.
        // Try those before the existing horizontal-only fallbacks.
        int3 slide;

        //Try zeroing X axis
        if (sides.x != 0)
        {
            slide = new int3(0, sides.y, sides.z);
            if (slide != int3.zero && CheckDirections(slide, position, canMoveThroughWalls, size, positioningService))
            {
                return slide;
            }
        }

        //Try zeroing Y axis
        if (sides.y != 0)
        {
            slide = new int3(sides.x, 0, sides.z);
            if (slide != int3.zero && CheckDirections(slide, position, canMoveThroughWalls, size, positioningService))
            {
                return slide;
            }
        }

        //Try zeroing Z axis
        if (sides.z != 0)
        {
            slide = new int3(sides.x, sides.y, 0);
            if (slide != int3.zero && CheckDirections(slide, position, canMoveThroughWalls, size, positioningService))
            {
                return slide;
            }
        }

        // No way to try double slide with Y=0.
        if (sides.x == 0 || sides.z == 0)
        {
            return int3.zero;
        }

        //Last attempt: zero on Y and one of X/Z axis

        //Try zeroing X and Y axis
        slide = new int3(0, 0, sides.z);
        if (CheckDirections(slide, position, canMoveThroughWalls, size, positioningService))
        {
            return slide;
        }

        //Try zeroing Y and Z axis
        slide = new int3(sides.x, 0, 0);
        return CheckDirections(slide, position, canMoveThroughWalls, size, positioningService) ? slide : int3.zero;
    }

    private static bool CheckDirections(int3 sides, int3 position, bool canMoveThroughWalls,
        RulesetActor.SizeParameters size, IGameLocationPositioningService positioning)
    {
        var surfaceSides = DirectionToAllSurfaceSides(sides);

        return AllSides
            .Where(side => (side & surfaceSides) != Side.None)
            .All(side => positioning.CanCharacterMoveThroughSide(size, position, side, canMoveThroughWalls));
    }

    private static int3 Step(Vector3 delta, double tolerance)
    {
        return new int3(
            (int)Mathf.Sign(delta.x) * (Mathf.Abs(delta.x) < tolerance ? 0 : 1),
            (int)Mathf.Sign(delta.y) * (Mathf.Abs(delta.y) < tolerance ? 0 : 1),
            (int)Mathf.Sign(delta.z) * (Mathf.Abs(delta.z) < tolerance ? 0 : 1)
        );
    }
}
