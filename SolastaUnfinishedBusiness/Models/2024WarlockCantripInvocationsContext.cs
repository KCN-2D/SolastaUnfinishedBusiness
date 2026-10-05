using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.InvocationDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly List<InvocationDefinition> CantripInvocations2024 = [];
    private static readonly Dictionary<InvocationDefinition, FeatureDefinition> CantripInvocationFeatures2014 = [];
    private static readonly Dictionary<InvocationDefinition, string> CantripInvocationDescriptions2014 = [];
    private static readonly Dictionary<InvocationDefinition, FeatureDefinition> CantripInvocationFeatures2024 = [];

    private enum CantripInvocationKind { Agonizing, Spear, Repelling }

    private static void SwitchWarlockCantripInvocations()
    {
        if (CantripInvocationFeatures2014.Count == 0)
        {
            foreach (var kind in new[] { CantripInvocationKind.Agonizing, CantripInvocationKind.Spear, CantripInvocationKind.Repelling })
            {
                var original = kind switch
                {
                    CantripInvocationKind.Agonizing => GetDefinition<InvocationDefinition>("AgonizingBlast"),
                    CantripInvocationKind.Spear => EldritchSpear,
                    _ => RepellingBlast
                };
                CantripInvocationFeatures2014.Add(original, original.GrantedFeature);
                CantripInvocationDescriptions2014.Add(original, original.GuiPresentation.Description);

                foreach (var spell in DatabaseRepository.GetDatabase<SpellDefinition>()
                             .Where(spell => spell.SpellLevel == 0 && IsCantripEligible(spell, kind)).ToArray())
                {
                    var feature = FeatureDefinitionBuilder.Create($"FeatureInvocation{kind}2024{spell.Name}")
                        .SetGuiPresentationNoContent(true)
                        .AddCustomSubFeatures(new CustomBehaviorCantripInvocation(spell, kind))
                        .AddToDB();

                    if (spell == EldritchBlast)
                    {
                        CantripInvocationFeatures2024.Add(original, feature);
                        continue;
                    }

                    var invocation = InvocationDefinitionWithPrerequisitesBuilder
                        .Create($"Invocation{kind}2024{spell.Name}")
                        .SetGuiPresentation(original.GuiPresentation.Title, $"Invocation/&{kind}2024Description", original)
                        .SetRequirements(2, spell: spell)
                        .SetGrantedFeature(feature)
                        .SetValidators((_, hero) =>
                        {
                            var valid = hero == null || hero.SpellRepertoires.Any(repertoire =>
                                repertoire.SpellCastingClass == Warlock && repertoire.HasKnowledgeOfSpell(spell));
                            var text = Gui.Format("Failure/&InvocationRequiresWarlockCantrip", spell.FormatTitle());
                            return (valid, valid ? string.Empty : Gui.Colorize(text, Gui.ColorFailure));
                        })
                        .AddCustomSubFeatures(new FormattedDefinitionText(title: () => Gui.Format(
                            "Invocation/&CantripChoiceTitle", original.FormatTitle(), spell.FormatTitle())))
                        .AddToDB();
                    CantripInvocations2024.Add(invocation);
                }
            }
        }

        var enabled = Main.Settings.EnableWarlockInvocationProgression2024;

        foreach (var invocation in CantripInvocations2024)
        {
            invocation.GuiPresentation.hidden = !enabled;
            PowerBundle.ClearSpellEffectCacheForDefinition(invocation.RequiredKnownSpell);
        }

        PowerBundle.ClearSpellEffectCacheForDefinition(EldritchBlast);

        foreach (var entry in CantripInvocationFeatures2014)
        {
            var invocation = entry.Key;
            invocation.grantedFeature = enabled ? CantripInvocationFeatures2024[invocation] : entry.Value;
            invocation.GuiPresentation.description = enabled
                ? $"Invocation/&{(invocation == EldritchSpear ? "Spear" : invocation == RepellingBlast ? "Repelling" : "Agonizing")}2024Description"
                : CantripInvocationDescriptions2014[invocation];
        }
    }

    private static bool IsCantripEligible(SpellDefinition spell, CantripInvocationKind kind)
    {
        var effect = spell.EffectDescription;
        return kind switch
        {
            CantripInvocationKind.Agonizing => effect.EffectForms.Any(form => form.FormType == EffectForm.EffectFormType.Damage),
            CantripInvocationKind.Spear => effect.RangeType is RangeType.Distance or RangeType.RangeHit && effect.RangeParameter >= 2,
            _ => effect.RangeType is RangeType.RangeHit or RangeType.MeleeHit
        };
    }

    private sealed class CustomBehaviorCantripInvocation(SpellDefinition spell, CantripInvocationKind kind)
        : IModifyEffectDescription, IMagicEffectBeforeHitConfirmedOnEnemy
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return Main.Settings.EnableWarlockInvocationProgression2024 && definition == spell &&
                   kind != CantripInvocationKind.Repelling;
        }

        public EffectDescription GetEffectDescription(BaseDefinition definition, EffectDescription effectDescription,
            RulesetCharacter character, RulesetEffect rulesetEffect)
        {
            if (kind == CantripInvocationKind.Agonizing)
            {
                var modifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                    character.TryGetAttributeValue(AttributeDefinitions.Charisma));

                foreach (var form in effectDescription.EffectForms.Where(form => form.FormType == EffectForm.EffectFormType.Damage))
                {
                    form.DamageForm.BonusDamage += modifier;
                }
            }
            else
            {
                effectDescription.rangeParameter += 6 * character.GetClassLevel(Warlock);
            }

            return effectDescription;
        }

        public IEnumerator OnMagicEffectBeforeHitConfirmedOnEnemy(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier actionModifier,
            RulesetEffect rulesetEffect, List<EffectForm> actualEffectForms, bool firstTarget, bool criticalHit)
        {
            if (!Main.Settings.EnableWarlockInvocationProgression2024 || kind != CantripInvocationKind.Repelling ||
                rulesetEffect.SourceDefinition != spell || defender.RulesetCharacter.WieldingSize > CreatureSize.Large)
            {
                yield break;
            }

            yield return attacker.MyReactToDoNothing(ExtraActionId.DoNothingFree, attacker, "RepellingBlast2024",
                Gui.Format("Reaction/&CustomReactionRepellingBlast2024Description", defender.Name),
                () => actualEffectForms.Add(EffectFormBuilder.Create()
                    .SetMotionForm(MotionForm.MotionType.PushFromOrigin, 2).Build()), battleManager: battleManager,
                target: defender);
        }
    }
}
