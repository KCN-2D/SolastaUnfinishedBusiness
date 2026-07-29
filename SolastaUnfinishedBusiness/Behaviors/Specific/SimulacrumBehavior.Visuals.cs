using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.Infrastructure;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Spells;
using SolastaUnfinishedBusiness.Subclasses;
using TA;
using UnityEngine;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal sealed partial class SimulacrumBehavior
{
    internal static SimulacrumRefreshState CaptureRefreshState(
        RulesetCharacterMonster character)
    {
        if (character is not RulesetCharacterSimulacrum
            {
                LifecycleState: SimulacrumLifecycleState.Ready
            } duplicate ||
            !TryGetSnapshot(duplicate, out var snapshot) ||
            !snapshot.IsCurrentSchema)
        {
            return null;
        }

        var restoresSnapshot = duplicate.BeginRefreshAllTransaction();

        try
        {
            return new SimulacrumRefreshState(duplicate, snapshot, restoresSnapshot);
        }
        catch
        {
            duplicate.EndRefreshAllTransaction(out _);
            throw;
        }
    }

    internal static void RestoreAfterRefresh(
        RulesetCharacterMonster character,
        SimulacrumRefreshState refreshState)
    {
        if (refreshState == null)
        {
            return;
        }

        if (character is not RulesetCharacterSimulacrum duplicate ||
            duplicate.Guid != refreshState.CharacterGuid)
        {
            return;
        }

        var restored = false;
        var publish = false;

        try
        {
            if (duplicate.LifecycleState != SimulacrumLifecycleState.Ready)
            {
                return;
            }

            refreshState.Restore(duplicate);
            restored = true;
        }
        catch (Exception ex)
        {
            HandleSnapshotFailure(
                duplicate,
                refreshState.Snapshot,
                "restoring runtime state after refresh",
                ex);
        }
        finally
        {
            publish = refreshState.Complete(duplicate, out _);
        }

        if (!restored)
        {
            if (publish)
            {
                duplicate.DiscardDeferredRepertoireRefreshes();
            }

            return;
        }

        if (!publish)
        {
            return;
        }

        PublishRestoredState(duplicate);
    }

    internal static void AbortRefreshAfterException(
        RulesetCharacterMonster character,
        SimulacrumRefreshState refreshState,
        Exception exception)
    {
        if (refreshState == null ||
            character is not RulesetCharacterSimulacrum duplicate ||
            duplicate.Guid != refreshState.CharacterGuid ||
            refreshState.Completed)
        {
            return;
        }

        var outermost = refreshState.Complete(duplicate, out _);

        if (outermost)
        {
            duplicate.DiscardDeferredRepertoireRefreshes();
        }

        Trace.LogException(new Exception(
            "Error completing a native Simulacrum refresh.",
            exception));
    }

    internal static bool ShouldDeferRefreshNotification(
        RulesetCharacterMonster character,
        string source)
    {
        if (character is not RulesetCharacterSimulacrum duplicate ||
            !duplicate.DeferRefreshNotification())
        {
            return false;
        }

        return true;
    }

    internal sealed class SimulacrumRefreshState
    {
        private readonly SimulacrumSnapshotRulesetCondition.DuplicateRuntimeState _runtimeState;

        internal SimulacrumRefreshState(
            RulesetCharacterSimulacrum character,
            SimulacrumSnapshotRulesetCondition snapshot,
            bool restoresSnapshot)
        {
            CharacterGuid = character.Guid;
            CurrentHitPoints = character.CurrentHitPoints;
            Snapshot = snapshot;
            RestoresSnapshot = restoresSnapshot;

            if (restoresSnapshot)
            {
                _runtimeState = new SimulacrumSnapshotRulesetCondition.DuplicateRuntimeState(
                    character,
                    CurrentHitPoints);
            }
        }

        internal ulong CharacterGuid { get; }
        internal int CurrentHitPoints { get; }
        internal SimulacrumSnapshotRulesetCondition Snapshot { get; }
        internal bool RestoresSnapshot { get; }
        internal bool Completed { get; private set; }

        internal void Restore(RulesetCharacterSimulacrum character)
        {
            if (RestoresSnapshot)
            {
                RestoreSnapshotPreservingRuntime(character, Snapshot, _runtimeState);
            }
        }

        internal bool Complete(
            RulesetCharacterSimulacrum character,
            out bool hadPendingNotification)
        {
            if (Completed)
            {
                hadPendingNotification = false;

                return false;
            }

            Completed = true;

            return character.EndRefreshAllTransaction(out hadPendingNotification);
        }
    }

    internal sealed class SimulacrumAppearanceSeed
    {
        internal SimulacrumAppearanceSeed(
            string displayName,
            bool usesInventoryAppearance,
            CreatureSex sex,
            string voiceId,
            string bodyAssetPrefix,
            string armorAssetPrefix,
            string helmetAssetPrefix,
            string faceShapeAssetPrefix,
            string beardShapeAssetPrefix,
            string hairShapeAssetPrefix,
            string hornsTailAssetPrefix,
            HumanoidMonsterPresentationDefinition humanoidPresentation,
            MonsterPresentationDefinition monsterPresentation,
            string definitionPresentationSourceName,
            IReadOnlyDictionary<MorphotypeElementDefinition.ElementCategory, string> morphotypeElements,
            IReadOnlyDictionary<MorphotypeElementDefinition.ElementCategory, float> morphotypeAdditionalValues)
        {
            DisplayName = displayName;
            UsesInventoryAppearance = usesInventoryAppearance;
            Sex = sex;
            VoiceId = voiceId;
            BodyAssetPrefix = bodyAssetPrefix;
            ArmorAssetPrefix = armorAssetPrefix;
            HelmetAssetPrefix = helmetAssetPrefix;
            FaceShapeAssetPrefix = faceShapeAssetPrefix;
            BeardShapeAssetPrefix = beardShapeAssetPrefix;
            HairShapeAssetPrefix = hairShapeAssetPrefix;
            HornsTailAssetPrefix = hornsTailAssetPrefix;
            HumanoidPresentation = humanoidPresentation;
            MonsterPresentation = monsterPresentation;
            DefinitionPresentationSourceName = definitionPresentationSourceName ?? string.Empty;
            MorphotypeElements = morphotypeElements == null
                ? new Dictionary<MorphotypeElementDefinition.ElementCategory, string>()
                : morphotypeElements.ToDictionary(pair => pair.Key, pair => pair.Value);
            MorphotypeAdditionalValues = morphotypeAdditionalValues == null
                ? new Dictionary<MorphotypeElementDefinition.ElementCategory, float>()
                : morphotypeAdditionalValues.ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        internal string DisplayName { get; }
        internal bool UsesInventoryAppearance { get; }
        internal CreatureSex Sex { get; }
        internal string VoiceId { get; }
        internal string BodyAssetPrefix { get; }
        internal string ArmorAssetPrefix { get; }
        internal string HelmetAssetPrefix { get; }
        internal string FaceShapeAssetPrefix { get; }
        internal string BeardShapeAssetPrefix { get; }
        internal string HairShapeAssetPrefix { get; }
        internal string HornsTailAssetPrefix { get; }
        internal HumanoidMonsterPresentationDefinition HumanoidPresentation { get; }
        internal MonsterPresentationDefinition MonsterPresentation { get; }
        internal string DefinitionPresentationSourceName { get; }
        internal IReadOnlyDictionary<MorphotypeElementDefinition.ElementCategory, string> MorphotypeElements { get; }
        internal IReadOnlyDictionary<MorphotypeElementDefinition.ElementCategory, float> MorphotypeAdditionalValues { get; }

        internal void ApplyTo(RulesetCharacterSimulacrum duplicate)
        {
            duplicate.SetCreationAppearanceMode(UsesInventoryAppearance);
            duplicate.ForcedName = DisplayName;
            duplicate.Sex = Sex;
            duplicate.VoiceID = VoiceId;
            duplicate.MorphotypeElements = MorphotypeElements.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
            duplicate.MorphotypeElementAdditionalValues =
                MorphotypeAdditionalValues.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value);

            MonsterBodyAssetPrefix(duplicate) = BodyAssetPrefix;
            MonsterArmorAssetPrefix(duplicate) = ArmorAssetPrefix;
            MonsterHelmetAssetPrefix(duplicate) = HelmetAssetPrefix;
            MonsterFaceShapeAssetPrefix(duplicate) = FaceShapeAssetPrefix;
            MonsterBeardShapeAssetPrefix(duplicate) = BeardShapeAssetPrefix;
            MonsterHairShapeAssetPrefix(duplicate) = HairShapeAssetPrefix;
            MonsterHornsTailAssetPrefix(duplicate) = HornsTailAssetPrefix;
            HumanoidPresentation(duplicate) = HumanoidPresentation;
            MonsterPresentation(duplicate) = MonsterPresentation;
        }
    }

    internal static bool UsesInventoryAppearance(RulesetCharacterSimulacrum character)
    {
        return character != null &&
               ((TryGetSnapshot(character, out var snapshot) &&
                 snapshot.IsCurrentSchema &&
                 snapshot.UsesInventoryAppearance) ||
                character.UsesInventoryAppearanceSeed);
    }

    internal static void PrepareInventoryAppearance(RulesetCharacterSimulacrum character)
    {
        if (character == null || !UsesInventoryAppearance(character))
        {
            return;
        }

        if (character.LifecycleState == SimulacrumLifecycleState.Initializing &&
            InitializingSnapshotSeeds.TryGetValue(character, out var seed))
        {
            seed.Appearance.ApplyTo(character);
        }
        else if (character.LifecycleState == SimulacrumLifecycleState.Ready &&
                 TryGetSnapshot(character, out var snapshot) &&
                 snapshot.IsCurrentSchema)
        {
            snapshot.PrepareAppearance(character);
        }
    }

    internal static void RefreshEquipment(RulesetCharacterSimulacrum character)
    {
        if (character?.LifecycleState != SimulacrumLifecycleState.Ready ||
            !TryGetSnapshot(character, out var snapshot) ||
            !snapshot.IsCurrentSchema)
        {
            return;
        }

        try
        {
            UnbindSnapshotHandlers(character);
            snapshot.Reapply(character);
            character.SetLifecycleState(SimulacrumLifecycleState.Ready);
            GrappleContext.ReleaseGrappleIfNoFreeHand(character);
            RefreshLocationState(character);
            RefreshVisuals(character);
        }
        catch (Exception ex)
        {
            HandleSnapshotFailure(character, snapshot, "refreshing Simulacrum equipment", ex);
        }
        finally
        {
            if (character.LifecycleState == SimulacrumLifecycleState.Ready)
            {
                BindSnapshotHandlers(character);
            }
        }
    }

    private static void RefreshLocationState(
        RulesetCharacterSimulacrum character,
        bool settleInitialPlacement = false)
    {
        if (character?.LifecycleState != SimulacrumLifecycleState.Ready ||
            GameLocationCharacter.GetFromActor(character) is not { } locationCharacter)
        {
            return;
        }

        if (settleInitialPlacement)
        {
            var wasProne = locationCharacter.Prone;

            // ApplySummonForm can leave the freshly placed location character in its transient
            // falling state even though native placement has already completed. GetActionStatus
            // rejects every non-reaction action while this flag is set, which disables attacks,
            // spells, rituals, powers, cautious movement, looting and jump execution together.
            locationCharacter.ForceEndFallIfNecessary();
            locationCharacter.Falling = false;

            if (wasProne)
            {
                locationCharacter.SetProne(false);
            }
        }

        // The native summon registers its GameLocationCharacter before the snapshot adds the
        // copied action affinities, powers and attack modes. Rebuild the location-side filters
        // after that state is complete; otherwise the actions are visible but remain disabled.
        locationCharacter.RefreshActionPerformances();

        // RefreshAll notifies visibility before the snapshot restores its final sense modes.
        // The native callback only marks this character's line of sight as dirty. Recompute both
        // stages now so targeting cannot observe the shell's stale sensor state for another frame.
        character.LineOfSightParametersModified?.Invoke(character);

        if (ServiceRepository.GetService<IGameLocationVisibilityService>() is
            GameLocationVisibilityManager visibilityManager)
        {
            visibilityManager.UpdateLineOfSight();
            visibilityManager.UpdatePerception();
        }
    }

    internal static string GetVisualEquipmentSignature(
        RulesetCharacterSimulacrum character)
    {
        var inventory = character?.CharacterInventory;

        if (inventory?.InventorySlotsByName == null)
        {
            return "<no-inventory>";
        }

        var equippedItems = inventory.InventorySlotsByName
            .Where(pair =>
                pair.Value != null &&
                !pair.Value.ConfigSlot &&
                !pair.Value.Disabled &&
                pair.Value.EquipedItem?.ItemDefinition != null)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var item = pair.Value.EquipedItem;
                var definition = item.ItemDefinition;
                var assetGuid = definition.ItemPresentation?.AssetReference?.AssetGUID;

                return
                    $"{pair.Key}={definition.Name}:{item.Guid}:" +
                    $"{assetGuid ?? "<no-asset>"}";
            })
            .ToArray();

        return equippedItems.Length == 0
            ? "<empty>"
            : string.Join(",", equippedItems);
    }

    internal static bool TryGetVisualRefreshState(
        RulesetCharacterSimulacrum character,
        out int requestedRevision,
        out string equipmentSignature)
    {
        requestedRevision = 0;
        equipmentSignature = GetVisualEquipmentSignature(character);

        if (character == null ||
            !VisualRefreshStates.TryGetValue(character, out var refreshState))
        {
            return false;
        }

        requestedRevision = refreshState.RequestedRevision;
        equipmentSignature =
            refreshState.RequestedEquipmentSignature ?? equipmentSignature;

        return true;
    }

    internal static bool IsVisualRevisionReady(
        RulesetCharacterSimulacrum character,
        int visualRevision,
        string equipmentSignature)
    {
        if (character == null ||
            !VisualRefreshStates.TryGetValue(character, out var refreshState) ||
            refreshState.RefreshPending ||
            refreshState.RequestedRevision != visualRevision)
        {
            return false;
        }

        var completed =
            refreshState.CompletedRevision == visualRevision &&
            string.Equals(
                refreshState.CompletedEquipmentSignature,
                equipmentSignature,
                StringComparison.Ordinal);
        var failed =
            refreshState.FailedRevision == visualRevision &&
            string.Equals(
                refreshState.FailedEquipmentSignature,
                equipmentSignature,
                StringComparison.Ordinal);

        // A terminal presentation failure must not leave portrait and inventory
        // coroutines waiting forever. They may publish the best available render,
        // while CompletedRevision remains unchanged until a later equipment event
        // retries successfully.
        return completed || failed;
    }

    private static void RefreshVisuals(RulesetCharacter character)
    {
        if (character is not RulesetCharacterSimulacrum
            {
                LifecycleState: SimulacrumLifecycleState.Ready
            } duplicate)
        {
            return;
        }

        var refreshState = VisualRefreshStates.GetValue(
            duplicate,
            _ => new VisualRefreshState());
        var equipmentSignature = GetVisualEquipmentSignature(duplicate);
        var sameRequestedSignature = string.Equals(
            refreshState.RequestedEquipmentSignature,
            equipmentSignature,
            StringComparison.Ordinal);
        var completedOnCurrentGraphics =
            sameRequestedSignature &&
            refreshState.RequestedRevision == refreshState.CompletedRevision &&
            string.Equals(
                refreshState.CompletedEquipmentSignature,
                equipmentSignature,
                StringComparison.Ordinal) &&
            IsCurrentWorldGraphics(
                duplicate,
                refreshState.CompletedGraphicsCharacter);

        if (sameRequestedSignature &&
            (refreshState.RefreshPending || completedOnCurrentGraphics))
        {
            return;
        }

        if (!sameRequestedSignature)
        {
            refreshState.RequestedRevision++;
            refreshState.RequestedEquipmentSignature = equipmentSignature;
            ResetVisualRefreshFailure(refreshState);
            NotifyVisualRefreshStarted(duplicate, refreshState);
        }
        else
        {
            // This is a later native equipment/location notification after a
            // terminal presentation failure. Give the unchanged signature a fresh,
            // bounded retry budget instead of suppressing it forever.
            ResetVisualRefreshFailure(refreshState);
        }

        if (refreshState.RefreshPending)
        {
            return;
        }

        BeginVisualRefresh(duplicate, refreshState);
    }

    private static void NotifyVisualRefreshStarted(
        RulesetCharacterSimulacrum duplicate,
        VisualRefreshState refreshState)
    {
        SimulacrumPortraits.MarkDirty(
            duplicate,
            refreshState.RequestedRevision,
            refreshState.RequestedEquipmentSignature);
        SimulacrumEquipmentPanel.MarkPreviewDirty(
            duplicate,
            refreshState.RequestedRevision,
            refreshState.RequestedEquipmentSignature);
    }

    private static void NotifyVisualRefreshAvailable(
        RulesetCharacterSimulacrum duplicate,
        int revision,
        string equipmentSignature)
    {
        SimulacrumPortraits.Refresh(duplicate, revision, equipmentSignature);
        SimulacrumEquipmentPanel.QueuePreviewRefresh(
            duplicate,
            revision,
            equipmentSignature);
    }

    private static void BeginVisualRefresh(
        RulesetCharacterSimulacrum duplicate,
        VisualRefreshState refreshState)
    {
        if (duplicate?.LifecycleState != SimulacrumLifecycleState.Ready)
        {
            StopVisualRefresh(refreshState);

            return;
        }

        var requestedRevision = refreshState.RequestedRevision;
        var equipmentSignature = refreshState.RequestedEquipmentSignature;

        if (ServiceRepository.GetService<IGraphicsCharacterFactoryService>() is not
            { } graphicsFactory)
        {
            ScheduleVisualRefreshRetry(duplicate, refreshState);

            return;
        }

        var locationCharacter = GameLocationCharacter.GetFromActor(duplicate);
        var entityFactory = ServiceRepository.GetService<IWorldLocationEntityFactoryService>();

        if (locationCharacter == null ||
            entityFactory == null ||
            !entityFactory.TryFindWorldCharacter(locationCharacter, out var worldCharacter) ||
            worldCharacter?.GraphicsCharacter is not GraphicsCharacterMonster graphicsCharacter)
        {
            ScheduleVisualRefreshRetry(duplicate, refreshState);

            return;
        }

        // A simulacrum can have both a world model and a temporary inventory-preview model.
        // Refreshing every matching GraphicsCharacter races the factory's shared refresh queue,
        // leaving the preview in bind-time T-pose or with fallback morph colors. Only the world
        // entity owns persistent graphics; the preview is rebuilt by its screen binding.
        Patches.GraphicsCharacterFactoryManagerPatcher.ApplySimulacrumWeaponStance(
            graphicsCharacter);

        var attempt = ++refreshState.NextAttempt;
        var requiresInventoryFinalization = UsesInventoryAppearance(duplicate);
        var finalizationGeneration = requiresInventoryFinalization
            ? Patches.GraphicsCharacterFactoryManagerPatcher
                .GetInventoryAppearanceFinalizationGeneration(graphicsCharacter)
            : 0;

        refreshState.ActiveAttempt = attempt;
        refreshState.RefreshPending = true;

        try
        {
            graphicsFactory.RefreshGraphicsCharacter(
                graphicsCharacter,
                () =>
                {
                    refreshState.LastCallbackAttempt = attempt;
                    var finalizationObserved = !requiresInventoryFinalization;
                    var finalizationSucceeded = !requiresInventoryFinalization;

                    if (requiresInventoryFinalization)
                    {
                        finalizationObserved =
                            Patches.GraphicsCharacterFactoryManagerPatcher
                            .TryGetInventoryAppearanceFinalizationResult(
                                graphicsCharacter,
                                finalizationGeneration,
                                out finalizationSucceeded);
                    }

                    try
                    {
                        Patches.GraphicsCharacterFactoryManagerPatcher.ApplySimulacrumWeaponStance(
                            graphicsCharacter);
                    }
                    catch (Exception exception)
                    {
                        finalizationSucceeded = false;
                        Trace.LogException(new Exception(
                            "Error applying the Simulacrum weapon stance after a graphics refresh.",
                            exception));
                    }

                    if (!finalizationObserved)
                    {
                        finalizationSucceeded = false;
                    }

                    // Native invokes this callback before its iterator releases the shared
                    // graphics load buffers. Complete or retry on the next frame only.
                    TryStartVisualRefreshCoroutine(
                        refreshState,
                        ContinueVisualRefreshAfterFactoryRelease(
                            duplicate,
                            refreshState,
                            graphicsCharacter,
                            attempt,
                            requestedRevision,
                            equipmentSignature,
                            finalizationSucceeded));
                });

            if (refreshState.ActiveAttempt == attempt &&
                refreshState.LastCallbackAttempt != attempt)
            {
                TryStartVisualRefreshCoroutine(
                    refreshState,
                    MonitorVisualRefreshCallback(
                        duplicate,
                        refreshState,
                        attempt));
            }
        }
        catch (Exception exception)
        {
            if (refreshState.ActiveAttempt == attempt)
            {
                refreshState.ActiveAttempt = 0;
            }

            Trace.LogException(new Exception(
                "Error dispatching a Simulacrum graphics refresh.",
                exception));
            ScheduleVisualRefreshRetry(duplicate, refreshState);
        }
    }

    private static IEnumerator ContinueVisualRefreshAfterFactoryRelease(
        RulesetCharacterSimulacrum duplicate,
        VisualRefreshState refreshState,
        GraphicsCharacterMonster graphicsCharacter,
        int attempt,
        int completedRevision,
        string completedEquipmentSignature,
        bool finalizationSucceeded)
    {
        yield return null;

        if (duplicate?.LifecycleState != SimulacrumLifecycleState.Ready)
        {
            StopVisualRefresh(refreshState);

            yield break;
        }

        var liveEquipmentSignature = GetVisualEquipmentSignature(duplicate);
        var matchesLatestRequest =
            string.Equals(
                refreshState.RequestedEquipmentSignature,
                completedEquipmentSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                liveEquipmentSignature,
                completedEquipmentSignature,
                StringComparison.Ordinal);
        var currentGraphics = IsCurrentWorldGraphics(duplicate, graphicsCharacter);

        if (finalizationSucceeded &&
            matchesLatestRequest &&
            currentGraphics)
        {
            if (refreshState.RequestedRevision == refreshState.CompletedRevision &&
                string.Equals(
                    refreshState.CompletedEquipmentSignature,
                    completedEquipmentSignature,
                    StringComparison.Ordinal) &&
                ReferenceEquals(
                    refreshState.CompletedGraphicsCharacter,
                    graphicsCharacter))
            {
                if (refreshState.ActiveAttempt == attempt)
                {
                    refreshState.ActiveAttempt = 0;
                    refreshState.RefreshPending = false;
                }

                yield break;
            }

            // An accepted native request can finish after its watchdog has already queued a
            // retry. Its result is still authoritative when it rendered the current world
            // graphics with the latest equipment signature.
            completedRevision = refreshState.RequestedRevision;
            CompleteVisualRefresh(
                duplicate,
                refreshState,
                graphicsCharacter,
                completedRevision,
                completedEquipmentSignature);

            yield break;
        }

        if (refreshState.ActiveAttempt == attempt)
        {
            refreshState.ActiveAttempt = 0;
        }

        if (refreshState.ActiveAttempt == 0)
        {
            ScheduleVisualRefreshRetry(duplicate, refreshState);
        }
    }

    private static IEnumerator MonitorVisualRefreshCallback(
        RulesetCharacterSimulacrum duplicate,
        VisualRefreshState refreshState,
        int attempt)
    {
        var deadline = Time.realtimeSinceStartup + VisualRefreshCallbackTimeoutSeconds;

        while (duplicate?.LifecycleState == SimulacrumLifecycleState.Ready &&
               refreshState.ActiveAttempt == attempt &&
               refreshState.LastCallbackAttempt != attempt &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        if (duplicate?.LifecycleState != SimulacrumLifecycleState.Ready)
        {
            StopVisualRefresh(refreshState);

            yield break;
        }

        if (refreshState.ActiveAttempt != attempt ||
            refreshState.LastCallbackAttempt == attempt)
        {
            yield break;
        }

        refreshState.ActiveAttempt = 0;
        ScheduleVisualRefreshRetry(duplicate, refreshState);
    }

    private static void ScheduleVisualRefreshRetry(
        RulesetCharacterSimulacrum duplicate,
        VisualRefreshState refreshState)
    {
        if (duplicate?.LifecycleState != SimulacrumLifecycleState.Ready)
        {
            StopVisualRefresh(refreshState);

            return;
        }

        if (refreshState.RetryScheduled)
        {
            return;
        }

        refreshState.ConsecutiveFailures++;

        if (refreshState.ConsecutiveFailures >=
            VisualRefreshMaximumConsecutiveFailures)
        {
            FailVisualRefresh(duplicate, refreshState);

            return;
        }

        refreshState.RefreshPending = true;
        refreshState.RetryScheduled = true;

        var retryGeneration = ++refreshState.RetryGeneration;
        var delayFrames = Math.Min(
            VisualRefreshMinimumRetryDelayFrames <<
            Math.Min(refreshState.ConsecutiveFailures - 1, 3),
            VisualRefreshMaximumRetryDelayFrames);

        TryStartVisualRefreshCoroutine(
            refreshState,
            RetryVisualRefreshAfterDelay(
                duplicate,
                refreshState,
                retryGeneration,
                delayFrames));
    }

    private static IEnumerator RetryVisualRefreshAfterDelay(
        RulesetCharacterSimulacrum duplicate,
        VisualRefreshState refreshState,
        int retryGeneration,
        int delayFrames)
    {
        for (var frame = 0; frame < delayFrames; frame++)
        {
            yield return null;

            if (duplicate?.LifecycleState != SimulacrumLifecycleState.Ready ||
                refreshState.RetryGeneration != retryGeneration)
            {
                yield break;
            }
        }

        if (duplicate?.LifecycleState != SimulacrumLifecycleState.Ready)
        {
            StopVisualRefresh(refreshState);

            yield break;
        }

        if (refreshState.RetryGeneration != retryGeneration)
        {
            yield break;
        }

        refreshState.RetryScheduled = false;
        refreshState.RefreshPending = false;

        if (!string.Equals(
                GetVisualEquipmentSignature(duplicate),
                refreshState.RequestedEquipmentSignature,
                StringComparison.Ordinal))
        {
            RefreshVisuals(duplicate);

            yield break;
        }

        BeginVisualRefresh(duplicate, refreshState);
    }

    private static void CompleteVisualRefresh(
        RulesetCharacterSimulacrum duplicate,
        VisualRefreshState refreshState,
        GraphicsCharacterMonster graphicsCharacter,
        int completedRevision,
        string equipmentSignature)
    {
        refreshState.CompletedRevision = completedRevision;
        refreshState.CompletedEquipmentSignature = equipmentSignature;
        refreshState.CompletedGraphicsCharacter = graphicsCharacter;
        refreshState.ActiveAttempt = 0;
        refreshState.ConsecutiveFailures = 0;
        refreshState.FailedRevision = 0;
        refreshState.FailedEquipmentSignature = null;
        refreshState.RefreshPending = false;
        refreshState.RetryScheduled = false;
        refreshState.RetryGeneration++;
        NotifyVisualRefreshAvailable(
            duplicate,
            completedRevision,
            equipmentSignature);
    }

    private static void FailVisualRefresh(
        RulesetCharacterSimulacrum duplicate,
        VisualRefreshState refreshState)
    {
        refreshState.ActiveAttempt = 0;
        refreshState.FailedRevision = refreshState.RequestedRevision;
        refreshState.FailedEquipmentSignature =
            refreshState.RequestedEquipmentSignature;
        refreshState.RefreshPending = false;
        refreshState.RetryScheduled = false;
        refreshState.RetryGeneration++;

        // Release UI consumers with the best render available. The failure is not
        // recorded as CompletedRevision, and any later native notification for the
        // same signature starts a new bounded retry series.
        NotifyVisualRefreshAvailable(
            duplicate,
            refreshState.RequestedRevision,
            refreshState.RequestedEquipmentSignature);
    }

    private static void ResetVisualRefreshFailure(VisualRefreshState refreshState)
    {
        refreshState.ConsecutiveFailures = 0;
        refreshState.FailedRevision = 0;
        refreshState.FailedEquipmentSignature = null;
    }

    private static bool IsCurrentWorldGraphics(
        RulesetCharacterSimulacrum duplicate,
        GraphicsCharacterMonster expectedGraphics)
    {
        if (!expectedGraphics ||
            GameLocationCharacter.GetFromActor(duplicate) is not { } locationCharacter ||
            ServiceRepository.GetService<IWorldLocationEntityFactoryService>() is not
                { } entityFactory ||
            !entityFactory.TryFindWorldCharacter(locationCharacter, out var worldCharacter))
        {
            return false;
        }

        return ReferenceEquals(worldCharacter?.GraphicsCharacter, expectedGraphics);
    }

    private static bool TryStartVisualRefreshCoroutine(
        VisualRefreshState refreshState,
        IEnumerator coroutine)
    {
        var host = Gui.GameLocation;

        if (host == null || !host.isActiveAndEnabled)
        {
            StopVisualRefresh(refreshState);

            return false;
        }

        try
        {
            host.StartCoroutine(coroutine);

            return true;
        }
        catch (Exception exception)
        {
            StopVisualRefresh(refreshState);
            Trace.LogException(new Exception(
                "Error starting a Simulacrum visual refresh coroutine.",
                exception));

            return false;
        }
    }

    private static void ResetVisualRefreshForLocationRestore(
        RulesetCharacterSimulacrum duplicate)
    {
        if (duplicate == null ||
            !VisualRefreshStates.TryGetValue(duplicate, out var refreshState))
        {
            return;
        }

        StopVisualRefresh(refreshState);
        refreshState.CompletedGraphicsCharacter = null;
        refreshState.CompletedEquipmentSignature = null;
        ResetVisualRefreshFailure(refreshState);
    }

    private static void StopVisualRefresh(VisualRefreshState refreshState)
    {
        refreshState.ActiveAttempt = 0;
        refreshState.RefreshPending = false;
        refreshState.RetryScheduled = false;
        refreshState.RetryGeneration++;
    }

    private sealed class VisualRefreshState
    {
        internal int ActiveAttempt { get; set; }
        internal int ConsecutiveFailures { get; set; }
        internal GraphicsCharacterMonster CompletedGraphicsCharacter { get; set; }
        internal int CompletedRevision { get; set; }
        internal string CompletedEquipmentSignature { get; set; }
        internal string FailedEquipmentSignature { get; set; }
        internal int FailedRevision { get; set; }
        internal int LastCallbackAttempt { get; set; }
        internal int NextAttempt { get; set; }
        internal bool RefreshPending { get; set; }
        internal int RequestedRevision { get; set; }
        internal string RequestedEquipmentSignature { get; set; }
        internal int RetryGeneration { get; set; }
        internal bool RetryScheduled { get; set; }
    }

}
