using System;

namespace SolastaUnfinishedBusiness.Behaviors;

public class ModifyAttunementLimit(int value, int minimum = 0, Func<bool> isEnabled = null)
{
    public int Value { get; } = value;

    public int Minimum { get; } = minimum;

    public bool IsEnabled => isEnabled?.Invoke() ?? true;
}
