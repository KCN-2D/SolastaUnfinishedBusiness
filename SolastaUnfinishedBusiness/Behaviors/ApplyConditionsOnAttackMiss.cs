using System;

namespace SolastaUnfinishedBusiness.Behaviors;

// Some attacks establish their non-damaging conditions even when the attack misses.
internal sealed class ApplyConditionsOnAttackMiss(Func<bool> isEnabled)
{
    internal bool IsEnabled => isEnabled();
}
