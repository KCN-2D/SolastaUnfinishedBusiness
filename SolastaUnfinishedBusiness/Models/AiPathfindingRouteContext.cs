using System;
using System.Collections;
using System.Collections.Generic;
using TA;

namespace SolastaUnfinishedBusiness.Models;

// Snapshot native predecessor links before the pathfinder resets its reusable grid.
// Keep only value data; cached routes must never retain mutable native nodes.
internal sealed class AiPathfindingRouteContext
{
    [ThreadStatic] private static AiPathfindingRouteContext _current;
    private readonly IGameLocationPathfindingService _service;
    private readonly GameLocationPathfindingManager _manager;
    private readonly PathfindingLocation _location;
    private bool _captured;
    private bool _ownsQuery;

    internal Dictionary<int3, int3> Parents { get; } = new();
    internal Dictionary<int3, int> Costs { get; } = new();
    internal List<GameLocationCharacterDefinitions.PathStep> Destinations { get; } = new();
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
