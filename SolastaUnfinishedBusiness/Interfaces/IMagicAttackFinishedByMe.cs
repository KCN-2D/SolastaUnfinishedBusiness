using JetBrains.Annotations;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Interfaces;

// Invoked once per resolved spell attack roll, including each beam of a multi-attack spell.
public interface IMagicAttackFinishedByMe
{
    [UsedImplicitly]
    void OnMagicAttackFinishedByMe(
        GameLocationCharacter attacker,
        GameLocationCharacter defender,
        RollOutcome outcome);
}
