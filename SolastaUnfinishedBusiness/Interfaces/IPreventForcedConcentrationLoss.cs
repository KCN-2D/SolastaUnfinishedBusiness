namespace SolastaUnfinishedBusiness.Interfaces;

// A policy for involuntary concentration loss. Voluntary release and the spell's
// normal lifecycle remain separate; the caller always permits death, unconsciousness and petrification.
public interface IPreventForcedConcentrationLoss
{
    bool PreventForcedConcentrationLoss(RulesetCharacter character);
}
