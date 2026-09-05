namespace SolastaUnfinishedBusiness.Interfaces;

// A feature owns the qualifying spells and the meaning of failure.
// Payment tracking only preserves the resource actually spent by that cast.
internal interface IRefundSpellSlotOnFailure
{
    bool IsEligible(RulesetCharacter character, RulesetEffectSpell spell);

    bool ShouldRefundSpellSlot(CharacterActionMagicEffect action);
}
