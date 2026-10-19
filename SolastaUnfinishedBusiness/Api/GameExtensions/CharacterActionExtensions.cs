using SolastaUnfinishedBusiness.Behaviors;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class CharacterActionExtensions
{
    internal const string ShouldKeepConcentration = "ActionShouldKeepConcentration";

    internal static bool CompletesAction(this CharacterAction action)
    {
        if (action?.ActingCharacter == null || action.Aborted)
        {
            return false;
        }

        var actor = action.ActingCharacter;
        var replacesAttack = action.ActionId == Id.CastMain &&
                             action.ActionParams.RulesetEffect is RulesetEffectSpell { SpellDefinition.SpellLevel: 0 } &&
                             actor.RulesetCharacter.HasSubFeatureOfType<IAttackReplaceWithCantrip>();
        if (Gui.Battle == null || action.ActionType != ActionType.Main ||
            action.ActionId != Id.AttackMain && !replacesAttack)
        {
            return true;
        }

        // The finish hook precedes native action accounting. An AttackMain is
        // one strike; the active performance's limit completes the Attack action.
        var rank = actor.CurrentActionRankByType[ActionType.Main];
        var performances = actor.ActionPerformancesByType[ActionType.Main];
        var attacks = action.ActionParams.AttackMode?.AttacksNumber ?? actor.GetAllowedMainAttacksForRank(rank);
        if (rank >= 0 && rank < performances.Count && performances[rank].MaxAttacksNumber >= 0)
        {
            attacks = System.Math.Min(attacks, performances[rank].MaxAttacksNumber);
        }

        return actor.UsedMainAttacks + 1 >= attacks;
    }

    internal static string FormatTitle(this CharacterAction action)
    {
        var magicEffect = action.actionParams.RulesetEffect;

        return magicEffect == null
            ? Gui.Localize("Action/&AttackTitle")
            : magicEffect.SourceDefinition.FormatTitle();
    }

    internal static bool ShouldKeepConcentrationOnPowerUseOrSpend(RulesetCharacter character)
    {
        var glc = GameLocationCharacter.GetFromActor(character);

        return glc != null && glc.UsedSpecialFeatures.ContainsKey(ShouldKeepConcentration);
    }

#if false
    internal static int GetSaveDC(this CharacterAction action)
    {
        var actionParams = action.actionParams;
        var magicEffect = actionParams.RulesetEffect;
        var saveDc = 0;

        if (magicEffect == null)
        {
            var attack = actionParams.AttackMode;
            var effect = attack.effectDescription;

            switch (effect.DifficultyClassComputation)
            {
                case EffectDifficultyClassComputation.FixedValue:
                    saveDc = effect.FixedSavingThrowDifficultyClass;
                    break;

                case EffectDifficultyClassComputation.SpellCastingFeature:
                {
                    var repertoire = action.ActingCharacter.RulesetCharacter?.GetClassSpellRepertoire();
                    if (repertoire != null)
                    {
                        saveDc = repertoire.SaveDC;
                    }

                    break;
                }
                //TODO: implement missing computation methods (like Ki and Breath Weapon)
                case EffectDifficultyClassComputation.AbilityScoreAndProficiency:
                    break;
                case EffectDifficultyClassComputation.Ki:
                    break;
                case EffectDifficultyClassComputation.BreathWeapon:
                    break;
                case EffectDifficultyClassComputation.CustomAbilityModifierAndProficiency:
                    break;
                default:
                    throw new ArgumentException("effect.DifficultyClassComputation");
            }
        }
        else
        {
            saveDc = magicEffect.SaveDC;
        }

        return saveDc;
    }
#endif
}
