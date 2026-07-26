using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IPreventRestRecovery
{
    bool PreventRestRecovery(RulesetCharacter character, RestType restType);
}
