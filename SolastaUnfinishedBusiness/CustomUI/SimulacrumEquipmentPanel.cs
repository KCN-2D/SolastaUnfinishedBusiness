using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Patches;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class SimulacrumEquipmentPanel
{
    private static readonly ConditionalWeakTable<CharacterInspectionScreen, SimulacrumInventorySession>
        Sessions = new();
    private static readonly HashSet<(ulong CharacterGuid, ulong ItemGuid)>
        PendingExternalContainerTransfers = [];

    internal static bool TryOpen(CharacterControlPanel controlPanel)
    {
        if (controlPanel?.GuiCharacter?.RulesetCharacter is not
            RulesetCharacterSimulacrum duplicate)
        {
            return false;
        }

        Open(
            duplicate,
            controlPanel.MainScreen,
            null,
            true);

        return true;
    }

    internal static bool TryOpenExternalContainer(
        RulesetCharacterSimulacrum duplicate,
        GuiScreen parentScreen,
        RulesetContainer container)
    {
        return duplicate != null &&
               container != null &&
               Open(
                   duplicate,
                   parentScreen,
                   container,
                   false);
    }

    private static bool Open(
        RulesetCharacterSimulacrum duplicate,
        GuiScreen parentScreen,
        RulesetContainer externalContainer,
        bool includeNearbyGroundItems)
    {
        if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
        {
            ShowUnavailable("Failure/&SimulacrumInventoryUnavailable");

            return false;
        }

        if (!SimulacrumBehavior.TryGetOwner(duplicate, out var owner))
        {
            ShowUnavailable("Failure/&SimulacrumOwnerNotFound");

            return false;
        }

        var screen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();

        if (!screen)
        {
            ShowUnavailable("Failure/&SimulacrumInventoryUnavailable");

            return false;
        }

        var inBattle = ServiceRepository.GetService<IGameLocationBattleService>() is
        {
            IsBattleInProgress: true
        };

        if (screen.InspectedCharacter != null)
        {
            screen.Unbind();
        }

        PrepareSession(screen, duplicate, owner);
        duplicate.NormalizeInventory();

        try
        {
            var mode = inBattle
                ? InventoryManagementMode.Battle
                : InventoryManagementMode.Free;

            if (externalContainer != null)
            {
                screen.Show(
                    owner,
                    parentScreen,
                    externalContainer,
                    mode);
            }
            else if (includeNearbyGroundItems &&
                     GameLocationCharacter.GetFromActor(duplicate) is { } locationCharacter &&
                ServiceRepository.GetService<IGameLocationItemService>() is { } itemService)
            {
                var groundItems = new Dictionary<RulesetItem, TA.int3>();

                itemService.EnumerateGroundItemsAroundCharacter(
                    locationCharacter,
                    5,
                    groundItems);
                screen.Show(
                    owner,
                    parentScreen,
                    locationCharacter.LocationPosition,
                    groundItems.Keys.ToList(),
                    mode);
            }
            else
            {
                screen.Show(
                    owner,
                    parentScreen,
                    duplicate.CharacterInventory.PersonalContainer,
                    mode);
            }
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogException(new Exception("Unable to open the Simulacrum inventory.", ex));

            try
            {
                if (screen)
                {
                    screen.Hide();
                }
            }
            catch (Exception cleanupException)
            {
                Trace.LogException(new Exception(
                    "Unable to hide a failed Simulacrum inventory screen.",
                    cleanupException));
            }
            finally
            {
                Abort(screen);
            }

            ShowUnavailable("Failure/&SimulacrumInventoryUnavailable");

            return false;
        }
    }

    internal static bool TrySelectSpellItem(CharacterActionPanel actionPanel)
    {
        var actionParams = actionPanel?.actionParams;

        if (actionParams?.ActingCharacter?.RulesetCharacter is not
                RulesetCharacterSimulacrum duplicate ||
            actionParams.RulesetEffect?.EffectDescription is not
                { TargetType: RuleDefinitions.TargetType.Item } effectDescription)
        {
            return false;
        }

        if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate) ||
            !SimulacrumBehavior.TryGetOwner(duplicate, out var owner))
        {
            ShowUnavailable("Failure/&SimulacrumInventoryUnavailable");

            return true;
        }

        var screen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();

        if (!screen)
        {
            ShowUnavailable("Failure/&SimulacrumInventoryUnavailable");

            return true;
        }

        try
        {
            if (screen.InspectedCharacter != null)
            {
                screen.Unbind();
            }

            PrepareSession(screen, duplicate, owner);
            duplicate.NormalizeInventory();

            if (!actionParams.TargetCharacters.Contains(actionParams.ActingCharacter))
            {
                actionParams.TargetCharacters.Add(actionParams.ActingCharacter);
                actionParams.ActionModifiers.Add(new ActionModifier());
            }

            var itemService = ServiceRepository.GetService<IGameLocationItemService>();
            var position = actionParams.ActingCharacter.LocationPosition;
            var groundItems = itemService?.EnumerateGroundItems(position) ?? [];

            screen.OptionalSourceCharacterForItems = actionParams.ActingCharacter;
            screen.Show(
                owner,
                actionPanel.MainScreen,
                position,
                groundItems,
                InventoryManagementMode.Free);
            screen.SelectRelevantItem(
                actionPanel.EquipedItemSelectedForSpell,
                actionPanel.ItemSelectionCancelled,
                effectDescription.ItemSelectionType,
                actionParams);
            actionPanel.RestoreDefaultCursor();
        }
        catch (Exception ex)
        {
            Trace.LogException(new Exception(
                "Unable to select a spell item from the Simulacrum inventory.",
                ex));
            actionParams.RulesetEffect?.Terminate(false);
            actionPanel.RestoreDefaultCursor();

            try
            {
                if (screen)
                {
                    screen.Hide();
                }
            }
            finally
            {
                Abort(screen);
            }

            ShowUnavailable("Failure/&SimulacrumInventoryUnavailable");
        }

        return true;
    }

    internal static void SetExternalContainer(
        RulesetCharacterSimulacrum duplicate,
        RulesetContainer container)
    {
        if (duplicate == null || container == null ||
            !TryGetActiveScreen(out var screen) ||
            !TryGetSession(screen, out var session) ||
            session.SubjectGuid != duplicate.Guid)
        {
            return;
        }

        session.ExternalContainer = container;
    }

    internal static bool IsExternalContainerItem(
        RulesetCharacterSimulacrum duplicate,
        RulesetItem item)
    {
        if (duplicate == null || item == null)
        {
            return false;
        }

        if (PendingExternalContainerTransfers.Contains((duplicate.Guid, item.Guid)))
        {
            return true;
        }

        if (
            !TryGetActiveScreen(out var screen) ||
            !TryGetSession(screen, out var session) ||
            session.SubjectGuid != duplicate.Guid)
        {
            return false;
        }

        var container = session.ExternalContainer ?? screen.externalContainer;

        return container != null &&
               container.InventorySlots.Any(slot => slot?.EquipedItem == item);
    }

    internal static IDisposable BeginExternalContainerTransfer(
        RulesetCharacterSimulacrum duplicate,
        RulesetItem item)
    {
        return duplicate == null || item == null
            ? null
            : new ExternalContainerTransferScope(duplicate.Guid, item.Guid);
    }

    internal static bool TryBind(
        CharacterInspectionScreen screen,
        RulesetCharacterHero transportHero,
        InventoryManagementMode mode,
        InventoryPanel.ItemSelectedHandler itemSelected)
    {
        if (!TryGetSession(screen, out var session))
        {
            return false;
        }

        if (!session.TryGetSubject(out var duplicate) ||
            !SimulacrumBehavior.CanAccessHumanoidInventory(duplicate) ||
            transportHero?.Guid != session.TransportGuid)
        {
            session.State = SimulacrumInventorySessionState.Failed;

            throw new InvalidOperationException(
                "The Simulacrum inventory session no longer matches its subject or transport.");
        }

        if (session.Binding)
        {
            return true;
        }

        session.Binding = true;
        session.State = SimulacrumInventorySessionState.Opening;
        var externalContainer = screen.externalContainer;

        try
        {
            if (screen.InspectedCharacter != null)
            {
                UnbindScreen(screen, false);
            }

            var guiCharacter = new GuiCharacter(duplicate);

            session.GuiCharacter = guiCharacter;
            session.PanelShown = false;

            screen.itemSelected = itemSelected;
            screen.inventoryManagementMode = mode;
            screen.InspectedCharacter = guiCharacter;
            screen.externalContainer = externalContainer ==
                                       duplicate.CharacterInventory.PersonalContainer
                ? null
                : externalContainer;
            session.ExternalContainer = screen.externalContainer;
            screen.externalContainerOpener = null;
            screen.currentToggle = 0;
            screen.staticTogglesNumber = 0;
            screen.SomethingChanged = false;
            Gui.GenderContext = duplicate.Sex;
            Global.InspectedHero = null;

            screen.characterPlate.Bind(guiCharacter);
            screen.characterPlate.Refresh();
            PrepareInventoryOnlyScreen(screen, session);
            screen.InventoryPanel.Bind(
                guiCharacter,
                mode,
                screen.externalContainer,
                itemSelected,
                screen.characterPlatesTable);
            BindEvents(screen, duplicate);

            screen.screenCaption.gameObject.SetActive(true);
            screen.screenCaption.Text = Gui.Localize("Screen/&SimulacrumEquipmentTitle");
            HideUnsupportedPanels(screen);
            CharacterStatsPanelPatcher.BindAbilityScores(
                screen.abilityScoresListingPanel,
                duplicate);
            CharacterStatsPanelPatcher.BindCharacter(screen.characterStatsPanel, duplicate);
            CharacterStatsPanelPatcher.BindAttackModes(screen.attackModesPanel, duplicate);

            session.Bound = true;
            session.State = SimulacrumInventorySessionState.Bound;
            return true;
        }
        finally
        {
            session.Binding = false;
        }
    }

    internal static bool TryUnbind(CharacterInspectionScreen screen)
    {
        if (!TryGetSession(screen, out _))
        {
            return false;
        }

        UnbindScreen(screen, true);

        return true;
    }

    internal static bool TryGetActiveCharacter(out RulesetCharacterSimulacrum character)
    {
        character = null;

        return TryGetActiveScreen(out var screen) &&
               TryGetActiveCharacter(screen, out character);
    }

    internal static bool TryGetActiveCharacter(
        CharacterInspectionScreen screen,
        out RulesetCharacterSimulacrum character)
    {
        character = null;

        return TryGetSession(screen, out var session) &&
               session.TryGetSubject(out character) &&
               SimulacrumBehavior.CanAccessHumanoidInventory(character);
    }

    internal static RulesetCharacterHero GetTransportHero(GuiCharacter guiCharacter)
    {
        if (guiCharacter?.RulesetCharacter is RulesetCharacterHero hero)
        {
            return hero;
        }

        if (guiCharacter?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate)
        {
            return null;
        }

        if (TryGetActiveScreen(out var screen) &&
            TryGetSession(screen, out var session) &&
            session.SubjectGuid == duplicate.Guid &&
            RulesetEntity.TryGetEntity<RulesetCharacterHero>(
                session.TransportGuid,
                out var transport))
        {
            return transport;
        }

        return SimulacrumBehavior.TryGetOwner(duplicate, out var owner)
            ? owner
            : null;
    }

    internal static RulesetCharacter GetInventorySubject(GuiCharacter guiCharacter)
    {
        return guiCharacter?.RulesetCharacter;
    }

    internal static void ReturnItemToInventorySubject(
        IInventoryCommandService inventoryCommands,
        RulesetCharacterHero transportHero,
        RulesetItem item,
        bool silent)
    {
        if (inventoryCommands == null || item == null)
        {
            return;
        }

        if (TryResolveInventoryDestination(transportHero, item, out var duplicate) &&
            item.ItemDefinition?.IsWealthPile != true)
        {
            ReturnItemToResolvedDuplicate(inventoryCommands, duplicate, item);

            return;
        }

        inventoryCommands.GrantItem(transportHero, item, silent);
    }

    internal static void ReturnReleasedItemToInventorySubject(
        IInventoryCommandService inventoryCommands,
        RulesetCharacterHero transportHero,
        RulesetItem item)
    {
        if (inventoryCommands == null || item == null)
        {
            return;
        }

        if (TryResolveInventoryDestination(transportHero, item, out var duplicate) &&
            item.ItemDefinition?.IsWealthPile != true)
        {
            ReturnItemToResolvedDuplicate(inventoryCommands, duplicate, item);

            return;
        }

        // Preserve the native EndInteraction behavior outside a bound Simulacrum
        // inventory session. This helper replaces ReleaseItem, not GrantItem.
        inventoryCommands.ReleaseItem(transportHero, item);
    }

    private static bool TryResolveInventoryDestination(
        RulesetCharacterHero transportHero,
        RulesetItem item,
        out RulesetCharacterSimulacrum duplicate)
    {
        if (TryGetActiveCharacter(out duplicate) &&
            TryGetActiveScreen(out var screen) &&
            TryGetSession(screen, out var session) &&
            transportHero?.Guid == session.TransportGuid)
        {
            return true;
        }

        return item != null &&
               RulesetEntity.TryGetEntity(item.BearerGuid, out duplicate);
    }

    private static void ReturnItemToResolvedDuplicate(
        IInventoryCommandService inventoryCommands,
        RulesetCharacterSimulacrum duplicate,
        RulesetItem item)
    {
        if (!SimulacrumBehavior.CanAccessHumanoidInventory(duplicate))
        {
            if (SimulacrumBehavior.TryGetOwner(duplicate, out var owner))
            {
                inventoryCommands.GrantItem(owner, item, false);
            }

            return;
        }

        inventoryCommands.AddContainerSubItem(
            duplicate.CharacterInventory.PersonalContainer,
            item,
            default);
    }

    internal static void AfterBeginShow(CharacterInspectionScreen screen)
    {
        if (!TryGetSession(screen, out var session) ||
            !session.Bound ||
            !session.TryGetSubject(out var duplicate))
        {
            return;
        }

        screen.characterPlatesTable.gameObject.SetActive(false);
        var inventoryPanel = screen.InventoryPanel;
        var wasShown = inventoryPanel.Visible || inventoryPanel.Showing;

        if (!wasShown)
        {
            inventoryPanel.Show(false);
        }
        else
        {
            inventoryPanel.RefreshNow();
        }

        session.PanelShown = true;
        Global.InspectedHero = null;
        CharacterStatsPanelPatcher.RefreshAbilityScores(screen.abilityScoresListingPanel);
        CharacterStatsPanelPatcher.RefreshCharacter(screen.characterStatsPanel);
        CharacterStatsPanelPatcher.RefreshAttackModes(screen.attackModesPanel);
    }

    internal static bool TryRefresh(CharacterInspectionScreen screen)
    {
        if (!TryGetSession(screen, out var session))
        {
            return false;
        }

        if (!session.Bound || !session.PanelShown)
        {
            return true;
        }

        HideUnsupportedPanels(screen);
        var inventoryPanel = screen.InventoryPanel;

        if (!inventoryPanel.Visible && !inventoryPanel.Showing)
        {
            inventoryPanel.Show(false);
        }
        else
        {
            inventoryPanel.RefreshNow();
        }

        screen.characterPlate.Refresh();
        CharacterStatsPanelPatcher.RefreshAbilityScores(screen.abilityScoresListingPanel);
        CharacterStatsPanelPatcher.RefreshCharacter(screen.characterStatsPanel);
        CharacterStatsPanelPatcher.RefreshAttackModes(screen.attackModesPanel);
        screen.RefreshCaption();

        return true;
    }

    internal static void MarkPreviewDirty(
        RulesetCharacterSimulacrum duplicate,
        int visualRevision,
        string equipmentSignature)
    {
        var screen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();

        if (!screen ||
            !TryGetSession(screen, out var session) ||
            session.SubjectGuid != duplicate?.Guid ||
            !session.Bound ||
            !session.PanelShown)
        {
            return;
        }

        if (visualRevision < session.PreviewRefreshRevision)
        {
            return;
        }

        session.PreviewRefreshRevision = visualRevision;
        session.PreviewEquipmentSignature = equipmentSignature;
    }

    internal static void QueuePreviewRefresh(
        RulesetCharacterSimulacrum duplicate,
        int visualRevision,
        string equipmentSignature)
    {
        MarkPreviewDirty(duplicate, visualRevision, equipmentSignature);

        var screen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();

        if (!screen ||
            !TryGetSession(screen, out var session) ||
            session.SubjectGuid != duplicate?.Guid ||
            !session.Bound ||
            !session.PanelShown ||
            visualRevision != session.PreviewRefreshRevision)
        {
            return;
        }

        if (session.PreviewRefreshPending)
        {
            return;
        }

        session.PreviewRefreshPending = true;
        screen.StartCoroutine(RefreshPreviewAfterInventoryMutation(screen, duplicate.Guid));
    }

    private static IEnumerator RefreshPreviewAfterInventoryMutation(
        CharacterInspectionScreen screen,
        ulong characterGuid)
    {
        SimulacrumInventorySession session = null;

        // The persistent world model, portrait photos, and this temporary viewport all use
        // GraphicsCharacterFactoryManager. Wait for the one shared visual revision to finish;
        // frame-count heuristics cannot detect a still-running world refresh.
        while (true)
        {
            yield return null;

            if (!screen ||
                !TryGetSession(screen, out session) ||
                session.SubjectGuid != characterGuid)
            {
                if (session != null)
                {
                    session.PreviewRefreshPending = false;
                }

                yield break;
            }

            if (!session.TryGetSubject(out var pendingDuplicate))
            {
                session.PreviewRefreshPending = false;

                yield break;
            }

            if (SimulacrumBehavior.IsVisualRevisionReady(
                    pendingDuplicate,
                    session.PreviewRefreshRevision,
                    session.PreviewEquipmentSignature))
            {
                break;
            }

        }

        session.PreviewRefreshPending = false;

        if (!session.Bound ||
            !session.PanelShown ||
            !session.TryGetSubject(out var duplicate) ||
            duplicate.LifecycleState != SimulacrumLifecycleState.Ready ||
            screen.InventoryPanel?.GuiCharacter?.RulesetCharacter != duplicate)
        {
            yield break;
        }

        var viewport = screen.InventoryPanel.characterViewport;

        if (!viewport)
        {
            yield break;
        }

        viewport.Unbind(false);
        viewport.Bind(
            GraphicsCharacterDefinitions.CharacterType.Inventory,
            duplicate,
            false);
    }

    internal static void Abort(CharacterInspectionScreen screen)
    {
        if (screen == null || !TryGetSession(screen, out _))
        {
            return;
        }

        UnbindScreen(screen, true);
    }

    internal static void CloseForCharacter(RulesetCharacterSimulacrum character)
    {
        if (character == null ||
            !TryGetActiveScreen(out var screen) ||
            !TryGetSession(screen, out var session) ||
            session.SubjectGuid != character.Guid)
        {
            return;
        }

        UnbindScreen(screen, true);
    }

    internal static void HandleBindFailure(CharacterInspectionScreen screen)
    {
        if (screen == null || !TryGetSession(screen, out var session))
        {
            return;
        }

        session.Bound = false;
        UnbindScreen(screen, false);
        session.State = SimulacrumInventorySessionState.Failed;
    }

    private static void PrepareInventoryOnlyScreen(
        CharacterInspectionScreen screen,
        SimulacrumInventorySession session)
    {
        screen.currentToggle = -1;
        screen.inspectionToggles.Clear();
        screen.inspectionPanels.Clear();
        screen.ignoreToggleCallback = true;
        session.ToggleGroupWasActive = screen.toggleGroup.gameObject.activeSelf;

        try
        {
            foreach (var existingToggle in screen.toggleGroup
                         .GetComponentsInChildren<CharacterInspectionToggle>(true))
            {
                existingToggle.Unbind();
            }

            Gui.ReleaseChildrenToPool(screen.toggleGroup.transform);

            foreach (var spellPanel in screen.spellPanelsContainer
                         .GetComponentsInChildren<SpellRepertoirePanel>(true))
            {
                spellPanel.Unbind();
                spellPanel.Hide(true);
            }

            Gui.ReleaseChildrenToPool(screen.spellPanelsContainer);
            screen.inspectionPanels.Add(screen.InventoryPanel);
            screen.currentToggle = 0;
            screen.toggleGroup.gameObject.SetActive(false);
        }
        finally
        {
            screen.ignoreToggleCallback = false;
        }

        HideUnsupportedPanels(screen);
    }

    private static void HideUnsupportedPanels(CharacterInspectionScreen screen)
    {
        screen.personalityMapPanel.gameObject.SetActive(false);
        screen.characterInformationPanel.gameObject.SetActive(false);
        screen.proficienciesPanel.gameObject.SetActive(false);
        screen.craftingPanel.gameObject.SetActive(false);
    }

    private static void BindEvents(
        CharacterInspectionScreen screen,
        RulesetCharacterSimulacrum duplicate)
    {
        if (GameLocationCharacter.GetFromActor(duplicate) is { } location)
        {
            location.Moved += screen.CharacterMoved;
            location.ActionsRefreshed += screen.ActionsRefreshed;
        }

        duplicate.CharacterInventory.ItemEquiped += screen.ItemEquiped;
        duplicate.CharacterInventory.ItemUnequiped += screen.ItemUnequiped;
    }

    private static void UnbindScreen(CharacterInspectionScreen screen, bool removeSession)
    {
        if (screen == null)
        {
            return;
        }

        TryGetSession(screen, out var session);
        if (session != null)
        {
            session.PanelShown = false;
            session.State = SimulacrumInventorySessionState.Closing;
        }

        var duplicate = session != null && session.TryGetSubject(out var subject)
            ? subject
            : screen.InspectedCharacter?.RulesetCharacter as RulesetCharacterSimulacrum;

        if (duplicate != null)
        {
            TryCleanup("events", () =>
            {
                if (GameLocationCharacter.GetFromActor(duplicate) is { } location)
                {
                    location.Moved -= screen.CharacterMoved;
                    location.ActionsRefreshed -= screen.ActionsRefreshed;
                }

                duplicate.CharacterInventory.ItemEquiped -= screen.ItemEquiped;
                duplicate.CharacterInventory.ItemUnequiped -= screen.ItemUnequiped;
            });
        }

        screen.OptionalSourceCharacterForItems = null;
        TryCleanup("inventory panel", () =>
        {
            var inventoryPanel = screen.InventoryPanel;

            if (inventoryPanel.Visible || inventoryPanel.Showing || inventoryPanel.Hiding)
            {
                inventoryPanel.Hide(true);
            }
            else
            {
                inventoryPanel.Unbind();
            }
        });
        TryCleanup("character plate", () => screen.characterPlate.Unbind());
        TryCleanup(
            "ability scores",
            () => CharacterStatsPanelPatcher.UnbindAbilityScores(screen.abilityScoresListingPanel));
        TryCleanup(
            "character stats",
            () => CharacterStatsPanelPatcher.UnbindCharacter(screen.characterStatsPanel));
        TryCleanup(
            "attack modes",
            () => CharacterStatsPanelPatcher.UnbindAttackModes(screen.attackModesPanel));
        TryCleanup("inspection toggles", () =>
        {
            screen.ignoreToggleCallback = true;

            try
            {
                Gui.ReleaseChildrenToPool(screen.toggleGroup.transform);
            }
            finally
            {
                screen.ignoreToggleCallback = false;
            }
        });
        TryCleanup("spell panels", () =>
        {
            foreach (var spellPanel in screen.spellPanelsContainer
                         .GetComponentsInChildren<SpellRepertoirePanel>(true))
            {
                spellPanel.Unbind();
                spellPanel.Hide(true);
            }

            Gui.ReleaseChildrenToPool(screen.spellPanelsContainer);
        });
        screen.currentToggle = -1;
        screen.inspectionToggles.Clear();
        screen.inspectionPanels.Clear();
        screen.toggleGroup.gameObject.SetActive(session?.ToggleGroupWasActive ?? true);
        session?.RestorePanelVisibility(screen);
        screen.characterPlatesTable.gameObject.SetActive(true);
        screen.InspectedCharacter = null;
        screen.externalContainer = null;
        screen.externalContainerOpener = null;
        Gui.GenderContext = default;
        Global.InspectedHero = null;

        if (session != null)
        {
            session.GuiCharacter = null;
        }

        if (session != null)
        {
            session.Bound = false;
            session.State = SimulacrumInventorySessionState.Closed;
        }

        if (removeSession)
        {
            Sessions.Remove(screen);
        }

        return;

        static void TryCleanup(string component, Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                Trace.LogException(new Exception(
                    $"Unable to clean up Simulacrum inventory {component}.",
                    ex));
            }
        }
    }

    private static bool TryGetActiveScreen(out CharacterInspectionScreen screen)
    {
        screen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();

        return screen != null && Sessions.TryGetValue(screen, out _);
    }

    private static void PrepareSession(
        CharacterInspectionScreen screen,
        RulesetCharacterSimulacrum duplicate,
        RulesetCharacterHero owner)
    {
        Sessions.Remove(screen);
        Sessions.Add(
            screen,
            new SimulacrumInventorySession(screen, duplicate.Guid, owner.Guid));
    }

    private static bool TryGetSession(
        CharacterInspectionScreen screen,
        out SimulacrumInventorySession session)
    {
        session = null;

        return screen != null && Sessions.TryGetValue(screen, out session);
    }

    private static void ShowUnavailable(string key)
    {
        Gui.GuiService.ShowAlert(key, Gui.ColorFailure, 2.5f);
    }

    private sealed class SimulacrumInventorySession
    {
        private readonly bool _abilityScoresWasActive;
        private readonly bool _attackModesWasActive;
        private readonly bool _characterInformationWasActive;
        private readonly bool _characterStatsWasActive;
        private readonly bool _craftingWasActive;
        private readonly bool _personalityMapWasActive;
        private readonly bool _proficienciesWasActive;

        internal SimulacrumInventorySession(
            CharacterInspectionScreen screen,
            ulong subjectGuid,
            ulong transportGuid)
        {
            SubjectGuid = subjectGuid;
            TransportGuid = transportGuid;
            _abilityScoresWasActive = screen.abilityScoresListingPanel.gameObject.activeSelf;
            _attackModesWasActive = screen.attackModesPanel.gameObject.activeSelf;
            _characterInformationWasActive =
                screen.characterInformationPanel.gameObject.activeSelf;
            _characterStatsWasActive = screen.characterStatsPanel.gameObject.activeSelf;
            _craftingWasActive = screen.craftingPanel.gameObject.activeSelf;
            _personalityMapWasActive = screen.personalityMapPanel.gameObject.activeSelf;
            _proficienciesWasActive = screen.proficienciesPanel.gameObject.activeSelf;
        }

        internal bool Binding { get; set; }
        internal bool Bound { get; set; }
        internal GuiCharacter GuiCharacter { get; set; }
        internal RulesetContainer ExternalContainer { get; set; }
        internal bool PanelShown { get; set; }
        internal string PreviewEquipmentSignature { get; set; }
        internal bool PreviewRefreshPending { get; set; }
        internal int PreviewRefreshRevision { get; set; }
        internal bool ToggleGroupWasActive { get; set; }
        internal SimulacrumInventorySessionState State { get; set; } =
            SimulacrumInventorySessionState.Opening;
        internal ulong SubjectGuid { get; }
        internal ulong TransportGuid { get; }

        internal bool TryGetSubject(out RulesetCharacterSimulacrum character)
        {
            return RulesetEntity.TryGetEntity(SubjectGuid, out character);
        }

        internal void RestorePanelVisibility(CharacterInspectionScreen screen)
        {
            screen.abilityScoresListingPanel.gameObject.SetActive(_abilityScoresWasActive);
            screen.attackModesPanel.gameObject.SetActive(_attackModesWasActive);
            screen.characterInformationPanel.gameObject.SetActive(
                _characterInformationWasActive);
            screen.characterStatsPanel.gameObject.SetActive(_characterStatsWasActive);
            screen.craftingPanel.gameObject.SetActive(_craftingWasActive);
            screen.personalityMapPanel.gameObject.SetActive(_personalityMapWasActive);
            screen.proficienciesPanel.gameObject.SetActive(_proficienciesWasActive);
        }
    }

    private sealed class ExternalContainerTransferScope : IDisposable
    {
        private readonly (ulong CharacterGuid, ulong ItemGuid) _key;
        private bool _disposed;

        internal ExternalContainerTransferScope(ulong characterGuid, ulong itemGuid)
        {
            _key = (characterGuid, itemGuid);
            PendingExternalContainerTransfers.Add(_key);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            PendingExternalContainerTransfers.Remove(_key);
        }
    }

    private enum SimulacrumInventorySessionState
    {
        Opening,
        Bound,
        Closing,
        Failed,
        Closed
    }
}
