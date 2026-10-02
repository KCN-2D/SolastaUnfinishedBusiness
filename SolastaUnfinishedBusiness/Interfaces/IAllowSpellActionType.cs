using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using static ActionDefinitions;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Interfaces;

internal interface IAllowSpellActionType
{
    bool IsAllowed(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        ActionType actionType);
}

internal static class SpellActionTypeContext
{
    internal static void QualifySpells(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        ActionType actionType,
        IEnumerable<SpellDefinition> candidates,
        List<SpellDefinition> relevantSpells)
    {
        if (character == null || repertoire == null)
        {
            return;
        }

        relevantSpells.RemoveAll(spell => !IsCastingTimeAvailable(spell));

        var providers = character.GetSubFeaturesByType<IAllowSpellActionType>();

        // None is the all-actions picker, matching HasSpellOfLevelAndActionType.
        // Native repertoire lines only implement that meaning outside battle.
        if (providers.Count == 0 && actionType != ActionType.None &&
            (Gui.Battle != null || actionType != ActionType.Main))
        {
            return;
        }

        foreach (var spell in candidates.Where(spell => spell != null && IsCastingTimeAvailable(spell)))
        {
            if (!relevantSpells.Contains(spell) &&
                (MatchesCastingActionType(spell, actionType) || providers.Any(provider => provider.IsAllowed(character,
                    SpellCastingResourceContext.ResolveCastingRepertoire(repertoire, spell, character), spell, actionType))))
            {
                relevantSpells.Add(spell);
            }
        }
    }

    internal static bool TryGetAvailableSpellAction(
        GameLocationCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        ActionScope scope,
        out Id actionId)
    {
        // Native exploration uses one casting action for both main and bonus-action spells.
        actionId = spell == null ? Id.NoAction :
            scope == ActionScope.Exploration ? Id.CastMain : spell.BattleActionId;
        if (caster?.RulesetCharacter == null || spell == null)
        {
            return false;
        }

        if (IsSpellActionAvailable(caster, spell, scope, actionId))
        {
            return true;
        }

        // A mixed-action picker must retain the same alternative casting actions as the normal panels.
        var character = caster.RulesetCharacter;
        var castingRepertoire = SpellCastingResourceContext.ResolveCastingRepertoire(repertoire, spell, character);
        var providers = character.GetSubFeaturesByType<IAllowSpellActionType>();
        foreach (var actionType in new[] { ActionType.Main, ActionType.Bonus })
        {
            var alternative = actionType == ActionType.Main ? Id.CastMain : Id.CastBonus;
            if (alternative != actionId && IsSpellActionAvailable(caster, spell, scope, alternative) &&
                providers.Any(provider => provider.IsAllowed(character, castingRepertoire, spell, actionType)))
            {
                actionId = alternative;
                return true;
            }
        }

        return false;
    }

    internal static void ApplyCantripOnlyRestrictions(
        GameLocationCharacter character,
        ActionType actionType,
        ref bool cantripOnly)
    {
        // Keep the native picker and mixed-action selection on the same restrictions.
        SpellSlotCastingLimit2024Context.RemoveLegacyBonusActionSpellRestriction(ref cantripOnly);

        if (actionType == ActionType.Main && character.UsedMainAttacks > 0 &&
            character.RulesetCharacter.HasSubFeatureOfType<IAttackReplaceWithCantrip>())
        {
            cantripOnly = true;
        }

        ActionSwitching.CheckSpellcastingCantrips(character, actionType, ref cantripOnly);
        MetamagicContext.RestrictToCantripsAfterQuickenedSpell2024(character, ref cantripOnly);
    }

