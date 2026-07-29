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
    public void OnBeforeEffectTerminated(RulesetEffect activeEffect)
    {
        if (activeEffect?.GetSourceDefinitionSafe() != _spellDefinition)
        {
            return;
        }

        var owner = EffectHelpers.GetCharacterByGuid(activeEffect.SourceGuid);

        try
        {
            foreach (var duplicate in EffectHelpers
                         .GetSummonedCreatures(activeEffect)
                         .OfType<RulesetCharacterSimulacrum>())
            {
                duplicate.SetLifecycleState(SimulacrumLifecycleState.Terminating);
                UnbindSnapshotHandlers(duplicate);
                SimulacrumEquipmentPanel.CloseForCharacter(duplicate);
                duplicate.EvacuateInventory(owner);
            }
        }
        catch (Exception ex)
        {
            Trace.LogException(new Exception(
                "Error preserving Simulacrum inventory before effect termination.",
                ex));
        }
    }

    internal static bool TryGetOwner(
        RulesetCharacterSimulacrum character,
        out RulesetCharacterHero owner)
    {
        owner = null;

        if (!TryGetSnapshot(character, out var snapshot) ||
            EffectHelpers.GetCharacterByGuid(snapshot.SourceGuid) is not
                RulesetCharacterHero rulesetOwner)
        {
            return false;
        }

        owner = rulesetOwner;

        return true;
    }

    internal static RulesetCharacter FindOwnedSimulacrum(RulesetCharacter owner)
    {
        if (owner == null)
        {
            return null;
        }

        return EnumerateOwnerSimulacrumEffects(owner)
            .SelectMany(effect => EffectHelpers
                .GetSummonedCreatures(effect)
                .Where(duplicate =>
                    duplicate is RulesetCharacterSimulacrum
                    {
                        LifecycleState: SimulacrumLifecycleState.Ready
                    } &&
                    duplicate.CurrentHitPoints > 0 &&
                    IsOwnedByEffect(duplicate, effect, owner.Guid)))
            .FirstOrDefault();
    }

    private static bool TryGetDismissEffect(
        RulesetCharacterSimulacrum duplicate,
        out RulesetEffect activeEffect,
        out RulesetCharacter owner)
    {
        activeEffect = null;
        owner = null;

        if (duplicate is not { LifecycleState: SimulacrumLifecycleState.Ready } ||
            !TryGetSnapshot(duplicate, out var snapshot) ||
            !TryResolveOwnedEffect(
                duplicate,
                snapshot.OwningEffectGuid,
                snapshot.SourceGuid,
                out var effect,
                out var effectOwner))
        {
            return false;
        }

        activeEffect = effect;
        owner = effectOwner;

        return true;
    }

    internal static void Reconcile(RulesetCharacter character)
    {
        if (!TryGetSnapshot(character, out var snapshot))
        {
            return;
        }

        if (character is not RulesetCharacterSimulacrum duplicate ||
            !snapshot.IsCurrentSchema)
        {
            return;
        }

        if (duplicate.LifecycleState is
            SimulacrumLifecycleState.CleanupPending or
            SimulacrumLifecycleState.Terminating)
        {
            return;
        }

        if (!TryResolveOwnedEffect(
                duplicate,
                snapshot.OwningEffectGuid,
                snapshot.SourceGuid,
                out var activeEffect,
                out var owner))
        {
            QueueRuntimeCleanup(duplicate, null, null);

            return;
        }

        if (character.CurrentHitPoints <= 0)
        {
            QueueRuntimeCleanup(duplicate, activeEffect, owner);

            return;
        }

        var runtimeState =
            new SimulacrumSnapshotRulesetCondition.DuplicateRuntimeState(
                duplicate,
                duplicate.CurrentHitPoints);

        UnbindSnapshotHandlers(character);

        try
        {
            snapshot.RestoreSnapshotValues(character, runtimeState);
            runtimeState.Restore(duplicate);
            duplicate.SetLifecycleState(SimulacrumLifecycleState.Ready);
            RefreshLocationState(duplicate);
            BindSnapshotHandlers(character);
            ResetVisualRefreshForLocationRestore(duplicate);
            RefreshVisuals(character);
        }
        catch (Exception ex)
        {
            HandleSnapshotFailure(character, snapshot, "restoring a location character", ex);

            return;
        }

        AddOwnerCondition(owner, activeEffect);
    }

    internal static void FlushDeferredCleanup()
    {
        if (DeferredCleanupCharacters.Count == 0)
        {
            return;
        }

        var pending = DeferredCleanupCharacters.ToArray();

        DeferredCleanupCharacters.Clear();

        foreach (var entry in pending)
        {
            var characterGuid = entry.Key;
            var showLegacyFeedback = entry.Value;
            var character = EffectHelpers.GetCharacterByGuid(characterGuid);

            if (character == null ||
                !TryGetSnapshot(character, out var snapshot))
            {
                continue;
            }

            try
            {
                if (showLegacyFeedback)
                {
                    Gui.GuiService.ShowAlert(
                        "Feedback/&SimulacrumLegacySnapshotRemoved",
                        Gui.ColorFailure,
                        4f);
                }

                if (!TerminateOwningEffect(character, snapshot))
                {
                    DestroyOrphan(character);
                }
            }
            catch (Exception ex)
            {
                Trace.LogException(new Exception(
                    "Error removing an invalid Simulacrum after guest restoration.",
                    ex));
            }
        }
    }

    private static void QueueEffectForRuntimeCleanup(
        RulesetCharacter owner,
        RulesetEffect activeEffect,
        IEnumerable<RulesetCharacterSimulacrum> knownSummons = null)
    {
        var summons = (knownSummons ?? EffectHelpers
                .GetSummonedCreatures(activeEffect)
                .OfType<RulesetCharacterSimulacrum>())
            .Where(summon => summon != null)
            .Distinct()
            .ToArray();

        if (summons.Length == 0)
        {
            activeEffect?.DoTerminate(owner);

            return;
        }

        foreach (var summon in summons)
        {
            QueueRuntimeCleanup(summon, activeEffect, owner);
        }
    }

    private static void AddOwnerCondition(RulesetCharacter owner, RulesetEffect activeEffect)
    {
        if (_ownerCondition == null ||
            owner.HasConditionOfCategoryAndType(AttributeDefinitions.TagEffect, _ownerCondition.Name))
        {
            return;
        }

        var condition = owner.InflictCondition(
            _ownerCondition.Name,
            DurationType.Permanent,
            0,
            TurnOccurenceType.EndOfTurn,
            AttributeDefinitions.TagEffect,
            owner.Guid,
            owner.CurrentFaction.Name,
            activeEffect.EffectLevel,
            _spellDefinition.Name,
            0,
            activeEffect.ComputeSourceAbilityBonus(owner),
            activeEffect.ComputeSourceProficiencyBonus(owner),
            0);

        activeEffect.TrackCondition(
            owner,
            owner.Guid,
            owner,
            owner.Guid,
            condition,
            AttributeDefinitions.TagEffect);
    }

    private static bool TerminateOwningEffect(
        RulesetCharacter duplicate,
        SimulacrumSnapshotRulesetCondition snapshot)
    {
        if (snapshot == null ||
            !TryResolveOwnedEffect(
                duplicate,
                snapshot.OwningEffectGuid,
                snapshot.SourceGuid,
                out var activeEffect,
                out var owner))
        {
            return false;
        }

        activeEffect.DoTerminate(owner);

        return true;
    }

    private static void DestroyOrphan(RulesetCharacter character)
    {
        if (character is RulesetCharacterSimulacrum duplicate)
        {
            duplicate.SetLifecycleState(SimulacrumLifecycleState.Terminating);
            UnbindSnapshotHandlers(duplicate);
            SimulacrumEquipmentPanel.CloseForCharacter(duplicate);
            TryGetOwner(duplicate, out var owner);
            duplicate.EvacuateInventory(owner);
            SimulacrumPortraits.Remove(duplicate);
        }

        if (GameLocationCharacter.GetFromActor(character) is not { } orphan)
        {
            return;
        }

        ServiceRepository.GetService<IGameLocationCharacterService>()
            ?.ForceDestroyCharacter(orphan, true);
    }

    private static bool HasOverlappingFootprint(
        GameLocationCharacter first,
        GameLocationCharacter second)
    {
        var occupiedPositions = new HashSet<int3>();

        foreach (var occupiedPosition in first.LocationBattleBoundingBox.EnumerateAllPositionsWithin())
        {
            occupiedPositions.Add(occupiedPosition);
        }

        foreach (var occupiedPosition in second.LocationBattleBoundingBox.EnumerateAllPositionsWithin())
        {
            if (occupiedPositions.Contains(occupiedPosition))
            {
                return true;
            }
        }

        return false;
    }

    private static void HandleSnapshotFailure(
        RulesetCharacter character,
        SimulacrumSnapshotRulesetCondition snapshot,
        string operation,
        Exception exception)
    {
        Trace.LogException(new Exception($"Error {operation} for Simulacrum.", exception));
        QueueRuntimeCleanup(
            character as RulesetCharacterSimulacrum,
            EffectHelpers.GetEffectByGuid(snapshot.OwningEffectGuid),
            EffectHelpers.GetCharacterByGuid(snapshot.SourceGuid));
    }

    private static void QueueRuntimeCleanup(
        RulesetCharacterSimulacrum character,
        RulesetEffect activeEffect,
        RulesetCharacter owner,
        bool showLegacyFeedback = false)
    {
        if (character == null)
        {
            return;
        }

        InitializingSnapshotSeeds.Remove(character);
        character.SetLifecycleState(SimulacrumLifecycleState.CleanupPending);
        UnbindSnapshotHandlers(character);
        SimulacrumEquipmentPanel.CloseForCharacter(character);
        SimulacrumPortraits.Remove(character);

        if (RuntimeCleanupCharacters.TryGetValue(character.Guid, out var existing))
        {
            existing.Merge(activeEffect?.Guid ?? 0, owner?.Guid ?? 0, showLegacyFeedback);

            if (showLegacyFeedback)
            {
                DeferredCleanupCharacters[character.Guid] = true;
            }

            return;
        }

        RuntimeCleanupCharacters.Add(
            character.Guid,
            new RuntimeCleanupRequest(
                activeEffect?.Guid ?? 0,
                owner?.Guid ?? 0,
                showLegacyFeedback));
        DeferredCleanupCharacters[character.Guid] =
            showLegacyFeedback ||
            (DeferredCleanupCharacters.TryGetValue(character.Guid, out var deferredFeedback) &&
             deferredFeedback);

        if (Gui.GameLocation)
        {
            Gui.GameLocation.StartCoroutine(CompleteRuntimeCleanup(character.Guid));

            return;
        }

        RuntimeCleanupCharacters.Remove(character.Guid);
        QueueDeferredCleanup(character, showLegacyFeedback);
    }

    private static IEnumerator CompleteRuntimeCleanup(ulong characterGuid)
    {
        // ApplySummonForm may return while WorldLocationCharacter.BindAsync is
        // still creating graphics. Keep the RulesetCharacter alive until that
        // request has either completed or timed out.
        yield return null;

        var deadline = Time.realtimeSinceStartup + 30f;

        while (Time.realtimeSinceStartup < deadline &&
               EffectHelpers.GetCharacterByGuid(characterGuid) is
                   RulesetCharacterSimulacrum character &&
               GameLocationCharacter.GetFromActor(character) is { } locationCharacter)
        {
            var entityFactory =
                ServiceRepository.GetService<IWorldLocationEntityFactoryService>();

            if (entityFactory != null &&
                entityFactory.TryFindWorldCharacter(locationCharacter, out var worldCharacter) &&
                worldCharacter?.GraphicsCharacter != null)
            {
                yield return null;

                break;
            }

            yield return null;
        }

        if (!RuntimeCleanupCharacters.TryGetValue(characterGuid, out var request))
        {
            yield break;
        }

        RuntimeCleanupCharacters.Remove(characterGuid);
        DeferredCleanupCharacters.Remove(characterGuid);

        if (EffectHelpers.GetCharacterByGuid(characterGuid) is not
            RulesetCharacterSimulacrum duplicate)
        {
            yield break;
        }

        duplicate.SetLifecycleState(SimulacrumLifecycleState.Terminating);

        if (request.ShowLegacyFeedback)
        {
            Gui.GuiService.ShowAlert(
                "Feedback/&SimulacrumLegacySnapshotRemoved",
                Gui.ColorFailure,
                4f);
        }

        var effectGuid = request.EffectGuid;
        var ownerGuid = request.OwnerGuid;

        if (TryGetSnapshot(duplicate, out var snapshot))
        {
            if (effectGuid != 0 && effectGuid != snapshot.OwningEffectGuid ||
                ownerGuid != 0 && ownerGuid != snapshot.SourceGuid)
            {
                DestroyOrphan(duplicate);

                yield break;
            }

            effectGuid = snapshot.OwningEffectGuid;
            ownerGuid = snapshot.SourceGuid;
        }

        if (TryResolveOwnedEffect(
                duplicate,
                effectGuid,
                ownerGuid,
                out var activeEffect,
                out var owner))
        {
            activeEffect.DoTerminate(owner);
        }
        else
        {
            DestroyOrphan(duplicate);
        }
    }

    private static void QueueDeferredCleanup(
        RulesetCharacter character,
        bool showLegacyFeedback)
    {
        if (character == null)
        {
            return;
        }

        if (character is RulesetCharacterSimulacrum duplicate)
        {
            duplicate.SetLifecycleState(SimulacrumLifecycleState.CleanupPending);
            UnbindSnapshotHandlers(duplicate);
            SimulacrumEquipmentPanel.CloseForCharacter(duplicate);
            SimulacrumPortraits.Remove(duplicate);
        }

        DeferredCleanupCharacters[character.Guid] =
            showLegacyFeedback ||
            (DeferredCleanupCharacters.TryGetValue(character.Guid, out var current) && current);
    }

    private sealed class RuntimeCleanupRequest(
        ulong effectGuid,
        ulong ownerGuid,
        bool showLegacyFeedback)
    {
        internal ulong EffectGuid { get; private set; } = effectGuid;
        internal ulong OwnerGuid { get; private set; } = ownerGuid;
        internal bool ShowLegacyFeedback { get; private set; } = showLegacyFeedback;

        internal void Merge(ulong newEffectGuid, ulong newOwnerGuid, bool showFeedback)
        {
            if ((EffectGuid == 0 || OwnerGuid == 0) &&
                newEffectGuid != 0 &&
                newOwnerGuid != 0)
            {
                EffectGuid = newEffectGuid;
                OwnerGuid = newOwnerGuid;
            }

            ShowLegacyFeedback |= showFeedback;
        }
    }

    private static void ReconcileOwner(RulesetCharacter owner)
    {
        if (owner == null)
        {
            return;
        }

        var effects = EnumerateOwnerSimulacrumEffects(owner).ToArray();
        var validEffects = effects
            .Where(effect => EffectHelpers
                .GetSummonedCreatures(effect)
                .Any(duplicate =>
                    duplicate.CurrentHitPoints > 0 &&
                    IsOwnedByEffect(duplicate, effect, owner.Guid)))
            .ToArray();

        foreach (var effect in effects.Except(validEffects))
        {
            effect.DoTerminate(owner);
        }

        if (validEffects.Length > 0)
        {
            AddOwnerCondition(owner, validEffects[0]);

            return;
        }

        if (_ownerCondition != null &&
            owner.TryGetConditionOfCategoryAndType(
                AttributeDefinitions.TagEffect,
                _ownerCondition.Name,
                out var orphanedOwnerCondition))
        {
            owner.RemoveCondition(orphanedOwnerCondition);
        }
    }

    private static IEnumerable<RulesetEffectSpell> EnumerateOwnerSimulacrumEffects(
        RulesetCharacter owner)
    {
        return owner == null
            ? Enumerable.Empty<RulesetEffectSpell>()
            : EffectHelpers
                .GetAllEffectsBySourceGuid(owner.Guid)
                .OfType<RulesetEffectSpell>()
                .Where(effect => effect.SpellDefinition == _spellDefinition);
    }

    private static bool IsOwnedByEffect(
        RulesetCharacter duplicate,
        RulesetEffect effect,
        ulong ownerGuid)
    {
        return TryGetSnapshot(duplicate, out var snapshot) &&
               snapshot.SourceGuid == ownerGuid &&
               snapshot.OwningEffectGuid == effect?.Guid &&
               IsExpectedSimulacrumEffect(duplicate, effect, ownerGuid);
    }

    private static bool IsExpectedSimulacrumEffect(
        RulesetCharacter duplicate,
        RulesetEffect effect,
        ulong ownerGuid)
    {
        return duplicate != null &&
               ownerGuid != 0 &&
               effect is RulesetEffectSpell
               {
                   Caster: { } caster,
                   SpellDefinition: { } spellDefinition
               } &&
               spellDefinition == _spellDefinition &&
               caster.Guid == ownerGuid &&
               EffectHelpers.GetSummonedCreatures(effect)
                   .Any(summon => summon?.Guid == duplicate.Guid);
    }

    private static bool TryResolveOwnedEffect(
        RulesetCharacter duplicate,
        ulong effectGuid,
        ulong ownerGuid,
        out RulesetEffectSpell activeEffect,
        out RulesetCharacter owner)
    {
        activeEffect = null;
        owner = null;

        if (effectGuid == 0 ||
            EffectHelpers.GetEffectByGuid(effectGuid) is not RulesetEffectSpell effect ||
            !IsExpectedSimulacrumEffect(duplicate, effect, ownerGuid) ||
            TryGetSnapshot(duplicate, out var snapshot) &&
            (snapshot.SourceGuid != ownerGuid ||
             snapshot.OwningEffectGuid != effectGuid))
        {
            return false;
        }

        activeEffect = effect;
        owner = effect.Caster;

        return true;
    }

    private static void BindSnapshotHandlers(RulesetCharacter character)
    {
        if (character is not RulesetCharacterSimulacrum
            {
                LifecycleState: SimulacrumLifecycleState.Ready
            })
        {
            return;
        }

        character.DamageSustained -= HandleDamageSustained;
        character.DamageSustained += HandleDamageSustained;
        character.CharacterRefreshed -= HandleCharacterRefreshed;
        character.CharacterRefreshed += HandleCharacterRefreshed;
    }

    private static void UnbindSnapshotHandlers(RulesetCharacter character)
    {
        if (character == null)
        {
            return;
        }

        character.DamageSustained -= HandleDamageSustained;
        character.CharacterRefreshed -= HandleCharacterRefreshed;
    }

    private static void HandleDamageSustained(
        RulesetCharacter character,
        int damage,
        string damageType,
        bool critical,
        bool stillConscious,
        bool notify)
    {
        if (character is not RulesetCharacterSimulacrum
            {
                LifecycleState: SimulacrumLifecycleState.Ready
            } ||
            character.CurrentHitPoints > 0 ||
            !TryGetSnapshot(character, out var snapshot))
        {
            return;
        }

        // Tactical damage schedules the native reduced-to-zero coroutine from the same event.
        // Let IOnReducedToZeroHp terminate the effect after that pipeline has completed.
        if (ServiceRepository.GetService<IGameLocationBattleService>() is
            {
                IsBattleInProgress: true
            } &&
            GameLocationCharacter.GetFromActor(character) != null)
        {
            return;
        }

        if (!TerminateOwningEffect(character, snapshot))
        {
            DestroyOrphan(character);
        }
    }

    private static void HandleCharacterRefreshed(RulesetCharacter character)
    {
        if (character is RulesetCharacterSimulacrum
            {
                LifecycleState: SimulacrumLifecycleState.Ready
            } duplicate &&
            !duplicate.PublishingRestoredState &&
            TryGetSnapshot(character, out var snapshot))
        {
            var runtimeState = new SimulacrumSnapshotRulesetCondition.DuplicateRuntimeState(
                duplicate,
                duplicate.CurrentHitPoints);

            try
            {
                RestoreSnapshotPreservingRuntime(duplicate, snapshot, runtimeState);
            }
            catch (Exception ex)
            {
                HandleSnapshotFailure(character, snapshot, "refreshing a character", ex);
            }
        }
    }

    private static void PublishRestoredState(RulesetCharacterSimulacrum character)
    {
        if (character.PublishingRestoredState)
        {
            return;
        }

        character.PublishingRestoredState = true;

        try
        {
            character.PublishDeferredRepertoireRefreshes();
            character.CharacterRefreshed?.Invoke(character);
        }
        finally
        {
            character.PublishingRestoredState = false;
        }
    }

    private static void RestoreSnapshotPreservingRuntime(
        RulesetCharacterSimulacrum character,
        SimulacrumSnapshotRulesetCondition snapshot,
        SimulacrumSnapshotRulesetCondition.DuplicateRuntimeState runtimeState)
    {
        UnbindSnapshotHandlers(character);

        try
        {
            snapshot.RestoreSnapshotValues(character, runtimeState);
            runtimeState.Restore(character);
            RefreshLocationState(character);
            PublishRestoredState(character);
        }
        finally
        {
            if (character.LifecycleState == SimulacrumLifecycleState.Ready)
            {
                BindSnapshotHandlers(character);
            }
        }
    }

    private sealed class OwnerReconciler : IOnLocationCharacterRestored
    {
        public int Priority => 1;

        public void OnLocationCharacterRestored(RulesetCharacter character)
        {
            ReconcileOwner(character);
        }
    }
}
