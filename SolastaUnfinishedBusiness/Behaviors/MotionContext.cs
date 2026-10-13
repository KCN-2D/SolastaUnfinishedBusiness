using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
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

                var slide = Slide(sides, delta, position, canMoveThroughWalls, size, positioningService);

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

    private static int3 Slide(int3 sides, Vector3 delta, int3 position, bool canMoveThroughWalls,
        RulesetActor.SizeParameters size, IGameLocationPositioningService positioningService)
    {
        if (CheckDirections(sides, position, canMoveThroughWalls, size, positioningService))
        {
            return sides;
        }

        //TODO: should this have a setting, or always allow sliding?
        //Full motion didn't succeed, try sliding
        //try zeroing each direction and pick passing one with shortest zeroed delta

        var d = float.MaxValue;
        var slide = int3.zero;
        int3 tmp;

        //Try zeroing X axis
        if (sides.x != 0 && delta.x < d)
        {
            tmp = new int3(0, sides.y, sides.z);
            if (tmp != int3.zero && CheckDirections(tmp, position, canMoveThroughWalls, size, positioningService))
            {
                d = delta.x;
                slide = tmp;
            }
        }

        //Try zeroing Y axis
        if (sides.y != 0 && delta.y < d)
        {
            tmp = new int3(sides.x, 0, sides.z);
            if (tmp != int3.zero && CheckDirections(tmp, position, canMoveThroughWalls, size, positioningService))
            {
                d = delta.y;
                slide = tmp;
            }
        }

        //Try zeroing Z axis
        if (sides.z != 0 && delta.z < d)
        {
            tmp = new int3(sides.x, sides.y, 0);
            if (tmp != int3.zero && CheckDirections(tmp, position, canMoveThroughWalls, size, positioningService))
            {
                d = delta.z;
                slide = tmp;
            }
        }

        //We either got successful slide, or no way to try double slide with Y=0
        if (slide != int3.zero || sides.x == 0 || sides.z == 0)
        {
            return slide;
        }

        //Last attempt: zero on Y and one of X/Z axis

        //Try zeroing X and Y axis
        if (delta.x < d)
        {
            tmp = new int3(0, 0, sides.z);
            if (CheckDirections(tmp, position, canMoveThroughWalls, size, positioningService))
            {
                d = delta.x;
                slide = tmp;
            }
        }

        //Try zeroing Y and Z axis
        if (delta.z >= d)
        {
            return slide;
        }

        tmp = new int3(sides.x, 0, 0);
        if (!CheckDirections(tmp, position, canMoveThroughWalls, size, positioningService))
        {
            return slide;
        }

#pragma warning disable IDE0059
        // ReSharper disable once RedundantAssignment
        d = delta.z;
#pragma warning restore IDE0059
        slide = tmp;

        return slide;
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