    private static bool IsSpellActionAvailable(
        GameLocationCharacter caster,
        SpellDefinition spell,
        ActionScope scope,
        Id actionId)
    {
        if (caster.GetActionStatus(actionId, scope) != ActionStatus.Available)
        {
            return false;
        }

        if (scope != ActionScope.Battle || spell.SpellLevel == 0)
        {
            return true;
        }

        var actionType = actionId switch
        {
            Id.CastMain => ActionType.Main,
            Id.CastBonus => ActionType.Bonus,
            _ => ActionType.None
        };
        var cantripOnly = caster.CanOnlyUseCantrips;
        ApplyCantripOnlyRestrictions(caster, actionType, ref cantripOnly);
        return !cantripOnly;
    }

    internal static bool CanCastSpellOfActionType(
        RulesetCharacter character,
        ActionType actionType,
        bool canOnlyUseCantrips)
    {
        if (character == null ||
            character is RulesetCharacterSimulacrum { LifecycleState: not SimulacrumLifecycleState.Ready })
        {
            return false;
        }

        var providers = character.GetSubFeaturesByType<IAllowSpellActionType>();

        var allowExplorationCasting = Gui.Battle == null && actionType == ActionType.Main;

        if (providers.Count == 0 && !allowExplorationCasting)
        {
            return false;
        }

        foreach (var repertoire in character.SpellRepertoires.Where(SpellSelectionContext.IsDisplayedRepertoire))
        {
            var candidates = EnumerateReadySpells(character, repertoire)
                .Where(spell => !canOnlyUseCantrips || spell.SpellLevel == 0);

            foreach (var spell in candidates)
            {
                if (!(allowExplorationCasting && IsExplorationCastingTime(spell, actionType)) &&
                    !providers.Any(provider => provider.IsAllowed(character,
                        SpellCastingResourceContext.ResolveCastingRepertoire(repertoire, spell, character), spell, actionType)))
                {
                    continue;
                }

                // Use the same repertoire and validation as the spell panel, including components,
                // multiclass slots, free casts, spell points, and the 2024 slot expenditure limit.
                using var scope = SpellCastingValidation.EnterSelectedRepertoire(repertoire);

                if (character.AreSpellComponentsValid(spell) &&
                    SpellCastingValidation.IsValid(character, repertoire, spell, null, out _) &&
                    HasAvailableSpellSlot(character, repertoire, spell))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool HasSpellOfLevelAndActionType(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        int spellLevel,
        ActionType actionType)
    {
        if (repertoire == null)
        {
            return false;
        }

        var providers = character.GetSubFeaturesByType<IAllowSpellActionType>();
        return EnumerateReadySpells(character, repertoire).Any(spell =>
            spell.SpellLevel == spellLevel && IsCastingTimeAvailable(spell) &&
            spell.ActivationTime is not ActivationTime.Reaction and not ActivationTime.OnAttackHit &&
            (MatchesCastingActionType(spell, actionType) ||
             providers.Any(provider => provider.IsAllowed(character,
                 SpellCastingResourceContext.ResolveCastingRepertoire(repertoire, spell, character), spell, actionType))));
    }

    internal static SpellRepertoireLine GetRepertoireLine(SpellActivationBox spellBox)
    {
        // Panels are bound before they are shown. Traverse explicitly because this Unity version's
        // GetComponentInParent ignores inactive parents and has no includeInactive overload.
        for (var parent = spellBox?.transform; parent != null; parent = parent.parent)
        {
            var line = parent.GetComponent<SpellRepertoireLine>();

            if (line != null)
            {
                return line;
            }
        }

        return null;
    }

    internal static ActivationTime GetDisplayedActivationTime(SpellDefinition spell, SpellActivationBox spellBox)
    {
        var activationTime = spell.ActivationTime;

        if (Gui.Battle == null || spellBox == null || spellBox.spellRepertoire == null ||
            spellBox.GuiSpellDefinition?.SpellDefinition != spell)
        {
            return activationTime;
        }

        var line = GetRepertoireLine(spellBox);
        var character = line?.caster?.RulesetCharacter;
        var repertoire = spellBox.spellRepertoire;

        if (character == null || SpellSelectionContext.Resolve(line.spellRepertoire) != repertoire ||
            !character.SpellRepertoires.Contains(repertoire))
        {
            return activationTime;
        }

        var actionType = line.actionType;
        if (actionType == ActionType.None)
        {
            // Use the action that this mixed picker will actually execute, including bonus-action grants.
            if (!TryGetAvailableSpellAction(line.caster.GameLocationCharacter, repertoire, spell,
                    ActionScope.Battle, out var actionId))
            {
                return activationTime;
            }

            actionType = actionId switch
            {
                Id.CastMain => ActionType.Main,
                Id.CastBonus => ActionType.Bonus,
                _ => ActionType.None
            };
        }
        else if (!character.GetSubFeaturesByType<IAllowSpellActionType>().Any(provider =>
                     provider.IsAllowed(character,
                         SpellCastingResourceContext.ResolveCastingRepertoire(repertoire, spell, character),
                         spell, actionType)))
        {
            return activationTime;
        }

        // Keep the shared definition unchanged: the same spell may be shown in both action panels.
        return actionType switch
        {
            ActionType.Main => ActivationTime.Action,
            ActionType.Bonus => ActivationTime.BonusAction,
            ActionType.Reaction => ActivationTime.Reaction,
            ActionType.NoCost => ActivationTime.NoCost,
            _ => activationTime
        };
    }

    internal static bool MatchesCastingActionType(SpellDefinition spell, ActionType actionType)
    {
        if (spell == null)
        {
            return false;
        }

        if (actionType == ActionType.None)
        {
            return IsCastingTimeAvailable(spell) &&
                   spell.ActivationTime is not ActivationTime.Reaction and not ActivationTime.OnAttackHit;
        }

        return spell.ActivationTime == LevelUpHelper.GetSpellActivationTime(actionType) ||
               IsExplorationCastingTime(spell, actionType);
    }

    private static bool IsExplorationCastingTime(SpellDefinition spell, ActionType actionType)
    {
        // Native long casting times have no battle action type, but use CastMain in exploration.
        return Gui.Battle == null && actionType == ActionType.Main &&
               CastingTimeToActionDefinition.TryGetValue(spell.ActivationTime, out var nativeActionType) &&
               nativeActionType == ActionType.None;
    }

    private static bool IsCastingTimeAvailable(SpellDefinition spell)
    {
        return Gui.Battle == null ||
               spell.ActivationTime is ActivationTime.Action or ActivationTime.BonusAction or ActivationTime.NoCost;
    }

    private static IEnumerable<SpellDefinition> EnumerateReadySpells(
        RulesetCharacter character, RulesetSpellRepertoire repertoire)
    {
        var readySpells = repertoire.SpellCastingFeature?.SpellReadyness == SpellReadyness.Prepared
            ? repertoire.PreparedSpells
            : repertoire.KnownSpells;

        return repertoire.KnownCantrips
            .Concat(readySpells)
            .Concat(repertoire.AutoPreparedSpells)
            .Concat(LevelUpHelper.EnumerateSlotCastableExtraSpellsForRepertoire(character, repertoire)
                .Select(entry => entry.Spell))
            .Concat(repertoire.ExtraSpellsByTag.Values.SelectMany(spells => spells))
            .Where(spell => spell != null)
            .Distinct();
    }

    private static bool HasAvailableSpellSlot(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell)
    {
        if (spell.SpellLevel == 0)
        {
            return true;
        }

        for (var slotLevel = spell.SpellLevel; slotLevel <= 9; slotLevel++)
        {
            if (repertoire.TryGetAvailableSlotLevel(character, slotLevel, spell, out var isAvailable) &&
                isAvailable &&
                SpellSlotCastingLimit2024Context.CanUseSpellSlotLevel(character, repertoire, spell, slotLevel))
            {
                return true;
            }
        }

        return false;
    }
}
