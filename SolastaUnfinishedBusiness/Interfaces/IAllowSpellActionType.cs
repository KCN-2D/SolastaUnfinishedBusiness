using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
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

        var providers = character.GetSubFeaturesByType<IAllowSpellActionType>();

        if (providers.Count == 0)
        {
            return;
        }

        foreach (var spell in candidates.Where(spell => spell != null))
        {
            if (!relevantSpells.Contains(spell) &&
                providers.Any(provider => provider.IsAllowed(character, repertoire, spell, actionType)))
            {
                relevantSpells.Add(spell);
            }
        }
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

        if (providers.Count == 0)
        {
            return false;
        }

        foreach (var repertoire in character.SpellRepertoires.Where(repertoire =>
                     repertoire?.SpellCastingFeature is { GuiPresentation.Hidden: false }))
        {
            var candidates = EnumerateReadySpells(repertoire)
                .Where(spell => !canOnlyUseCantrips || spell.SpellLevel == 0);

            foreach (var spell in candidates)
            {
                if (!providers.Any(provider => provider.IsAllowed(character, repertoire, spell, actionType)))
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
        var activationTime = LevelUpHelper.GetSpellActivationTime(actionType);

        return EnumerateReadySpells(repertoire).Any(spell =>
            spell.SpellLevel == spellLevel &&
            spell.ActivationTime is not ActivationTime.Reaction and not ActivationTime.OnAttackHit &&
            (actionType == ActionType.None || spell.ActivationTime == activationTime ||
             providers.Any(provider => provider.IsAllowed(character, repertoire, spell, actionType))));
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

        if (character == null || line.spellRepertoire != repertoire ||
            !character.SpellRepertoires.Contains(repertoire) ||
            !character.GetSubFeaturesByType<IAllowSpellActionType>().Any(provider =>
                provider.IsAllowed(character, repertoire, spell, line.actionType)))
        {
            return activationTime;
        }

        // Keep the shared definition unchanged: the same spell may be shown in both action panels.
        return line.actionType switch
        {
            ActionType.Main => ActivationTime.Action,
            ActionType.Bonus => ActivationTime.BonusAction,
            ActionType.Reaction => ActivationTime.Reaction,
            ActionType.NoCost => ActivationTime.NoCost,
            _ => activationTime
        };
    }

    private static IEnumerable<SpellDefinition> EnumerateReadySpells(RulesetSpellRepertoire repertoire)
    {
        var readySpells = repertoire.SpellCastingFeature?.SpellReadyness == SpellReadyness.Prepared
            ? repertoire.PreparedSpells
            : repertoire.KnownSpells;

        return repertoire.KnownCantrips
            .Concat(readySpells)
            .Concat(repertoire.AutoPreparedSpells)
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
