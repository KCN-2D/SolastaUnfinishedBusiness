using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class ActionPanelContext
{
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
