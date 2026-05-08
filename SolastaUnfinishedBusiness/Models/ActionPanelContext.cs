using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class ActionPanelContext
{
    internal static bool ShouldSuppressBattleBonusActionType(
        GameLocationCharacter character,
        ActionType actionType,
        ActionScope scope,
        out string reason)
    {
        if (scope != ActionScope.Battle || actionType != ActionType.Bonus)
        {
            reason = "wrong-action-type";
            return false;
        }

        return TryGetUnableToUseBattleActionsReason(character, out reason);
    }

    internal static bool ShouldSuppressBattleBonusPanel(
        GameLocationCharacter character,
        ActionScope panelScope,
        ActionType panelType,
        out string reason)
    {
        return ShouldSuppressBattleBonusActionType(character, panelType, panelScope, out reason);
    }

    internal static bool ShouldSuppressNoAction(
        GameLocationCharacter character,
        ActionScope scope,
        out string reason)
    {
        if (scope != ActionScope.Battle)
        {
            reason = "wrong-scope";
            return false;
        }

        return TryGetUnableToUseBattleActionsReason(character, out reason);
    }

    internal static bool ShouldSuppressNoActionInPanel(
        GameLocationCharacter character,
        ActionScope panelScope,
        ActionType panelType,
        out string reason)
    {
        return ShouldSuppressBattleBonusPanel(character, panelScope, panelType, out reason);
    }

    internal static int FilterSuppressedNoActionGuiActions(CharacterActionPanel panel, Id parentActionId)
    {
        if (panel == null ||
            !ShouldSuppressNoActionInPanel(
                panel.GuiCharacter?.GameLocationCharacter,
                panel.ActionScope,
                panel.ActionType,
                out _) ||
            !panel.guiActionsById.TryGetValue(parentActionId, out var guiActions) ||
            guiActions.Count == 0)
        {
            return 0;
        }

        return guiActions.RemoveAll(guiAction => guiAction?.ActionId == Id.NoAction);
    }

    private static bool TryGetUnableToUseBattleActionsReason(GameLocationCharacter character, out string reason)
    {
        var rulesetCharacter = character?.RulesetCharacter;

        if (rulesetCharacter == null)
        {
            reason = "no-character";
            return false;
        }

        if (rulesetCharacter.IsDeadOrDyingOrUnconscious)
        {
            reason = "dead-or-unconscious";
            return true;
        }

        if (rulesetCharacter.IsIncapacitated)
        {
            reason = "incapacitated";
            return true;
        }

        reason = "available";
        return false;
    }

}
