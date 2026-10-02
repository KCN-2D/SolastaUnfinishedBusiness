using System;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Spells;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class ActionPanelContext
{
    private sealed class FamiliarTouchSelection(RulesetCharacter caster, ulong familiarGuid)
    {
        internal RulesetCharacter Caster { get; } = caster;
        internal ulong FamiliarGuid { get; } = familiarGuid;
    }

    private static readonly ConditionalWeakTable<SpellSelectionPanel, FamiliarTouchSelection> FamiliarTouchSelections = new();
    [ThreadStatic] private static FamiliarTouchSelection _openingFamiliarTouchSelection;

    internal static bool TryGetFamiliarTouchCaster(
        RulesetCharacter character,
        out GameLocationCharacter locationCaster,
        out string failure)
    {
        return TryGetFamiliarTouchSelection(character, 0, out locationCaster, out _, out failure);
    }

    private static bool TryGetFamiliarTouchSelection(
        RulesetCharacter caster,
        ulong familiarGuid,
        out GameLocationCharacter locationCaster,
        out GameLocationCharacter familiar,
        out string failure)
    {
        locationCaster = null;
        familiar = null;
        var candidate = caster == null ? null : GameLocationCharacter.GetFromActor(caster);
        if (candidate == null || SpellBuilders.IsFamiliar(caster) || caster.IsDeadOrDyingOrUnconscious)
        {
            failure = "Failure/&FailureFlagFamiliarTouchCasterUnavailable";
            return false;
        }

        var familiars = SpellBuilders.GetFamiliars(caster);
        familiar = familiarGuid == 0
            ? familiars.FirstOrDefault(character => candidate.IsWithinRange(character, 20) && character.CanReact()) ??
              SpellBuilders.GetFamiliar(caster, false)
            : familiars.FirstOrDefault(character => character.Guid == familiarGuid);
        if (familiar == null)
        {
            failure = "Failure/&FailureFlagFamiliarTouchFamiliarUnavailable";
            return false;
        }

        if (!candidate.IsWithinRange(familiar, 20))
        {
            failure = "Failure/&FailureFlagFamiliarTouchOutOfRange";
            return false;
        }

        if (!familiar.CanReact())
        {
            failure = "Failure/&FailureFlagFamiliarTouchReactionUnavailable";
            return false;
        }

        var controller = ServiceRepository.GetService<IPlayerControllerService>()?.ActivePlayerController;
        if (controller?.ControlledCharacters.Contains(candidate) != true)
        {
            failure = "Failure/&FailureFlagFamiliarTouchCasterNotControlled";
            return false;
        }

        if (!IsFamiliarTouchActionAvailable(candidate))
        {
            failure = "Failure/&FailureFlagFamiliarTouchCasterActionUnavailable";
            return false;
        }

        locationCaster = candidate;
        failure = string.Empty;
        return true;
    }

    internal static bool IsFamiliarTouchActionAvailable(GameLocationCharacter caster, Id actionId = Id.NoAction)
    {
        var battle = Gui.Battle;
        var scope = battle == null ? ActionScope.Exploration : ActionScope.Battle;

        // Native action status checks remaining actions, not whose combat turn it is.
        return caster != null && (battle == null || battle.ActiveContender == caster) &&
               (actionId == Id.NoAction
                   ? caster.GetActionStatus(Id.CastMain, scope) == ActionStatus.Available ||
                     caster.GetActionStatus(Id.CastBonus, scope) == ActionStatus.Available
                   : caster.GetActionStatus(actionId, scope) == ActionStatus.Available);
    }

    internal sealed class ValidateFamiliarTouchDelivery : IValidatePowerUseWithFailure
    {
        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
        {
            return TryGetFamiliarTouchCaster(character, out _, out _);
        }

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power, out string failure)
        {
            return TryGetFamiliarTouchCaster(character, out _, out failure);
        }
    }

    internal static bool TrySelectFamiliarTouchSpell(CharacterActionPanel panel, RulesetUsablePower usablePower)
    {
        if (usablePower.PowerDefinition != SpellBuilders.FamiliarTouchDeliveryPower)
        {
            return false;
        }

        if (!TryGetFamiliarTouchSelection(panel.GuiCharacter.RulesetCharacter, 0,
                out var caster, out var familiar, out _))
        {
            return true;
        }
        panel.PowerSelectionPanel.Hide(true);

        var previous = _openingFamiliarTouchSelection;
        _openingFamiliarTouchSelection = new FamiliarTouchSelection(caster.RulesetCharacter, familiar.Guid);
        try
        {
            // This opens the native picker. The selected spell, rather than the shortcut power,
            // determines the action and repertoire that will actually be spent.
            panel.OnActivateAction(Id.CastMain, null);
        }
        finally
        {
            _openingFamiliarTouchSelection = previous;
        }

        return true;
    }

    private static bool IsFamiliarTouchSelectionPanel(CharacterActionPanel panel, FamiliarTouchSelection selection)
    {
        return panel.GuiCharacter?.RulesetCharacter == selection.Caster;
    }

    internal static ActionType GetSpellSelectionActionType(CharacterActionPanel panel)
    {
        return _openingFamiliarTouchSelection != null &&
               _openingFamiliarTouchSelection.Caster == panel.actionParams?.ActingCharacter?.RulesetCharacter
            ? ActionType.None : panel.ActionType;
    }

    internal static void BindFamiliarTouchSelection(SpellSelectionPanel panel, RulesetCharacter caster)
    {
        FamiliarTouchSelections.Remove(panel);
        if (_openingFamiliarTouchSelection != null && _openingFamiliarTouchSelection.Caster == caster)
        {
            FamiliarTouchSelections.Add(panel, _openingFamiliarTouchSelection);
        }
    }

    internal static void UnbindFamiliarTouchSelection(SpellSelectionPanel panel)
    {
        FamiliarTouchSelections.Remove(panel);
    }

    internal static void FilterFamiliarTouchSpells(SpellRepertoireLine line)
    {
        var panel = line.GetComponentsInParent<SpellSelectionPanel>(true).FirstOrDefault();
        if ((_openingFamiliarTouchSelection == null ||
             _openingFamiliarTouchSelection.Caster != line.caster?.RulesetCharacter) &&
            (panel == null || !FamiliarTouchSelections.TryGetValue(panel, out _)))
        {
            return;
        }

        line.relevantSpells.RemoveAll(spell => !EffectHelpers.IsFamiliarTouchSpell(spell, spell.EffectDescription));
    }

    internal static bool PrepareFamiliarTouchSpell(
        CharacterActionPanel panel, RulesetSpellRepertoire repertoire, SpellDefinition spell)
    {
        if (!FamiliarTouchSelections.TryGetValue(panel.SpellSelectionPanel, out var selection))
        {
            return true;
        }

        var validSelection = TryGetFamiliarTouchSelection(selection.Caster, selection.FamiliarGuid,
            out var caster, out var familiar, out _);
        var scope = Gui.Battle == null ? ActionScope.Exploration : ActionScope.Battle;
        if (!validSelection || !IsFamiliarTouchSelectionPanel(panel, selection) ||
            !EffectHelpers.IsFamiliarTouchSpell(spell, spell.EffectDescription) ||
            !SpellActionTypeContext.TryGetAvailableSpellAction(caster, repertoire, spell, scope, out var actionId))
        {
            return false;
        }

        panel.actionId = actionId;
        panel.actionParams = new CharacterActionParams(caster, actionId);
        EffectHelpers.SelectFamiliarTouchDelivery(panel.actionParams, familiar);
        return true;
    }

    internal static bool TrySelectRitualSubspell(CharacterActionPanel panel, SpellDefinition spell)
    {
        var caster = panel.actionParams?.ActingCharacter?.RulesetCharacter;
        if (caster == null || !spell.SpellsBundle || spell.SubspellsList.Count == 0)
        {
            return false;
        }

        var spellBox = panel.RitualSelectionPanel.GetComponentsInChildren<RitualBox>(true)
            .FirstOrDefault(box => box.GuiSpellDefinition?.SpellDefinition == spell);
        var repertoire = SpellCastingValidation.ResolveRepertoire(caster, null, spell, preserveSource: true);
        var modal = Gui.GuiService.GetScreen<SubspellSelectionModal>();

        // Keep native ritual execution after the child choice, without selecting or spending a spell resource.
        modal.Bind(spell, caster, repertoire, (_, child, _) => panel.RitualCastEngaged(child),
            spell.SpellLevel, spellBox ? spellBox.RectTransform : panel.RitualSelectionPanel.RectTransform);
        modal.Show();
        return true;
    }

    internal static bool ShouldSuppressBattleBonusActionType(
        GameLocationCharacter character,
        ActionType actionType,
        ActionScope scope)
    {
        if (scope != ActionScope.Battle || actionType != ActionType.Bonus)
        {
            return false;
        }

        return CannotUseBattleActions(character);
    }

    internal static bool ShouldSuppressBattleBonusPanel(
        GameLocationCharacter character,
        ActionScope panelScope,
        ActionType panelType)
    {
        return ShouldSuppressBattleBonusActionType(character, panelType, panelScope);
    }

    internal static bool ShouldSuppressNoAction(
        GameLocationCharacter character,
        ActionScope scope)
    {
        if (scope != ActionScope.Battle)
        {
            return false;
        }

        return CannotUseBattleActions(character);
    }

    internal static bool ShouldSuppressNoActionInPanel(
        GameLocationCharacter character,
        ActionScope panelScope,
        ActionType panelType)
    {
        return ShouldSuppressBattleBonusPanel(character, panelScope, panelType);
    }

    internal static int FilterSuppressedNoActionGuiActions(CharacterActionPanel panel, Id parentActionId)
    {
        if (panel == null ||
            !ShouldSuppressNoActionInPanel(
                panel.GuiCharacter?.GameLocationCharacter,
                panel.ActionScope,
                panel.ActionType) ||
            !panel.guiActionsById.TryGetValue(parentActionId, out var guiActions) ||
            guiActions.Count == 0)
        {
            return 0;
        }

        return guiActions.RemoveAll(guiAction => guiAction?.ActionId == Id.NoAction);
    }

    private static bool CannotUseBattleActions(GameLocationCharacter character)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            return false;
        }

        if (rulesetCharacter.IsDeadOrDyingOrUnconscious)
        {
            return true;
        }

        if (rulesetCharacter.IsIncapacitated)
        {
            return true;
        }

        return false;
    }

}
