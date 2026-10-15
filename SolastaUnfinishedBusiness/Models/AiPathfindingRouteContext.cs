using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TA;

namespace SolastaUnfinishedBusiness.Models;

// Snapshot native predecessor links before the pathfinder resets its reusable grid.
// Keep only value data; cached routes must never retain mutable native nodes.
internal sealed class AiPathfindingRouteContext
{
    [ThreadStatic] private static AiPathfindingRouteContext _current;
    private static readonly ConditionalWeakTable<PathfindingJob, GameLocationCharacter> MoveJobs = new();
    private readonly IGameLocationPathfindingService _service;
    private readonly GameLocationPathfindingManager _manager;
    private readonly PathfindingLocation _location;
    private bool _captured;
    private bool _ownsQuery;
    private GameLocationCharacter _character;
    private int3 _start;
    private bool _isMoveScope;

    internal Dictionary<int3, int3> Parents { get; } = new();
    internal Dictionary<int3, int> Costs { get; } = new();
    internal List<GameLocationCharacterDefinitions.PathStep> Destinations { get; } = new();
    internal HashSet<int3> HazardPositions { get; } = new();
    internal bool Completed { get; private set; }
    internal bool IsCurrent => ReferenceEquals(ServiceRepository.GetService<IGameLocationPathfindingService>(), _service) &&
                               ReferenceEquals(_manager?.PathfindingLocation_ForValidDestinations, _location);
    internal bool CanStart => _location != null && IsCurrent &&
                              !_location.isComputingValidDestinations && _location.HasFinishedComputing;

    internal AiPathfindingRouteContext(IGameLocationPathfindingService service)
    {
        _service = service;
        _manager = service as GameLocationPathfindingManager;
        _location = _manager?.PathfindingLocation_ForValidDestinations;
    }

    internal IEnumerator Compute(GameLocationCharacter character, int3 start, int budget, Func<bool> isValid)
    {
        if (!CanStart || !isValid())
        {
            yield break;
        }

        _character = character;
        _start = start;
        var iterators = new Stack<IEnumerator>();
        iterators.Push(_service.ComputeValidDestinationsAsync(character, start, budget, 0, true, true));
        try
        {
            while (iterators.Count > 0)
            {
                if (!IsCurrent || !isValid())
                {
                    yield break;
                }

                var iterator = iterators.Peek();
                bool hasNext;
                object current;
                var previous = _current;
                using (FreeJumpContext.SuppressAiPathfindingFreeJump(character))
                {
                    try
                    {
                        _current = this;
                        CombatAiContext.ClearAiHazardCache(_character);
                        hasNext = iterator.MoveNext();
                        current = hasNext ? iterator.Current : null;
                    }
                    finally
                    {
                        _current = previous;
                    }
                }

                if (!hasNext)
                {
                    iterators.Pop();
                    (iterator as IDisposable)?.Dispose();
                }
                else if (current is IEnumerator nested)
                {
                    // The service yields its native iterator. Advance that leaf under the
                    // query and free-jump scopes too; neither may span Unity frames.
                    iterators.Push(nested);
                }
                else
                {
                    yield return current;
                }
            }

            // The caller can resume on a later frame, after another native query has
            // reused ValidDestinations. Snapshot its value records before returning.
            Destinations.AddRange(_location.ValidDestinations);
            Completed = _ownsQuery && !_location.isComputingValidDestinations;
        }
        finally
        {
            try
            {
                foreach (var iterator in iterators)
                {
                    (iterator as IDisposable)?.Dispose();
                }
            }
            finally
            {
                try
                {
                    if (!Completed)
                    {
                        AbortOwnedQuery();
                    }
                }
                finally
                {
                    _ownsQuery = false;
                }
            }
        }
    }

    internal static void FilterNeighbours(PathfindingNode from, List<PathfindingNeighbour> neighbours)
    {
        var scope = _current;
        if (scope?._character == null || neighbours == null ||
            from is not PathfindingGridNode { Grid.Character: { } character } || character != scope._character)
        {
            return;
        }

        if (!scope._isMoveScope)
        {
            scope.HazardPositions.Add(from.AbsolutePosition);
        }
        neighbours.RemoveAll(neighbour =>
        {
            if (!scope._isMoveScope)
            {
                scope.HazardPositions.Add(neighbour.node.AbsolutePosition);
            }
            return !CombatAiContext.IsSafeAiHazardTraversal(scope._character, scope._start, from, neighbour.node);
        });
    }

    internal static bool IsSafeWarmedPath(GameLocationCharacter character,
        List<GameLocationCharacterDefinitions.PathStep> path, int3 startPosition) =>
        _current?._isMoveScope != true || _current._character != character || character == null ||
        CombatAiContext.IsSafeAiHazardPath(character, startPosition, path);

