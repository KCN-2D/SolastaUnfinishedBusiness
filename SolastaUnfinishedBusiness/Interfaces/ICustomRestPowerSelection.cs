namespace SolastaUnfinishedBusiness.Interfaces;

/// <summary>
/// Lets a rest power collect its parameters before the native rest functor
/// spends the power and dispatches its action.
/// </summary>
internal interface ICustomRestPowerSelection
{
    bool TryOpen(AfterRestActionItem item);
}
