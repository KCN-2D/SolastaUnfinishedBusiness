using System;
using System.Diagnostics;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Main-thread cooperative scheduling shared by sequential and nested AI planners.
// This is a frame window, not a CPU-time metric or a hard deadline for native work.
internal static class AiWorkBudget
{
    private static readonly long BudgetTicks = Math.Max(1, Stopwatch.Frequency * 2 / 1000);
    private static int _budgetFrame = -1;
    private static long _frameStarted;

    internal static bool ShouldYield()
    {
        return Stopwatch.GetTimestamp() - _frameStarted >= BudgetTicks;
    }

    internal static void Resume()
    {
        var frame = Time.frameCount;
        if (_budgetFrame == frame)
        {
            return;
        }

        // Call on entry and after resuming from a yield, before evaluating a candidate.
        // Only an actual new Unity frame grants more work; nested/synchronous callers
        // cannot reset the window at each new stage or yielded item.
        _budgetFrame = frame;
        _frameStarted = Stopwatch.GetTimestamp();
    }

    internal static void Reset()
    {
        _budgetFrame = -1;
        _frameStarted = 0;
    }
}