    internal static void CaptureMoveJob(PathfindingJob job)
    {
        var scope = _current;
        if (scope?._isMoveScope != true || scope._character != job.Character ||
            !CombatAiContext.ShouldAvoidAiHazards(job.Character))
        {
            return;
        }

        MoveJobs.Remove(job);
        MoveJobs.Add(job, scope._character);
    }

    internal static IDisposable BeginMoveJobScope(PathfindingJob job)
    {
        if (!MoveJobs.TryGetValue(job, out var character))
        {
            return null;
        }

        if (!CombatAiContext.ShouldAvoidAiHazards(job.Character))
        {
            return null;
        }

        var scope = new AiPathfindingRouteContext(ServiceRepository.GetService<IGameLocationPathfindingService>())
        {
            _character = character,
            _isMoveScope = true
        };
        return new MoveJobScope(scope, job.Origin);
    }

    internal static void ReleaseMoveJob(PathfindingJob job) => MoveJobs.Remove(job);

    internal static Exception EndMoveJobScope(PathfindingJob job, IDisposable scope, Exception exception)
    {
        try
        {
            scope?.Dispose();
        }
        finally
        {
            // Failed jobs remain in the native queue and can restart without another
            // constructor. Retain only actor ownership until native completion or abort.
            if (job.Complete)
            {
                MoveJobs.Remove(job);
            }
        }

        return exception;
    }

    private sealed class MoveJobScope : IDisposable
    {
        private readonly AiPathfindingRouteContext _previous;
        private bool _disposed;

        internal MoveJobScope(AiPathfindingRouteContext scope, int3 start)
        {
            _previous = _current;
            scope._start = start;
            _current = scope;
            CombatAiContext.ClearAiHazardCache(scope._character);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current = _previous;
        }
    }

    internal static IEnumerator WithMovePathfindingScope(IEnumerator values, CharacterActionMove action)
    {
        if (!CombatAiContext.IsAiHazardMove(action))
        {
            using var iterator = values as IDisposable;
            while (values.MoveNext())
            {
                yield return values.Current;
            }

            yield break;
        }

        var scope = new AiPathfindingRouteContext(ServiceRepository.GetService<IGameLocationPathfindingService>())
        {
            _character = action.ActingCharacter,
            _start = action.ActingCharacter.LocationPosition,
            _isMoveScope = true
        };
        CombatAiContext.ClearAiHazardCache(scope._character);
        var iterators = new Stack<IEnumerator>();
        iterators.Push(values);
        try
        {
            while (iterators.Count > 0)
            {
                var iterator = iterators.Peek();
                bool hasNext;
                object current;
                var previous = _current;
                try
                {
                    // Replanning after a reaction starts at the actor's new position.
                    // The scope belongs to this leaf MoveNext, never to a Unity frame.
                    scope._start = scope._character.LocationPosition;
                    _current = scope;
                    CombatAiContext.ClearAiHazardCache(scope._character);
                    hasNext = iterator.MoveNext();
                    current = hasNext ? iterator.Current : null;
                }
                finally
                {
                    _current = previous;
                }

                if (!hasNext)
                {
                    iterators.Pop();
                    (iterator as IDisposable)?.Dispose();
                }
                else if (current is IEnumerator nested)
                {
                    iterators.Push(nested);
                }
                else
                {
                    yield return current;
                }
            }
        }
        finally
        {
            foreach (var iterator in iterators)
            {
                (iterator as IDisposable)?.Dispose();
            }
        }
    }

    private void AbortOwnedQuery()
    {
        if (!_ownsQuery || !_location.isComputingValidDestinations)
        {
            return;
        }

        // Native iterator disposal leaves its busy flag and reusable nodes intact.
        // Mirror its completion cleanup only on the location this query acquired;
        // a replacement manager/location and a rejected busy query remain untouched.
        foreach (var sector in _location.SectorsById.Values)
        {
            sector.ResetSector(false);
        }

        _location.isComputingValidDestinations = false;
        _location.slicer.Stop();
        _location.ValidDestinations.Clear();
    }

    internal static void MarkQueryStarted(PathfindingLocation location)
    {
        if (_current != null && ReferenceEquals(_current._location, location))
        {
            _current._ownsQuery = true;
        }
    }

    internal static void CaptureCompletedRoutes(PathfindingSector sector)
    {
        var scope = _current;
        if (scope == null || scope._captured || scope._location == null ||
            !scope._location.SectorsById.ContainsValue(sector))
        {
            return;
        }

        // The completed-query hook can still run when no starting node could be explored.
        if (scope._location.closedSet.Count == 0)
        {
            return;
        }

        scope._captured = true;
        foreach (var node in scope._location.closedSet)
        {
            if (node.Parent != null && node.Parent != node)
            {
                scope.Parents[node.AbsolutePosition] = node.Parent.AbsolutePosition;
                scope.Costs[node.AbsolutePosition] = node.MCost;
            }
        }
    }
}
