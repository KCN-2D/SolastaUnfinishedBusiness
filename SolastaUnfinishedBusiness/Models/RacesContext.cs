using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Races;
using SolastaUnfinishedBusiness.Validators;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSenses;

namespace SolastaUnfinishedBusiness.Models;

internal static class RacesContext
{
    private const int SpeciesBaseWalkSpeed2024 = 6;

    private static readonly FeatureDefinitionAbilityCheckAffinity AbilityCheckAffinityDarknessPerceptive =
        FeatureDefinitionAbilityCheckAffinityBuilder
            .Create("AbilityCheckAffinityDarknessPerceptive")
            .SetGuiPresentation(Category.Feature)
            .BuildAndSetAffinityGroups(CharacterAbilityCheckAffinity.Advantage,
                abilityProficiencyPairs: (AttributeDefinitions.Wisdom, SkillDefinitions.Perception))
            .AddCustomSubFeatures(ValidatorsCharacter.IsUnlitOrDarkness)
            .AddToDB();

    internal static Dictionary<CharacterRaceDefinition, float> RaceScaleMap { get; } = [];
    internal static HashSet<CharacterRaceDefinition> Races { get; private set; } = [];
    internal static HashSet<CharacterRaceDefinition> Subraces { get; private set; } = [];

    private static readonly Dictionary<RacePresentation, bool> OriginalBodyShapeCategories = [];
    private static readonly MethodInfo BindCharacterCustomizationElements =
        AccessTools.Method(typeof(CharacterStageIdentityDefinitionPanel), "BindCharacterCustomizationElements");
    private static readonly AccessTools.FieldRef<CharacterStagePanel, RulesetCharacterHero> IdentityHero =
        AccessTools.FieldRefAccess<CharacterStagePanel, RulesetCharacterHero>("currentHero");
    private static readonly FieldInfo IdentitySelectedOptions =
        AccessTools.Field(typeof(CharacterStageIdentityDefinitionPanel), "selectedOptionsByPart");
    private static readonly FieldInfo IdentityCustomizationOptions =
        AccessTools.Field(typeof(CharacterStageIdentityDefinitionPanel), "customizationOptionsBySelectedOrigin");
    private static readonly AccessTools.FieldRef<RacePresentation, MorphotypeElementDefinition.ElementCategory[]>
        BodyShapeCategories = AccessTools.FieldRefAccess<RacePresentation,
            MorphotypeElementDefinition.ElementCategory[]>("availableMorphotypeCategories");

    internal static void Load()
    {
        _bodyShapesUnloading = false;
        _characterModelsUnloading = false;
        if (_characterModelCatalog == null) _characterModelCatalogAttempted = false;
        Morphotypes.Load();

        LoadRace(RaceBattlebornBuilder.RaceBattleborn);
        LoadRace(RaceBolgrifBuilder.RaceBolgrif);
        LoadRace(RaceFairyBuilder.RaceFairy);
        LoadRace(RaceImpBuilder.RaceImp);
        LoadRace(RaceKoboldBuilder.RaceKobold);
        LoadRace(RaceMalakhBuilder.RaceMalakh);
        LoadRace(RaceOligathBuilder.RaceOligath);
        LoadRace(RaceWendigoBuilder.RaceWendigo);
        LoadRace(RaceWildlingBuilder.RaceWildling);
        LoadRace(RaceWyrmkinBuilder.RaceWyrmkin);
        LoadRace(RaceLizardfolkBuilder.RaceLizardfolk);
        LoadRace(RaceOniBuilder.RaceOni);

        _ = RaceTieflingBuilder.RaceTiefling;

        LoadSubrace(RaceTieflingBuilder.RaceTieflingDevilTongue);
        LoadSubrace(RaceTieflingBuilder.RaceTieflingFeral);
        LoadSubrace(RaceTieflingBuilder.RaceTieflingMephistopheles);
        LoadSubrace(RaceTieflingBuilder.RaceTieflingZariel);

        _ = RaceHalfElfBuilder.RaceHalfElfVariant;

        LoadSubrace(RaceHalfElfBuilder.RaceHalfElfHighVariant);
        LoadSubrace(RaceHalfElfBuilder.RaceHalfElfSylvanVariant);
        LoadSubrace(RaceHalfElfBuilder.RaceHalfElfDarkVariant);

        LoadSubrace(SubraceDarkelfBuilder.SubraceDarkelf);
        LoadSubrace(SubraceGrayDwarfBuilder.SubraceGrayDwarf);
        LoadSubrace(SubraceIronbornDwarfBuilder.SubraceIronbornDwarf);
        LoadSubrace(SubraceObsidianDwarfBuilder.SubraceObsidianDwarf);
        LoadSubrace(SubraceShadarKaiBuilder.SubraceShadarKai);

        // sorting
        Races = Races.OrderBy(x => x.FormatTitle()).ToHashSet();
        Subraces = Subraces.OrderBy(x => x.FormatTitle()).ToHashSet();

        // settings paring
        foreach (var name in Main.Settings.RaceEnabled
                     .Where(name => Races.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.RaceEnabled.Remove(name);
        }

        foreach (var name in Main.Settings.SubraceEnabled
                     .Where(name => Subraces.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.SubraceEnabled.Remove(name);
        }

        DatabaseRepository.GetDatabase<CharacterRaceDefinition>()
            .Do(x => x.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock));

        // final bootstrap
        LoadVision();
        SwitchDarknessPerceptive();
        SwitchDragonbornElementalBreathUsages();
        FlexibleBackgroundsContext.Load();
        FlexibleBackgroundsContext.SwitchFlexibleBackgrounds();
        FlexibleRacesContext.SwitchFlexibleRaces();
        SwitchBodyShapeSetting(false);
    }

    internal static void SwitchBodyShapeSetting(bool refreshViews = true)
    {
        var enabled = SettingsContext.GuiModManagerInstance.EnableAllRaceBodyShapes;
        const MorphotypeElementDefinition.ElementCategory category =
            MorphotypeElementDefinition.ElementCategory.Musculature;

        foreach (var race in DatabaseRepository.GetDatabase<CharacterRaceDefinition>())
        {
            var presentation = race.RacePresentation;
            if (presentation == null)
            {
                continue;
            }

            if (!OriginalBodyShapeCategories.TryGetValue(presentation, out var originallyAvailable))
            {
                originallyAvailable = presentation.IsAvailable(category);
                OriginalBodyShapeCategories.Add(presentation, originallyAvailable);
            }

            if (originallyAvailable || presentation.IsAvailable(category) == enabled)
            {
                continue;
            }

            BodyShapeCategories(presentation) = enabled
                ? [.. presentation.AvailableMorphotypeCategories, category]
                : presentation.AvailableMorphotypeCategories.Where(value => value != category).ToArray();
        }

        if (!refreshViews)
        {
            return;
        }

        // The factory owns every view of an actor, including creation and inventory previews.
        // No ruleset appearance values are rewritten when the setting changes.
        var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>();
        if (factory != null)
        {
            foreach (var graphics in factory.GraphicsCharacters.ToArray())
            {
                RefreshCharacterModels(graphics);
                RefreshBodyShape(graphics);
            }
        }

        foreach (var panel in Resources.FindObjectsOfTypeAll<CharacterStageIdentityDefinitionPanel>())
        {
            var hero = IdentityHero(panel);
            if (panel.gameObject.activeInHierarchy && panel.Visible && hero?.RaceDefinition != null &&
                ReferenceEquals(panel.CharacterBuildingService?.CurrentLocalHeroCharacter, hero) &&
                IdentitySelectedOptions.GetValue(panel) is Dictionary<MorphotypeElementDefinition.ElementCategory, int>
                    selectedOptions && selectedOptions.ContainsKey(MorphotypeElementDefinition.ElementCategory.Origin) &&
                IdentityCustomizationOptions.GetValue(panel) is System.Collections.IDictionary { Count: > 0 })
            {
                // Refresh commits all current controls back into the hero. Rebinding the
                // modifiers only changes visibility and keeps those appearance values intact.
                BindCharacterCustomizationElements.Invoke(panel, null);
            }
        }
    }

    internal static void RefreshBodyShape(GraphicsCharacter graphics)
    {
        if (!_bodyShapesUnloading && graphics && graphics.RulesetCharacter != null && graphics.Animator &&
            graphics.GetComponent<CharacterModelPresentation>() is not { IsInitializing: true })
        {
            ServiceRepository.GetService<IGraphicsCharacterFactoryService>()?
                .RefreshMorphotypeParameters(graphics, MorphotypeElementDefinition.ElementCategory.Musculature);
        }
    }

    internal static void ReleaseBodyShape(GraphicsCharacter graphics)
    {
        if (graphics) graphics.GetComponent<BodyShapePresentation>()?.Release(false);
    }

    internal static bool ApplyBodyShape(GraphicsCharacter graphics)
    {
        if (_bodyShapesUnloading) return true;
        var character = graphics?.RulesetCharacter;
        if (!UsesBodyShapeAppearance(character))
        {
            return true;
        }

        if (!TryGetRaceIdentity(character, out var race, out var subRace))
        {
            return true;
        }

        var enabled = SettingsContext.GuiModManagerInstance.EnableAllRaceBodyShapes;
        var presentation = subRace?.RacePresentation ?? race.RacePresentation;
        if (presentation == null)
        {
            return true;
        }

        var originallyAvailable = HasNativeBodyShape(presentation);
        var value = (float)presentation.DefaultMusculature;
        if ((enabled || originallyAvailable) && character.MorphotypeElementAdditionalValues.TryGetValue(
                MorphotypeElementDefinition.ElementCategory.Musculature, out var savedValue) &&
            BodyShapeFinite(savedValue)) value = Mathf.Clamp(savedValue, 0f, 100f);
        foreach (var renderer in graphics.BlendShapeRenderers)
        {
            if (IsProtectedBodyRenderer(graphics, renderer)) continue;
            var mesh = renderer ? renderer.sharedMesh : null;
            var index = mesh ? mesh.GetBlendShapeIndex("Muscular") : -1;
            if (index >= 0) renderer.SetBlendShapeWeight(index,
                originallyAvailable ? value : presentation.DefaultMusculature);
        }
        ApplySupplementalBodyShape(graphics, value, presentation.DefaultMusculature, enabled);
        return false;
    }

    private static bool HasNativeBodyShape(RacePresentation presentation) =>
        presentation != null && (!OriginalBodyShapeCategories.TryGetValue(presentation, out var available) || available);

    private static bool UsesBodyShapeAppearance(RulesetCharacter character) => character is RulesetCharacterHero ||
        character is RulesetCharacterSimulacrum duplicate && SimulacrumBehavior.UsesInventoryAppearance(duplicate);

    private static bool UsesCharacterModelPresentation(GraphicsCharacter graphics) =>
        SettingsContext.GuiModManagerInstance.EnableCustomFaces ||
        SettingsContext.GuiModManagerInstance.EnableAllRaceBodyShapes && UsesBodyShapeAppearance(graphics?.RulesetCharacter);

    private const string BodyVolumePositive = "UB_BodyVolumePositive";
    private const string BodyVolumeNegative = "UB_BodyVolumeNegative";
    private static readonly ConditionalWeakTable<Mesh, BodyShapeRest> BodyShapeRestCache = new();
    private static readonly BodyShapeField[] BodyShapeFields = new BodyShapeField[2];
    private static readonly bool[] BodyShapeFieldLoading = new bool[2];
    private static readonly HashSet<string> BodyShapeErrors = [];
    private static readonly HashSet<BodyShapePresentation> BodyShapeOwners = [];
    private static bool _bodyShapesUnloading;

    internal static void UnloadBodyShapes()
    {
        _bodyShapesUnloading = true;
        foreach (var owner in BodyShapeOwners.ToArray()) owner.Release(Main.IsApplicationQuitting);
        UnloadCharacterModels();
        const MorphotypeElementDefinition.ElementCategory category = MorphotypeElementDefinition.ElementCategory.Musculature;
        foreach (var original in OriginalBodyShapeCategories)
        {
            if (original.Key.IsAvailable(category) == original.Value) continue;
            BodyShapeCategories(original.Key) = original.Value
                ? [.. original.Key.AvailableMorphotypeCategories, category]
                : original.Key.AvailableMorphotypeCategories.Where(value => value != category).ToArray();
        }
    }
    private static readonly ConditionalWeakTable<Mesh, string> CharacterModelFingerprints = new();
    private static readonly ConditionalWeakTable<Texture, string> CharacterModelTextureFingerprints = new();
    private static readonly HashSet<CharacterModelPresentation> CharacterModelOwners = [];
    private static readonly Dictionary<GraphicsCharacter, int> CharacterModelCreations = [];
    private static readonly Dictionary<GraphicsCharacter, (LODGroup Group, LOD[] Levels, Dictionary<Renderer, bool> Hidden)>
        CharacterModelLodStates = [];
    private static readonly HashSet<string> CharacterModelErrors = [];
    private static CharacterModelCatalog _characterModelCatalog;
    private static readonly HashSet<CharacterModelResourceLease> CharacterModelResourceOwners = [];
    private static bool _characterModelsUnloading, _characterModelCatalogAttempted, _characterModelRefreshPending;
    private sealed class CharacterModelBlueprint(string digest, JObject template, CharacterModelManifest resources, string directory)
    {
        internal readonly string Digest = digest;
        internal readonly JObject Template = template;
        internal readonly CharacterModelManifest Resources = resources;
        internal readonly string Directory = directory;
    }
    private static CharacterModelBlueprint _characterModelBlueprint;
    private static Task<CharacterModelBlueprint> _characterModelPreparation, _characterModelPreparationWorker;
    private static CancellationTokenSource _characterModelPreparationCancellation;
    [ThreadStatic] private static CancellationToken _characterModelReadCancellation;
    private static int _characterModelPreparationGeneration;
    private static bool _characterModelPreparationStarted;

    private static void PrepareCharacterModelCatalog()
    {
        if (_characterModelsUnloading || !SettingsContext.GuiModManagerInstance.EnableCustomFaces ||
            _characterModelCatalog != null) return;
        if (_characterModelPreparation == null && !_characterModelCatalogAttempted)
        {
            _characterModelCatalogAttempted = true;
            var generation = _characterModelPreparationGeneration;
            var cancellation = new CancellationTokenSource();
            _characterModelPreparationCancellation = cancellation;
            var previous = _characterModelBlueprint;
            var directory = System.IO.Path.Combine(Main.ModFolder, "BlenderModels", "CharacterModels");
            CharacterModelBlueprint Read()
            {
                if (generation != Volatile.Read(ref _characterModelPreparationGeneration)) return null;
                _characterModelReadCancellation = cancellation.Token;
                try
                {
                    var blueprint = CharacterModelCatalog.ReadBlueprint(directory, previous);
                    if (blueprint.Resources == null) CustomModels.SkinMeshSource.PrepareNativeMetadata();
                    return blueprint;
                }
                finally { _characterModelReadCancellation = default; }
            }
            _characterModelPreparation = _characterModelPreparationWorker == null
                ? Task.Run(Read)
                : _characterModelPreparationWorker.ContinueWith(previousWorker =>
                {
                    _ = previousWorker.Exception;
                    return Read();
                }, TaskScheduler.Default);
            _characterModelPreparationWorker = _characterModelPreparation;
        }
        if (_characterModelPreparation != null && !_characterModelPreparationStarted &&
            ServiceRepository.GetService<IGraphicsCharacterFactoryService>() is MonoBehaviour factory)
        {
            _characterModelPreparationStarted = true;
            factory.StartCoroutine(CompleteCharacterModelCatalog(_characterModelPreparation,
                _characterModelPreparationGeneration));
        }
    }

    private static IEnumerator CompleteCharacterModelCatalog(Task<CharacterModelBlueprint> preparation, int generation)
    {
        bool Current() => generation == _characterModelPreparationGeneration && !_characterModelsUnloading &&
                          SettingsContext.GuiModManagerInstance.EnableCustomFaces;
        try
        {
            while (!preparation.IsCompleted)
            {
                if (!Current()) yield break;
                yield return null;
            }
            if (!Current()) yield break;
            try
            {
                var blueprint = preparation.GetAwaiter().GetResult();
                _characterModelCatalog = CharacterModelCatalog.FromBlueprint(
                    System.IO.Path.Combine(Main.ModFolder, "BlenderModels", "CharacterModels"), blueprint);
                _characterModelBlueprint = blueprint;
                if (_characterModelCatalog.UsesCompleteParts) SwitchCharacterModels();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { ReportCharacterModelError("catalog", exception); }
        }
        finally
        {
            if (generation == _characterModelPreparationGeneration)
            {
                _characterModelPreparation = null;
                _characterModelPreparationStarted = false;
                _characterModelPreparationCancellation?.Dispose();
                _characterModelPreparationCancellation = null;
            }
            _ = preparation.ContinueWith(failed => _ = failed.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        if (Current()) RefreshPendingCharacterModels();
    }

    private static void CancelCharacterModelCatalogPreparation(bool unloading = false)
    {
        UpdateContext.CancelCharacterModelDownload();
        var cancellation = _characterModelPreparationCancellation;
        _characterModelPreparationCancellation = null;
        cancellation?.Cancel();
        if (cancellation != null)
        {
            if (_characterModelPreparationWorker == null || _characterModelPreparationWorker.IsCompleted) cancellation.Dispose();
            else _ = _characterModelPreparationWorker.ContinueWith(_ => cancellation.Dispose());
        }
        _characterModelPreparationGeneration++;
        if (_characterModelPreparation != null)
            _ = _characterModelPreparation.ContinueWith(failed => _ = failed.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
        _characterModelPreparation = null;
        _characterModelPreparationStarted = false;
        if (!unloading) return;
        _characterModelBlueprint = null;
        _characterModelPreparationWorker = null;
    }

    private static bool DeferCharacterModelRefresh => LoadingContext.IsLoadInProgress &&
        ServiceRepository.GetService<IGameLocationService>()?.LocationIsReady != true;

    internal static void RefreshPendingCharacterModels()
    {
        if (!_characterModelRefreshPending || _characterModelsUnloading || _bodyShapesUnloading ||
            DeferCharacterModelRefresh) return;
        if (_characterModelPreparation != null && _characterModelCatalog == null) return;
        _characterModelRefreshPending = false;
        foreach (var owner in CharacterModelOwners.ToArray())
        {
            if (owner) owner.RefreshPending();
            else CharacterModelOwners.Remove(owner);
        }
        foreach (var owner in BodyShapeOwners.ToArray())
        {
            if (owner) owner.RefreshPending();
            else BodyShapeOwners.Remove(owner);
        }
        SynchronizeCharacterModelRefreshPending();
    }

    private static void SynchronizeCharacterModelRefreshPending() =>
        _characterModelRefreshPending = CharacterModelOwners.Any(owner => owner && owner.IsPending) ||
                                        BodyShapeOwners.Any(owner => owner && owner.IsPending);

    internal static void SwitchCharacterModels()
    {
        if (_characterModelsUnloading) return;
        if (!SettingsContext.GuiModManagerInstance.EnableCustomFaces)
            foreach (var graphics in CharacterModelLodStates.Keys.ToArray()) RestoreCharacterModelLods(graphics);
        CharacterModelTextureReadback.SetEnabled(SettingsContext.GuiModManagerInstance.EnableCustomFaces);
        if (_characterModelCatalog == null) _characterModelCatalogAttempted = false;
        if (SettingsContext.GuiModManagerInstance.EnableCustomFaces) PrepareCharacterModelCatalog();
        else CancelCharacterModelCatalogPreparation();
        var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>();
        if (factory != null)
            foreach (var graphics in factory.GraphicsCharacters.ToArray())
            {
                if (graphics && CharacterModelCreations.ContainsKey(graphics)) continue;
                if (_characterModelCatalog?.UsesCompleteParts == true && graphics && graphics.Renderers?.Any() == true)
                    RefreshNativeCharacterModel(factory, graphics);
                else RefreshCharacterModels(graphics);
            }
        if (!SettingsContext.GuiModManagerInstance.EnableCustomFaces) ReleaseUnusedCharacterModelCatalog();
    }

    private static void RefreshNativeCharacterModel(IGraphicsCharacterFactoryService factory, GraphicsCharacter graphics)
    {
        if (graphics is GraphicsCharacterHero hero) factory.RefreshGraphicsCharacter(hero, false);
        else if (graphics is GraphicsCharacterMonster monster) factory.RefreshGraphicsCharacter(monster, null);
    }

    private static void ReleaseUnusedCharacterModelCatalog()
    {
        if (_characterModelCatalog is { HasPreparedSources: false } catalog &&
            (_characterModelsUnloading || catalog.IsRetiring || !SettingsContext.GuiModManagerInstance.EnableCustomFaces) &&
            !catalog.HasLiveSourceOwners &&
            !CharacterModelOwners.Any(owner => owner && owner.HasCatalogSources))
        {
            catalog.Dispose();
            _characterModelCatalog = null;
            _characterModelCatalogAttempted = false;
            CharacterModelPresentation.ClearUnusedDerivedData();
            if (!_characterModelsUnloading && SettingsContext.GuiModManagerInstance.EnableCustomFaces)
                PrepareCharacterModelCatalog();
        }
    }

    internal static void RefreshCharacterModels(GraphicsCharacter graphics)
    {
        if (_characterModelsUnloading || !graphics || graphics.RulesetCharacter == null) return;
        var owner = graphics.GetComponent<CharacterModelPresentation>();
        try
        {
            if (!UsesCharacterModelPresentation(graphics))
            {
                if (owner && (owner.HasSources || owner.IsPending)) owner.Release(false, owner.HasSources);
                return;
            }
            if (_characterModelCatalog?.UsesCompleteParts == true)
            {
                if (owner && (owner.HasSources || owner.IsPending)) owner.Release(false, false);
                var lease = graphics.GetComponent<CharacterModelResourceLease>() ??
                    graphics.gameObject.AddComponent<CharacterModelResourceLease>();
                lease.Bind(graphics, _characterModelCatalog);
                lease.Synchronize();
                return;
            }
            owner ??= graphics.gameObject.AddComponent<CharacterModelPresentation>();
            owner.Bind(graphics);
            if (!owner.isActiveAndEnabled || owner.IsInitializing) return;
            // Native loading creates presentations before sectors and culling decide
            // which ones remain active. Resolve their custom sources at that boundary.
            if (DeferCharacterModelRefresh)
            {
                owner.DeferRefresh();
                return;
            }
            if (SettingsContext.GuiModManagerInstance.EnableCustomFaces &&
                (_characterModelCatalog == null || _characterModelCatalog.IsRetiring))
            {
                PrepareCharacterModelCatalog();
                if (_characterModelPreparation != null || _characterModelCatalog?.IsRetiring == true)
                {
                    owner.DeferRefresh();
                    return;
                }
            }
            owner.Refresh(graphics, SettingsContext.GuiModManagerInstance.EnableCustomFaces ? _characterModelCatalog : null);
        }
        catch (Exception exception) { ReportCharacterModelError("refresh", exception); }
    }

    internal static void RefreshCharacterModelMaterials(GraphicsCharacter graphics)
    {
        if (_characterModelsUnloading || !graphics || !SettingsContext.GuiModManagerInstance.EnableCustomFaces) return;
        try { graphics.GetComponent<CharacterModelPresentation>()?.RefreshMaterials(); }
        catch (Exception exception) { ReportCharacterModelError("materials", exception); }
    }

    internal static void RefreshCharacterModelLods(GraphicsCharacter graphics)
    {
        RestoreCharacterModelLods(graphics);
        if (_characterModelsUnloading || !graphics || !SettingsContext.GuiModManagerInstance.EnableCustomFaces ||
            _characterModelCatalog?.UsesCompleteParts != true || !UsesBodyShapeAppearance(graphics.RulesetCharacter)) return;
        var group = AccessTools.Field(typeof(GraphicsCharacter), "sharedLodGroup")?.GetValue(graphics) as LODGroup;
        if (!group) return;
        var lods = group.GetLODs();
        if (lods.Length < 3 || lods[1].renderers.Length == 0) return;
        var retained = new HashSet<Renderer>(lods.Take(2).SelectMany(lod => lod.renderers));
        var hidden = lods.Skip(2).SelectMany(lod => lod.renderers).Where(renderer => renderer && !retained.Contains(renderer))
            .Distinct().ToDictionary(renderer => renderer, renderer => renderer.forceRenderingOff);
        CharacterModelLodStates.Add(graphics, (group, (LOD[])lods.Clone(), hidden));
        // A renderer removed from an LODGroup becomes independently visible.
        // Keep those source renderers hidden until their native levels return.
        foreach (var renderer in hidden.Keys) renderer.forceRenderingOff = true;
        // Select the complete character at one detail level. Mixing higher-detail
        // hands or feet with a lower-detail torso separates their shared cuts.
        for (var index = 2; index < lods.Length; index++) lods[index].renderers = lods[1].renderers;
        group.SetLODs(lods);
    }

    internal static void RestoreCharacterModelLods(GraphicsCharacter graphics)
    {
        if (ReferenceEquals(graphics, null) || !CharacterModelLodStates.TryGetValue(graphics, out var state)) return;
        CharacterModelLodStates.Remove(graphics);
        foreach (var pair in state.Hidden)
            if (pair.Key && pair.Key.forceRenderingOff) pair.Key.forceRenderingOff = pair.Value;
        if (state.Group && !Main.IsApplicationQuitting) state.Group.SetLODs(state.Levels);
    }

    internal static void BindCharacterPartParameters(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics,
        GraphicsCharacterDefinitions.BodyPart part, bool shape)
    {
        if (_characterModelCatalog?.UsesCompleteParts != true || !graphics) return;
        var index = (int)part;
        var keys = shape ? factory.shapePartsToLoad : factory.armorPartsToLoad;
        var instances = shape ? graphics.ShapeInstancePerBodySlot : graphics.ArmorInstancePerBodySlot;
        if (index < 0 || index >= keys.Length || index >= instances.Length || !instances[index]) return;
        _characterModelCatalog.BindPartParameters(graphics, keys[index], instances[index], part, shape);
    }

    internal static object BeginCharacterModelMaterialRefresh(GraphicsCharacter graphics)
    {
        if (_characterModelsUnloading || !graphics || graphics.RulesetCharacter == null ||
            !SettingsContext.GuiModManagerInstance.EnableCustomFaces ||
            _characterModelCatalog?.UsesCompleteParts == true) return null;
        var owner = graphics.GetComponent<CharacterModelPresentation>() ??
            graphics.gameObject.AddComponent<CharacterModelPresentation>();
        owner.Bind(graphics);
        owner.BeginMaterialRefresh();
        return owner;
    }

    internal static void EndCharacterModelMaterialRefresh(object state, bool failed)
    {
        try { if (state is CharacterModelPresentation owner && owner) owner.EndMaterialRefresh(failed); }
        catch (Exception exception) { ReportCharacterModelError("materials-finalize", exception); }
    }

    internal static object SuspendCharacterModels(GraphicsCharacter graphics)
    {
        if (!graphics) return null;
        ReleaseBodyShape(graphics);
        var owner = graphics.GetComponent<CharacterModelPresentation>();
        if (!owner && _characterModelCatalog?.UsesCompleteParts == true) return null;
        if (!owner && !_characterModelsUnloading && graphics.RulesetCharacter != null &&
            UsesCharacterModelPresentation(graphics))
        {
            owner = graphics.gameObject.AddComponent<CharacterModelPresentation>();
            owner.Bind(graphics);
        }
        if (!owner) return null;
        owner.Suspend();
        return owner;
    }

    internal static void ResumeCharacterModels(GraphicsCharacter graphics, object state, bool failed)
    {
        try
        {
            if (state is CharacterModelPresentation owner && owner) owner.Resume(failed);
            else if (!failed) RefreshCharacterModels(graphics);
            if (!failed) RefreshBodyShape(graphics);
        }
        catch (Exception exception) { ReportCharacterModelError("resume", exception); }
        finally { if (state is CharacterModelPresentation owner && owner && !owner.IsInitializing && !owner.IsPending) owner.ReleasePreparedSources(); }
    }

    internal static void ReleaseCharacterModels(GraphicsCharacter graphics)
    {
        if (!graphics) return;
        RestoreCharacterModelLods(graphics);
        try { graphics.GetComponent<CharacterModelPresentation>()?.Release(false, false); }
        catch (Exception exception) { ReportCharacterModelError("release", exception); }
        graphics.GetComponent<CharacterModelResourceLease>()?.Release();
    }

    private static void UnloadCharacterModels()
    {
        _characterModelsUnloading = true;
        foreach (var graphics in CharacterModelLodStates.Keys.ToArray()) RestoreCharacterModelLods(graphics);
        CharacterModelTextureReadback.SetEnabled(false);
        _characterModelCatalog?.Retire();
        CancelCharacterModelCatalogPreparation(true);
        _characterModelRefreshPending = false;
        foreach (var owner in CharacterModelOwners.ToArray())
        {
            try { owner.Release(Main.IsApplicationQuitting, false); }
            catch (Exception exception) { ReportCharacterModelError("unload-restore", exception); }
        }
        var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>();
        foreach (var lease in CharacterModelResourceOwners.ToArray())
        {
            if (!lease) { CharacterModelResourceOwners.Remove(lease); continue; }
            if (!Main.IsApplicationQuitting && factory != null && lease.Graphics && lease.Graphics.RulesetCharacter != null)
                RefreshNativeCharacterModel(factory, lease.Graphics);
            else lease.Release();
        }
        if (CharacterModelOwners.Any(owner => owner && owner.HasSources))
        {
            ReportCharacterModelError("unload", new InvalidOperationException("Custom character model sources are still owned by a renderer."));
            return;
        }
        ReleaseUnusedCharacterModelCatalog();
    }

    private static void ReportCharacterModelError(string stage, Exception exception)
    {
        var text = stage + ": " + exception;
        if (CharacterModelErrors.Add(text)) Main.Error("Unable to apply a custom character model (" + text + ").");
    }

    internal static string CharacterModelFingerprint(Mesh source) => CharacterModelFingerprints.GetValue(source,
        static mesh => CharacterModelCurrentFingerprint(mesh));

    internal static string CharacterModelCurrentFingerprint(Mesh source) => CharacterModelMeshData.Capture(source, false).Fingerprint();

    private sealed class CharacterModelMeshPacketLimit : Exception { }

    private sealed class CharacterModelMeshData
    {
        internal sealed class Uv(int channel, int dimension, Vector4[] values)
        { internal readonly int Channel = channel, Dimension = dimension; internal readonly Vector4[] Values = values; }
        internal sealed class Frame(int count)
        {
            internal float Weight;
            internal readonly Vector3[] Positions = new Vector3[count], Normals = new Vector3[count], Tangents = new Vector3[count];
        }
        internal sealed class Shape(string name, int frameCount, IEnumerable<Frame> frames)
        { internal readonly string Name = name; internal readonly int FrameCount = frameCount; internal readonly IEnumerable<Frame> Frames = frames; }
        internal readonly int VertexCount, IndexFormat, ShapeCount, OwnerThread;
        internal readonly bool Snapshot;
        internal readonly Vector3[] Positions, Normals;
        internal readonly Vector4[] Tangents;
        internal readonly Color[] Colors;
        internal readonly Matrix4x4[] Poses;
        internal readonly BoneWeight[] Weights;
        internal readonly int[][] SubmeshIndices;
        internal readonly Uv[] Uvs;
        internal readonly IEnumerable<Shape> Shapes;
        private long _bytes;

        private CharacterModelMeshData(Mesh source, Mesh readable, bool snapshot, long maximumBytes)
        {
            VertexCount = source.vertexCount; IndexFormat = (int)source.indexFormat;
            ShapeCount = source.blendShapeCount; OwnerThread = Thread.CurrentThread.ManagedThreadId; Snapshot = snapshot;
            Positions = readable.vertices; Normals = readable.normals; Tangents = readable.tangents; Colors = readable.colors;
            if (Positions.Length != VertexCount || Normals.Length != VertexCount ||
                Tangents.Length != 0 && Tangents.Length != VertexCount || Colors.Length != 0 && Colors.Length != VertexCount)
                throw new InvalidOperationException("A native character model rest channel is incomplete.");
            Poses = source.bindposes; Weights = source.boneWeights;
            _bytes = 1024L + 12L * (Positions.Length + Normals.Length) + 16L * (Tangents.Length + Colors.Length) +
                     64L * Poses.Length + 32L * Weights.Length;
            Budget(maximumBytes);
            SubmeshIndices = new int[readable.subMeshCount][];
            for (var index = 0; index < SubmeshIndices.Length; index++)
            { SubmeshIndices[index] = readable.GetIndices(index); _bytes += 4L * SubmeshIndices[index].Length; Budget(maximumBytes); }
            var uvs = new List<Uv>();
            for (var channel = 0; channel < 8; channel++)
            {
                var values = new List<Vector4>(); readable.GetUVs(channel, values);
                if (values.Count == 0) continue;
                var dimension = readable.GetVertexAttributeDimension((UnityEngine.Rendering.VertexAttribute)
                    ((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel));
                if (values.Count != VertexCount || dimension < 1 || dimension > 4)
                    throw new InvalidOperationException("A native character model UV channel is incomplete.");
                uvs.Add(new Uv(channel, dimension, values.ToArray())); _bytes += 16L * values.Count; Budget(maximumBytes);
            }
            Uvs = uvs.ToArray();
            var shapes = ReadShapes(source, snapshot, maximumBytes);
            Shapes = snapshot ? shapes.ToArray() : shapes;
        }
        internal static CharacterModelMeshData Capture(Mesh source, bool snapshot, long maximumBytes = long.MaxValue) =>
            ReadNativeRestMesh(source, readable => new CharacterModelMeshData(source, readable, snapshot, maximumBytes));
        private void Budget(long maximumBytes)
        { if (_bytes > maximumBytes) throw new CharacterModelMeshPacketLimit(); }
        private IEnumerable<Shape> ReadShapes(Mesh source, bool snapshot, long maximumBytes)
        {
            var reusable = !snapshot && ShapeCount > 0 ? new Frame(VertexCount) : null;
            for (var shape = 0; shape < ShapeCount; shape++)
            {
                if (Thread.CurrentThread.ManagedThreadId != OwnerThread)
                    throw new InvalidOperationException("A native character mesh channel was read outside its owner thread.");
                var name = source.GetBlendShapeName(shape); var frameCount = source.GetBlendShapeFrameCount(shape);
                _bytes += 128L + 2L * (name?.Length ?? 0); Budget(maximumBytes);
                var frames = ReadFrames(source, shape, frameCount, snapshot, reusable, maximumBytes);
                yield return new Shape(name, frameCount, snapshot ? frames.ToArray() : frames);
            }
        }
        private IEnumerable<Frame> ReadFrames(Mesh source, int shape, int count, bool snapshot, Frame reusable, long maximumBytes)
        {
            for (var frame = 0; frame < count; frame++)
            {
                if (Thread.CurrentThread.ManagedThreadId != OwnerThread)
                    throw new InvalidOperationException("A native character mesh frame was read outside its owner thread.");
                if (snapshot) { _bytes += 128L + 36L * VertexCount; Budget(maximumBytes); }
                var value = snapshot ? new Frame(VertexCount) : reusable;
                source.GetBlendShapeFrameVertices(shape, frame, value.Positions, value.Normals, value.Tangents);
                value.Weight = source.GetBlendShapeFrameWeight(shape, frame);
                yield return value;
            }
        }
        internal string Fingerprint(Func<bool> cancelled = null)
        {
            if (!Snapshot && Thread.CurrentThread.ManagedThreadId != OwnerThread)
                throw new InvalidOperationException("A live native character mesh reader was sent to a fingerprint worker.");
            CheckCancellation(cancelled);
            var vectorBytes = new byte[4096];
            using var sha = CustomModels.CreateAssetSha256();
            using var hashing = new System.Security.Cryptography.CryptoStream(System.IO.Stream.Null, sha,
                System.Security.Cryptography.CryptoStreamMode.Write);
            using var stream = new System.IO.BufferedStream(hashing, 65536);
            using var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            writer.Write("Solasta.MeshSource.1"); writer.Write(VertexCount); writer.Write(IndexFormat);
            CharacterModelWriteVectors(writer, Positions, vectorBytes, cancelled);
            CharacterModelWriteVectors(writer, Normals, vectorBytes, cancelled);
            CharacterModelWriteVectors(writer, Tangents, vectorBytes, cancelled);
            writer.Write(checked(Colors.Length * 4));
            foreach (var value in Colors)
            {
                CharacterModelWriteFloat(writer, value.r); CharacterModelWriteFloat(writer, value.g);
                CharacterModelWriteFloat(writer, value.b); CharacterModelWriteFloat(writer, value.a);
            }
            var poses = Poses;
            writer.Write(checked(poses.Length * 16));
            foreach (var matrix in poses)
                for (var index = 0; index < 16; index++) CharacterModelWriteFloat(writer, matrix[index]);
            var weights = Weights;
            writer.Write(checked(weights.Length * 4));
            foreach (var weight in weights)
            {
                CharacterModelWriteFloat(writer, weight.weight0); CharacterModelWriteFloat(writer, weight.weight1);
                CharacterModelWriteFloat(writer, weight.weight2); CharacterModelWriteFloat(writer, weight.weight3);
            }
            writer.Write(checked(weights.Length * 4));
            foreach (var weight in weights)
            {
                writer.Write(weight.boneIndex0); writer.Write(weight.boneIndex1);
                writer.Write(weight.boneIndex2); writer.Write(weight.boneIndex3);
            }
            writer.Write(SubmeshIndices.Length);
            foreach(var indices in SubmeshIndices) CharacterModelWriteInts(writer, indices);
            var uvChannels = Uvs;
            writer.Write(uvChannels.Length);
            foreach (var uv in uvChannels)
            {
                if (uv.Values.Length != VertexCount || uv.Dimension < 1 || uv.Dimension > 4)
                    throw new InvalidOperationException("A native character model UV channel is incomplete.");
                writer.Write(uv.Channel); writer.Write(uv.Dimension); writer.Write(checked(VertexCount * uv.Dimension));
                foreach (var value in uv.Values)
                    for (var component = 0; component < uv.Dimension; component++) CharacterModelWriteFloat(writer, value[component]);
            }
            writer.Write(ShapeCount);
            foreach(var shape in Shapes)
            {
                CheckCancellation(cancelled);
                writer.Write(shape.Name); writer.Write(shape.FrameCount);
                foreach(var frame in shape.Frames)
                {
                    CheckCancellation(cancelled);
                    CharacterModelWriteFloat(writer, frame.Weight);
                    CharacterModelWriteVectors(writer, frame.Positions, vectorBytes, cancelled);
                    CharacterModelWriteVectors(writer, frame.Normals, vectorBytes, cancelled);
                    CharacterModelWriteVectors(writer, frame.Tangents, vectorBytes, cancelled);
                }
            }
            writer.Flush();
            stream.Flush();
            hashing.FlushFinalBlock();
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();

        }
    }

    private static void CheckCancellation(Func<bool> cancelled)
    { if (cancelled != null && cancelled()) throw new OperationCanceledException("A character model source request was superseded."); }

    private static class CharacterModelMeshPreparation
    {
        private const long MaximumPacketBytes = 32L * 1024 * 1024;
        private static int _busy;
        private sealed class Pending(CharacterModelMeshData data)
        {
            internal CharacterModelMeshData Data = data;
            internal volatile bool Cancelled;
        }
        private static Task<string> Queue(Pending pending) => Task.Run(() =>
        {
            try { return pending.Data.Fingerprint(() => pending.Cancelled); }
            finally { pending.Data = null; Interlocked.Exchange(ref _busy, 0); }
        });
        internal static IEnumerator Prepare(Mesh source, Func<bool> current, Action onFailure = null)
        {
            if (!source || !current() || CharacterModelFingerprints.TryGetValue(source, out _)) yield break;
            while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            { if (!current()) yield break; yield return null; }
            Pending pending = null;
            var workerOwns = false;
            try
            {
                if (!current()) yield break;
                try { pending = new Pending(CharacterModelMeshData.Capture(source, true, MaximumPacketBytes)); }
                catch (CharacterModelMeshPacketLimit) { yield break; }
                catch (Exception exception) { onFailure?.Invoke(); ReportCharacterModelError("mesh-prefetch", exception); yield break; }
                if (!current()) yield break;
                Task<string> job;
                try { job = Queue(pending); workerOwns = true; }
                catch (Exception exception) { onFailure?.Invoke(); ReportCharacterModelError("mesh-prefetch", exception); yield break; }
                while (!job.IsCompleted)
                {
                    if (!current())
                    {
                        pending.Cancelled = true;
                        _ = job.ContinueWith(failed => _ = failed.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        yield break;
                    }
                    yield return null;
                }
                if (job.IsFaulted)
                { if (!pending.Cancelled) { onFailure?.Invoke(); ReportCharacterModelError("mesh-prefetch", job.Exception); } yield break; }
                if (!current() || !source) yield break;
                if (CharacterModelFingerprints.GetValue(source, _ => job.Result) != job.Result)
                { onFailure?.Invoke(); ReportCharacterModelError("mesh-prefetch", new InvalidOperationException("A native mesh changed during content verification.")); }
            }
            finally
            {
                if (pending != null) pending.Cancelled = true;
                if (!workerOwns) Interlocked.Exchange(ref _busy, 0);
            }
        }
    }

    private static void CharacterModelWriteFloat(System.IO.BinaryWriter writer, float value)
    {
        if (!BodyShapeFinite(value)) throw new InvalidOperationException("A native character model source contains nonfinite data.");
        writer.Write(value == 0f ? 0f : value);
    }
    private static unsafe void CharacterModelWriteVectors(System.IO.BinaryWriter writer, Vector3[] values,
        byte[] buffer, Func<bool> cancelled = null)
    {
        writer.Write(checked(values.Length * 3));
        if (sizeof(Vector3) != 12) throw new InvalidOperationException("A native Vector3 has an incompatible packed layout.");
        fixed (Vector3* data = values) CharacterModelWriteVectorBytes(writer, (float*)data, values.Length, 3, buffer, cancelled);
    }
    private static unsafe void CharacterModelWriteVectors(System.IO.BinaryWriter writer, Vector4[] values,
        byte[] buffer, Func<bool> cancelled = null)
    {
        writer.Write(checked(values.Length * 4));
        if (sizeof(Vector4) != 16) throw new InvalidOperationException("A native Vector4 has an incompatible packed layout.");
        fixed (Vector4* data = values) CharacterModelWriteVectorBytes(writer, (float*)data, values.Length, 4, buffer, cancelled);
    }
    private static unsafe void CharacterModelWriteVectorBytes(System.IO.BinaryWriter writer, float* values,
        int vertices, int dimension, byte[] buffer, Func<bool> cancelled)
    {
        if (!BitConverter.IsLittleEndian)
        {
            for (var vertex = 0; vertex < vertices; vertex++)
            {
                if ((vertex & 255) == 0) CheckCancellation(cancelled);
                for (var axis = 0; axis < dimension; axis++) CharacterModelWriteFloat(writer, values[vertex * dimension + axis]);
            }
            return;
        }
        for (var first = 0; first < vertices; first += 256)
        {
            CheckCancellation(cancelled);
            var count = Math.Min(256, vertices - first) * dimension;
            var input = (uint*)(values + first * dimension);
            fixed (byte* bytes = buffer)
            {
                var output = (uint*)bytes;
                for (var index = 0; index < count; index++)
                {
                    var bits = input[index];
                    if ((bits & 0x7f800000u) == 0x7f800000u)
                        throw new InvalidOperationException("A native character model source contains nonfinite data.");
                    output[index] = (bits & 0x7fffffffu) == 0 ? 0u : bits;
                }
            }
            writer.Write(buffer, 0, count * sizeof(float));
        }
    }
    private static void CharacterModelWriteInts(System.IO.BinaryWriter writer, int[] values)
    {
        writer.Write(values?.Length ?? 0);
        if (values != null) foreach (var value in values) writer.Write(value);
    }

    private static string CharacterModelTextureFingerprint(Texture source) => CharacterModelTextureFingerprints.GetValue(source,
        static texture =>
        {
            var previous = RenderTexture.active;
            var srgbWrite = GL.sRGBWrite;
            RenderTexture target = null;
            Texture2D copy = null;
            try
            {
                target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                GL.sRGBWrite = false;
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                var rgba = copy.GetRawTextureData();
                if (rgba.Length != checked(texture.width * texture.height * 4))
                    throw new InvalidOperationException("A native character model texture did not yield RGBA32 linear pixels.");
                return CustomModels.TextureRgba32LinearFingerprint(texture.width, texture.height, rgba, rgba.Length);
            }
            finally
            {
                GL.sRGBWrite = srgbWrite;
                RenderTexture.active = previous;
                if (target) RenderTexture.ReleaseTemporary(target);
                if (copy) UnityEngine.Object.Destroy(copy);
            }
        });

    internal static IEnumerator PrepareCharacterModelSources(GraphicsCharacterFactoryManager factory,
        GraphicsCharacter graphics, IEnumerator native)
    {
        var prepared = false;
        RulesetCharacter actor = null;
        CharacterModelCatalog.PreparedSources preparation = null;
        CharacterModelResourceLease resourceLease = null;
        bool Current() => !_characterModelsUnloading && SettingsContext.GuiModManagerInstance.EnableCustomFaces &&
                          factory && graphics && ReferenceEquals(actor, graphics.RulesetCharacter) &&
                          !factory.IsAbortingGraphicsCharacter(graphics);
        try
        {
            actor = graphics ? graphics.RulesetCharacter : null;
            if (!factory) yield break;
            if (Current())
            {
                PrepareCharacterModelCatalog();
                while (Current() && _characterModelCatalog == null && _characterModelPreparation != null) yield return null;
                if (Current() && _characterModelCatalog?.UsesCompleteParts == true)
                {
                    var catalog = _characterModelCatalog;
                    using var sourceLease = catalog.PrepareSources();
                    resourceLease = graphics.GetComponent<CharacterModelResourceLease>() ??
                        graphics.gameObject.AddComponent<CharacterModelResourceLease>();
                    resourceLease.Bind(graphics, catalog);
                    resourceLease.BeginLoad();
                    bool PartCurrent() => Current() && ReferenceEquals(catalog, _characterModelCatalog) && !catalog.IsRetiring;
                    yield return catalog.PrepareCompleteParts(factory, PartCurrent);
                    if (_characterModelsUnloading || !factory || !graphics ||
                        !ReferenceEquals(actor, graphics.RulesetCharacter) || factory.IsAbortingGraphicsCharacter(graphics)) yield break;
                    while (native.MoveNext())
                    {
                        resourceLease.Synchronize(factory);
                        yield return native.Current;
                    }
                    yield break;
                }
            }
            while (native.MoveNext())
            {
                var value = native.Current;
                yield return value;
                // Native code has one null wait after its addressable loads complete,
                // while old parts and the loaded prefab source handles are still owned.
                if (value != null || prepared) continue;
                prepared = true;
                actor = graphics ? graphics.RulesetCharacter : null;
                if (!Current()) continue;
                // Retry the superseded generation before native prefab handles are released.
                while (Current())
                {
                    CharacterModelTextureReadback.SetEnabled(true);
                    PrepareCharacterModelCatalog();
                    while (Current() && (_characterModelCatalog == null || _characterModelCatalog.IsRetiring))
                    {
                        PrepareCharacterModelCatalog();
                        if (_characterModelPreparation == null && _characterModelCatalog?.IsRetiring != true) break;
                        yield return null;
                    }
                    if (!Current() || _characterModelCatalog == null || _characterModelCatalog.IsRetiring) break;
                    var catalog = _characterModelCatalog;
                    var generation = _characterModelPreparationGeneration;
                    bool SourceCurrent() => Current() && generation == _characterModelPreparationGeneration &&
                                            ReferenceEquals(catalog, _characterModelCatalog) && !catalog.IsRetiring &&
                                            (preparation == null || preparation.SourcesCurrent(factory, graphics, true));
                    preparation = catalog.PrepareSources();
                    preparation.CaptureSources(factory, graphics, true);
                    yield return ObserveCharacterModelPreparation(PrepareCharacterModelAssets(factory, graphics,
                        preparation, SourceCurrent, true), () => preparation.Failed = true);
                    if (SourceCurrent()) break;
                    preparation.Dispose();
                    preparation = null;
                }
            }
            if (preparation is { Completed: true, Failed: false } && Current() && preparation.SourcesCurrent(factory, graphics, false) &&
                ReferenceEquals(preparation.Catalog, _characterModelCatalog) && !_characterModelCatalog.IsRetiring)
            {
                var owner = graphics.GetComponent<CharacterModelPresentation>() ?? graphics.gameObject.AddComponent<CharacterModelPresentation>();
                owner.Bind(graphics); owner.PrepareSources(preparation); preparation = null;
            }
        }
        finally
        {
            try { (native as IDisposable)?.Dispose(); }
            finally
            {
                try
                {
                    if (resourceLease) resourceLease.EndLoad();
                    else if (graphics) graphics.GetComponent<CharacterModelResourceLease>()?.Synchronize();
                }
                finally { preparation?.Dispose(); }
            }
        }
    }

    internal static IEnumerator PrepareCharacterModelCreation(GraphicsCharacterFactoryManager factory, IEnumerator native,
        int requestId, GraphicsCharacter existing)
    {
        GraphicsCharacter created = null;
        var generation = _characterModelPreparationGeneration;
        var completed = false;
        void RetainCreation()
        {
            if (!ReferenceEquals(created, null) || _characterModelCatalog?.UsesCompleteParts != true) return;
            var graphics = existing;
            if (!graphics && requestId >= 0) factory.characterPerRequestId.TryGetValue(requestId, out graphics);
            if (!graphics) return;
            created = graphics;
            CharacterModelCreations.TryGetValue(created, out var owners);
            CharacterModelCreations[created] = owners + 1;
        }
        try
        {
            if (!_characterModelsUnloading && SettingsContext.GuiModManagerInstance.EnableCustomFaces)
            {
                PrepareCharacterModelCatalog();
                while (factory && !_characterModelsUnloading && SettingsContext.GuiModManagerInstance.EnableCustomFaces &&
                       _characterModelCatalog == null && _characterModelPreparation != null) yield return null;
            }
            if (!factory || _characterModelsUnloading) yield break;
            generation = _characterModelPreparationGeneration;
            RetainCreation();
            while (native.MoveNext())
            {
                RetainCreation();
                yield return native.Current;
            }
            completed = true;
        }
        finally
        {
            try { (native as IDisposable)?.Dispose(); }
            finally
            {
                if (!ReferenceEquals(created, null))
                {
                    var owners = CharacterModelCreations[created] - 1;
                    if (owners == 0) CharacterModelCreations.Remove(created);
                    else CharacterModelCreations[created] = owners;
                }
            }
        }
        if (completed && generation != _characterModelPreparationGeneration && factory && created &&
            !_characterModelsUnloading && !CharacterModelCreations.ContainsKey(created) &&
            !factory.IsAbortingGraphicsCharacter(created)) RefreshNativeCharacterModel(factory, created);
    }

    private static object NativeCharacterModelResource(object key)
    {
        const string prefix = "UB/CharacterResources/";
        return key is string text && text.StartsWith(prefix, StringComparison.Ordinal) ? text.Substring(prefix.Length) : key;
    }

    internal static bool NativeClothMeshPoolingEnabled => Patches.RenderMeshDeformerPatcher.RuntimeMeshLifetime_Patch.Enabled;
    private static readonly Dictionary<Mesh, CharacterModelCatalog> CharacterModelClothMeshOwners = [];

    internal static Mesh AcquireCharacterModelClothMesh(Mesh source) =>
        NativeClothMeshPoolingEnabled && !_characterModelsUnloading && SettingsContext.GuiModManagerInstance.EnableCustomFaces &&
        _characterModelCatalog?.TryAcquireClothMesh(source, out var mesh) == true ? mesh : UnityEngine.Object.Instantiate(source);

    internal static void ReleaseCharacterModelClothMesh(UnityEngine.Object value)
    {
        if (value is Mesh mesh && CharacterModelClothMeshOwners.TryGetValue(mesh, out var catalog)) catalog.ReturnClothMesh(mesh);
        else UnityEngine.Object.Destroy(value);
    }

    internal static void SetCharacterModelClothBindPoses(Mesh mesh, Matrix4x4[] poses)
    {
        if (CharacterModelClothMeshOwners.ContainsKey(mesh))
        {
            var current = mesh.bindposes;
            if (current.Length == poses.Length && current.SelectMany((pose, index) => Enumerable.Range(0, 16)
                    .Select(component => pose[component] == poses[index][component])).All(equal => equal)) return;
        }
        mesh.bindposes = poses;
    }

    internal static AsyncOperationHandle<T> LoadCharacterModelResourceAsync<T>(object key) where T : UnityEngine.Object
    {
        if (!_characterModelsUnloading && SettingsContext.GuiModManagerInstance.EnableCustomFaces &&
            _characterModelCatalog?.UsesCompleteParts == true && _characterModelCatalog.TryGetResource(key, out var part))
            return _characterModelCatalog.LoadResourceAsync<T>(part);
        return Addressables.LoadAssetAsync<T>(NativeCharacterModelResource(key));
    }

    internal static void LoadCharacterModelResource<T>(ref GraphicsCharacterFactoryManager.LoadAssetWrapper<T> source)
        where T : UnityEngine.Object
    {
        if (!_characterModelsUnloading && SettingsContext.GuiModManagerInstance.EnableCustomFaces &&
            _characterModelCatalog?.UsesCompleteParts == true && _characterModelCatalog.TryGetResource(source.Key, out var resource))
            source.Handle = _characterModelCatalog.LoadResourceAsync<T>(resource);
        else if (!Equals(NativeCharacterModelResource(source.Key), source.Key))
            source.Handle = Addressables.LoadAssetAsync<T>(NativeCharacterModelResource(source.Key));
        else source.LoadAsset();
    }

    private static IEnumerator PrepareCharacterModelAssets(GraphicsCharacterFactoryManager factory,
        GraphicsCharacter graphics, CharacterModelCatalog.PreparedSources preparation, Func<bool> current, bool includePending)
    {
        if (!current()) yield break;
        // The live route owns only this actor's retained sources. Factory pending maps
        // belong to its in-flight native request and must never be borrowed by another actor.
        preparation.RetainNativeSources(factory, graphics, includePending);
        var meshes = new HashSet<Mesh>();
        var materials = new HashSet<Material>();
        var parts = new HashSet<GameObject>();
        foreach (var prefab in CharacterModelPartSources(factory, graphics, includePending).Where(part => part))
        {
            parts.Add(prefab);
            var skins = CustomModels.SkinMeshSource.CapturePrefab(prefab,
                prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(renderer => renderer.sharedMesh));
            if (skins != null) foreach (var skin in skins.Values) if (skin.Original) meshes.Add(skin.Original);
            foreach (var material in prefab.GetComponentsInChildren<Renderer>(true).SelectMany(renderer => renderer.sharedMaterials))
                if (material) materials.Add(material);
        }
        if (includePending)
            foreach (var source in factory.customArmorMaterialLoadOperations.Values)
                if (source.IsDone && source.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && source.Result)
                    materials.Add(source.Result);
        foreach (var source in graphics.CustomArmorMaterialPerBodySlot ?? [])
            if (source.Value) materials.Add(source.Value);
        foreach (var mesh in meshes)
        {
            if (!current()) break;
            if (CharacterModelFingerprints.TryGetValue(mesh, out _)) continue;
            yield return CharacterModelMeshPreparation.Prepare(mesh, current, () => preparation.Failed = true);
        }
        foreach (var mesh in meshes)
        {
            if (!current()) break;
            var fingerprint = CharacterModelFingerprint(mesh);
            yield return ObserveCharacterModelPreparation(preparation.Catalog.PrepareMesh(mesh, fingerprint, preparation, current), () => preparation.Failed = true);
        }
        var bodyTextures = preparation.Catalog.HasBodyTextureConditions ? CharacterModelBodyTextureSources(factory, graphics, includePending).ToHashSet() : [];
        var nativeTextures = CharacterModelNativeTextureSources(factory, graphics, includePending);
        if (SystemInfo.supportsAsyncGPUReadback && current())
        {
            var textures = materials.SelectMany(preparation.Catalog.NativeTextureSources).Concat(bodyTextures)
                .Concat(nativeTextures.Values.SelectMany(values => values)).ToHashSet();
            foreach (var texture in textures)
            {
                if (!current()) break;
                if (CharacterModelTextureFingerprints.TryGetValue(texture, out _)) continue;
                yield return CharacterModelTextureReadback.Read(texture, current, () => preparation.Failed = true);
            }
        }
        yield return ObserveCharacterModelPreparation(preparation.Catalog.PrepareMaterialTextures(parts, bodyTextures, preparation, current, nativeTextures), () => preparation.Failed = true);
        yield return ObserveCharacterModelPreparation(preparation.Catalog.PrepareJointMeshes(factory, graphics, parts, preparation, current, includePending), () => preparation.Failed = true);
        // Prepare the immutable prefab binding while the old native parts are
        // still published. No extra asynchronous wait may follow instantiation.
        if (current() && preparation.HasPacks)
        {
            preparation.RetainNativeSources(factory, graphics, includePending);
            yield return ObserveCharacterModelPreparation(PrepareCharacterModelData(parts, preparation, current), () => preparation.Failed = true);
        }
        if (current() && !preparation.Failed) preparation.Completed = true;
    }

    private static IEnumerator PrepareCharacterModelData(IEnumerable<GameObject> parts,
        CharacterModelCatalog.PreparedSources preparation, Func<bool> current)
    {
        if (!current()) yield break;
        var prefabs = parts.Where(part => part).Distinct().ToArray();
        var sources = new Dictionary<SkinnedMeshRenderer, CustomModels.SkinMeshSource>();
        foreach (var part in prefabs)
        {
            var captured = CustomModels.SkinMeshSource.CapturePrefab(part,
                part.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(renderer => renderer.sharedMesh));
            if (captured == null) yield break;
            foreach (var pair in captured) sources[pair.Key] = pair.Value;
        }
        var changes = new Dictionary<SkinnedMeshRenderer, (CustomModels.SkinMeshSource Source, Mesh Mesh)>();
        var bindings = new Dictionary<SkinnedMeshRenderer, CustomModels.SkinMeshSource.SurfaceBinding>();
        foreach (var pair in sources)
        {
            // Joint attachments depend on the final actor pair and retain the regular path.
            if (!pair.Value.ClothOwner || IsJointSkin(pair.Key) ||
                !CharacterModelFingerprints.TryGetValue(pair.Value.Original, out var fingerprint) ||
                !preparation.Catalog.HasPreparedBinding(fingerprint)) continue;
            var authored = preparation.Catalog.Mesh(pair.Value.Original, fingerprint, preparation.Packs, out _);
            var binding = preparation.Catalog.Binding(pair.Value.Original, fingerprint);
            if (authored == pair.Value.Original || binding == null || binding.BoundaryOverrides != null) continue;
            changes.Add(pair.Key, (pair.Value, authored)); bindings.Add(pair.Key, binding);
        }
        if (changes.Count == 0) yield break;
        yield return CustomModels.SkinMeshSource.PrepareNativeBindings(prefabs, changes, bindings,
            key => CharacterModelPresentation.RetainExistingSourceData(preparation.Derived, key),
            (key, data) => CharacterModelPresentation.RetainPreparedSourceData(preparation.Derived, key, data),
            source => preparation.Catalog.PrepareNativeFingerprint(source, current),
            preparation.Catalog.NativeDataFingerprint, current);
        foreach (var pair in changes)
        {
            if (!current()) yield break;
            if (bindings.TryGetValue(pair.Key, out var binding))
                yield return preparation.Catalog.PrepareSurfacePacket(pair.Value.Source.Original, binding, preparation, current);
        }
    }

    private static IReadOnlyDictionary<int, IReadOnlyCollection<Texture>> CharacterModelNativeTextureSources(
        GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics, bool includePending = true)
    {
        var sources = new Dictionary<int, HashSet<Texture>>();
        void Add(int id, Texture current,
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<Texture> pending)
        {
            if (id == -1) return;
            if (!sources.TryGetValue(id, out var textures)) sources.Add(id, textures = []);
            if (current) textures.Add(current);
            if (includePending && pending.IsValid() && pending.IsDone &&
                pending.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && pending.Result)
                textures.Add(pending.Result);
        }
        // These are the native parameter channels assigned after part instantiation.
        // Keep their current and pending sources while the factory owns the handles.
        Add(factory.bdMainTexId, graphics.BDMainTex.Value, factory.bdMainTexWrapper.Handle);
        Add(factory.bdNormalMapId, graphics.BDNormalMap.Value, factory.bdNormalMapWrapper.Handle);
        Add(factory.bdMetallicGlossMapId, graphics.BDMetallicGlossMap.Value, factory.bdMetallicGlossMapWrapper.Handle);
        Add(factory.scarsMainTexId, graphics.ScarsMainTex.Value, factory.scarsMainTexWrapper.Handle);
        Add(factory.scarsNormalMapId, graphics.ScarsNormalMap.Value, factory.scarsNormalMapWrapper.Handle);
        Add(factory.mainTexId, graphics.EyeMainTex.Value, factory.eyeMainTexWrapper.Handle);
        Add(factory.colorMaskId, graphics.EyeColorMask.Value, factory.eyeColorMaskWrapper.Handle);
        return sources.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<Texture>)pair.Value);
    }

    private static GameObject CharacterModelPartSource(GraphicsCharacterDefinitions.BodyPart part,
        IReadOnlyDictionary<GraphicsCharacterDefinitions.BodyPart,
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject>> pending,
        KeyValuePair<string, GameObject>[] native, bool includePending = true)
    {
        if (includePending && pending.TryGetValue(part, out var load) && load.IsValid() && load.IsDone &&
            load.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) return load.Result;
        var index = (int)part;
        return native != null && index >= 0 && index < native.Length ? native[index].Value : null;
    }

    private static IEnumerable<GameObject> CharacterModelPartSources(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics, bool includePending = true)
    {
        for (var index = 0; index < GraphicsCharacterDefinitions.BodyPartCount; index++)
        {
            var part = (GraphicsCharacterDefinitions.BodyPart)index;
            var shape = CharacterModelPartSource(part, factory.shapePartsLoadOperations, graphics.ShapeObjectPerBodySlot, includePending);
            var armor = CharacterModelPartSource(part, factory.armorPartsLoadOperations, graphics.ArmorObjectPerBodySlot, includePending);
            if (shape) yield return shape;
            if (armor) yield return armor;
        }
    }

    private static IEnumerable<Texture> CharacterModelBodyTextureSources(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics, bool includePending = true)
    {
        var id = Shader.PropertyToID("_MainTex");
        for (var index = 0; index < GraphicsCharacterDefinitions.BodyPartCount; index++)
        {
            var part = (GraphicsCharacterDefinitions.BodyPart)index;
            if (part == GraphicsCharacterDefinitions.BodyPart.Head) continue;
            var shape = CharacterModelPartSource(part, factory.shapePartsLoadOperations, graphics.ShapeObjectPerBodySlot, includePending);
            var armor = CharacterModelPartSource(part, factory.armorPartsLoadOperations, graphics.ArmorObjectPerBodySlot, includePending);
            Material custom = null;
            if (includePending && factory.customArmorMaterialLoadOperations.TryGetValue(part, out var customLoad) && customLoad.IsDone &&
                customLoad.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded) custom = customLoad.Result;
            else if (graphics.CustomArmorMaterialPerBodySlot is { } customs && index < customs.Length) custom = customs[index].Value;
            foreach (var material in new[] { shape, armor }.Where(value => value)
                         .SelectMany(value => value.GetComponentsInChildren<Renderer>(true)).SelectMany(renderer => renderer.sharedMaterials).Concat([custom]))
            {
                if (!material || !material.shader || !material.shader.name.StartsWith("Lux/Human/Skin", StringComparison.Ordinal) || !material.HasProperty(id)) continue;
                var texture = material.GetTexture(id);
                if (texture && texture.dimension == UnityEngine.Rendering.TextureDimension.Tex2D) yield return texture;
            }
        }
    }

    private static IEnumerable<UnityEngine.Object> CharacterModelSourceReferences(
        GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics, bool includePending)
    {
        for (var index = 0; index < GraphicsCharacterDefinitions.BodyPartCount; index++)
        {
            var part = (GraphicsCharacterDefinitions.BodyPart)index;
            yield return CharacterModelPartSource(part, factory.shapePartsLoadOperations, graphics.ShapeObjectPerBodySlot, includePending);
            yield return CharacterModelPartSource(part, factory.armorPartsLoadOperations, graphics.ArmorObjectPerBodySlot, includePending);
            var native = graphics.CustomArmorMaterialPerBodySlot;
            var material = native != null && index < native.Length ? native[index].Value : null;
            if (includePending && factory.customArmorMaterialLoadOperations.TryGetValue(part, out var pending) && pending.IsValid() &&
                pending.IsDone && pending.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded)
                material = pending.Result;
            yield return material;
        }
        Texture Select(Texture native, UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<Texture> pending) =>
            includePending && pending.IsValid() && pending.IsDone &&
            pending.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded ? pending.Result : native;
        yield return Select(graphics.BDMainTex.Value, factory.bdMainTexWrapper.Handle);
        yield return Select(graphics.BDNormalMap.Value, factory.bdNormalMapWrapper.Handle);
        yield return Select(graphics.BDMetallicGlossMap.Value, factory.bdMetallicGlossMapWrapper.Handle);
        yield return Select(graphics.ScarsMainTex.Value, factory.scarsMainTexWrapper.Handle);
        yield return Select(graphics.ScarsNormalMap.Value, factory.scarsNormalMapWrapper.Handle);
        yield return Select(graphics.EyeMainTex.Value, factory.eyeMainTexWrapper.Handle);
        yield return Select(graphics.EyeColorMask.Value, factory.eyeColorMaskWrapper.Handle);
    }

    private static IEnumerator ObserveCharacterModelPreparation(IEnumerator preparation, Action failed = null)
    {
        try
        {
            while (true)
            {
                bool more;
                try { more = preparation.MoveNext(); }
                catch (Exception exception) { failed?.Invoke(); ReportCharacterModelError("mesh-prefetch", exception); yield break; }
                if (!more) yield break;
                if (preparation.Current is IEnumerator nested)
                {
                    var observed = ObserveCharacterModelPreparation(nested, failed);
                    try { while (observed.MoveNext()) yield return observed.Current; }
                    finally { (observed as IDisposable)?.Dispose(); }
                }
                else yield return preparation.Current;
            }
        }
        finally { (preparation as IDisposable)?.Dispose(); }
    }

    private static class CharacterModelTextureReadback
    {
        private const int Capacity = 16 * 1024 * 1024;
        private static readonly object BufferLock = new();
        private static byte[] _buffer;
        private static bool _enabled, _leased;

        internal static void SetEnabled(bool enabled)
        {
            lock (BufferLock)
            {
                _enabled = enabled;
                if (enabled) _buffer ??= new byte[Capacity];
                else if (!_leased) _buffer = null;
            }
        }
        private static bool TryAcquire(out byte[] buffer)
        {
            lock (BufferLock)
            {
                buffer = null;
                if (!_enabled || _leased) return false;
                _buffer ??= new byte[Capacity];
                _leased = true;
                buffer = _buffer;
                return true;
            }
        }
        private static void Return(byte[] buffer)
        {
            lock (BufferLock)
            {
                if (!_leased || !ReferenceEquals(buffer, _buffer))
                    throw new InvalidOperationException("A native texture readback returned a different byte buffer.");
                _leased = false;
                if (!_enabled) _buffer = null;
            }
        }
        private sealed class Pending(byte[] buffer, int count)
        {
            internal RenderTexture Target;
            internal byte[] Buffer = buffer, Bytes;
            internal readonly int Count = count;
            internal bool Done, Cancelled, WorkerOwns;
            internal Exception Error;
            internal void ReturnBuffer()
            {
                if (Buffer == null) return;
                var returning = Buffer;
                Buffer = Bytes = null;
                Return(returning);
            }
            internal void Complete(UnityEngine.Rendering.AsyncGPUReadbackRequest request)
            {
                try
                {
                    if (Cancelled) return;
                    if (request.hasError) throw new InvalidOperationException("A native texture asynchronous readback failed.");
                    var pixels = request.GetData<byte>();
                    if (pixels.Length != Count) throw new InvalidOperationException("A native texture readback changed its RGBA32 byte count.");
                    Unity.Collections.NativeArray<byte>.Copy(pixels, 0, Buffer, 0, Count);
                    Bytes = Buffer;
                }
                catch (Exception exception) { Error = exception; }
                finally
                {
                    if (Target) RenderTexture.ReleaseTemporary(Target);
                    Target = null;
                    Done = true;
                    if (Cancelled) ReturnBuffer();
                }
            }
        }
        internal static IEnumerator Read(Texture source, Func<bool> current, Action onFailure = null)
        {
            var width = source.width; var height = source.height; var update = source.updateCount;
            var count = checked(width * height * 4);
            if (count > Capacity) yield break;
            byte[] buffer;
            while (!TryAcquire(out buffer))
            {
                if (!current()) yield break;
                yield return null;
            }
            var pending = new Pending(buffer, count);
            try
            {
                var active = RenderTexture.active;
                var write = GL.sRGBWrite;
                try
                {
                    pending.Target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    GL.sRGBWrite = false;
                    Graphics.Blit(source, pending.Target);
                    UnityEngine.Rendering.AsyncGPUReadback.Request(pending.Target, 0, TextureFormat.RGBA32, pending.Complete);
                }
                catch (Exception exception)
                {
                    if (pending.Target) RenderTexture.ReleaseTemporary(pending.Target);
                    pending.Target = null;
                    pending.Done = true;
                    onFailure?.Invoke(); ReportCharacterModelError("texture-prefetch", exception);
                    yield break;
                }
                finally { GL.sRGBWrite = write; RenderTexture.active = active; }
                while (!pending.Done)
                {
                    if (!current()) yield break;
                    yield return null;
                }
                if (pending.Error != null)
                {
                    onFailure?.Invoke(); ReportCharacterModelError("texture-prefetch", pending.Error);
                    yield break;
                }
                if (!current() || !source || source.width != width || source.height != height || source.updateCount != update)
                    yield break;
                var bytes = pending.Bytes;
                pending.WorkerOwns = true;
                Task<string> job;
                try
                {
                    job = Task.Run(() =>
                    {
                        try { return CustomModels.TextureRgba32LinearFingerprint(width, height, bytes, count); }
                        finally { pending.ReturnBuffer(); }
                    });
                }
                catch (Exception exception)
                {
                    pending.WorkerOwns = false;
                    onFailure?.Invoke(); ReportCharacterModelError("texture-prefetch", exception);
                    yield break;
                }
                while (!job.IsCompleted)
                {
                    if (!current())
                    {
                        _ = job.ContinueWith(failed => _ = failed.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        yield break;
                    }
                    yield return null;
                }
                if (job.IsFaulted)
                {
                    onFailure?.Invoke(); ReportCharacterModelError("texture-prefetch", job.Exception);
                    yield break;
                }
                if (!current() || !source || source.width != width || source.height != height || source.updateCount != update)
                    yield break;
                if (CharacterModelTextureFingerprints.GetValue(source, _ => job.Result) != job.Result)
                { onFailure?.Invoke(); ReportCharacterModelError("texture-prefetch", new InvalidOperationException("A native texture changed during content verification.")); }
            }
            finally
            {
                pending.Cancelled = true;
                if (!pending.WorkerOwns && pending.Done) pending.ReturnBuffer();
            }
        }
    }

    [Serializable] private sealed class CharacterModelManifest
    {
        // The strict schema reader populates these declarations before asset validation.
        public int schemaVersion = 0;
        public string recipeVersion = null;
        public CharacterModelBundle[] bundles = null;
        public CharacterModelMeshAsset[] meshes = null;
        public CharacterModelMaterialAsset[] materials = null;
        public CharacterModelProperty[] textureRemaps = null;
        public CharacterModelResource[] resources = null;
    }
    private sealed class CharacterModelResource(string source, string asset, string kind,
        CharacterModelResourceBundle[] bundles, CharacterModelPartParameter[] parameters)
    {
        internal readonly string Source = source, Asset = asset, Kind = kind;
        internal Type AssetType => Kind switch
        {
            "GameObject" => typeof(GameObject), "Material" => typeof(Material), "Texture" => typeof(Texture),
            "RuntimeAnimatorController" => typeof(RuntimeAnimatorController), "AnimationClip" => typeof(AnimationClip),
            "Avatar" => typeof(Avatar), "AudioClip" => typeof(AudioClip), _ => null
        };
        internal readonly CharacterModelResourceBundle[] Bundles = bundles;
        internal readonly CharacterModelPartParameter[] Parameters = parameters;
        internal string Key => "UB/CharacterResources/" + Source;
    }
    private sealed class CharacterModelResourceBundle(string file, string name)
    {
        internal readonly string File = file, Name = name;
    }
    private sealed class CharacterModelPartParameter(string renderer, int slot, string set)
    {
        internal readonly string Renderer = renderer, Set = set;
        internal readonly int Slot = slot;
    }
    // Managed file verification owns its read lock independently of catalog/actor life.
    private sealed class BundleVerification : IDisposable
    {
        private sealed class Result
        {
            internal Exception Error;
            internal bool Cancelled;
        }
        private readonly string _path, _expected;
        private readonly object _gate = new();
        private readonly Task<Result> _task;
        private System.IO.FileStream _readLock;
        private bool _hashFinished;
        private int _cancelled;
        internal bool Completed => _task.IsCompleted;
        internal bool Cancelled => Volatile.Read(ref _cancelled) != 0;

        internal BundleVerification(string path, string expected)
        {
            _path = System.IO.Path.GetFullPath(path); _expected = expected;
            // This delegate captures only this managed operation: no entry/catalog/actor.
            _task = Task.Run(Verify);
            _ = _task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        private void CheckCancellation()
        { if (Cancelled) throw new OperationCanceledException(); }
        private Result Verify()
        {
            var result = new Result(); System.IO.FileStream stream = null;
            try
            {
                CheckCancellation();
                stream = new System.IO.FileStream(_path, System.IO.FileMode.Open,
                    System.IO.FileAccess.Read, System.IO.FileShare.Read, 65536,
                    System.IO.FileOptions.SequentialScan);
                using (var hash = CustomModels.CreateAssetSha256())
                {
                    var buffer = new byte[65536];
                    while (true)
                    {
                        CheckCancellation(); var count = stream.Read(buffer, 0, buffer.Length);
                        if (count == 0) break;
                        hash.TransformBlock(buffer, 0, count, buffer, 0);
                    }
                    CheckCancellation(); hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var digest = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                    if (!StringComparer.Ordinal.Equals(digest, _expected))
                        throw new InvalidOperationException("A custom character model pack does not match its catalog: " + _path);
                }
                lock (_gate)
                {
                    CheckCancellation();
                    _readLock = stream; stream = null;
                    // No hash/read touches the transferred stream after this point.
                    _hashFinished = true;
                }
            }
            catch (OperationCanceledException) { result.Cancelled = true; }
            catch (Exception error) { result.Error = error; }
            finally
            {
                try { stream?.Dispose(); }
                catch (Exception error) { result.Error ??= error; }
                lock (_gate)
                {
                    _hashFinished = true;
                    if (Cancelled && _readLock != null)
                    {
                        try { _readLock.Dispose(); }
                        catch (Exception error) { result.Error ??= error; }
                        _readLock = null;
                    }
                }
            }
            return result;
        }
        internal void RequireReady()
        {
            if (!Completed) throw new InvalidOperationException("A character pack is still being verified.");
            var result = _task.GetAwaiter().GetResult();
            if (Cancelled || result.Cancelled) throw new OperationCanceledException();
            if (result.Error != null)
                throw new InvalidOperationException("A character pack verification failed: " + _path, result.Error);
        }
        // Called on the Unity owner thread. The callback is never stored or queued.
        internal void Consume(Action<long> load)
        {
            RequireReady();
            lock (_gate)
            {
                CheckCancellation();
                if (_readLock == null) throw new InvalidOperationException("A verified character pack lost its read lock.");
                try { load(_readLock.Length); }
                finally { _readLock.Dispose(); _readLock = null; Interlocked.Exchange(ref _cancelled, 1); }
            }
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _cancelled, 1);
            lock (_gate)
            {
                // In-flight hashing alone owns its stream and closes it in finally.
                if (_hashFinished) { _readLock?.Dispose(); _readLock = null; }
            }
        }
    }

    [Serializable] private sealed class CharacterModelBundle
    {
        public string file = null, sha256 = null;
        [NonSerialized] internal AssetBundle Loaded;
        [NonSerialized] internal BundleVerification Verification;
        [NonSerialized] internal int Owners;
        [NonSerialized] internal List<CharacterModelMeshAsset> Meshes;
        [NonSerialized] internal List<CharacterModelProperty> Textures;
        [NonSerialized] internal List<CharacterModelMaterialProfile> Profiles;
        [NonSerialized] internal List<CharacterModelJointAsset> Joints;
        [NonSerialized] internal LinkedListNode<CharacterModelBundle> Unused;
        [NonSerialized] internal long StoredBytes, CachedBytes;
    }
    [Serializable] private sealed class CharacterModelMeshAsset
    {
        public string sourceFingerprint = null, mode = null, authoredMeshAsset = null, authoredFingerprint = null,
            bundleFile = null, bindingFile = null, bindingSha256 = null;
        public CharacterModelJointAsset[] jointAttachments;
        [NonSerialized] internal Mesh Loaded;
        [NonSerialized] internal CustomModels.SkinMeshSource.SourceGraph.NativeSurfacePacket PreparedFinishPacket;
        [NonSerialized] internal CustomModels.SkinMeshSource.SurfaceBinding LoadedBinding;
        [NonSerialized] internal CustomModels.SkinMeshSource.SurfaceBinding LoadedBaseBinding;
        [NonSerialized] internal bool LoadedBaseBindingPrepared;
        [NonSerialized] internal Mesh LoadedBaseBindingTarget;
        [NonSerialized] internal CharacterModelBindingTemplate ParsedBinding;
        [NonSerialized] internal LinkedListNode<CharacterModelMeshAsset> ParsedBindingNode;
        [NonSerialized] internal CharacterModelSurfaceLayout[] Layouts;
        [NonSerialized] internal (string BundleSha256, string Asset, string Fingerprint) VerifiedContent;
    }
    // Coupled derived surfaces remain declarations of the existing immutable mesh catalog.
    [Serializable] private sealed class CharacterModelJointAsset
    {
        public GraphicsCharacterDefinitions.BodyPart donorPart = GraphicsCharacterDefinitions.BodyPart.Body;
        public string tangentMode = "uvDerivative";
        public string bodySourceFingerprint = null, headFingerprint = null, bodyFingerprint = null, authoredFingerprint = null, bundleFile = null, authoredMeshAsset = null;
        public int[] origins = null, bodyToHead = null, donorBones = null, removedTriangles = null, uvEdges = null;
        public float[] uvFractions = null, headToBody = null;
        [NonSerialized] internal Mesh Loaded = null;
        [NonSerialized] internal string InvalidReason = null;
        [NonSerialized] internal readonly HashSet<(Mesh Head, Mesh Body)> Verified = [];
    }

    private static void ValidateCharacterModelJoint(Mesh head, Mesh body, Mesh target, CharacterModelJointAsset declaration)
    {
        if (!head || !body || !target || !head.isReadable || !target.isReadable)
            throw new InvalidOperationException("A coupled joint requires its complete readable immutable sources.");
        var h = CharacterModelMeshData.Capture(head, true); var b = CharacterModelMeshData.Capture(body, true);
        var t = CharacterModelMeshData.Capture(target, true); var n = h.VertexCount;
        var origins = declaration.origins;
        if (origins == null || origins.Length != t.VertexCount || t.VertexCount <= n ||
            origins.Take(n).Where((v, i) => v != i).Any() || declaration.headToBody?.Length != 16 ||
            declaration.bodyToHead?.Length != b.Poses.Length || declaration.donorBones == null ||
            declaration.uvEdges?.Length != 2 * (t.VertexCount - n) || declaration.uvFractions?.Length != t.VertexCount - n ||
            declaration.removedTriangles == null || h.SubmeshIndices.Length != 1 || t.SubmeshIndices.Length != 1 ||
            h.Uvs.Length != t.Uvs.Length || h.Tangents.Length != n || b.Tangents.Length != b.VertexCount ||
            t.Poses.Length != h.Poses.Length + declaration.donorBones.Length ||
            !t.Poses.Take(h.Poses.Length).SequenceEqual(h.Poses) || declaration.headToBody.Any(v => !BodyShapeFinite(v)))
            throw new InvalidOperationException("A coupled joint has incomplete source, palette, topology or UV correspondence.");
        var frame = new Matrix4x4(); for (var i = 0; i < 16; i++) frame[i] = declaration.headToBody[i];
        if (!BodyShapeFinite(frame.determinant) || frame.determinant == 0f || frame[3] != 0f || frame[7] != 0f || frame[11] != 0f || frame[15] != 1f)
            throw new InvalidOperationException("A coupled joint has an invalid common source frame.");
        var toHead = frame.inverse; var normalToHead = frame.transpose;
        for (var i = 0; i < declaration.bodyToHead.Length; i++)
        {
            var mapped = declaration.bodyToHead[i]; if (mapped == -1) continue;
            if (mapped < 0 || mapped >= t.Poses.Length || !JointPose(t.Poses[mapped], b.Poses[i] * frame))
                throw new InvalidOperationException("A coupled joint changed a native donor bind pose.");
        }
        for (var i = 0; i < declaration.donorBones.Length; i++)
            if (declaration.donorBones[i] < 0 || declaration.donorBones[i] >= b.Poses.Length ||
                declaration.bodyToHead[declaration.donorBones[i]] != h.Poses.Length + i ||
                declaration.donorBones.Take(i).Contains(declaration.donorBones[i]))
                throw new InvalidOperationException("A coupled joint changed its appended native palette order.");
        var nativeCut = new BodyShapeCut(h.Positions, h.SubmeshIndices.SelectMany(v => v).ToArray());
        var sourceCutEdges = nativeCut.Edges.Select(e => e.First < e.Second ? (e.First, e.Second) : (e.Second, e.First)).ToHashSet();
        var sourceRemoved = declaration.removedTriangles.Where(i => i >= 0 && i < h.SubmeshIndices[0].Length / 3)
            .SelectMany(i => h.SubmeshIndices[0].Skip(i * 3).Take(3)).Select(i => h.Positions[i]).ToHashSet();
        // Closed patches can consume their removed native ridge edges. One
        // source topology serves both that domain and the per-face chart proof.
        var sourceRemovedFaces = declaration.removedTriangles.ToHashSet();
        Dictionary<(int, int), List<(int Face, int A, int B)>> sourceChartEdges = null;
        int[] sourceChartGroups = null;
        void PrepareSourceChartTopology()
        {
            if (sourceChartEdges != null) return;
            var points = new Dictionary<Vector3, int>();
            sourceChartGroups = h.Positions.Select(point =>
            {
                if (!points.TryGetValue(point, out var id)) points.Add(point, id = points.Count);
                return id;
            }).ToArray();
            sourceChartEdges = [];
            var indices = h.SubmeshIndices[0];
            for (var face = 0; face < indices.Length / 3; face++)
                for (var corner = 0; corner < 3; corner++)
                {
                    var a = indices[face * 3 + corner]; var z = indices[face * 3 + (corner + 1) % 3];
                    var ga = sourceChartGroups[a]; var gz = sourceChartGroups[z]; if (ga == gz) continue;
                    var key = ga < gz ? (ga, gz) : (gz, ga);
                    if (!sourceChartEdges.TryGetValue(key, out var faces)) sourceChartEdges.Add(key, faces = []);
                    faces.Add((face, a, z));
                }
        }
        bool SameSourcePoint(int a, int z) => h.Positions[a].Equals(h.Positions[z]) && SameWeight(h.Weights[a], h.Weights[z], null) &&
            h.Shapes.All(shape => shape.Frames.All(physical => physical.Positions[a].Equals(physical.Positions[z])));
        bool OppositeSourceFaces(List<(int Face, int A, int B)> faces) => faces.Count == 2 &&
            SameSourcePoint(faces[0].A, faces[1].B) && SameSourcePoint(faces[0].B, faces[1].A);
        HashSet<(int, int)> sourceCavityUvEdges = null;
        bool IsCavityUvEdge((int, int) uvEdge)
        {
            if (sourceCavityUvEdges != null) return sourceCavityUvEdges.Contains(uvEdge);
            PrepareSourceChartTopology();
            sourceCavityUvEdges = sourceChartEdges.Values.Where(OppositeSourceFaces)
                .SelectMany(faces => faces.Where(face => sourceRemovedFaces.Contains(face.Face)))
                .Select(edge => edge.A < edge.B ? (edge.A, edge.B) : (edge.B, edge.A)).ToHashSet();
            return sourceCavityUvEdges.Contains(uvEdge);
        }
        for (var vertex = 0; vertex < origins.Length; vertex++)
        {
            var donor = origins[vertex] < 0; var source = donor ? b : h; var index = donor ? -origins[vertex] - 1 : origins[vertex];
            if (index < 0 || index >= source.VertexCount || !SameWeight(source.Weights[index], t.Weights[vertex], donor ? declaration.bodyToHead : null) ||
                !(donor ? JointVector(toHead.MultiplyPoint3x4(source.Positions[index]), t.Positions[vertex]) : source.Positions[index].Equals(t.Positions[vertex])) ||
                !(donor ? JointVector(normalToHead.MultiplyVector(source.Normals[index]).normalized, t.Normals[vertex]) : source.Normals[index].Equals(t.Normals[vertex])) ||
                !donor && !source.Tangents[index].Equals(t.Tangents[vertex]))
                throw new InvalidOperationException("A coupled joint changed a source vertex, normal or positive native influence.");
            if (h.Colors.Length != t.Colors.Length && h.Colors.Length == 0 || h.Colors.Length != 0 &&
                (t.Colors.Length != t.VertexCount || !donor && !h.Colors[index].Equals(t.Colors[vertex])))
                throw new InvalidOperationException("A coupled joint changed its native colour channel.");
            for (var channel = 0; channel < h.Uvs.Length; channel++)
            {
                var hu = h.Uvs[channel]; var tu = t.Uvs[channel];
                if (hu.Channel != tu.Channel || hu.Dimension != tu.Dimension)
                    throw new InvalidOperationException("A coupled joint changed a native UV layout.");
                if (vertex < n)
                { if (!hu.Values[vertex].Equals(tu.Values[vertex])) throw new InvalidOperationException("A coupled joint changed a native UV coordinate."); continue; }
                var row = vertex - n; var a = declaration.uvEdges[row * 2]; var z = declaration.uvEdges[row * 2 + 1]; var f = declaration.uvFractions[row];
                if (a < 0 || z < 0 || a >= n || z >= n || !BodyShapeFinite(f) || f < 0 || f > 1)
                    throw new InvalidOperationException("A coupled joint has invalid native UV edge correspondence.");
                if (donor)
                {
                    var ga = nativeCut.Groups[a]; var gz = nativeCut.Groups[z]; var edge = ga < gz ? (ga, gz) : (gz, ga);
                    var sourceUvEdge = a < z ? (a, z) : (z, a);
                    var originalCutEdge = ga >= 0 && gz >= 0 && sourceCutEdges.Contains(edge);
                    if (!(originalCutEdge || IsCavityUvEdge(sourceUvEdge)) ||
                        !sourceRemoved.Contains(h.Positions[a]) || !sourceRemoved.Contains(h.Positions[z]))
                        throw new InvalidOperationException("A coupled joint's UV donor is outside the removed native cut.");
                }
                var expected = donor ? Vector4.LerpUnclamped(hu.Values[a], hu.Values[z], f) : hu.Values[index];
                var actual = tu.Values[vertex];
                for (var component = 0; component < hu.Dimension; component++)
                {
                    var delta = actual[component] - expected[component];
                    if (component < 2 ? Mathf.Abs(delta - Mathf.Round(delta)) > 0.000002f : delta != 0f)
                        throw new InvalidOperationException("A coupled joint changed its source UV footprint.");
                }
            }
        }
        var oldTriangles = h.SubmeshIndices[0]; var removed = sourceRemovedFaces;
        if (oldTriangles.Length % 3 != 0 || declaration.removedTriangles.Length != removed.Count ||
            removed.Any(i => i < 0 || i >= oldTriangles.Length / 3))
            throw new InvalidOperationException("A coupled joint removed an invalid source triangle.");
        var retained = Enumerable.Range(0, oldTriangles.Length / 3).Where(i => !removed.Contains(i))
            .SelectMany(i => oldTriangles.Skip(i * 3).Take(3)).ToArray(); var triangles = t.SubmeshIndices[0];
        if (!triangles.Take(retained.Length).SequenceEqual(retained) || triangles.Length <= retained.Length ||
            triangles.Length % 3 != 0 || triangles.Any(i => i < 0 || i >= t.VertexCount))
            throw new InvalidOperationException("A coupled joint changed an undeclared native triangle.");
        var strip = triangles.Skip(retained.Length).ToArray();
        ValidateRemovedSourceCharts();
        var hs = h.Shapes.ToArray(); var bs = b.Shapes.ToArray(); var ts = t.Shapes.ToArray();
        if (hs.Select(s => s.Name).Distinct().Count() != hs.Length || bs.Select(s => s.Name).Distinct().Count() != bs.Length ||
            ts.Length != hs.Length + bs.Count(s => hs.All(v => v.Name != s.Name)))
            throw new InvalidOperationException("A coupled joint has ambiguous physical channel ownership.");
        var expectedNames = hs.Select(s => s.Name).Concat(bs.Where(s => hs.All(v => v.Name != s.Name)).Select(s => s.Name)).ToArray();
        for (var shape = 0; shape < ts.Length; shape++)
        {
            var targetShape = ts[shape]; var headShape = hs.SingleOrDefault(v => v.Name == targetShape.Name); var bodyShape = bs.SingleOrDefault(v => v.Name == targetShape.Name);
            var frames = targetShape.Frames.ToArray(); var originalFrames = (headShape ?? bodyShape).Frames.ToArray();
            if (targetShape.Name != expectedNames[shape] || frames.Length != originalFrames.Length ||
                !frames.Select(f => f.Weight).SequenceEqual(originalFrames.Select(f => f.Weight)) ||
                bodyShape != null && bodyShape.Frames.Any(f => !frames.Any(v => v.Weight == f.Weight)))
                throw new InvalidOperationException("A coupled joint changed the native physical frame layout.");
            foreach (var physical in frames)
            {
                var hf = headShape?.Frames.SingleOrDefault(v => v.Weight == physical.Weight);
                var bf = bodyShape?.Frames.SingleOrDefault(v => v.Weight == physical.Weight);
                var positions = new Vector3[t.VertexCount]; var normals = new Vector3[t.VertexCount];
                for (var vertex = 0; vertex < origins.Length; vertex++)
                {
                    var donor = origins[vertex] < 0; var index = donor ? -origins[vertex] - 1 : origins[vertex];
                    var dp = donor ? toHead.MultiplyVector(bf?.Positions[index] ?? Vector3.zero) : hf?.Positions[index] ?? Vector3.zero;
                    var dn = donor ? normalToHead.MultiplyVector(b.Normals[index] + (bf?.Normals[index] ?? Vector3.zero)).normalized - t.Normals[vertex] : hf?.Normals[index] ?? Vector3.zero;
                    if (!(donor ? JointVector(dp, physical.Positions[vertex]) && JointVector(dn, physical.Normals[vertex]) :
                        dp.Equals(physical.Positions[vertex]) && dn.Equals(physical.Normals[vertex]) &&
                        (hf?.Tangents[index] ?? Vector3.zero).Equals(physical.Tangents[vertex])))
                        throw new InvalidOperationException("A coupled joint changed an owned physical frame value.");
                    positions[vertex] = t.Positions[vertex] + physical.Positions[vertex]; normals[vertex] = t.Normals[vertex] + physical.Normals[vertex];
                }
                ValidateJointTangents(t, origins, strip, positions, normals, physical, h, declaration, hf);
            }
        }
        ValidateJointTangents(t, origins, strip, t.Positions, t.Normals, null, h, declaration, null);
        ValidateJointTopology(h, b, t, origins, retained.Length);
        return;

        void ValidateRemovedSourceCharts()
        {
            PrepareSourceChartTopology();
            var pointIds = sourceChartGroups; var sourceEdges = sourceChartEdges;
            var parents = removed.ToDictionary(face => face, face => face);
            int Find(int face)
            {
                while (parents[face] != face) { parents[face] = parents[parents[face]]; face = parents[face]; }
                return face;
            }
            bool SameUv(int a, int z) => h.Positions[a].Equals(h.Positions[z]) && h.Uvs.All(uv => uv.Values[a].Equals(uv.Values[z]));
            bool SameCorner(int a, int z) => SameSourcePoint(a, z) && h.Normals[a].Equals(h.Normals[z]) && h.Tangents[a].Equals(h.Tangents[z]) &&
                h.Uvs.All(uv => uv.Values[a].Equals(uv.Values[z])) && (h.Colors.Length == 0 || h.Colors[a].Equals(h.Colors[z])) &&
                h.Shapes.All(shape => shape.Frames.All(physical => physical.Normals[a].Equals(physical.Normals[z]) && physical.Tangents[a].Equals(physical.Tangents[z])));
            foreach (var faces in sourceEdges.Values.Where(faces => OppositeSourceFaces(faces) && faces.All(face => removed.Contains(face.Face))))
            {
                if (!SameUv(faces[0].A, faces[1].B) || !SameUv(faces[0].B, faces[1].A)) continue;
                var a = Find(faces[0].Face); var z = Find(faces[1].Face); if (a != z) parents[z] = a;
            }
            var rawEdges = new Dictionary<(int, int), HashSet<int>>();
            foreach (var faces in sourceEdges.Values.Where(faces => faces.Count == 1 || OppositeSourceFaces(faces)))
                foreach (var edge in faces.Where(face => removed.Contains(face.Face)))
                {
                    var key = edge.A < edge.B ? (edge.A, edge.B) : (edge.B, edge.A);
                    if (!rawEdges.TryGetValue(key, out var charts)) rawEdges.Add(key, charts = []);
                    charts.Add(Find(edge.Face));
                }
            var removedCorners = removed.SelectMany(face => oldTriangles.Skip(face * 3).Take(3).Select(vertex => (Face: face, Vertex: vertex))).ToArray();
            var cornerCharts = new Dictionary<int, HashSet<int>>();
            var retainedEdges = new HashSet<(int, int)>();
            for (var face = 0; face < retained.Length / 3; face++)
                for (var corner = 0; corner < 3; corner++)
                {
                    var a = pointIds[retained[face * 3 + corner]]; var z = pointIds[retained[face * 3 + (corner + 1) % 3]];
                    retainedEdges.Add(a < z ? (a, z) : (z, a));
                }
            for (var face = 0; face < strip.Length; face += 3)
            {
                HashSet<int> common = null;
                for (var corner = 0; corner < 3; corner++)
                {
                    var vertex = strip[face + corner]; var origin = origins[vertex]; HashSet<int> candidates;
                    if (origin >= 0)
                    {
                        if (!cornerCharts.TryGetValue(origin, out candidates))
                        {
                            candidates = removedCorners.Where(source => SameCorner(origin, source.Vertex)).Select(source => Find(source.Face)).ToHashSet();
                            cornerCharts.Add(origin, candidates);
                        }
                    }
                    else
                    {
                        var row = vertex - n; var a = declaration.uvEdges[row * 2]; var z = declaration.uvEdges[row * 2 + 1];
                        rawEdges.TryGetValue(a < z ? (a, z) : (z, a), out candidates);
                    }
                    if (candidates == null || candidates.Count == 0) throw new InvalidOperationException("A coupled joint corner has no raw removed native chart owner.");
                    if (common == null) common = new HashSet<int>(candidates); else common.IntersectWith(candidates);
                    var next = strip[face + (corner + 1) % 3];
                    if (origin < 0 || origins[next] < 0) continue;
                    var ga = pointIds[origin]; var gz = pointIds[origins[next]]; var key = ga < gz ? (ga, gz) : (gz, ga);
                    if (!retainedEdges.Contains(key)) continue;
                    var sides = sourceEdges[key]; var missing = sides.Where(source => removed.Contains(source.Face)).ToArray();
                    if (!OppositeSourceFaces(sides) || missing.Length != 1 ||
                        !(SameCorner(origin, missing[0].A) && SameCorner(origins[next], missing[0].B) ||
                          SameCorner(origin, missing[0].B) && SameCorner(origins[next], missing[0].A)))
                        throw new InvalidOperationException("A coupled joint changed the raw removed-side receiver chart at a retained boundary.");
                }
                if (common == null || common.Count != 1) throw new InvalidOperationException("A coupled joint triangle crosses ambiguous or unrelated removed native charts.");
            }
        }

        static bool SameWeight(BoneWeight source, BoneWeight target, int[] remap)
        {
            for (var slot = 0; slot < 4; slot++)
            {
                var w = BodyShapeWeight(source, slot); if (w != BodyShapeWeight(target, slot)) return false;
                if (w <= 0f) continue; var index = BodyShapeWeightIndex(source, slot);
                var mapped = remap == null ? index : remap[index];
                if (mapped < 0 || mapped != BodyShapeWeightIndex(target, slot)) return false;
            }
            return true;
        }
    }
    private static bool JointVector(Vector3 a, Vector3 b) => BodyShapeFinite(a) && BodyShapeFinite(b) && (a - b).sqrMagnitude <= 0.000002f * 0.000002f;
    private static bool JointPose(Matrix4x4 a, Matrix4x4 b) => Enumerable.Range(0, 16).All(i => BodyShapeFinite(a[i]) && BodyShapeFinite(b[i]) && Mathf.Abs(a[i] - b[i]) <= 0.000001f);

    private static void ValidateJointTangents(CharacterModelMeshData target, int[] origins, int[] strip,
        Vector3[] positions, Vector3[] normals, CharacterModelMeshData.Frame physical,
        CharacterModelMeshData source, CharacterModelJointAsset declaration, CharacterModelMeshData.Frame sourcePhysical)
    {
        var uv = target.Uvs.Single(v => v.Channel == 0); var ts = new Vector3[target.VertexCount]; var bs = new Vector3[target.VertexCount];
        for (var k = 0; k < strip.Length; k += 3)
        {
            var a = strip[k]; var b = strip[k + 1]; var c = strip[k + 2];
            var p1 = positions[b] - positions[a]; var p2 = positions[c] - positions[a];
            var u1 = uv.Values[b] - uv.Values[a]; var u2 = uv.Values[c] - uv.Values[a]; var det = u1.x * u2.y - u1.y * u2.x;
            if (!BodyShapeFinite(det) || Mathf.Abs(det) <= 0.00000000000001f || Vector3.Dot(Vector3.Cross(p1, p2), (normals[a] + normals[b] + normals[c]) / 3f) <= 0.000000000001f)
                throw new InvalidOperationException("A coupled joint has a singular UV or inward physical triangle.");
            var tangent = (p1 * u2.y - p2 * u1.y) / det; var bitangent = (p2 * u1.x - p1 * u2.x) / det;
            for (var corner = 0; corner < 3; corner++) { var v = strip[k + corner]; ts[v] += tangent; bs[v] += bitangent; }
        }
        for (var i = 0; i < origins.Length; i++)
        {
            if (origins[i] >= 0) continue;
            var n = normals[i].normalized; Vector3 tangent; float sign;
            if (declaration.tangentMode == "sourceUvEdge")
            {
                var row = i - source.VertexCount; var a = declaration.uvEdges[row * 2]; var z = declaration.uvEdges[row * 2 + 1];
                var f = declaration.uvFractions[row];
                var first = (Vector3)source.Tangents[a] + (sourcePhysical?.Tangents[a] ?? Vector3.zero);
                var second = (Vector3)source.Tangents[z] + (sourcePhysical?.Tangents[z] ?? Vector3.zero);
                if (!BodyShapeFinite(first) || !BodyShapeFinite(second) ||
                    source.Tangents[a].w != source.Tangents[z].w || Mathf.Abs(source.Tangents[a].w) != 1f ||
                    (first.normalized + second.normalized).sqrMagnitude <= 0.000000000001f)
                    throw new InvalidOperationException("A coupled joint has an incompatible native tangent chart edge.");
                var field = Vector3.LerpUnclamped(first, second, f);
                field -= n * Vector3.Dot(n, field);
                if (!BodyShapeFinite(field) || field.sqrMagnitude <= 0.000000000001f)
                    throw new InvalidOperationException("A coupled joint has a singular native source tangent field.");
                tangent = field.normalized; sign = source.Tangents[a].w;
            }
            else
            {
                tangent = (ts[i] - n * Vector3.Dot(ts[i], n)).normalized;
                sign = Mathf.Sign(Vector3.Dot(Vector3.Cross(n, tangent), bs[i]));
            }
            var actual = (Vector3)target.Tangents[i] + (physical?.Tangents[i] ?? Vector3.zero);
            if (!BodyShapeFinite(actual) || !BodyShapeFinite(n) || n.sqrMagnitude < 0.99f ||
                !BodyShapeFinite(tangent) || tangent.sqrMagnitude < 0.99f || Mathf.Abs(Vector3.Dot(n, actual)) > 0.0001f ||
                Mathf.Abs(actual.sqrMagnitude - 1f) > 0.0002f || (tangent - actual).sqrMagnitude > 0.0001f * 0.0001f || sign != target.Tangents[i].w)
                throw new InvalidOperationException("A coupled joint has an invalid source-derived tangent frame.");
        }
    }
    private static void ValidateJointTopology(CharacterModelMeshData head, CharacterModelMeshData body,
        CharacterModelMeshData target, int[] origins, int retainedIndexCount)
    {
        // Group by immutable source identity, not rounded target positions.
        var sourceHead = new Dictionary<Vector3, int>(); var sourceBody = new Dictionary<Vector3, int>(); var groups = new int[target.VertexCount];
        var next = 0;
        for (var i = 0; i < groups.Length; i++)
        {
            var donor = origins[i] < 0; var dictionary = donor ? sourceBody : sourceHead;
            var point = donor ? body.Positions[-origins[i] - 1] : head.Positions[origins[i]];
            if (!dictionary.TryGetValue(point, out var group)) dictionary.Add(point, group = next++);
            groups[i] = group;
        }
        var edges = new Dictionary<(int, int), List<(int, int)>>(); var indices = target.SubmeshIndices[0];
        var stripEdges = new HashSet<(int, int)>();
        for (var i = 0; i < indices.Length; i += 3)
        for (var corner = 0; corner < 3; corner++)
        {
            var a = groups[indices[i + corner]]; var b = groups[indices[i + (corner + 1) % 3]];
            if (a == b) throw new InvalidOperationException("A coupled joint has a degenerate source edge.");
            var key = a < b ? (a, b) : (b, a); if (!edges.TryGetValue(key, out var values)) edges.Add(key, values = []);
            values.Add((a, b));
            if (i >= retainedIndexCount) stripEdges.Add(key);
        }
        // The preceding source-prefix check preserves native retained topology exactly.
        // Every edge used by the new strip must still be manifold and oppositely wound.
        if (stripEdges.Any(key => edges[key].Count > 2 || edges[key].Count == 2 && edges[key][0] == edges[key][1]))
            throw new InvalidOperationException("A coupled joint has a nonmanifold or duplicated edge.");
        var cut = new BodyShapeCut(body.Positions, body.SubmeshIndices.SelectMany(v => v).ToArray());
        var expected = cut.Edges.Select(e => (cut.Points[e.First], cut.Points[e.Second])).ToArray();
        var selected = sourceBody.Keys.ToHashSet();
        var cutIndices = cut.Points.Select((point, index) => (point, index)).ToDictionary(v => v.point, v => v.index);
        if (selected.Any(point => !cutIndices.ContainsKey(point)))
            throw new InvalidOperationException("A coupled joint source is not a native body boundary.");
        var components = selected.Select(point => cut.Components[cutIndices[point]]).Distinct().ToArray();
        if (components.Length == 0 || components.Any(component => !cut.Closed[component] ||
                cut.Components.Count(value => value == component) != selected.Count(point => cut.Components[cutIndices[point]] == component)))
            throw new InvalidOperationException("A coupled joint does not copy complete native donor boundaries.");
        var donorBoundary = new HashSet<(int, int)>();
        foreach (var edge in expected.Where(e => selected.Contains(e.Item1) && selected.Contains(e.Item2)))
        {
            var a = sourceBody[edge.Item1]; var b = sourceBody[edge.Item2]; var key = a < b ? (a, b) : (b, a);
            donorBoundary.Add(key);
            if (!edges.TryGetValue(key, out var values) || values.Count != 1)
                throw new InvalidOperationException("A coupled joint does not retain its actual donor boundary.");
        }
        var nativeGroups = head.Positions.Select(point => sourceHead[point]).ToArray();
        var nativeBoundary = BodyShapeEdges(head.SubmeshIndices.SelectMany(v => v).ToArray(), nativeGroups)
            .Where(pair => pair.Value == 1).Select(pair => pair.Key).ToHashSet();
        // Original open cuts remain legal; a removed receiver cavity or dangling strip may not remain open.
        if (edges.Any(pair => pair.Value.Count == 1 && !donorBoundary.Contains(pair.Key) && !nativeBoundary.Contains(pair.Key)))
            throw new InvalidOperationException("A coupled joint leaves a new source boundary open.");
        if (selected.Count < 3 || indices.Skip(retainedIndexCount).All(i => origins[i] >= 0))
            throw new InvalidOperationException("A coupled joint has no complete donor boundary.");
    }

    private static bool JointPeriodicSamplers(SkinnedMeshRenderer renderer)
    {
        var found = false;
        foreach (var material in renderer.sharedMaterials)
        {
            if (!material || !material.shader) continue;
            for (var index = 0; index < material.shader.GetPropertyCount(); index++)
            {
                if (material.shader.GetPropertyType(index) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var name = material.shader.GetPropertyName(index); var texture = material.GetTexture(name); if (!texture) continue;
                var scale = material.GetTextureScale(name); found = true;
                if (texture.wrapModeU != TextureWrapMode.Repeat || texture.wrapModeV != TextureWrapMode.Repeat ||
                    !BodyShapeFinite(scale.x) || !BodyShapeFinite(scale.y) || scale.x != Mathf.Round(scale.x) || scale.y != Mathf.Round(scale.y)) return false;
            }
        }
        return found;
    }

    [Serializable] private sealed class CharacterModelMaterialAsset
    {
        public string sourceFingerprint = null, sourceShader = null, bundleFile = null;
        public int materialSlot = 0;
        public CharacterModelProperty[] properties = null;
        public CharacterModelMaterialProfile authoredProfile = null;
        internal bool UsesTextures
        {
            get
            {
                foreach (var property in properties) if (property.kind is "texture" or "texture-remap") return true;
                return false;
            }
        }
        internal string AssetBundleFile => UsesTextures ? bundleFile : null;
        internal bool UsesBodyTexture
        {
            get
            {
                foreach (var property in properties)
                {
                    if (property.sourceBodyTextureFingerprint != null) return true;
                    foreach (var candidate in property.SourceCandidates ?? [])
                        if (candidate.sourceBodyTextureFingerprint != null) return true;
                }
                return false;
            }
        }
    }
    [Serializable] private sealed class CharacterModelMaterialProfile
    {
        public string bundleFile = null, materialAsset = null, shader = null;
        public CharacterModelSurfaceLayout layout = null;
        [NonSerialized] internal Material Loaded;
    }
    [Serializable] private sealed class CharacterModelSurfaceLayout
    {
        internal const string DefaultGroomCodec = "tile16-v1";
        internal const string FineFiberGroomCodec = "fineFiber32-v1";
        public int sourceUv = 0, groomUv = 0;
        public int[] preservedUv = null;
        public string groomCodec = DefaultGroomCodec;

        internal bool HasSupportedGroomCodec => groomCodec is DefaultGroomCodec or FineFiberGroomCodec;
        internal bool IsValidGroomCoordinate(Vector4 value)
        {
            if (!HasSupportedGroomCodec || !BodyShapeFinite(value.x) || !BodyShapeFinite(value.y) ||
                !BodyShapeFinite(value.z) || !BodyShapeFinite(value.w) || value.z < 0f || value.z != (int)value.z)
                return false;
            if (value.z <= 16f) return value.w is -1f or 1f;
            return groomCodec == FineFiberGroomCodec && value.z <= 48f &&
                   value.x is >= 0f and <= 1f && value.y is >= 0f and <= 1f &&
                   Mathf.Abs(value.w) is >= 1f and < 2f;
        }
        internal bool IsSameGroomSurface(Vector4 first, Vector4 second, Vector4 third) =>
            first.z == second.z && first.z == third.z &&
            (groomCodec != FineFiberGroomCodec || first.z <= 16f ||
             first.w == second.w && first.w == third.w);
    }
    [Serializable] private sealed class CharacterModelProperty
    {
        public string name = null, kind = null, textureAsset = null, sourceTextureFingerprint = null,
            sourceShader = null, bundleFile = null, sourceBodyTextureFingerprint = null;
        public int? sourceWidth = null, sourceHeight = null;
        public float value = 0f;
        [NonSerialized] internal Texture2D Loaded;
        [NonSerialized] internal CharacterModelProperty[] SourceCandidates;
        internal bool MatchesSourceDimensions(int width, int height) =>
            !sourceWidth.HasValue || sourceWidth.Value == width && sourceHeight.Value == height;
    }

    private sealed class CharacterModelBindingPacketLimit : Exception { }

    private sealed class CharacterModelBindingTemplate(Matrix4x4 frame,
        CustomModels.SkinMeshSource.SurfaceVertex[] vertices, int originalCount, int authoredCount, int slots)
    {
        internal readonly int OriginalCount = originalCount, AuthoredCount = authoredCount, Slots = slots;
        internal readonly long Bytes = 256L + vertices.Sum(vertex => vertex.EstimatedMemory);
        // Only the immutable format data is retained. Native tangents and boundary
        // meshes remain owned by the current mesh/actor binding, never this template.
        internal CustomModels.SkinMeshSource.SurfaceBinding Instantiate(string identity, Vector4[] tangents) =>
            new(identity, frame, tangents, (CustomModels.SkinMeshSource.SurfaceVertex[])vertices.Clone());
    }

    private sealed class CharacterModelCatalog : IDisposable, IResourceLocator
    {
        private const long MaximumParsedBindingBytes = 32L * 1024 * 1024;
        private const int MaximumParsedBindings = 24;
        private const long MaximumBindingPacketBytes = 8L * 1024 * 1024;
        private readonly LinkedList<CharacterModelMeshAsset> _parsedBindings = new();
        private long _parsedBindingBytes;
        private static int _bindingPreparationBusy;
        private const long MaximumUnusedBundleBytes = 256L * 1024 * 1024;
        private const int MaximumUnusedBundles = 24;
        private const long MaximumUnusedNativeSourceBytes = 64L * 1024 * 1024;
        private const int MaximumUnusedNativeSources = 24;
        private sealed class NativeSourceEntry
        {
            internal readonly UnityEngine.Object Source;
            internal readonly UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle Handle;
            internal readonly HashSet<UnityEngine.Object> Data;
            internal readonly Dictionary<UnityEngine.Object, long> Assets;
            internal int Owners;
            internal LinkedListNode<NativeSourceEntry> Unused;
            internal NativeSourceEntry(UnityEngine.Object source, UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle handle)
            {
                Source = source; Handle = handle;
                Data = source is GameObject prefab ? CustomModels.SkinMeshSource.NativePrefabData(prefab) : [];
                Assets = NativeSourceAssets(source).ToDictionary(asset => asset,
                    asset => Math.Max(0L, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(asset)));
                foreach (var data in Data) Assets[data] = CustomModels.SkinMeshSource.NativeDataMemorySize(data);
            }
        }
        private readonly Dictionary<UnityEngine.Object, NativeSourceEntry> _nativeSources = [];
        private readonly Dictionary<UnityEngine.Object, string> _nativeDataFingerprints = [];
        private readonly LinkedList<NativeSourceEntry> _unusedNativeSources = new();
        private int _preparedSources, _preparedNativeSourceReferences;
        internal bool HasPreparedSources => _preparedSources != 0;
        internal bool HasLiveSourceOwners => _nativeSources.Values.Any(entry => entry.Owners != 0);
        internal bool IsRetiring { get; private set; }
        internal void Retire() => IsRetiring = true;
        internal sealed class PreparedSources(CharacterModelCatalog catalog) : IDisposable
        {
            internal readonly CharacterModelCatalog Catalog = catalog;
            internal readonly HashSet<string> Packs = [];
            internal readonly HashSet<UnityEngine.Object> NativeSources = [];
            internal readonly HashSet<string> Derived = new(StringComparer.Ordinal);
            internal readonly int Generation = _characterModelPreparationGeneration;
            private RulesetCharacter _actor;
            private UnityEngine.Object[] _sourceReferences;
            internal bool Completed, Failed;
            internal bool HasPacks => Packs.Count != 0;
            internal void CaptureSources(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics, bool includePending)
            {
                _actor = graphics.RulesetCharacter;
                _sourceReferences = CharacterModelSourceReferences(factory, graphics, includePending).ToArray();
            }
            internal bool SourcesCurrent(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics, bool includePending) =>
                factory && graphics && ReferenceEquals(_actor, graphics.RulesetCharacter) &&
                Generation == _characterModelPreparationGeneration && !Catalog.IsRetiring &&
                ReferenceEquals(Catalog, _characterModelCatalog) && _sourceReferences != null &&
                !_sourceReferences.Any(value => !ReferenceEquals(value, null) && !value) &&
                _sourceReferences.SequenceEqual(CharacterModelSourceReferences(factory, graphics, includePending));
            internal void RetainNativeSources(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics, bool includePending = true)
            {
                var before = NativeSources.Count;
                try { Catalog.RetainNativePrefabSources(factory, graphics, NativeSources, includePending); }
                finally { Catalog._preparedNativeSourceReferences += NativeSources.Count - before; }
            }
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                CharacterModelPresentation.ReleaseSourceData(Derived);
                Catalog.ReleaseBundles(Packs);
                var before = NativeSources.Count;
                try { Catalog.ReleaseNativeSources(NativeSources); }
                finally { Catalog._preparedNativeSourceReferences -= before - NativeSources.Count; }
                if (--Catalog._preparedSources < 0) throw new InvalidOperationException("A catalog preparation has no matching source owner.");
                _disposed = true;
                ReleaseUnusedCharacterModelCatalog();
            }
        }
        internal PreparedSources PrepareSources()
        {
            if (IsRetiring) throw new InvalidOperationException("A retiring catalog cannot prepare native sources.");
            _preparedSources++;
            return new PreparedSources(this);
        }

        private readonly string _directory;
        private readonly Dictionary<string, CharacterModelBundle> _bundles;
        private readonly Dictionary<string, CharacterModelMeshAsset> _meshes;
        private readonly Dictionary<string, CharacterModelResource> _resources;
        private readonly Dictionary<string, IResourceLocation> _resourceLocations = new(StringComparer.Ordinal);
        private readonly HashSet<string> _verifiedResourceFiles = new(StringComparer.Ordinal);
        // A chooser retains bodies, materials and animation assets as well as styles.
        // Keep that working set under the byte budget without evicting at each choice.
        private const int MaximumRetainedResources = 256;
        private const long MaximumRetainedResourceBytes = 512L * 1024 * 1024;
        private sealed class RetainedResource
        {
            internal (string Source, Type Type) Key;
            internal AsyncOperationHandle Handle;
            internal Action<AsyncOperationHandle> Completed;
            internal Dictionary<UnityEngine.Object, long> Assets;
            internal LinkedListNode<RetainedResource> Node;
        }
        private readonly Dictionary<(string Source, Type Type), RetainedResource> _retainedResources = [];
        private readonly LinkedList<RetainedResource> _retainedResourceOrder = new();
        private long _retainedResourceBytes;
        private const int MaximumUnusedClothMeshes = 24;
        private const long MaximumUnusedClothMeshBytes = 256L * 1024 * 1024;
        private sealed class ClothMeshEntry(Mesh source, Mesh mesh)
        {
            internal readonly Mesh Source = source, Mesh = mesh;
            internal LinkedListNode<ClothMeshEntry> Unused;
            internal long Bytes;
        }
        private readonly Dictionary<Mesh, ClothMeshEntry> _clothMeshes = [];
        private readonly LinkedList<ClothMeshEntry> _unusedClothMeshes = new();
        private long _unusedClothMeshBytes;
        private bool _disposed;
        internal bool UsesCompleteParts { get; }

        internal bool TryAcquireClothMesh(Mesh source, out Mesh mesh)
        {
            mesh = null;
            if (_disposed || IsRetiring || !UsesCompleteParts || !source || !source.isReadable ||
                !_retainedResources.Values.Any(entry => entry.Assets?.ContainsKey(source) == true)) return false;
            TrimClothMeshes();
            var entry = _unusedClothMeshes.FirstOrDefault(candidate => ReferenceEquals(candidate.Source, source));
            if (entry != null)
            {
                _unusedClothMeshes.Remove(entry.Unused); entry.Unused = null;
                _unusedClothMeshBytes -= entry.Bytes;
                mesh = entry.Mesh;
                // Physics changes only these mutable streams. Expressions and the
                // appended identity bind pose remain owned by this runtime clone.
                mesh.vertices = source.vertices;
                mesh.normals = source.normals;
                mesh.tangents = source.tangents;
                var bones = source.GetBonesPerVertex();
                if (bones.Length != 0) mesh.SetBoneWeights(bones, source.GetAllBoneWeights());
                mesh.bounds = source.bounds;
                return true;
            }
            mesh = UnityEngine.Object.Instantiate(source);
            _clothMeshes.Add(mesh, new ClothMeshEntry(source, mesh));
            CharacterModelClothMeshOwners.Add(mesh, this);
            return true;
        }
        internal void ReturnClothMesh(Mesh mesh)
        {
            if (!_clothMeshes.TryGetValue(mesh, out var entry)) return;
            if (entry.Unused != null) return;
            if (_disposed || IsRetiring || _characterModelsUnloading || !entry.Source || !mesh)
            {
                DestroyClothMesh(entry);
                return;
            }
            entry.Bytes = Math.Max(0L, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(mesh));
            entry.Unused = _unusedClothMeshes.AddLast(entry);
            _unusedClothMeshBytes += entry.Bytes;
            TrimClothMeshes();
        }
        private void TrimClothMeshes()
        {
            foreach (var entry in _unusedClothMeshes.Where(entry => !entry.Source || !entry.Mesh).ToArray()) DestroyClothMesh(entry);
            while (_unusedClothMeshes.First is { } first &&
                   (_unusedClothMeshes.Count > MaximumUnusedClothMeshes || _unusedClothMeshBytes > MaximumUnusedClothMeshBytes))
                DestroyClothMesh(first.Value);
        }
        private void DestroyClothMesh(ClothMeshEntry entry)
        {
            if (entry.Unused != null)
            {
                _unusedClothMeshes.Remove(entry.Unused); entry.Unused = null;
                _unusedClothMeshBytes -= entry.Bytes;
            }
            _clothMeshes.Remove(entry.Mesh);
            CharacterModelClothMeshOwners.Remove(entry.Mesh);
            if (entry.Mesh) UnityEngine.Object.Destroy(entry.Mesh);
        }

        private sealed class ResourceLoadOperation<T>(CharacterModelCatalog catalog, CharacterModelResource part)
            : AsyncOperationBase<T>, IUpdateReceiver where T : UnityEngine.Object
        {
            private PreparedSources _lease;
            private AsyncOperationHandle<T> _loaded;
            private bool _watching;

            protected override void Execute()
            {
                _lease = catalog.PrepareSources();
                _watching = true;
                Addressables.ResourceManager.AddUpdateReceiver(this);
                Update(0);
            }
            public void Update(float unscaledDeltaTime)
            {
                if (!_watching) return;
                try
                {
                    if (!catalog.VerifyResourceFiles(part)) return;
                    StopWatching();
                    _loaded = catalog.LoadVerifiedResource<T>(part);
                    _loaded.Completed += Loaded;
                }
                catch (Exception exception)
                {
                    StopWatching();
                    _lease?.Dispose(); _lease = null;
                    Complete(null, false, exception.Message, false);
                }
            }
            private void Loaded(AsyncOperationHandle<T> operation)
            {
                _lease?.Dispose(); _lease = null;
                Complete(operation.Result, operation.Status == AsyncOperationStatus.Succeeded,
                    operation.OperationException?.Message, false);
            }
            private void StopWatching()
            {
                if (!_watching) return;
                _watching = false;
                Addressables.ResourceManager.RemoveUpdateReciever(this);
            }
            protected override void GetDependencies(List<AsyncOperationHandle> dependencies)
            {
                if (_loaded.IsValid()) dependencies.Add(_loaded);
            }
            protected override void Destroy()
            {
                StopWatching();
                _lease?.Dispose(); _lease = null;
                if (!_loaded.IsValid()) return;
                _loaded.Completed -= Loaded;
                Addressables.Release(_loaded); _loaded = default;
            }
        }

        internal bool TryGetResource(object key, out CharacterModelResource part)
        {
            const string prefix = "UB/CharacterResources/";
            part = null;
            if (key is not string text) return false;
            if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text.Substring(prefix.Length);
            return _resources.TryGetValue(text, out part);
        }
        internal AsyncOperationHandle<T> LoadResourceAsync<T>(CharacterModelResource part) where T : UnityEngine.Object => VerifyResourceFiles(part)
            ? LoadVerifiedResource<T>(part)
            : Addressables.ResourceManager.StartOperation(new ResourceLoadOperation<T>(this, part), default);
        private AsyncOperationHandle<T> LoadVerifiedResource<T>(CharacterModelResource part) where T : UnityEngine.Object
        {
            if (IsRetiring) return Addressables.LoadAssetAsync<T>(part.Key);
            var key = (part.Source, typeof(T));
            if (_retainedResources.TryGetValue(key, out var retained))
            {
                if (retained.Handle.IsValid())
                {
                    _retainedResourceOrder.Remove(retained.Node);
                    _retainedResourceOrder.AddLast(retained.Node);
                    Addressables.ResourceManager.Acquire(retained.Handle);
                    return retained.Handle.Convert<T>();
                }
                ReleaseRetainedResource(retained);
            }
            var loaded = Addressables.LoadAssetAsync<T>(part.Key);
            retained = new RetainedResource { Key = key, Handle = loaded };
            retained.Node = _retainedResourceOrder.AddLast(retained);
            _retainedResources.Add(key, retained);
            // The cache owns the original reference; the factory owns its acquired reference.
            Addressables.ResourceManager.Acquire(retained.Handle);
            retained.Completed = operation =>
            {
                if (!_retainedResources.TryGetValue(key, out var current) || !ReferenceEquals(current, retained)) return;
                if (operation.Status != AsyncOperationStatus.Succeeded || operation.Result is not UnityEngine.Object source || !source)
                {
                    ReleaseRetainedResource(retained);
                    return;
                }
                retained.Assets = NativeSourceAssets(source).ToDictionary(asset => asset,
                    asset => Math.Max(0L, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(asset)));
                TrimRetainedResources();
            };
            retained.Handle.Completed += retained.Completed;
            TrimRetainedResources();
            return loaded;
        }
        private void TrimRetainedResources()
        {
            long Size() => _retainedResources.Values.Where(entry => entry.Assets != null)
                .SelectMany(entry => entry.Assets).GroupBy(asset => asset.Key).Sum(group => group.First().Value);
            _retainedResourceBytes = Size();
            while (_retainedResourceOrder.First is { } first &&
                   (_retainedResources.Count > MaximumRetainedResources || _retainedResourceBytes > MaximumRetainedResourceBytes))
            {
                ReleaseRetainedResource(first.Value);
                _retainedResourceBytes = Size();
            }
        }
        private void ReleaseRetainedResource(RetainedResource retained)
        {
            _retainedResources.Remove(retained.Key);
            _retainedResourceOrder.Remove(retained.Node);
            if (retained.Handle.IsValid())
            {
                if (retained.Completed != null) retained.Handle.Completed -= retained.Completed;
                Addressables.Release(retained.Handle);
            }
            retained.Assets = null;
        }
        private bool VerifyResourceFiles(CharacterModelResource part)
        {
            var ready = true;
            foreach (var dependency in part.Bundles)
            {
                if (_verifiedResourceFiles.Contains(dependency.File)) continue;
                var entry = _bundles[dependency.File];
                var verification = entry.Verification;
                if (verification == null || verification.Cancelled)
                {
                    verification?.Dispose();
                    entry.Verification = verification = new BundleVerification(System.IO.Path.Combine(_directory, entry.file), entry.sha256);
                }
                if (!verification.Completed) { ready = false; continue; }
                verification.RequireReady();
                _verifiedResourceFiles.Add(dependency.File);
            }
            return ready;
        }
        public string LocatorId => "UB.CharacterModelResources";
        public IEnumerable<object> Keys => _resources.Values.Select(part => (object)part.Key);
        private readonly bool _hasJointAttachments;
        private readonly Dictionary<string, CharacterModelMaterialAsset> _materials;
        private readonly Dictionary<(string Shader, string Property, string Fingerprint, string Body), CharacterModelProperty> _textureRemaps = [];
        private readonly Dictionary<string, CharacterModelProperty[]> _textureProperties = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, CharacterModelProperty[]>> _nativeTextureProperties;
        private readonly HashSet<string> _bodyTextureFingerprints;
        internal bool HasBodyTextureConditions => _bodyTextureFingerprints.Count != 0;
        internal bool IsBodyTextureFingerprint(string fingerprint) => _bodyTextureFingerprints.Contains(fingerprint);
        private readonly LinkedList<CharacterModelBundle> _unusedBundles = new();
        private long _unusedBundleBytes;
        private CharacterModelCatalog(string directory, CharacterModelManifest manifest)
        {
            _directory = directory;
            UsesCompleteParts = manifest.schemaVersion == 3;
            _resources = (manifest.resources ?? []).ToDictionary(part => part.Source, StringComparer.Ordinal);
            _bodyTextureFingerprints = (manifest.textureRemaps ?? []).Concat(manifest.materials.SelectMany(material => material.properties))
                .Select(property => property.sourceBodyTextureFingerprint).Where(value => value != null).ToHashSet(StringComparer.Ordinal);
            _nativeTextureProperties = (manifest.textureRemaps ?? []).Select(property => (property.sourceShader, Property: property))
                .Concat(manifest.materials.SelectMany(material => material.properties
                    .Where(property => property.kind == "texture-remap").Select(property => (material.sourceShader, Property: property))))
                .GroupBy(row => row.sourceShader, StringComparer.Ordinal).ToDictionary(group => group.Key,
                    group => group.GroupBy(row => row.Property.name, StringComparer.Ordinal)
                        .ToDictionary(properties => properties.Key, properties => properties.Select(row => row.Property).ToArray(), StringComparer.Ordinal),
                    StringComparer.Ordinal);
            _bundles = manifest.bundles.ToDictionary(value => value.file, StringComparer.Ordinal);
            _meshes = manifest.meshes.ToDictionary(value => value.sourceFingerprint, StringComparer.Ordinal);
            _hasJointAttachments = manifest.meshes.Any(mesh => (mesh.jointAttachments ?? []).Any(joint => joint.InvalidReason == null));
            _materials = manifest.materials.ToDictionary(value => MaterialKey(value.sourceFingerprint, value.materialSlot, value.sourceShader), StringComparer.Ordinal);
            foreach (var entry in _bundles.Values)
            {
                entry.Meshes = [];
                entry.Textures = [];
                entry.Profiles = [];
                entry.Joints = [];
            }
            var layouts = manifest.materials.Where(value => value.authoredProfile != null)
                .ToLookup(value => value.sourceFingerprint, value => value.authoredProfile.layout, StringComparer.Ordinal);
            foreach (var mesh in manifest.meshes)
            {
                mesh.Layouts = layouts[mesh.sourceFingerprint].ToArray();
                mesh.VerifiedContent = default;
                if (mesh.mode == "authored") _bundles[mesh.bundleFile].Meshes.Add(mesh);
                foreach (var joint in mesh.jointAttachments ?? [])
                    if (joint.InvalidReason == null && _bundles.TryGetValue(joint.bundleFile, out var owner)) owner.Joints.Add(joint);
            }
            foreach (var material in manifest.materials)
            {
                foreach (var property in material.properties)
                    if (property.kind is "texture" or "texture-remap")
                        _bundles[property.bundleFile ?? material.AssetBundleFile].Textures.Add(property);
                if (material.authoredProfile is { } profile) _bundles[profile.bundleFile].Profiles.Add(profile);
            }
            foreach (var property in manifest.textureRemaps ?? [])
            {
                _textureRemaps.Add((property.sourceShader, property.name, property.sourceTextureFingerprint, property.sourceBodyTextureFingerprint), property);
                _bundles[property.bundleFile].Textures.Add(property);
            }
            foreach (var group in (manifest.textureRemaps ?? []).GroupBy(property => property.sourceShader, StringComparer.Ordinal))
                _textureProperties.Add(group.Key, group.GroupBy(property => property.name, StringComparer.Ordinal)
                    .OrderBy(properties => properties.Key, StringComparer.Ordinal).Select(properties => new CharacterModelProperty
                    { name = properties.Key, kind = "texture-index", SourceCandidates = properties.ToArray() }).ToArray());
            foreach (var key in _materials.Keys.ToArray())
            {
                var entry = _materials[key];
                if (entry.authoredProfile == null && _textureProperties.TryGetValue(entry.sourceShader, out var selectors))
                    _materials[key] = WithSharedTextures(entry, selectors);
            }
            if (UsesCompleteParts) Addressables.AddResourceLocator(this);
        }
        private static bool IsHash(string value) => value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        private static bool SourceDimensionsValid(CharacterModelProperty property) =>
            property.sourceWidth.HasValue == property.sourceHeight.HasValue &&
            (!property.sourceWidth.HasValue || property.kind == "texture-remap" && property.sourceWidth.Value > 0 && property.sourceHeight.Value > 0);
        private static bool IsBundleFile(string value) => !string.IsNullOrWhiteSpace(value) &&
            value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && System.IO.Path.GetFileName(value) == value && value is not ("." or "..");
        private static string MaterialKey(string fingerprint, int slot, string shader) => fingerprint + "|" + slot + "|" + shader;
        public bool Locate(object key, Type type, out IList<IResourceLocation> locations)
        {
            locations = null;
            if (!UsesCompleteParts || key is not string text ||
                !text.StartsWith("UB/CharacterResources/", StringComparison.Ordinal) ||
                !_resources.TryGetValue(text.Substring("UB/CharacterResources/".Length), out var part)) return false;
            if (type != null && !type.IsAssignableFrom(part.AssetType)) return false;
            if (!_resourceLocations.TryGetValue(text, out var selected))
            {
                IResourceLocation original = null;
                foreach (var locator in Addressables.ResourceLocators)
                {
                    if (ReferenceEquals(locator, this) || !locator.Locate(part.Source, part.AssetType, out var found)) continue;
                    original = found.FirstOrDefault();
                    if (original != null) break;
                }
                if (original == null || !original.HasDependencies)
                    throw new InvalidOperationException("The character part source address is unavailable: " + part.Source);
                var template = original.Dependencies.FirstOrDefault(dependency => dependency.Data is AssetBundleRequestOptions);
                if (template == null) throw new InvalidOperationException("The character part source has no bundle provider.");
                // A finished part owns its full dependency closure. The native
                // catalog supplies provider metadata, without loading its assets.
                var dependencies = part.Bundles.Select(bundle => ResourceBundleLocation(template, bundle)).ToArray();
                selected = new ResourceLocationBase(part.Key, part.Asset, original.ProviderId, part.AssetType, dependencies) { Data = original.Data };
                _resourceLocations.Add(text, selected);
            }
            locations = new[] { selected };
            return true;
        }
        private IResourceLocation ResourceBundleLocation(IResourceLocation template, CharacterModelResourceBundle bundle)
        {
            var options = (AssetBundleRequestOptions)typeof(object).GetMethod("MemberwiseClone",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(template.Data, null);
            var entry = _bundles[bundle.File];
            var path = System.IO.Path.Combine(_directory, entry.file);
            options.BundleName = bundle.Name;
            options.Crc = 0;
            options.Hash = entry.sha256.Substring(0, 32);
            options.BundleSize = new System.IO.FileInfo(path).Length;
            return new ResourceLocationBase("UB.CharacterBundle/" + entry.file, path, template.ProviderId, template.ResourceType) { Data = options };
        }
        internal IEnumerator PrepareCompleteParts(GraphicsCharacterFactoryManager factory, Func<bool> current)
        {
            var sources = new[] { factory.shapePartsToLoad, factory.armorPartsToLoad };
            var required = sources.SelectMany(parts => parts).Select(key => TryGetResource(key, out var part) ? part : null)
                .Where(part => part != null).Distinct().ToArray();
            foreach (var part in required)
            {
                while (current() && !VerifyResourceFiles(part))
                {
                    yield return null;
                }
                if (!current()) yield break;
            }
            if (!current()) yield break;
            foreach (var parts in sources)
                for (var index = 0; index < parts.Length; index++)
                    if (TryGetResource(parts[index], out var part)) parts[index] = part.Key;
        }
        internal void BindPartParameters(GraphicsCharacter graphics, string key, GameObject instance,
            GraphicsCharacterDefinitions.BodyPart bodyPart, bool shape)
        {
            const string prefix = "UB/CharacterResources/";
            if (!UsesCompleteParts || key == null || !key.StartsWith(prefix, StringComparison.Ordinal) ||
                !_resources.TryGetValue(key.Substring(prefix.Length), out var part)) return;
            foreach (var parameter in part.Parameters)
            {
                var target = parameter.Renderer.Length == 0 ? instance.transform : instance.transform.Find(parameter.Renderer);
                var renderer = target ? target.GetComponent<Renderer>() : null;
                var materials = renderer ? renderer.sharedMaterials : [];
                if (!renderer || parameter.Slot >= materials.Length || !materials[parameter.Slot] ||
                    !graphics.MaterialsToDestroy.Contains(materials[parameter.Slot]))
                    throw new InvalidOperationException("A completed character part parameter does not reference a native-owned material: " + part.Source);
                var owners = parameter.Set switch
                {
                    "Skin" => graphics.SkinMaterials, "Face" => graphics.FaceMaterials, "Eyes" => graphics.EyeMaterials,
                    "Groom" when shape && bodyPart == GraphicsCharacterDefinitions.BodyPart.Hair => graphics.HairMaterials,
                    "Groom" when shape && bodyPart == GraphicsCharacterDefinitions.BodyPart.Beard => graphics.BeardMaterials,
                    "Groom" => null,
                    "Armor" => graphics.ArmorMaterials,
                    _ => throw new InvalidOperationException("Unknown character part parameter set.")
                };
                owners?.Add(materials[parameter.Slot]);
            }
        }
        internal static CharacterModelBlueprint ReadBlueprint(string directory, CharacterModelBlueprint previous)
        {
            directory = UpdateContext.PrepareCharacterModelFiles(directory, _characterModelReadCancellation);
            var path = System.IO.Path.Combine(directory, "CharacterModels.manifest.json");
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("The custom character model catalog is missing.", path);
            var bytes = System.IO.File.ReadAllBytes(path);
            using var sha = CustomModels.CreateAssetSha256();
            var digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (previous?.Digest == digest && previous.Directory == directory) return previous;
            using var stream = new System.IO.MemoryStream(bytes, false);
            using var text = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8, true);
            var template = Parse(text.ReadToEnd());
            if ((int)template["schemaVersion"] != 3) return new CharacterModelBlueprint(digest, template, null, directory);
            var resources = CreateManifest(template);
            Validate(resources);
            return new CharacterModelBlueprint(digest, null, resources, directory);
        }
        internal static CharacterModelCatalog FromBlueprint(string directory, CharacterModelBlueprint blueprint)
        {
            CharacterModelManifest manifest;
            if (blueprint.Resources is { } resources)
            {
                // Resource declarations are immutable; live bundle state belongs to each catalog.
                manifest = new CharacterModelManifest
                {
                    schemaVersion = resources.schemaVersion, recipeVersion = resources.recipeVersion,
                    bundles = resources.bundles.Select(bundle => new CharacterModelBundle
                        { file = bundle.file, sha256 = bundle.sha256 }).ToArray(),
                    resources = resources.resources, meshes = [], materials = [], textureRemaps = []
                };
            }
            else
            {
                manifest = CreateManifest(blueprint.Template);
                Validate(manifest);
            }
            return new CharacterModelCatalog(blueprint.Directory, manifest);
        }
        private static JObject Parse(string json)
        {
            using var text = new System.IO.StringReader(json);
            using var reader = new JsonTextReader(text)
                { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double };
            var data = JObject.Load(reader,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || data.Properties().Any(property => property.Name is not ("schemaVersion" or "recipeVersion" or "bundles" or "meshes" or "materials" or "textureRemaps" or "resources")) ||
                !JsonField(data, "schemaVersion", JTokenType.Integer) || !JsonField(data, "recipeVersion", JTokenType.String) ||
                data["bundles"] is not JArray bundles || data["meshes"] is not JArray meshes || data["materials"] is not JArray materials)
                throw new System.IO.InvalidDataException("Invalid custom character model catalog JSON schema.");
            var version = (int)data["schemaVersion"];
            if (version is not (1 or 2 or 3) || version == 1 && data.Property("textureRemaps") != null ||
                version >= 2 && data["textureRemaps"] is not JArray || version < 3 && data.Property("resources") != null ||
                version == 3 && (data["resources"] is not JArray { Count: > 0 } || meshes.Count != 0 || materials.Count != 0 || ((JArray)data["textureRemaps"]).Count != 0))
                throw new System.IO.InvalidDataException("Invalid custom character model shared texture catalog version.");
            foreach (var token in bundles)
                if (token is not JObject bundle || bundle.Properties().Any(property => property.Name is not ("file" or "sha256")) ||
                    !JsonField(bundle, "file", JTokenType.String) || !JsonField(bundle, "sha256", JTokenType.String))
                    throw new System.IO.InvalidDataException("Invalid custom character model pack JSON schema.");
            foreach (var token in meshes)
                if (token is not JObject mesh || mesh.Properties().Any(property => property.Name is not ("sourceFingerprint" or "mode" or "authoredMeshAsset" or "authoredFingerprint" or "bundleFile" or "bindingFile" or "bindingSha256" or "jointAttachments")) ||
                    !JsonField(mesh, "sourceFingerprint", JTokenType.String) || !JsonField(mesh, "mode", JTokenType.String) ||
                    !JsonField(mesh, "authoredMeshAsset", JTokenType.String, false) || !JsonField(mesh, "authoredFingerprint", JTokenType.String, false) ||
                    !JsonField(mesh, "bundleFile", JTokenType.String, false) ||
                    !JsonField(mesh, "bindingFile", JTokenType.String, false) || !JsonField(mesh, "bindingSha256", JTokenType.String, false))
                    throw new System.IO.InvalidDataException("Invalid custom character model mesh JSON schema.");
            foreach (var token in materials)
            {
                if (token is not JObject material || material.Properties().Any(property => property.Name is not ("sourceFingerprint" or "sourceShader" or "bundleFile" or "materialSlot" or "properties" or "authoredProfile")) ||
                    !JsonField(material, "sourceFingerprint", JTokenType.String) || !JsonField(material, "sourceShader", JTokenType.String) ||
                    !JsonField(material, "bundleFile", JTokenType.String, false) || !JsonField(material, "materialSlot", JTokenType.Integer) ||
                    material["properties"] is not JArray properties)
                    throw new System.IO.InvalidDataException("Invalid custom character model material JSON schema.");
                if (material["authoredProfile"] is { Type: not JTokenType.Null } profileToken &&
                    (profileToken is not JObject profile || profile.Properties().Any(field => field.Name is not ("bundleFile" or "materialAsset" or "shader" or "layout")) ||
                     !JsonField(profile, "bundleFile", JTokenType.String) || !JsonField(profile, "materialAsset", JTokenType.String) ||
                     !JsonField(profile, "shader", JTokenType.String) || profile["layout"] is not JObject layout ||
                     layout.Properties().Any(field => field.Name is not ("sourceUv" or "groomUv" or "preservedUv" or "groomCodec")) ||
                     !JsonField(layout, "sourceUv", JTokenType.Integer) || !JsonField(layout, "groomUv", JTokenType.Integer) ||
                     layout.Property("groomCodec") != null && !JsonField(layout, "groomCodec", JTokenType.String) ||
                     layout["preservedUv"] is not JArray preserved ||
                     preserved.Any(channel => channel.Type != JTokenType.Integer)))
                    throw new System.IO.InvalidDataException("Invalid authored material profile JSON schema.");
                foreach (var value in properties)
                    if (value is not JObject property || property.Properties().Any(field => field.Name is not ("name" or "kind" or "textureAsset" or "sourceTextureFingerprint" or "sourceBodyTextureFingerprint" or "sourceWidth" or "sourceHeight" or "bundleFile" or "value")) ||
                        !JsonField(property, "name", JTokenType.String) || !JsonField(property, "kind", JTokenType.String) ||
                        !JsonField(property, "textureAsset", JTokenType.String, false) || !JsonField(property, "sourceTextureFingerprint", JTokenType.String, false) ||
                        !JsonField(property, "sourceBodyTextureFingerprint", JTokenType.String, false) ||
                        !JsonField(property, "sourceWidth", JTokenType.Integer, false) || !JsonField(property, "sourceHeight", JTokenType.Integer, false) ||
                        !JsonField(property, "bundleFile", JTokenType.String, false) ||
                        !JsonField(property, "value", JTokenType.Float, (string)property["kind"] == "float"))
                        throw new System.IO.InvalidDataException("Invalid custom character model property JSON schema.");
            }
            foreach (var token in data["textureRemaps"] as JArray ?? [])
                if (token is not JObject property || property.Properties().Any(field => field.Name is not
                        ("sourceShader" or "name" or "kind" or "sourceTextureFingerprint" or "sourceBodyTextureFingerprint" or "sourceWidth" or "sourceHeight" or "bundleFile" or "textureAsset")) ||
                    !JsonField(property, "sourceShader", JTokenType.String) || !JsonField(property, "name", JTokenType.String) ||
                    !JsonField(property, "kind", JTokenType.String) || !JsonField(property, "sourceTextureFingerprint", JTokenType.String) ||
                    !JsonField(property, "sourceBodyTextureFingerprint", JTokenType.String, false) ||
                    !JsonField(property, "sourceWidth", JTokenType.Integer, false) || !JsonField(property, "sourceHeight", JTokenType.Integer, false) ||
                    !JsonField(property, "bundleFile", JTokenType.String) || !JsonField(property, "textureAsset", JTokenType.String))
                    throw new System.IO.InvalidDataException("Invalid shared custom character model texture JSON schema.");
            foreach (var token in data["resources"] as JArray ?? [])
            {
                if (token is not JObject part || part.Properties().Any(field => field.Name is not ("source" or "asset" or "type" or "bundles" or "parameters")) ||
                    !JsonField(part, "source", JTokenType.String) || !JsonField(part, "asset", JTokenType.String) || !JsonField(part, "type", JTokenType.String) ||
                    part["bundles"] is not JArray partBundles || part["parameters"] is not JArray parameters)
                    throw new System.IO.InvalidDataException("Invalid completed character part schema.");
                foreach (var value in partBundles)
                    if (value is not JObject bundle || bundle.Properties().Any(field => field.Name is not ("file" or "name")) ||
                        !JsonField(bundle, "file", JTokenType.String) || !JsonField(bundle, "name", JTokenType.String))
                        throw new System.IO.InvalidDataException("Invalid completed character part dependency.");
                foreach (var value in parameters)
                    if (value is not JObject parameter || parameter.Properties().Any(field => field.Name is not ("renderer" or "slot" or "set")) ||
                        !JsonField(parameter, "renderer", JTokenType.String) || !JsonField(parameter, "slot", JTokenType.Integer) ||
                        !JsonField(parameter, "set", JTokenType.String))
                        throw new System.IO.InvalidDataException("Invalid completed character part parameter binding.");
            }
            return data;
        }

        private static CharacterModelManifest CreateManifest(JObject data)
        {
            var version = (int)data["schemaVersion"];
            var bundles = (JArray)data["bundles"];
            var meshes = (JArray)data["meshes"];
            var materials = (JArray)data["materials"];
            // All token types and member names were checked above. Construct the declaration
            // graph directly so first actor loading does not build Json.NET reflection contracts
            // for thousands of repeated material properties on the Unity thread.
            return new CharacterModelManifest
            {
                schemaVersion = version,
                recipeVersion = (string)data["recipeVersion"],
                bundles = bundles.Cast<JObject>().Select(bundle => new CharacterModelBundle
                {
                    file = (string)bundle["file"], sha256 = (string)bundle["sha256"]
                }).ToArray(),
                meshes = meshes.Cast<JObject>().Select(mesh => new CharacterModelMeshAsset
                {
                    sourceFingerprint = (string)mesh["sourceFingerprint"], mode = (string)mesh["mode"],
                    authoredMeshAsset = (string)mesh["authoredMeshAsset"], authoredFingerprint = (string)mesh["authoredFingerprint"],
                    bundleFile = (string)mesh["bundleFile"], bindingFile = (string)mesh["bindingFile"],
                    bindingSha256 = (string)mesh["bindingSha256"],
                    jointAttachments = mesh["jointAttachments"] == null ? null : mesh["jointAttachments"] is JArray joints
                        ? joints.Select(ReadJoint).ToArray() : [new CharacterModelJointAsset { InvalidReason = "Joint attachments must be an array." }]
                }).ToArray(),
                materials = materials.Cast<JObject>().Select(material => new CharacterModelMaterialAsset
                {
                    sourceFingerprint = (string)material["sourceFingerprint"], sourceShader = (string)material["sourceShader"],
                    bundleFile = (string)material["bundleFile"], materialSlot = ReadInteger(material["materialSlot"]),
                    properties = ((JArray)material["properties"]).Cast<JObject>().Select(ReadProperty).ToArray(),
                    authoredProfile = material["authoredProfile"] is JObject profile ? new CharacterModelMaterialProfile
                    {
                        bundleFile = (string)profile["bundleFile"], materialAsset = (string)profile["materialAsset"],
                        shader = (string)profile["shader"], layout = new CharacterModelSurfaceLayout
                        {
                            sourceUv = ReadInteger(profile["layout"]["sourceUv"]), groomUv = ReadInteger(profile["layout"]["groomUv"]),
                            preservedUv = ((JArray)profile["layout"]["preservedUv"]).Select(channel => ReadInteger(channel)).ToArray(),
                            groomCodec = (string)profile["layout"]["groomCodec"] ?? CharacterModelSurfaceLayout.DefaultGroomCodec
                        }
                    } : null
                }).ToArray(),
                textureRemaps = (data["textureRemaps"] as JArray)?.Cast<JObject>().Select(ReadProperty).ToArray(),
                resources = (data["resources"] as JArray)?.Cast<JObject>().Select(part => new CharacterModelResource(
                    (string)part["source"], (string)part["asset"], (string)part["type"],
                    ((JArray)part["bundles"]).Cast<JObject>().Select(bundle => new CharacterModelResourceBundle(
                        (string)bundle["file"], (string)bundle["name"])).ToArray(),
                    ((JArray)part["parameters"]).Cast<JObject>().Select(parameter => new CharacterModelPartParameter(
                        (string)parameter["renderer"], ReadInteger(parameter["slot"]), (string)parameter["set"])).ToArray())).ToArray()
            };
        }
        private static CharacterModelProperty ReadProperty(JObject property) => new()
        {
            name = (string)property["name"], kind = (string)property["kind"],
            textureAsset = (string)property["textureAsset"], sourceTextureFingerprint = (string)property["sourceTextureFingerprint"],
            sourceShader = (string)property["sourceShader"], bundleFile = (string)property["bundleFile"],
            sourceBodyTextureFingerprint = (string)property["sourceBodyTextureFingerprint"],
            sourceWidth = property["sourceWidth"] is { Type: JTokenType.Integer } width ? ReadInteger(width) : null,
            sourceHeight = property["sourceHeight"] is { Type: JTokenType.Integer } height ? ReadInteger(height) : null,
            value = property["value"] is { Type: JTokenType.Null } value
                ? throw new JsonSerializationException($"A custom character model numeric value is null at {value.Path}.")
                : (float?)property["value"] ?? 0f
        };
        private static int ReadInteger(JToken value)
        {
            try { return (int)value; }
            catch (OverflowException exception)
            {
                throw new JsonReaderException($"A custom character model integer is out of range at {value.Path}.", exception);
            }
        }
        private static bool JsonField(JObject data, string name, JTokenType type, bool required = true) =>
            data.TryGetValue(name, StringComparison.Ordinal, out var token)
                ? token.Type == type || type == JTokenType.Float && token.Type == JTokenType.Integer || !required && token.Type == JTokenType.Null
                : !required;
        internal IEnumerable<Texture> NativeTextureSources(Material material)
        {
            if (!material || !material.shader || !_nativeTextureProperties.TryGetValue(material.shader.name, out var properties))
                yield break;
            foreach (var property in properties)
            {
                if (!material.HasProperty(property.Key)) continue;
                var texture = material.GetTexture(property.Key);
                if (texture && texture.dimension == UnityEngine.Rendering.TextureDimension.Tex2D &&
                    property.Value.Any(candidate => candidate.MatchesSourceDimensions(texture.width, texture.height)))
                    yield return texture;
            }
        }
        private static void Validate(CharacterModelManifest manifest)
        {
            if (manifest == null || manifest.schemaVersion is not (1 or 2 or 3) || manifest.schemaVersion == 1 && manifest.textureRemaps != null ||
                manifest.schemaVersion >= 2 && manifest.textureRemaps == null || string.IsNullOrWhiteSpace(manifest.recipeVersion) ||
                manifest.bundles == null || manifest.meshes == null || manifest.schemaVersion < 3 && manifest.meshes.Length == 0 || manifest.materials == null ||
                manifest.bundles.Any(value => value == null || !IsBundleFile(value.file) || !IsHash(value.sha256)) ||
                manifest.bundles.Select(value => value.file).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.bundles.Length)
                throw new InvalidOperationException("The custom character model catalog is incomplete or has duplicate packs.");
            var bundles = new HashSet<string>(manifest.bundles.Select(value => value.file), StringComparer.Ordinal);
            var parts = manifest.resources ?? [];
            if (manifest.schemaVersion == 3 && (parts.Length == 0 || manifest.meshes.Length != 0 || manifest.materials.Length != 0 || manifest.textureRemaps.Length != 0) ||
                manifest.schemaVersion < 3 && parts.Length != 0 ||
                parts.Any(part => part.AssetType == null || string.IsNullOrWhiteSpace(part.Source) || part.Source.StartsWith("UB/", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(part.Asset) ||
                    part.AssetType != typeof(GameObject) && part.Parameters.Length != 0 ||
                    part.Bundles.Length == 0 || part.Bundles.Any(bundle => !bundles.Contains(bundle.File) || string.IsNullOrWhiteSpace(bundle.Name)) ||
                    part.Bundles.Select(bundle => bundle.File).Distinct(StringComparer.Ordinal).Count() != part.Bundles.Length ||
                    part.Parameters.Any(parameter => parameter.Slot < 0 || parameter.Renderer == null || parameter.Renderer.StartsWith("/", StringComparison.Ordinal) ||
                        parameter.Renderer.Split('/').Any(segment => segment is "." or "..") || parameter.Set is not ("Skin" or "Face" or "Eyes" or "Groom" or "Armor"))) ||
                parts.Select(part => part.Source).Distinct(StringComparer.Ordinal).Count() != parts.Length)
                throw new InvalidOperationException("The completed character part catalog has invalid source or parameter bindings.");
            var partBundles = parts.SelectMany(part => part.Bundles).ToArray();
            if (partBundles.GroupBy(bundle => bundle.File, StringComparer.Ordinal)
                    .Any(group => group.Select(bundle => bundle.Name).Distinct(StringComparer.Ordinal).Count() != 1) ||
                partBundles.GroupBy(bundle => bundle.Name, StringComparer.Ordinal)
                    .Any(group => group.Select(bundle => bundle.File).Distinct(StringComparer.Ordinal).Count() != 1))
                throw new InvalidOperationException("A completed character bundle has conflicting file or provider identities.");
            if (manifest.schemaVersion == 3) return;
            var sourceFingerprints = new HashSet<string>(StringComparer.Ordinal);
            if (manifest.meshes.Any(value => value == null || !IsHash(value.sourceFingerprint) || value.mode is not ("native" or "authored") ||
                    value.mode == "native" && (!string.IsNullOrEmpty(value.authoredMeshAsset) || !string.IsNullOrEmpty(value.authoredFingerprint) || !string.IsNullOrEmpty(value.bundleFile) ||
                        !string.IsNullOrEmpty(value.bindingFile) || !string.IsNullOrEmpty(value.bindingSha256)) ||
                    (!string.IsNullOrEmpty(value.bindingFile) || !string.IsNullOrEmpty(value.bindingSha256)) &&
                        (!IsBundleFile(value.bindingFile) || !IsHash(value.bindingSha256)) ||
                    value.mode == "authored" && (!IsHash(value.authoredFingerprint) || string.IsNullOrWhiteSpace(value.authoredMeshAsset) ||
                        value.bundleFile == null || !bundles.Contains(value.bundleFile))) ||
                manifest.meshes.Any(value => !sourceFingerprints.Add(value.sourceFingerprint)))
                throw new InvalidOperationException("The custom character model catalog has invalid or duplicate mesh identities.");
            var authoredSources = new HashSet<string>(manifest.meshes.Where(mesh => mesh.mode == "authored")
                .Select(mesh => mesh.sourceFingerprint), StringComparer.Ordinal);
            var nativeProperties = NativeFaceAppearanceProperties();
            var nativeTextures = manifest.schemaVersion == 2 ? NativeFaceAppearanceProperties(true) : [];
            if ((manifest.textureRemaps ?? []).Any(property => property == null || string.IsNullOrWhiteSpace(property.sourceShader) ||
                    string.IsNullOrWhiteSpace(property.name) || property.kind != "texture-remap" || !IsHash(property.sourceTextureFingerprint) ||
                    property.sourceBodyTextureFingerprint != null && !IsHash(property.sourceBodyTextureFingerprint) || !SourceDimensionsValid(property) ||
                    string.IsNullOrWhiteSpace(property.textureAsset) || property.bundleFile == null || !bundles.Contains(property.bundleFile) ||
                    nativeProperties.Contains(Shader.PropertyToID(property.name)) && !nativeTextures.Contains(Shader.PropertyToID(property.name))) ||
                (manifest.textureRemaps ?? []).GroupBy(property => (property.sourceShader, property.name, property.sourceTextureFingerprint, property.sourceBodyTextureFingerprint)).Any(group => group.Count() != 1))
                throw new InvalidOperationException("A shared custom character model texture declaration is invalid or replaces a native choice parameter.");
            foreach (var material in manifest.materials)
            {
                if (material?.authoredProfile is { } profile &&
                    (!bundles.Contains(profile.bundleFile ?? "") || string.IsNullOrWhiteSpace(profile.materialAsset) ||
                     string.IsNullOrWhiteSpace(profile.shader) || profile.layout == null || profile.layout.preservedUv == null ||
                     !profile.layout.HasSupportedGroomCodec ||
                     new[] { profile.layout.sourceUv, profile.layout.groomUv }.Any(channel => channel is < 0 or > 7) ||
                     profile.layout.sourceUv == profile.layout.groomUv ||
                     profile.layout.preservedUv.Any(channel => channel is < 0 or > 7 || channel == profile.layout.sourceUv ||
                         channel == profile.layout.groomUv) ||
                     profile.layout.preservedUv.Distinct().Count() != profile.layout.preservedUv.Length ||
                     !authoredSources.Contains(material.sourceFingerprint)))
                    throw new InvalidOperationException("An authored material profile has no valid pack, source mesh or coordinate layout.");
                if (material == null || !IsHash(material.sourceFingerprint) || material.materialSlot < 0 || string.IsNullOrWhiteSpace(material.sourceShader) ||
                    !sourceFingerprints.Contains(material.sourceFingerprint) ||
                    material.properties == null || material.properties.Length == 0 && material.authoredProfile == null || material.properties.Any(property => property == null || string.IsNullOrWhiteSpace(property.name) ||
                        property.kind is not ("float" or "texture" or "texture-remap") || !SourceDimensionsValid(property) ||
                        property.sourceBodyTextureFingerprint != null && (property.kind != "texture-remap" || !IsHash(property.sourceBodyTextureFingerprint)) ||
                        property.kind == "float" && !BodyShapeFinite(property.value) ||
                        property.kind is "texture" or "texture-remap" && string.IsNullOrWhiteSpace(property.textureAsset) ||
                        property.kind == "texture-remap" && !IsHash(property.sourceTextureFingerprint) ||
                        property.bundleFile != null && (property.kind is not ("texture" or "texture-remap") || !bundles.Contains(property.bundleFile)) ||
                        property.kind is "texture" or "texture-remap" && !bundles.Contains(property.bundleFile ?? material.bundleFile ?? "") ||
                        nativeProperties.Contains(Shader.PropertyToID(property.name)) &&
                            !(property.kind == "texture-remap" && (manifest.schemaVersion == 2
                                ? nativeTextures.Contains(Shader.PropertyToID(property.name))
                                : property.name == "_MainTex" && material.sourceShader != "Custom/CharacterEye"))) ||
                    material.properties.GroupBy(value => value.name, StringComparer.Ordinal).Any(group => group.Count() > 1 &&
                        (group.Any(value => value.kind != "texture-remap") || group.Select(value => (value.sourceTextureFingerprint, value.sourceBodyTextureFingerprint)).Distinct().Count() != group.Count())) ||
                    !string.IsNullOrEmpty(material.bundleFile) && !bundles.Contains(material.bundleFile))
                    throw new InvalidOperationException("A custom character model material declaration is invalid or replaces a native appearance property.");
            }
            if (manifest.materials.Select(value => MaterialKey(value.sourceFingerprint, value.materialSlot, value.sourceShader)).Distinct(StringComparer.Ordinal).Count() != manifest.materials.Length)
                throw new InvalidOperationException("The custom character model catalog has duplicate material identities.");
        }
        private static HashSet<int> NativeFaceAppearanceProperties(bool texturesOnly = false)
        {
            var result = new HashSet<int>();
            var textures = new[] { "bdMainTexId", "bdMetallicGlossMapId", "bdNormalMapId", "colorMaskId", "mainTexId", "scarsMainTexId", "scarsNormalMapId" };
            // Native choice parameters stay native; selected texture sources can be remapped by exact identity.
            foreach (var name in texturesOnly ? textures : textures.Concat(new[] { "bdBlendFactorId", "bdColorId", "bdEmissiveColorId",
                         "bodyDecorationId", "bumpMapScaleId", "colorId", "scarsId", "specColorId" }))
                result.Add(NativeFaceAppearanceProperty(name));
            return result;
        }
        private static int NativeFaceAppearanceProperty(string name)
        {
            var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>() as GraphicsCharacterFactoryManager;
            if (factory == null) throw new InvalidOperationException("Native face appearance properties are not available.");
            var field = AccessTools.Field(typeof(GraphicsCharacterFactoryManager), name);
            if (field?.GetValue(factory) is not int id) throw new MissingFieldException(typeof(GraphicsCharacterFactoryManager).FullName, name);
            return id;
        }
        private IEnumerator PrepareBundle(string file, Func<bool> current)
        {
            var entry = _bundles[file];
            if (entry.Owners <= 0) throw new InvalidOperationException("A custom character model pack has no active presentation owner.");
            if (entry.Loaded || !current()) yield break;
            var operation = entry.Verification;
            if (operation == null || operation.Cancelled)
            {
                operation?.Dispose();
                entry.Verification = operation = new BundleVerification(System.IO.Path.Combine(_directory, entry.file), entry.sha256);
            }
            while (!operation.Completed)
            {
                if (!current() || entry.Owners <= 0 || !ReferenceEquals(entry.Verification, operation)) yield break;
                yield return null;
            }
            if (current() && entry.Owners > 0 && ReferenceEquals(entry.Verification, operation)) operation.RequireReady();
        }
        private AssetBundle Bundle(string file)
        {
            var entry = _bundles[file];
            if (entry.Owners <= 0) throw new InvalidOperationException("A custom character model pack has no active presentation owner.");
            if (entry.Loaded) return entry.Loaded;
            var path = System.IO.Path.Combine(_directory, entry.file);
            var operation = entry.Verification; entry.Verification = null;
            if (operation != null && operation.Completed && !operation.Cancelled)
            {
                try
                {
                    operation.Consume(length =>
                    {
                        entry.StoredBytes = length;
                        entry.Loaded = AssetBundle.LoadFromFile(path);
                    });
                }
                finally { operation.Dispose(); }
            }
            else
            {
                operation?.Dispose();
                // Compatibility path for consumers which did not run preparation.
                using (var stream = System.IO.File.OpenRead(path))
                using (var sha = CustomModels.CreateAssetSha256())
                {
                    if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != entry.sha256)
                        throw new InvalidOperationException("A custom character model pack does not match its catalog: " + file);
                    entry.StoredBytes = stream.Length;
                    entry.Loaded = AssetBundle.LoadFromFile(path);
                }
            }
            if (!entry.Loaded) throw new InvalidOperationException("A custom character model pack could not be loaded: " + file);
            return entry.Loaded;
        }
        private static CharacterModelJointAsset ReadJoint(JToken token)
        {
            var result = new CharacterModelJointAsset();
            try
            {
                if (token is not JObject data || data.Properties().Any(p => p.Name is not
                    ("bodySourceFingerprint" or "headFingerprint" or "bodyFingerprint" or "authoredFingerprint" or "bundleFile" or "authoredMeshAsset" or
                     "origins" or "bodyToHead" or "donorBones" or "removedTriangles" or "uvEdges" or "uvFractions" or "headToBody" or "donorPart" or "tangentMode")))
                    throw new JsonReaderException("Invalid coupled joint declaration.");
                string Text(string name) => JsonField(data, name, JTokenType.String) ? (string)data[name] : throw new JsonReaderException("Missing joint identity: " + name);
                int[] Ints(string name) => data[name] is JArray a && a.Count <= 1000000 && a.All(t => t.Type == JTokenType.Integer)
                    ? a.Select(ReadInteger).ToArray() : throw new JsonReaderException("Invalid joint index channel: " + name);
                float[] Floats(string name) => data[name] is JArray a && a.Count <= 1000000 && a.All(t => t.Type is JTokenType.Integer or JTokenType.Float)
                    ? a.Select(t => (float)t).ToArray() : throw new JsonReaderException("Invalid joint scalar channel: " + name);
                if (data["donorPart"] != null &&
                    (!JsonField(data, "donorPart", JTokenType.String) ||
                     !Enum.TryParse(Text("donorPart"), false, out result.donorPart) ||
                     !Enum.IsDefined(typeof(GraphicsCharacterDefinitions.BodyPart), result.donorPart)))
                    throw new JsonReaderException("Invalid coupled joint donor body part.");
                if (data["tangentMode"] != null)
                {
                    result.tangentMode = Text("tangentMode");
                    if (result.tangentMode is not ("uvDerivative" or "sourceUvEdge"))
                        throw new JsonReaderException("Invalid coupled joint tangent owner.");
                }
                result.bodySourceFingerprint = Text("bodySourceFingerprint"); result.headFingerprint = Text("headFingerprint");
                result.bodyFingerprint = Text("bodyFingerprint"); result.authoredFingerprint = Text("authoredFingerprint");
                result.bundleFile = Text("bundleFile"); result.authoredMeshAsset = Text("authoredMeshAsset");
                if (!IsHash(result.bodySourceFingerprint) || !IsHash(result.headFingerprint) || !IsHash(result.bodyFingerprint) ||
                    !IsHash(result.authoredFingerprint) || !IsBundleFile(result.bundleFile) || string.IsNullOrWhiteSpace(result.authoredMeshAsset))
                    throw new JsonReaderException("Invalid coupled joint source identity.");
                result.origins = Ints("origins"); result.bodyToHead = Ints("bodyToHead"); result.donorBones = Ints("donorBones");
                result.removedTriangles = Ints("removedTriangles"); result.uvEdges = Ints("uvEdges");
                result.uvFractions = Floats("uvFractions"); result.headToBody = Floats("headToBody");
            }
            catch (Exception e) { result.InvalidReason = e.Message; }
            return result;
        }

        internal IEnumerator PrepareJointMeshes(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics,
            IEnumerable<GameObject> parts, PreparedSources preparation, Func<bool> current, bool includePending = true)
        {
            if (!_hasJointAttachments || !current()) yield break;
            preparation.RetainNativeSources(factory, graphics, includePending);
            // Coupled choices are resolved while immutable prefabs are still owned,
            // before native publication. Only matching actual source pairs are loaded.
            var sources = new Dictionary<SkinnedMeshRenderer, CustomModels.SkinMeshSource>();
            foreach (var part in parts.Concat(CharacterModelPartSources(factory, graphics, includePending))
                         .Where(p => p && preparation.NativeSources.Contains(p)).Distinct())
            {
                var captured = CustomModels.SkinMeshSource.CapturePrefab(part,
                    part.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh));
                if (captured == null) continue;
                foreach (var pair in captured) sources[pair.Key] = pair.Value;
            }
            var immutable = sources.Values.Select(v => v.Original).Distinct().ToArray();
            foreach (var originalHead in immutable)
            {
                if (!current()) yield break;
                var hfp = CharacterModelFingerprint(originalHead);
                if (!_meshes.TryGetValue(hfp, out var entry)) continue;
                foreach (var joint in entry.jointAttachments ?? [])
                {
                    if (!current()) yield break;
                    if (joint.InvalidReason != null) { preparation.Failed = true; ReportCharacterModelError("joint-declaration", new InvalidOperationException(joint.InvalidReason)); continue; }
                    var originalBody = immutable.FirstOrDefault(v => CharacterModelFingerprint(v) == joint.bodySourceFingerprint);
                    if (!originalBody) continue;
                    var verificationFiles = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var fingerprint in new[] { hfp, joint.bodySourceFingerprint })
                        if (_meshes.TryGetValue(fingerprint, out var sourceEntry) && sourceEntry.mode != "native")
                            verificationFiles.Add(sourceEntry.bundleFile);
                    var verificationFailed = false;
                    foreach (var file in verificationFiles)
                    {
                        RetainBundle(preparation.Packs, file);
                        if (!_bundles[file].Loaded)
                            yield return ObserveCharacterModelPreparation(PrepareBundle(file, current), () => verificationFailed = true);
                        if (!current()) yield break;
                        if (verificationFailed) break;
                    }
                    if (verificationFailed) { preparation.Failed = true; continue; }
                    Mesh head = null, body = null, loaded = null;
                    try
                    {
                        head = Mesh(originalHead, hfp, preparation.Packs, out _);
                        body = Mesh(originalBody, joint.bodySourceFingerprint, preparation.Packs, out _);
                        if (CharacterModelFingerprint(head) != joint.headFingerprint || CharacterModelFingerprint(body) != joint.bodyFingerprint) continue;
                        if (!_bundles.ContainsKey(joint.bundleFile)) throw new InvalidOperationException("A joint has no declared immutable pack.");
                        RetainBundle(preparation.Packs, joint.bundleFile);
                    }
                    catch (Exception e) { preparation.Failed = true; ReportCharacterModelError("joint-prefetch", e); continue; }
                    var jointVerificationFailed = false;
                    if (!_bundles[joint.bundleFile].Loaded)
                        yield return ObserveCharacterModelPreparation(PrepareBundle(joint.bundleFile, current), () => jointVerificationFailed = true);
                    if (!current()) yield break;
                    if (jointVerificationFailed) { preparation.Failed = true; continue; }
                    try
                    {
                        loaded = joint.Loaded ? joint.Loaded : Bundle(joint.bundleFile).LoadAsset<Mesh>(joint.authoredMeshAsset);
                        if (!loaded || !loaded.isReadable) throw new InvalidOperationException("A coupled joint mesh is missing or unreadable.");
                    }
                    catch (Exception e) { preparation.Failed = true; ReportCharacterModelError("joint-prefetch", e); continue; }
                    yield return CharacterModelMeshPreparation.Prepare(loaded, current, () => preparation.Failed = true);
                    if (!current()) yield break;
                    try
                    {
                        if (CharacterModelFingerprint(loaded) != joint.authoredFingerprint) throw new InvalidOperationException("A coupled joint content fingerprint changed.");
                        if (!joint.Verified.Contains((head, body))) ValidateCharacterModelJoint(head, body, loaded, joint);
                        joint.Verified.Add((head, body)); joint.Loaded = loaded;
                    }
                    catch (Exception e) { joint.Verified.Remove((head, body)); preparation.Failed = true; ReportCharacterModelError("joint-source", e); }
                    yield return null;
                }
            }
        }
        // Keep the four-argument Head/Body entry used by existing source witnesses.
        internal (CharacterModelJointAsset Declaration, Mesh Mesh) Joint(string headSource, string bodySource,
            Mesh head, Mesh body) => JointForPart(headSource, bodySource, head, body, GraphicsCharacterDefinitions.BodyPart.Body);
        internal IEnumerable<GraphicsCharacterDefinitions.BodyPart> JointDonorParts(string source) =>
            _meshes.TryGetValue(source, out var entry)
                ? (entry.jointAttachments ?? []).Where(j => j.InvalidReason == null).Select(j => j.donorPart).Distinct()
                : [];
        internal (CharacterModelJointAsset Declaration, Mesh Mesh) JointForPart(string headSource, string bodySource,
            Mesh head, Mesh body, GraphicsCharacterDefinitions.BodyPart donorPart)
        {
            if (!_meshes.TryGetValue(headSource, out var entry)) return default;
            var matches = (entry.jointAttachments ?? []).Where(j => j.InvalidReason == null &&
                j.donorPart == donorPart && j.bodySourceFingerprint == bodySource && j.Loaded && j.Verified.Contains((head, body)) &&
                j.headFingerprint == CharacterModelFingerprint(head) && j.bodyFingerprint == CharacterModelFingerprint(body)).ToArray();
            if (matches.Length != 1) return default;
            var selected = matches[0];
            return (selected, selected.Loaded);
        }
        internal bool HasJointAttachments(string source) => _hasJointAttachments && _meshes.TryGetValue(source, out var entry) &&
            (entry.jointAttachments ?? []).Any(j => j.InvalidReason == null);

        internal Mesh Mesh(Mesh original, string fingerprint, HashSet<string> retained, out string bundleFile)
        {
            bundleFile = null;
            if (!_meshes.TryGetValue(fingerprint, out var entry) || entry.mode == "native") return original;
            bundleFile = entry.bundleFile;
            RetainBundle(retained, bundleFile);
            if (!entry.Loaded)
            {
                var identity = (_bundles[entry.bundleFile].sha256, entry.authoredMeshAsset, entry.authoredFingerprint);
                var verifyContent = entry.VerifiedContent != identity;
                try
                {
                    // Bundle verifies its complete file before each native load. Only
                    // immutable authored channels may reuse a previous successful proof.
                    var loaded = Bundle(entry.bundleFile).LoadAsset<Mesh>(entry.authoredMeshAsset);
                    if (!loaded || !loaded.isReadable || verifyContent && CharacterModelFingerprint(loaded) != entry.authoredFingerprint)
                        throw new InvalidOperationException("An authored character model mesh is missing or its content identity changed: " + entry.authoredMeshAsset);
                    ValidateCharacterModelBinding(original, loaded, verifyContent);
                    foreach (var layout in entry.Layouts) ValidateSurfaceLayout(original, loaded, layout, verifyContent);
                    entry.VerifiedContent = identity;
                    entry.Loaded = loaded;
                }
                catch
                {
                    entry.VerifiedContent = default;
                    throw;
                }
            }
            return entry.Loaded;
        }
        internal IEnumerator PrepareMesh(Mesh original, string fingerprint, PreparedSources preparation, Func<bool> current)
        {
            if (!_meshes.TryGetValue(fingerprint, out var entry) || entry.mode == "native") yield break;
            RetainBundle(preparation.Packs, entry.bundleFile);
            var wasReady = _bundles[entry.bundleFile].Loaded && entry.Loaded &&
                           (string.IsNullOrEmpty(entry.bindingFile) || entry.LoadedBinding != null);
            if (!_bundles[entry.bundleFile].Loaded)
                yield return PrepareBundle(entry.bundleFile, current);
            if (!current()) yield break;
            var loaded = entry.Loaded ? entry.Loaded : Bundle(entry.bundleFile).LoadAsset<Mesh>(entry.authoredMeshAsset);
            if (!loaded || !loaded.isReadable)
                throw new InvalidOperationException("An authored character model mesh is missing or unreadable: " + entry.authoredMeshAsset);
            var identity = (_bundles[entry.bundleFile].sha256, entry.authoredMeshAsset, entry.authoredFingerprint);
            var verifyContent = entry.VerifiedContent != identity;
            if (verifyContent)
                yield return CharacterModelMeshPreparation.Prepare(loaded, current, () => preparation.Failed = true);
            if (!current()) yield break;
            if (verifyContent && CharacterModelFingerprint(loaded) != entry.authoredFingerprint)
                throw new InvalidOperationException("An authored character model mesh content identity changed: " + entry.authoredMeshAsset);
            // Reuse only the immutable authored proof. Current native binding
            // and coordinate-layout compatibility are checked on every load.
            Mesh(original, fingerprint, preparation.Packs, out _);
            yield return PrepareBinding(original, fingerprint, entry, current);
            if (!wasReady) yield return null;
        }
        private static void ValidateSurfaceLayout(Mesh original, Mesh authored, CharacterModelSurfaceLayout layout, bool validateAuthored)
        {
            var nativeDimensions = ReadNativeRestMesh(original, readable => Enumerable.Range(0, 8).Select(channel => Dimension(readable, channel)).ToArray());
            if (nativeDimensions[layout.sourceUv] != 2 || nativeDimensions[layout.groomUv] != 0 ||
                Enumerable.Range(0, 8).Any(channel => nativeDimensions[channel] != 0 && channel != layout.sourceUv && !layout.preservedUv.Contains(channel)))
                throw new InvalidOperationException("An authored material layout overwrites an undeclared native coordinate channel.");
            foreach (var channel in layout.preservedUv)
                if (Dimension(authored, channel) != nativeDimensions[channel])
                    throw new InvalidOperationException("An authored material layout changed a retained native coordinate dimension.");
            if (Enumerable.Range(0, 8).Any(channel => Dimension(authored, channel) != 0 && nativeDimensions[channel] == 0 && channel != layout.groomUv))
                throw new InvalidOperationException("An authored material layout added an undeclared coordinate channel.");
            if (!validateAuthored) return;
            Coordinates(layout.sourceUv, 2);
            var groom = Coordinates(layout.groomUv, 4);
            // The declared codec owns its roles, handedness and optional strand rank.
            if (groom.Any(value => !layout.IsValidGroomCoordinate(value)))
                throw new InvalidOperationException("An authored material layout has an invalid surface tile or handedness.");
            for (var slot = 0; slot < authored.subMeshCount; slot++)
            {
                if (authored.GetTopology(slot) != MeshTopology.Triangles)
                    throw new InvalidOperationException("An authored material surface role requires triangle topology.");
                var indices = authored.GetIndices(slot);
                if (indices.Length % 3 != 0) throw new InvalidOperationException("An authored material surface has incomplete triangles.");
                for (var index = 0; index < indices.Length; index += 3)
                    if (!layout.IsSameGroomSurface(groom[indices[index]], groom[indices[index + 1]], groom[indices[index + 2]]))
                        throw new InvalidOperationException("An authored material surface role or strand rank changes within a triangle.");
            }
            return;
            int Dimension(Mesh mesh, int channel) => mesh.GetVertexAttributeDimension(
                (UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel));
            List<Vector4> Coordinates(int channel, int dimension)
            {
                var values = new List<Vector4>();
                authored.GetUVs(channel, values);
                if (Dimension(authored, channel) != dimension || values.Count != authored.vertexCount ||
                    values.Any(value => !BodyShapeFinite(value.x) || !BodyShapeFinite(value.y) || !BodyShapeFinite(value.z) || !BodyShapeFinite(value.w)))
                    throw new InvalidOperationException("An authored material coordinate channel is missing, nonfinite or has an invalid dimension.");
                return values;
            }
        }
        internal CustomModels.SkinMeshSource.SurfaceBinding Binding(Mesh original, string fingerprint)
        {
            if (!_meshes.TryGetValue(fingerprint, out var entry) || entry.mode != "authored" || string.IsNullOrEmpty(entry.bindingFile)) return null;
            if (!entry.Loaded || _bundles[entry.bundleFile].Owners <= 0)
                throw new InvalidOperationException("An authored source attachment has no mesh pack owner.");
            if (entry.LoadedBinding != null)
            {
                TouchBinding(entry);
                return entry.LoadedBinding;
            }
            var bytes = ReadBinding(entry);
            var tangents = ReadNativeRestMesh(original, readable => readable.tangents);
            ValidateBindingTangents(tangents, original.vertexCount);
            var template = CachedBinding(entry, original.vertexCount, entry.Loaded.vertexCount, entry.Loaded.subMeshCount);
            if (template == null)
            {
                template = ParseBindingTemplate(bytes, fingerprint, original.vertexCount, entry.Loaded.vertexCount, entry.Loaded.subMeshCount);
                RememberBinding(entry, template);
            }
            return entry.LoadedBinding = template.Instantiate(BindingIdentity(entry, fingerprint), tangents);
        }
        internal CustomModels.SkinMeshSource.SurfaceBinding BaseBinding(Mesh original, string fingerprint)
        {
            if (!_meshes.TryGetValue(fingerprint, out var entry) || entry.mode != "authored" ||
                !entry.Loaded || entry.Loaded.vertexCount != original.vertexCount || !string.IsNullOrEmpty(entry.bindingFile)) return null;
            if (_bundles[entry.bundleFile].Owners <= 0) throw new InvalidOperationException("An authored base has no immutable mesh owner.");
            if (entry.LoadedBaseBindingPrepared && ReferenceEquals(entry.LoadedBaseBindingTarget, entry.Loaded)) return entry.LoadedBaseBinding;
            var binding = CreateAuthoredBaseBinding(original, entry.Loaded,
                "authored-base:1:" + fingerprint + ":" + entry.authoredFingerprint);
            if (binding != null) binding.CacheAuthoredBaseValidation = true;
            entry.LoadedBaseBinding = binding; entry.LoadedBaseBindingTarget = entry.Loaded; entry.LoadedBaseBindingPrepared = true;
            return binding;
        }
        private byte[] ReadBinding(CharacterModelMeshAsset entry)
        {
            var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(_directory, entry.bindingFile));
            using var sha = CustomModels.CreateAssetSha256();
            if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != entry.bindingSha256)
                throw new InvalidOperationException("An authored source attachment does not match its catalog.");
            return bytes;
        }
        private static string BindingIdentity(CharacterModelMeshAsset entry, string fingerprint) =>
            fingerprint + ":" + entry.authoredFingerprint + ":" + entry.bindingSha256;
        private CharacterModelBindingTemplate CachedBinding(CharacterModelMeshAsset entry, int originalCount, int authoredCount, int slots)
        {
            var template = entry.ParsedBinding;
            if (template == null || template.OriginalCount != originalCount || template.AuthoredCount != authoredCount || template.Slots != slots) return null;
            TouchBinding(entry);
            return template;
        }
        private void TouchBinding(CharacterModelMeshAsset entry)
        {
            if (entry.ParsedBindingNode == null || ReferenceEquals(entry.ParsedBindingNode, _parsedBindings.Last)) return;
            _parsedBindings.Remove(entry.ParsedBindingNode);
            _parsedBindings.AddLast(entry.ParsedBindingNode);
        }
        private void RememberBinding(CharacterModelMeshAsset entry, CharacterModelBindingTemplate template)
        {
            if (entry.ParsedBindingNode != null)
            {
                _parsedBindings.Remove(entry.ParsedBindingNode);
                _parsedBindingBytes -= entry.ParsedBinding.Bytes;
            }
            entry.ParsedBinding = null;
            entry.ParsedBindingNode = null;
            if (template.Bytes > MaximumParsedBindingBytes) return;
            entry.ParsedBinding = template;
            entry.ParsedBindingNode = _parsedBindings.AddLast(entry);
            _parsedBindingBytes += template.Bytes;
            while (_parsedBindings.Count > MaximumParsedBindings || _parsedBindingBytes > MaximumParsedBindingBytes)
            {
                var evicted = _parsedBindings.First.Value;
                _parsedBindings.RemoveFirst();
                _parsedBindingBytes -= evicted.ParsedBinding.Bytes;
                evicted.ParsedBinding = null;
                evicted.ParsedBindingNode = null;
            }
        }
        private sealed class BindingPending(byte[] bytes, string fingerprint, int originalCount, int authoredCount, int slots)
        {
            internal readonly byte[] Bytes = bytes;
            internal readonly string Fingerprint = fingerprint;
            internal readonly int OriginalCount = originalCount, AuthoredCount = authoredCount, Slots = slots;
            internal volatile bool Cancelled;
            private int _references = 1;
            internal void RetainWorker()
            {
                if (Interlocked.CompareExchange(ref _references, 2, 1) != 1)
                    throw new InvalidOperationException("A binding packet has no exclusive preparing owner.");
            }
            internal void Release()
            {
                var remaining = Interlocked.Decrement(ref _references);
                if (remaining < 0) throw new InvalidOperationException("A binding packet has no matching owner.");
                if (remaining == 0) Interlocked.Exchange(ref _bindingPreparationBusy, 0);
            }
        }
        private static Task<CharacterModelBindingTemplate> QueueBinding(BindingPending pending)
        {
            pending.RetainWorker();
            try
            {
                return Task.Run(() =>
                {
                    try { return ParseBindingTemplate(pending.Bytes, pending.Fingerprint, pending.OriginalCount,
                        pending.AuthoredCount, pending.Slots, () => pending.Cancelled, MaximumParsedBindingBytes); }
                    finally { pending.Release(); }
                });
            }
            catch { pending.Release(); throw; }
        }
        internal CustomModels.SkinMeshSource.SourceGraph.NativeSurfacePacket PreparedSurfacePacket(Mesh original, Mesh target,
            CustomModels.SkinMeshSource.SurfaceBinding binding, CustomModels.SkinMeshSource.SourceGraph.NativeSurfacePlan plan)
        {
            if (!original || !CharacterModelFingerprints.TryGetValue(original, out var fingerprint) ||
                binding == null || IsRetiring || !ReferenceEquals(this, _characterModelCatalog) ||
                !_meshes.TryGetValue(fingerprint, out var entry) || entry.mode != "authored" ||
                !ReferenceEquals(binding, entry.LoadedBinding) || binding.BoundaryOverrides != null || binding.AuthoredBase ||
                !ReferenceEquals(target, entry.Loaded) || !target || plan == null || !ReferenceEquals(plan, binding.SurfacePlan) ||
                string.IsNullOrEmpty(entry.bundleFile) || _bundles[entry.bundleFile].Owners <= 0 ||
                binding.Fingerprint != BindingIdentity(entry, entry.sourceFingerprint)) return null;
            var owner = (_bundles[entry.bundleFile].sha256, entry.authoredMeshAsset, entry.authoredFingerprint);
            return entry.VerifiedContent == owner && entry.PreparedFinishPacket?.Matches(target, plan, binding.Fingerprint, owner) == true
                ? entry.PreparedFinishPacket : null;
        }
        internal IEnumerator PrepareSurfacePacket(Mesh original, CustomModels.SkinMeshSource.SurfaceBinding binding,
            PreparedSources preparation, Func<bool> current)
        {
            if (!original || !CharacterModelFingerprints.TryGetValue(original, out var fingerprint) ||
                !_meshes.TryGetValue(fingerprint, out var entry) || entry.mode != "authored" || binding == null ||
                !ReferenceEquals(entry.LoadedBinding, binding) || binding.BoundaryOverrides != null || binding.AuthoredBase ||
                binding.SurfacePlan == null || string.IsNullOrEmpty(entry.bundleFile) || !preparation.Packs.Contains(entry.bundleFile)) yield break;
            var target = entry.Loaded; var plan = binding.SurfacePlan;
            var owner = (_bundles[entry.bundleFile].sha256, entry.authoredMeshAsset, entry.authoredFingerprint);
            bool Current() => current() && !IsRetiring && ReferenceEquals(this, _characterModelCatalog) && target &&
                ReferenceEquals(target, entry.Loaded) && ReferenceEquals(binding, entry.LoadedBinding) &&
                ReferenceEquals(plan, binding.SurfacePlan) && entry.VerifiedContent == owner &&
                _bundles[entry.bundleFile].Owners > 0 && binding.Fingerprint == BindingIdentity(entry, fingerprint) &&
                CharacterModelFingerprints.TryGetValue(original, out var currentFingerprint) && currentFingerprint == fingerprint;
            if (!Current() || PreparedSurfacePacket(original, target, binding, plan) != null) yield break;
            while (Interlocked.CompareExchange(ref _bindingPreparationBusy, 1, 0) != 0)
            { if (!Current()) yield break; yield return null; }
            IEnumerator operation = null;
            try
            {
                if (!Current() || PreparedSurfacePacket(original, target, binding, plan) != null) yield break;
                operation = CustomModels.SkinMeshSource.SourceGraph.NativeSurfaceFinishState.PreparePacket(target, plan,
                    binding.Fingerprint, owner, Current, packet => { if (Current()) entry.PreparedFinishPacket = packet; });
                while (Current() && operation.MoveNext()) yield return operation.Current;
            }
            finally
            {
                try { (operation as IDisposable)?.Dispose(); }
                finally { Interlocked.Exchange(ref _bindingPreparationBusy, 0); }
            }
        }

        private IEnumerator PrepareBinding(Mesh original, string fingerprint, CharacterModelMeshAsset entry, Func<bool> current)
        {
            if (string.IsNullOrEmpty(entry.bindingFile) || !current()) yield break;
            if (entry.LoadedBinding != null) { TouchBinding(entry); yield break; }
            // The slot covers the input capture, worker and main-thread publication.
            // Cancelled work returns it only after both coroutine and worker release it.
            while (Interlocked.CompareExchange(ref _bindingPreparationBusy, 1, 0) != 0)
            {
                if (!current()) yield break;
                yield return null;
            }
            BindingPending pending = null;
            try
            {
                if (!current()) yield break;
                if (entry.LoadedBinding != null) { TouchBinding(entry); yield break; }
                var bytes = ReadBinding(entry);
                var originalCount = original.vertexCount;
                var authoredCount = entry.Loaded.vertexCount;
                var slots = entry.Loaded.subMeshCount;
                var tangents = ReadNativeRestMesh(original, readable => readable.tangents);
                ValidateBindingTangents(tangents, originalCount);
                var template = CachedBinding(entry, originalCount, authoredCount, slots);
                if (template == null)
                {
                    if (bytes.LongLength > MaximumBindingPacketBytes) yield break;
                    pending = new BindingPending(bytes, fingerprint, originalCount, authoredCount, slots);
                    var job = QueueBinding(pending);
                    while (!job.IsCompleted)
                    {
                        if (!current())
                        {
                            pending.Cancelled = true;
                            _ = job.ContinueWith(failed => _ = failed.Exception, TaskContinuationOptions.OnlyOnFaulted);
                            yield break;
                        }
                        yield return null;
                    }
                    if (job.IsFaulted && job.Exception.InnerException is CharacterModelBindingPacketLimit) yield break;
                    template = job.GetAwaiter().GetResult();
                    if (!current()) yield break;
                    RememberBinding(entry, template);
                }
                if (current()) entry.LoadedBinding = template.Instantiate(BindingIdentity(entry, fingerprint), tangents);
            }
            finally
            {
                if (pending != null)
                {
                    pending.Cancelled = true;
                    pending.Release();
                    pending = null;
                }
                else Interlocked.Exchange(ref _bindingPreparationBusy, 0);
            }
        }
        private static void ValidateBindingTangents(Vector4[] tangents, int originalVertexCount)
        {
            if (tangents == null || tangents.Length != originalVertexCount || tangents.Any(tangent => !BodyShapeFinite(tangent.x) || !BodyShapeFinite(tangent.y) ||
                    !BodyShapeFinite(tangent.z) || tangent.w is not (-1f or 1f)))
                throw new InvalidOperationException("The native source has incomplete tangent frames.");
        }
        private static CharacterModelBindingTemplate ParseBindingTemplate(byte[] bytes, string fingerprint,
            int originalVertexCount, int authoredVertexCount, int subMeshCount, Func<bool> cancelled = null, long maximumMemory = long.MaxValue)
        {
            CheckCancellation(cancelled);
            var estimatedMemory = 256L + CustomModels.SkinMeshSource.SurfaceVertex.EstimatedBytes * authoredVertexCount;
            Budget();
            using var stream = new System.IO.MemoryStream(bytes, false);
            using var compressed = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new System.IO.BinaryReader(compressed, new System.Text.UTF8Encoding(false, true));
            var magic = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8));
            var multiSource = magic == "UBSFB002";
            var geometryFrame = magic == "UBSFB004";
            var explicitSurface = geometryFrame || magic == "UBSFB003";
            Require(multiSource || explicitSurface || magic == "UBSFB001");
            Require(!(multiSource || explicitSurface) || originalVertexCount > 0 && authoredVertexCount >= originalVertexCount);
            Require(BitConverter.ToString(reader.ReadBytes(32)).Replace("-", "").ToLowerInvariant() == fingerprint);
            var clothGenerated = Boolean();
            Require(!multiSource || !clothGenerated);
            var matrix = new Matrix4x4();
            for (var index = 0; index < 16; index++) matrix[index] = Number();
            var determinant = Vector3.Dot(Vector3.Cross((Vector3)matrix.GetColumn(0), (Vector3)matrix.GetColumn(1)), (Vector3)matrix.GetColumn(2));
            Require(BodyShapeFinite(determinant) && determinant != 0f &&
                matrix[3] == 0f && matrix[7] == 0f && matrix[11] == 0f && matrix[15] == 1f);
            if (explicitSurface) Require(reader.ReadInt32() == originalVertexCount);
            var roleCount = reader.ReadInt32();
            Require(roleCount > 0 && roleCount <= authoredVertexCount);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < roleCount; index++)
            {
                if ((index & 255) == 0) CheckCancellation(cancelled);
                var length = reader.ReadInt32();
                Require(length > 0 && length <= 4096);
                estimatedMemory += 64L + 2L * length; Budget();
                var encoded = reader.ReadBytes(length);
                Require(encoded.Length == length);
                var role = new System.Text.UTF8Encoding(false, true).GetString(encoded);
                Require(!string.IsNullOrWhiteSpace(role) && roles.Add(role));
            }
            Require(reader.ReadInt32() == authoredVertexCount);
            var vertices = new CustomModels.SkinMeshSource.SurfaceVertex[authoredVertexCount];
            for (var index = 0; index < vertices.Length; index++)
            {
                if ((index & 255) == 0) CheckCancellation(cancelled);
                var anchor = reader.ReadInt32(); var native = Boolean();
                var role = reader.ReadInt32(); var slot = reader.ReadInt32();
                Require(role >= 0 && role < roleCount && slot >= 0 && slot < subMeshCount);
                var sourceUv = new Vector2(Number(), Number());
                var offset = Vector(); var normal = Vector(); var tangent = Vector();
                var tangentW = Number();
                Require(tangentW is -1f or 1f);
                Require(anchor >= 0 && anchor < originalVertexCount && normal.sqrMagnitude >= 1e-8f && tangent.sqrMagnitude >= 1e-8f &&
                    Vector3.Cross(normal, tangent).sqrMagnitude >= 1e-8f &&
                    (!native || offset.Equals(Vector3.zero) && normal.Equals(Vector3.forward) && tangent.Equals(Vector3.right)));
                (int Anchor, float Weight)[] sources = null;
                if (multiSource)
                {
                    Require(native == (index < originalVertexCount) && (!native || anchor == index));
                    var count = reader.ReadInt32();
                    Require(count >= 1 && count <= 4);
                    estimatedMemory += 32L + 8L * count; Budget();
                    sources = new (int Anchor, float Weight)[count];
                    var total = 0d;
                    for (var source = 0; source < count; source++)
                    {
                        var sourceAnchor = reader.ReadInt32(); var weight = Number();
                        Require(sourceAnchor >= 0 && sourceAnchor < originalVertexCount && weight > 0f && weight <= 1f);
                        for (var previous = 0; previous < source; previous++) Require(sources[previous].Anchor != sourceAnchor);
                        sources[source] = (sourceAnchor, weight); total += weight;
                    }
                    Require(sources[0].Anchor == anchor && Math.Abs(total - 1d) <= 0.000001d &&
                        (!native || count == 1 && sources[0].Anchor == index && sources[0].Weight == 1f));
                }
                (int A, int B, int C)? surfaceTriangle = null;
                var surfaceBary = Vector3.zero;
                if (explicitSurface)
                {
                    Require(index >= originalVertexCount || native && anchor == index);
                    var present = Boolean();
                    Require(present == (!native && index >= originalVertexCount));
                    if (present)
                    {
                        var a = reader.ReadInt32(); var b = reader.ReadInt32(); var c = reader.ReadInt32();
                        surfaceBary = Vector(); var total = surfaceBary.x + surfaceBary.y + surfaceBary.z;
                        Require(a >= 0 && a < originalVertexCount && b >= 0 && b < originalVertexCount &&
                            c >= 0 && c < originalVertexCount && a != b && a != c && b != c &&
                            (anchor == a || anchor == b || anchor == c) &&
                            surfaceBary.x >= 0f && surfaceBary.y >= 0f && surfaceBary.z >= 0f &&
                            surfaceBary.x <= 1f && surfaceBary.y <= 1f && surfaceBary.z <= 1f &&
                            Math.Abs(total - 1f) <= .000001f);
                        surfaceTriangle = (a, b, c);
                    }
                }
                (int A, int B, int C)? geometryTriangle = null;
                var geometryBary = Vector3.zero;
                if (geometryFrame && Boolean())
                {
                    var a = reader.ReadInt32(); var b = reader.ReadInt32(); var c = reader.ReadInt32();
                    geometryBary = Vector();
                    Require(!native && surfaceTriangle.HasValue && a >= 0 && a < originalVertexCount &&
                        b >= 0 && b < originalVertexCount && c >= 0 && c < originalVertexCount &&
                        a != b && a != c && b != c && geometryBary.x >= 0f && geometryBary.y >= 0f &&
                        geometryBary.z >= 0f && geometryBary.x <= 1f && geometryBary.y <= 1f && geometryBary.z <= 1f &&
                        Math.Abs(geometryBary.x + geometryBary.y + geometryBary.z - 1f) <= .000001f);
                    geometryTriangle = (a, b, c);
                }
                vertices[index] = new CustomModels.SkinMeshSource.SurfaceVertex(anchor, offset, normal, tangent, native,
                    sources, tangentW, sourceUv, slot, surfaceTriangle, surfaceBary, geometryTriangle, geometryBary);
            }
            Require(compressed.ReadByte() == -1);
            return new CharacterModelBindingTemplate(matrix, vertices, originalVertexCount, authoredVertexCount, subMeshCount);
            void Budget() { if (estimatedMemory > maximumMemory) throw new CharacterModelBindingPacketLimit(); }
            void Require(bool valid)
            {
                if (!valid) throw new InvalidOperationException("An authored source attachment has an invalid schema, coordinate or source identity.");
            }
            bool Boolean()
            {
                var value = reader.ReadByte(); Require(value <= 1); return value == 1;
            }
            float Number()
            {
                var value = reader.ReadSingle(); Require(BodyShapeFinite(value)); return value;
            }
            Vector3 Vector() => new(Number(), Number(), Number());
        }
        private static CharacterModelMaterialAsset WithSharedTextures(CharacterModelMaterialAsset entry, CharacterModelProperty[] selectors)
        {
            var absolute = entry.properties.Where(property => property.kind is "texture" or "float")
                .Select(property => property.name).ToHashSet(StringComparer.Ordinal);
            return new CharacterModelMaterialAsset
            {
                sourceFingerprint = entry.sourceFingerprint, materialSlot = entry.materialSlot,
                sourceShader = entry.sourceShader, bundleFile = entry.bundleFile,
                // Conditional declarations retain their global fallback for other native sources.
                properties = entry.properties.Concat(selectors.Where(property => !absolute.Contains(property.name))).ToArray()
            };
        }
        internal CharacterModelMaterialAsset Material(string fingerprint, int slot, Shader shader)
        {
            if (!shader) return null;
            var key = MaterialKey(fingerprint, slot, shader.name);
            if (_materials.TryGetValue(key, out var entry) || !_textureProperties.TryGetValue(shader.name, out var selectors)) return entry;
            entry = new CharacterModelMaterialAsset
            {
                sourceFingerprint = fingerprint, materialSlot = slot, sourceShader = shader.name, properties = selectors
            };
            _materials[key] = entry;
            return entry;
        }
        internal CharacterModelProperty TextureRemap(string shader, string property, string fingerprint, string bodyFingerprint) =>
            fingerprint == null ? null : bodyFingerprint != null && _textureRemaps.TryGetValue((shader, property, fingerprint, bodyFingerprint), out var specific)
                ? specific : _textureRemaps.TryGetValue((shader, property, fingerprint, null), out var fallback) ? fallback : null;
        internal IEnumerator PrepareMaterialTextures(IEnumerable<GameObject> parts, IReadOnlyCollection<Texture> bodyTextures,
            PreparedSources preparation, Func<bool> current,
            IReadOnlyDictionary<int, IReadOnlyCollection<Texture>> nativeTextures)
        {
            var declarations = new Dictionary<Material, List<CharacterModelMaterialAsset>>();
            var allRenderers = new HashSet<Renderer>();
            foreach (var part in parts)
            {
                if (!current()) yield break;
                foreach (var renderer in part.GetComponentsInChildren<Renderer>(true)) allRenderers.Add(renderer);
                var sources = CustomModels.SkinMeshSource.CapturePrefab(part,
                    part.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(renderer => renderer.sharedMesh));
                if (sources == null) continue;
                foreach (var pair in sources)
                {
                    if (!CharacterModelFingerprints.TryGetValue(pair.Value.Original, out var fingerprint)) continue;
                    var materials = pair.Key.sharedMaterials;
                    for (var slot = 0; slot < materials.Length; slot++)
                    {
                        var material = materials[slot];
                        if (!material || !material.shader || Material(fingerprint, slot, material.shader) is not { } declaration) continue;
                        // Prefab materials may be shared across native parts and LODs.
                        // Only the final instantiated material has a single live owner.
                        if (!declarations.TryGetValue(material, out var candidates))
                            declarations.Add(material, candidates = []);
                        if (!candidates.Any(previous => CharacterModelMaterial.SameDeclaration(previous, declaration)))
                            candidates.Add(declaration);
                    }
                }
            }
            foreach (var renderer in allRenderers)
            {
                var materials = renderer.sharedMaterials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (material && material.shader && !declarations.ContainsKey(material) &&
                        Material(null, slot, material.shader) is { } declaration)
                        declarations.Add(material, [declaration]);
                }
            }
            // Unknown or ambiguous final pairing still needs its normal fallback prepared.
            string[] unconstrained = [null];
            var bodyFingerprints = bodyTextures.Count == 0 ? unconstrained : bodyTextures.Select(CharacterModelTextureFingerprint)
                .Where(IsBodyTextureFingerprint).Concat(unconstrained).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var row in declarations)
            foreach (var declaration in row.Value)
            {
                if (!current()) yield break;
                Material profileMaterial = null;
                if (declaration.authoredProfile is { } profile)
                {
                    RetainBundle(preparation.Packs, profile.bundleFile);
                    if (!_bundles[profile.bundleFile].Loaded)
                        yield return PrepareBundle(profile.bundleFile, current);
                    if (!current()) yield break;
                    profileMaterial = Profile(profile);
                    yield return null;
                }
                foreach (var group in declaration.properties.GroupBy(property => property.name, StringComparer.Ordinal))
                {
                    if (!current()) yield break;
                    var properties = group.ToArray();
                    if (properties[0].kind == "float") continue;
                    var id = Shader.PropertyToID(group.Key);
                    // A new profile property has the authored default, while every
                    // retained native channel keeps the exact native source texture.
                    var native = row.Key.HasProperty(id) ? row.Key.GetTexture(id) :
                        profileMaterial && profileMaterial.HasProperty(id) ? profileMaterial.GetTexture(id) : null;
                    var nativeSources = nativeTextures.TryGetValue(id, out var overrides)
                        ? overrides.Prepend(native).Distinct() : new[] { native };
                    // Resolve the same declarations for prefab and native parameter sources.
                    // Final pairing and texture writes remain with the live material owner.
                    foreach (var source in nativeSources)
                    foreach (var bodyFingerprint in HasBodyTextureConditions && declaration.UsesBodyTexture ? bodyFingerprints : unconstrained)
                    {
                        if (!current()) yield break;
                        var selected = CharacterModelMaterialProperty.SelectTexture(properties, source, this,
                            declaration.sourceShader, declaration.AssetBundleFile, bodyFingerprint);
                        if (selected.Property == null) continue;
                        RetainBundle(preparation.Packs, selected.Bundle);
                        if (selected.Property.Loaded) continue;
                        if (!_bundles[selected.Bundle].Loaded)
                            yield return PrepareBundle(selected.Bundle, current);
                        if (!current()) yield break;
                        _ = Texture(selected.Bundle, selected.Property);
                        yield return null;
                    }
                }
            }
        }
        internal Texture2D Texture(string bundle, CharacterModelProperty property)
        {
            if (_bundles[bundle].Owners <= 0) throw new InvalidOperationException("A custom character model texture has no active presentation owner.");
            if (!property.Loaded) property.Loaded = Bundle(bundle).LoadAsset<Texture2D>(property.textureAsset);
            if (!property.Loaded) throw new InvalidOperationException("An authored character model texture is missing: " + property.textureAsset);
            return property.Loaded;
        }
        internal Material Profile(CharacterModelMaterialProfile profile)
        {
            if (_bundles[profile.bundleFile].Owners <= 0)
                throw new InvalidOperationException("An authored material profile has no active presentation owner.");
            if (!profile.Loaded) profile.Loaded = Bundle(profile.bundleFile).LoadAsset<Material>(profile.materialAsset);
            if (!profile.Loaded || !profile.Loaded.shader || !profile.Loaded.shader.isSupported || profile.Loaded.shader.name != profile.shader)
                throw new InvalidOperationException("An authored material profile or its declared shader is missing.");
            return profile.Loaded;
        }
        internal void RetainBundle(HashSet<string> retained, string file)
        {
            var entry = _bundles[file];
            if (!retained.Add(file)) return;
            RemoveUnusedBundle(entry);
            entry.Owners++;
        }
        internal void RetainNativeSources(GraphicsCharacter graphics, HashSet<UnityEngine.Object> retained,
            HashSet<UnityEngine.Object> required)
        {
            required.Clear();
            foreach (var source in graphics.ShapeObjectPerBodySlot ?? []) RetainNativeSource(source, retained, required);
            foreach (var source in graphics.ArmorObjectPerBodySlot ?? []) RetainNativeSource(source, retained, required);
            foreach (var source in graphics.CustomArmorMaterialPerBodySlot ?? []) RetainNativeSource(source, retained, required);
            RetainNativeSource(graphics.BDMainTex, retained, required);
            RetainNativeSource(graphics.BDNormalMap, retained, required);
            RetainNativeSource(graphics.BDMetallicGlossMap, retained, required);
            RetainNativeSource(graphics.ScarsMainTex, retained, required);
            RetainNativeSource(graphics.ScarsNormalMap, retained, required);
            RetainNativeSource(graphics.EyeMainTex, retained, required);
            RetainNativeSource(graphics.EyeColorMask, retained, required);
            ReleaseNativeSources(retained, required);
        }
        internal void RetainCompletePartSources(GraphicsCharacter graphics, HashSet<UnityEngine.Object> retained,
            GraphicsCharacterFactoryManager factory = null)
        {
            var required = new HashSet<UnityEngine.Object>();
            void Keep(UnityEngine.Object source, Func<AsyncOperationHandle> acquire)
            {
                if (source) RetainNativeSource(source, acquire, retained, required);
            }
            void KeepLoaded(UnityEngine.Object source)
            {
                if (!source) return;
                if (_nativeSources.ContainsKey(source))
                {
                    Keep(source, () => throw new InvalidOperationException("An owned resource lost its source handle."));
                    return;
                }
                var cached = _retainedResources.Values.FirstOrDefault(entry => entry.Handle.IsValid() && entry.Handle.IsDone &&
                    entry.Handle.Status == AsyncOperationStatus.Succeeded && entry.Handle.Result as UnityEngine.Object == source);
                if (cached != null) Keep(source, () =>
                {
                    Addressables.ResourceManager.Acquire(cached.Handle);
                    return cached.Handle;
                });
            }
            foreach (var source in (graphics.ShapeObjectPerBodySlot ?? []).Concat(graphics.ArmorObjectPerBodySlot ?? []))
            {
                if (source.Value && source.Key?.StartsWith("UB/CharacterResources/", StringComparison.Ordinal) == true)
                    Keep(source.Value, () => Addressables.LoadAssetAsync<GameObject>(source.Key));
                else KeepLoaded(source.Value);
            }
            foreach (var source in graphics.CustomArmorMaterialPerBodySlot ?? []) KeepLoaded(source.Value);
            if (graphics.Animator)
            {
                KeepLoaded(graphics.Animator.avatar);
                var controller = graphics.Animator.runtimeAnimatorController;
                while (controller)
                {
                    KeepLoaded(controller);
                    if (controller is not AnimatorOverrideController overridden) break;
                    controller = overridden.runtimeAnimatorController;
                }
            }
            if (factory)
            {
                KeepPending(factory.shapePartsLoadOperations, factory.shapePartsToLoad);
                KeepPending(factory.armorPartsLoadOperations, factory.armorPartsToLoad);
                foreach (var handle in factory.customArmorMaterialLoadOperations.Values)
                    if (handle.IsValid() && handle.IsDone && handle.Status == AsyncOperationStatus.Succeeded) KeepLoaded(handle.Result);
            }
            ReleaseNativeSources(retained, required);
            return;
            void KeepPending(IReadOnlyDictionary<GraphicsCharacterDefinitions.BodyPart, AsyncOperationHandle<GameObject>> pending, string[] keys)
            {
                foreach (var pair in pending)
                {
                    var index = (int)pair.Key;var handle = pair.Value;
                    if (index < 0 || index >= keys.Length || keys[index]?.StartsWith("UB/CharacterResources/", StringComparison.Ordinal) != true ||
                        !handle.IsValid() || !handle.IsDone || handle.Status != AsyncOperationStatus.Succeeded || !handle.Result) continue;
                    Keep(handle.Result, () => { Addressables.ResourceManager.Acquire(handle); return handle; });
                }
            }
        }
        internal void RetainNativePrefabSources(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics,
            HashSet<UnityEngine.Object> retained, bool includePending = true)
        {
            var required = new HashSet<UnityEngine.Object>();
            if (!includePending)
            {
                RetainNativeSources(graphics, retained, required);
                return;
            }
            foreach (var source in factory.shapePartsLoadOperations.Values.Concat(factory.armorPartsLoadOperations.Values))
            {
                if (!source.IsValid() || !source.IsDone ||
                    source.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || !source.Result) continue;
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle handle = source;
                RetainNativeSource(source.Result, () => { Addressables.ResourceManager.Acquire(handle); return handle; }, retained, required);
            }
            // Unchanged parts have no pending factory handle. Keep their exact loaded
            // prefab Result while a new adjacent source is prepared, just as live owners do.
            if (_hasJointAttachments)
            {
                RetainUnchanged(factory.shapePartsLoadOperations, graphics.ShapeObjectPerBodySlot);
                RetainUnchanged(factory.armorPartsLoadOperations, graphics.ArmorObjectPerBodySlot);
            }
            ReleaseNativeSources(retained, required);
            return;

            void RetainUnchanged(IReadOnlyDictionary<GraphicsCharacterDefinitions.BodyPart,
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject>> pending,
                KeyValuePair<string, GameObject>[] native)
            {
                if (native == null) return;
                for (var index = 0; index < native.Length; index++)
                    if (native[index].Value && native[index].Value == CharacterModelPartSource(
                        (GraphicsCharacterDefinitions.BodyPart)index, pending, native))
                        RetainNativeSource(native[index], retained, required);
            }
        }
        private void RetainNativeSource<T>(KeyValuePair<string, T> native,
            HashSet<UnityEngine.Object> retained, HashSet<UnityEngine.Object> required) where T : UnityEngine.Object
        {
            if (!native.Value || string.IsNullOrEmpty(native.Key)) return;
            RetainNativeSource(native.Value, () => Addressables.LoadAssetAsync<T>(native.Key), retained, required);
        }
        private void RetainNativeSource(UnityEngine.Object source,
            Func<UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle> acquire,
            HashSet<UnityEngine.Object> retained, HashSet<UnityEngine.Object> required)
        {
            if (!_nativeSources.TryGetValue(source, out var entry))
            {
                // Acquire an actual completed Addressable result, never an identity
                // inferred from its asset key or the instance currently using it.
                var handle = acquire();
                try
                {
                    if (!handle.IsValid() || !handle.IsDone ||
                        handle.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded ||
                        handle.Result as UnityEngine.Object != source) return;
                    var acquired = new NativeSourceEntry(source, handle);
                    _nativeSources.Add(source, acquired);
                    entry = acquired;
                }
                finally
                {
                    if (entry == null && handle.IsValid()) Addressables.Release(handle);
                }
            }
            required.Add(source);
            if (!retained.Add(source)) return;
            if (entry.Unused != null)
            {
                _unusedNativeSources.Remove(entry.Unused);
                entry.Unused = null;
            }
            entry.Owners++;
        }
        internal void ReleaseNativeSources(HashSet<UnityEngine.Object> retained, ISet<UnityEngine.Object> keep = null)
        {
            var releasing = false;
            foreach (var source in retained)
                if (keep?.Contains(source) != true) { releasing = true; break; }
            if (!releasing) return;
            foreach (var source in retained.ToArray())
            {
                if (keep?.Contains(source) == true) continue;
                var entry = _nativeSources[source];
                if (entry.Owners <= 0) throw new InvalidOperationException("A native appearance source lease has no matching owner.");
                retained.Remove(source);
                if (--entry.Owners != 0) continue;
                entry.Unused = _unusedNativeSources.AddLast(entry);
            }
            TrimUnusedNativeSources();
        }
        internal string NativeDataFingerprint(UnityEngine.Object source, Func<string> create)
        {
            // Only the original serialized data of an acquired prefab participates.
            // Mutable runtime clones and shared-binding validation always hash fresh.
            if (!source || !_nativeSources.Values.Any(entry => entry.Data.Contains(source))) return create();
            if (_nativeDataFingerprints.TryGetValue(source, out var fingerprint)) return fingerprint;
            fingerprint = create();
            _nativeDataFingerprints.Add(source, fingerprint);
            return fingerprint;
        }
        internal IEnumerator PrepareNativeFingerprint(UnityEngine.Object source, Func<bool> current)
        {
            if (!source || _nativeDataFingerprints.ContainsKey(source) ||
                !_nativeSources.Values.Any(entry => entry.Owners > 0 && entry.Data.Contains(source))) yield break;
            string fingerprint = null;
            yield return CustomModels.SkinMeshSource.PrepareNativeFingerprint(source, current, value => fingerprint = value);
            if (current() && fingerprint != null && _nativeSources.Values.Any(entry => entry.Owners > 0 && entry.Data.Contains(source)))
                _ = NativeDataFingerprint(source, () => fingerprint);
        }
        internal bool HasPreparedBinding(string fingerprint) => _meshes.TryGetValue(fingerprint, out var entry) &&
            entry.mode == "authored" && !string.IsNullOrEmpty(entry.bindingFile) && entry.LoadedBinding != null;
        private static HashSet<UnityEngine.Object> NativeSourceAssets(UnityEngine.Object source)
        {
            var assets = new HashSet<UnityEngine.Object> { source };
            if (source is Material nativeMaterial) AddMaterial(nativeMaterial);
            if (source is not GameObject prefab) return assets;
            foreach (var component in prefab.GetComponentsInChildren<Component>(true))
            {
                if (!component) continue;
                assets.Add(component);
                if (component is MeshFilter filter && filter.sharedMesh) assets.Add(filter.sharedMesh);
                if (component is SkinnedMeshRenderer skin && skin.sharedMesh) assets.Add(skin.sharedMesh);
                if (component is not Renderer renderer) continue;
                foreach (var material in renderer.sharedMaterials) AddMaterial(material);
            }
            return assets;
            void AddMaterial(Material material)
            {
                if (!material || !assets.Add(material) && material != source) return;
                var shader = material.shader;
                if (!shader) return;
                assets.Add(shader);
                for (var index = 0; index < shader.GetPropertyCount(); index++)
                    if (shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Texture &&
                        material.GetTexture(shader.GetPropertyName(index)) is { } texture && texture) assets.Add(texture);
            }
        }
        private long UnusedNativeSourceBytes()
        {
            // Shared dependencies are counted once and active sources already own theirs.
            var assets = new Dictionary<UnityEngine.Object, long>();
            foreach (var entry in _unusedNativeSources)
                foreach (var asset in entry.Assets) assets[asset.Key] = asset.Value;
            foreach (var entry in _nativeSources.Values)
                if (entry.Owners != 0)
                    foreach (var asset in entry.Assets.Keys) assets.Remove(asset);
            return assets.Values.Sum();
        }
        private void TrimUnusedNativeSources()
        {
            while (_unusedNativeSources.Count > MaximumUnusedNativeSources ||
                   _unusedNativeSources.Count != 0 && UnusedNativeSourceBytes() > MaximumUnusedNativeSourceBytes)
                ReleaseUnusedNativeSource(_unusedNativeSources.First.Value);
        }
        private void ReleaseUnusedNativeSource(NativeSourceEntry entry)
        {
            if (entry.Owners != 0) throw new InvalidOperationException("An active native appearance source cannot be evicted.");
            _unusedNativeSources.Remove(entry.Unused);
            entry.Unused = null;
            _nativeSources.Remove(entry.Source);
            foreach (var data in entry.Data)
                if (!_nativeSources.Values.Any(source => source.Data.Contains(data))) _nativeDataFingerprints.Remove(data);
            Addressables.Release(entry.Handle);
        }
        internal void ReleaseBundles(HashSet<string> retained, ISet<string> keep = null)
        {
            var releasing = false;
            foreach (var file in retained)
                if (keep?.Contains(file) != true) { releasing = true; break; }
            if (!releasing) return;
            foreach (var file in retained.ToArray())
            {
                if (keep?.Contains(file) == true) continue;
                var entry = _bundles[file];
                if (entry.Owners <= 0) throw new InvalidOperationException("A custom character model pack lease has no matching owner.");
                entry.Owners--;
                retained.Remove(file);
                if (entry.Owners == 0)
                {
                    entry.Verification?.Dispose(); entry.Verification = null;
                }
                if (entry.Owners == 0 && entry.Loaded)
                {
                    if (_characterModelsUnloading) UnloadBundle(entry);
                    else
                    {
                        entry.CachedBytes = BundleMemorySize(entry);
                        entry.Unused = _unusedBundles.AddLast(entry);
                        _unusedBundleBytes += entry.CachedBytes;
                    }
                }
            }
            TrimUnusedBundles();
        }
        private static long BundleMemorySize(CharacterModelBundle entry)
        {
            var assets = new HashSet<UnityEngine.Object> { entry.Loaded };
            foreach (var mesh in entry.Meshes) if (mesh.Loaded) assets.Add(mesh.Loaded);
            foreach (var joint in entry.Joints) if (joint.Loaded) assets.Add(joint.Loaded);
            foreach (var property in entry.Textures) if (property.Loaded) assets.Add(property.Loaded);
            foreach (var profile in entry.Profiles)
            {
                var material = profile.Loaded;
                if (!material) continue;
                assets.Add(material);
                if (!material.shader) continue;
                assets.Add(material.shader);
                for (var index = 0; index < material.shader.GetPropertyCount(); index++)
                    if (material.shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Texture &&
                        material.GetTexture(material.shader.GetPropertyName(index)) is { } texture && texture)
                        assets.Add(texture);
            }
            var skinBytes = entry.Meshes.Select(mesh => mesh.LoadedBinding?.SkinEncodingSource)
                .Where(binding => binding != null).Distinct().Sum(binding => binding.SkinEncodingBytes);
            var baseBytes = entry.Meshes.Select(mesh => mesh.LoadedBaseBinding).Where(binding => binding != null).Distinct()
                .Sum(binding => binding.VertexBytes + binding.SourceTangents.LongLength * 16L + binding.AuthoredBaseProofBytes);
            var surfaceBytes = entry.Meshes.Select(mesh => mesh.LoadedBinding?.SurfacePlanSource)
                .Where(binding => binding != null).Distinct().Sum(binding => binding.NativeSurface?.Bytes ?? 0L);
            var finishBytes = entry.Meshes.Select(mesh => mesh.PreparedFinishPacket).Where(packet => packet != null).Distinct().Sum(packet => packet.Bytes);
            return Math.Max(entry.StoredBytes, CharacterModelMemorySize(assets)) + skinBytes + baseBytes + surfaceBytes + finishBytes;
        }
        private void RemoveUnusedBundle(CharacterModelBundle entry)
        {
            if (entry.Unused == null) return;
            _unusedBundles.Remove(entry.Unused);
            entry.Unused = null;
            _unusedBundleBytes -= entry.CachedBytes;
            entry.CachedBytes = 0;
        }
        private void TrimUnusedBundles()
        {
            // A presentation lease protects its native objects. Only idle packs
            // participate in the LRU budget when previews or culling release them.
            while (_unusedBundles.Count > MaximumUnusedBundles || _unusedBundleBytes > MaximumUnusedBundleBytes)
                UnloadBundle(_unusedBundles.First.Value);
        }
        private void UnloadBundle(CharacterModelBundle entry)
        {
            if (entry.Owners != 0)
                throw new InvalidOperationException("An active custom character model pack cannot be evicted.");
            RemoveUnusedBundle(entry);
            entry.Verification?.Dispose(); entry.Verification = null;
            if (entry.Loaded) entry.Loaded.Unload(true);
            entry.Loaded = null;
            foreach (var mesh in entry.Meshes)
            {
                mesh.PreparedFinishPacket = null;
                mesh.Loaded = null;
                mesh.LoadedBinding = null;
                mesh.LoadedBaseBinding = null; mesh.LoadedBaseBindingTarget = null; mesh.LoadedBaseBindingPrepared = false;
            }
            foreach (var joint in entry.Joints) { joint.Loaded = null; joint.Verified.Clear(); }
            foreach (var property in entry.Textures) property.Loaded = null;
            foreach (var profile in entry.Profiles) profile.Loaded = null;
        }
        public void Dispose()
        {
            if (_disposed) return;
            if (HasPreparedSources) throw new InvalidOperationException("Custom character model native sources are still preparing.");
            if (_nativeSources.Values.Any(entry => entry.Owners != 0))
                throw new InvalidOperationException("Custom character model presentations still own native appearance sources.");
            if (_bundles.Values.Any(entry => entry.Owners != 0))
                throw new InvalidOperationException("Custom character model presentations still own catalog packs.");
            _disposed = true;
            while (_unusedClothMeshes.First is { } unused) DestroyClothMesh(unused.Value);
            if (UsesCompleteParts) Addressables.RemoveResourceLocator(this);
            while (_retainedResourceOrder.First is { } retained) ReleaseRetainedResource(retained.Value);
            _retainedResourceBytes = 0;
            _resourceLocations.Clear();
            _verifiedResourceFiles.Clear();
            while (_unusedNativeSources.First != null) ReleaseUnusedNativeSource(_unusedNativeSources.First.Value);
            _nativeDataFingerprints.Clear();
            foreach (var entry in _parsedBindings)
            {
                entry.ParsedBinding = null;
                entry.ParsedBindingNode = null;
            }
            _parsedBindings.Clear();
            _parsedBindingBytes = 0;
            foreach (var entry in _bundles.Values) UnloadBundle(entry);
            foreach (var entry in _meshes.Values)
            {
                entry.PreparedFinishPacket = null;
                entry.LoadedBinding = null;
                entry.LoadedBaseBinding = null; entry.LoadedBaseBindingTarget = null; entry.LoadedBaseBindingPrepared = false;
                entry.VerifiedContent = default;
            }
        }
    }

    private static void ValidateCharacterModelBinding(Mesh original, Mesh authored, bool validateAuthored)
    {
        var originalPoses = original.bindposes; var authoredPoses = authored.bindposes;
        if (originalPoses.Length != authoredPoses.Length || original.blendShapeCount != authored.blendShapeCount)
            throw new InvalidOperationException("An authored character model changed the native binding or expression count.");
        for (var bone = 0; bone < originalPoses.Length; bone++)
            for (var element = 0; element < 16; element++)
                if (!BitConverter.GetBytes(originalPoses[bone][element]).SequenceEqual(BitConverter.GetBytes(authoredPoses[bone][element])))
                    throw new InvalidOperationException("An authored character model changed a native bind pose.");
        if (validateAuthored)
        {
            var weights = authored.boneWeights;
            if (weights.Length != authored.vertexCount) throw new InvalidOperationException("An authored character model has incomplete skin weights.");
            foreach (var weight in weights)
            {
                var sum = weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3;
                if (!BodyShapeFinite(sum) || Mathf.Abs(sum - 1f) > 0.00001f)
                    throw new InvalidOperationException("An authored character model skin weight is not normalized.");
                for (var influence = 0; influence < 4; influence++)
                    if (!BodyShapeFinite(BodyShapeWeight(weight, influence)) || BodyShapeWeight(weight, influence) < 0f || BodyShapeWeight(weight, influence) > 1f ||
                        BodyShapeWeight(weight, influence) > 0f && (BodyShapeWeightIndex(weight, influence) < 0 || BodyShapeWeightIndex(weight, influence) >= originalPoses.Length))
                        throw new InvalidOperationException("An authored character model references an invalid native bone.");
            }
        }
        for (var shape = 0; shape < original.blendShapeCount; shape++)
        {
            if (original.GetBlendShapeName(shape) != authored.GetBlendShapeName(shape) || original.GetBlendShapeFrameCount(shape) != authored.GetBlendShapeFrameCount(shape))
                throw new InvalidOperationException("An authored character model changed native expression names or ordering.");
            for (var frame = 0; frame < original.GetBlendShapeFrameCount(shape); frame++)
                if (!BitConverter.GetBytes(original.GetBlendShapeFrameWeight(shape, frame)).SequenceEqual(BitConverter.GetBytes(authored.GetBlendShapeFrameWeight(shape, frame))))
                    throw new InvalidOperationException("An authored character model changed a native expression frame weight.");
        }
    }
    private static long CharacterModelMemorySize(IEnumerable<UnityEngine.Object> data) => data.Where(value => value)
        .Distinct().Sum(CustomModels.SkinMeshSource.NativeDataMemorySize);

    internal static CustomModels.SkinMeshSource.SourceGraph BeginCharacterModelNativeSurfaceFinish(object deformer, int bufferIndex)
    {
        var graphics = CustomModels.SkinMeshSource.NativeSourceGraphics(deformer);
        var graph = graphics ? graphics.GetComponent<CharacterModelPresentation>()?._sourceGraph : null;
        return graph?.BeginNativeSurfaceFinish(deformer, bufferIndex) == true ? graph : null;
    }

    private sealed class CharacterModelResourceLease : MonoBehaviour
    {
        internal GraphicsCharacter Graphics { get; private set; }
        private CharacterModelCatalog _catalog;
        private readonly HashSet<UnityEngine.Object> _sources = [];
        private int _loading;
        internal void Bind(GraphicsCharacter graphics, CharacterModelCatalog catalog)
        {
            if (_catalog != null && !ReferenceEquals(_catalog, catalog))
            {
                if (_sources.Count != 0) throw new InvalidOperationException("A live character still owns another resource catalog.");
                Release();
            }
            Graphics = graphics;_catalog = catalog;
            CharacterModelResourceOwners.Add(this);
        }
        internal void BeginLoad() => _loading++;
        internal void EndLoad()
        {
            if (_loading <= 0) throw new InvalidOperationException("A character source load has no matching lease.");
            _loading--;
            Synchronize();
        }
        internal void Synchronize(GraphicsCharacterFactoryManager factory = null)
        {
            if (_catalog == null || !Graphics || _loading != 0 && !factory) return;
            _catalog.RetainCompletePartSources(Graphics, _sources, factory);
            ReleaseUnusedCharacterModelCatalog();
        }
        internal void Release()
        {
            var catalog = _catalog;_catalog = null;
            catalog?.ReleaseNativeSources(_sources);
            CharacterModelResourceOwners.Remove(this);
            ReleaseUnusedCharacterModelCatalog();
        }
        // Cached previews remain owners while hidden; their meshes and material
        // instances still use these assets when the preview is shown again.
        private void LateUpdate()
        {
            if (_catalog?.IsRetiring == true) Synchronize();
        }
        private void OnDestroy() => Release();
    }

    private sealed class CharacterModelPresentation : MonoBehaviour
    {
        private const long MaximumUnusedDerivedBytes = 64L * 1024 * 1024;
        private const int MaximumUnusedDerivedEntries = 32;
        private sealed class DerivedDataEntry(UnityEngine.Object[] data)
        {
            internal readonly UnityEngine.Object[] Data = data;
            internal readonly long Bytes = CharacterModelMemorySize(data);
            internal int Owners;
            internal LinkedListNode<string> Unused;
        }
        private static readonly Dictionary<string, DerivedDataEntry> _derivedData = new(StringComparer.Ordinal);
        private static readonly LinkedList<string> _unusedDerivedData = new();
        private static long _unusedDerivedBytes;
        private UnityEngine.Object[] RetainDerivedData(string key, Func<UnityEngine.Object[]> create) =>
            RetainSourceData(_derivedKeys, key, create, null);
        internal static bool RetainExistingSourceData(HashSet<string> retained, string key)
        {
            if (!_derivedData.TryGetValue(key, out var entry)) return false;
            if (retained.Add(key)) { RemoveUnusedDerivedData(entry); entry.Owners++; }
            return true;
        }
        internal static void RetainPreparedSourceData(HashSet<string> retained, string key, UnityEngine.Object[] data) =>
            _ = RetainSourceData(retained, key, null, data);
        private static UnityEngine.Object[] RetainSourceData(HashSet<string> retained, string key,
            Func<UnityEngine.Object[]> create, UnityEngine.Object[] prepared)
        {
            if (!_derivedData.TryGetValue(key, out var entry))
            {
                var data = prepared ?? create();
                try
                {
                    if (data == null || data.Length == 0 || data.Any(value => !value))
                        throw new InvalidOperationException("Derived source binding construction did not return owned native data.");
                    entry = new DerivedDataEntry(data);
                    _derivedData.Add(key, entry);
                }
                catch { foreach (var value in data ?? []) if (value) UnityEngine.Object.Destroy(value); throw; }
            }
            else if (prepared != null)
                foreach (var value in prepared) if (value) UnityEngine.Object.Destroy(value);
            if (retained.Add(key)) { RemoveUnusedDerivedData(entry); entry.Owners++; }
            return entry.Data;
        }
        private static void RemoveUnusedDerivedData(DerivedDataEntry entry)
        {
            if (entry.Unused == null) return;
            _unusedDerivedData.Remove(entry.Unused);
            entry.Unused = null;
            _unusedDerivedBytes -= entry.Bytes;
        }
        private static void DestroyUnusedDerivedData(string key)
        {
            var entry = _derivedData[key];
            if (entry.Owners != 0)
                throw new InvalidOperationException("An active derived native binding cannot be evicted.");
            RemoveUnusedDerivedData(entry);
            foreach (var data in entry.Data) if (data) UnityEngine.Object.Destroy(data);
            _derivedData.Remove(key);
        }
        internal static void ClearUnusedDerivedData()
        {
            while (_unusedDerivedData.First != null) DestroyUnusedDerivedData(_unusedDerivedData.First.Value);
        }
        private static void TrimUnusedDerivedData()
        {
            while (_unusedDerivedData.Count > MaximumUnusedDerivedEntries || _unusedDerivedBytes > MaximumUnusedDerivedBytes)
                DestroyUnusedDerivedData(_unusedDerivedData.First.Value);
        }
        private void ReleaseDerivedData() => ReleaseSourceData(_derivedKeys);
        internal static void ReleaseSourceData(HashSet<string> retained)
        {
            foreach (var key in retained.ToArray())
            {
                var entry = _derivedData[key];
                if (entry.Owners <= 0) throw new InvalidOperationException("A derived native binding has no active presentation owner.");
                if (--entry.Owners == 0)
                {
                    // Cache immutable mesh/data clones, never the source graph or its
                    // live renderer, deformer and actor ownership snapshots.
                    entry.Unused = _unusedDerivedData.AddLast(key);
                    _unusedDerivedBytes += entry.Bytes;
                    if (_characterModelsUnloading) DestroyUnusedDerivedData(key);
                }
                retained.Remove(key);
            }
            TrimUnusedDerivedData();
        }

        private GraphicsCharacter _graphics;
        private RulesetCharacter _actor;
        private readonly List<CharacterModelSkin> _skins = [];
        internal CustomModels.SkinMeshSource.SourceGraph _sourceGraph;
        private readonly HashSet<string> _derivedKeys = new(StringComparer.Ordinal);
        private readonly List<CharacterModelMaterial> _materials = [];
        private readonly HashSet<string> _packs = new(StringComparer.Ordinal);
        private readonly HashSet<UnityEngine.Object> _nativeSources = [];
        private readonly HashSet<UnityEngine.Object> _requiredNativeSources = [];
        private CharacterModelCatalog _catalog;
        private CharacterModelCatalog.PreparedSources _preparedSources, _sourcePreparationReceipt;
        private IEnumerator _sourcePreparation;
        private Coroutine _sourcePreparationCoroutine;
        private GraphicsCharacterFactoryManager _sourcePreparationFactory;
        private int _sourcePreparationRequest;
        private bool _busy, _suspended, _initializationFailed, _refreshPending;
        private int _materialRefreshDepth;
        private bool _materialRefreshPending, _materialRefreshFailed;
        private int _initializationDepth;
        internal bool IsInitializing => _initializationDepth != 0;
        internal bool IsPending => _refreshPending;
        internal bool HasSources => _skins.Count != 0 || _materials.Count != 0 || _packs.Count != 0 || _nativeSources.Count != 0;
        internal bool HasCatalogSources => _catalog != null && HasSources || _preparedSources != null || _sourcePreparation != null;
        internal void PrepareSources(CharacterModelCatalog.PreparedSources preparation)
        {
            CancelSourcePreparation();
            ReleasePreparedSources(); _preparedSources = preparation; DeferRefresh();
        }
        internal void ReleasePreparedSources()
        {
            var preparation = _preparedSources; _preparedSources = null;
            try { preparation?.Dispose(); }
            catch { _preparedSources = preparation; throw; }
        }

        internal void Bind(GraphicsCharacter graphics)
        {
            if (_graphics && !ReferenceEquals(_graphics, graphics)) CancelSourcePreparation();
            _graphics = graphics;
        }

        private void CancelSourcePreparation()
        {
            _sourcePreparationRequest++;
            var operation = _sourcePreparation;
            var coroutine = _sourcePreparationCoroutine;
            var factory = _sourcePreparationFactory;
            _sourcePreparation = null; _sourcePreparationCoroutine = null; _sourcePreparationFactory = null;
            try { if (factory && coroutine != null) factory.StopCoroutine(coroutine); }
            finally { (operation as IDisposable)?.Dispose(); }
        }

        private bool EnsurePreparedSources(GraphicsCharacter graphics, CharacterModelCatalog catalog)
        {
            if (catalog == null) { CancelSourcePreparation(); return true; }
            var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>() as GraphicsCharacterFactoryManager;
            if (_preparedSources is { Completed: true, Failed: false } prepared && ReferenceEquals(prepared.Catalog, catalog) &&
                prepared.SourcesCurrent(factory, graphics, false)) return true;
            if (_sourcePreparationReceipt is { } receipt && ReferenceEquals(receipt.Catalog, catalog) &&
                receipt.SourcesCurrent(factory, graphics, false))
            {
                if (receipt.Completed && !receipt.Failed) return true;
                _refreshPending = false;
                SynchronizeCharacterModelRefreshPending();
                return false;
            }
            if (_sourcePreparation != null) { DeferRefresh(); return false; }
            ReleasePreparedSources();
            _sourcePreparationReceipt = null;
            if (!factory) { DeferRefresh(); return false; }
            var preparation = catalog.PrepareSources();
            preparation.CaptureSources(factory, graphics, false);
            var request = ++_sourcePreparationRequest;
            _sourcePreparationFactory = factory;
            _sourcePreparation = PrepareLiveSources(factory, graphics, preparation, request);
            try { _sourcePreparationCoroutine = factory.StartCoroutine(_sourcePreparation); }
            catch { CancelSourcePreparation(); preparation.Dispose(); throw; }
            DeferRefresh();
            return false;
        }

        private IEnumerator PrepareLiveSources(GraphicsCharacterFactoryManager factory, GraphicsCharacter graphics,
            CharacterModelCatalog.PreparedSources preparation, int request)
        {
            var requestedSources = preparation;
            bool Current() => this && factory && graphics && isActiveAndEnabled && !_characterModelsUnloading &&
                              SettingsContext.GuiModManagerInstance.EnableCustomFaces && !IsInitializing &&
                              request == _sourcePreparationRequest && ReferenceEquals(_graphics, graphics) &&
                              !factory.IsAbortingGraphicsCharacter(graphics) && requestedSources.SourcesCurrent(factory, graphics, false);
            IEnumerator operation = null;
            try
            {
                // Ensure StartCoroutine has returned its handle before completion can publish.
                yield return null;
                if (!Current()) yield break;
                CharacterModelTextureReadback.SetEnabled(true);
                operation = ObserveCharacterModelPreparation(PrepareCharacterModelAssets(factory, graphics, preparation, Current, false), () => requestedSources.Failed = true);
                while (Current() && operation.MoveNext()) yield return operation.Current;
                if (!Current()) yield break;
                if (preparation.Completed && !preparation.Failed)
                {
                    _preparedSources = preparation;
                    preparation = null;
                    DeferRefresh();
                }
                else
                {
                    // Keep failed-source metadata, not its leases, until the source changes.
                    _sourcePreparationReceipt = preparation;
                    _refreshPending = false;
                }
            }
            finally
            {
                try { (operation as IDisposable)?.Dispose(); }
                finally
                {
                    try { preparation?.Dispose(); }
                    finally
                    {
                        if (request == _sourcePreparationRequest)
                        {
                            _sourcePreparation = null; _sourcePreparationCoroutine = null; _sourcePreparationFactory = null;
                            if (!this || !factory || !graphics || !isActiveAndEnabled || _characterModelsUnloading ||
                                !SettingsContext.GuiModManagerInstance.EnableCustomFaces || IsInitializing ||
                                graphics.RulesetCharacter == null || factory.IsAbortingGraphicsCharacter(graphics))
                            {
                                _refreshPending = false;
                                if (!HasSources && _preparedSources == null) CharacterModelOwners.Remove(this);
                            }
                            SynchronizeCharacterModelRefreshPending();
                        }
                    }
                }
            }
            // Outgame has no world-ready Update drain. Publish through the normal
            // owner path only after this factory job has released its running scope.
            if (ReferenceEquals(_preparedSources, requestedSources) && requestedSources.Completed &&
                !requestedSources.Failed && Current()) RefreshCharacterModels(graphics);
        }

        internal void DeferRefresh()
        {
            _refreshPending = _characterModelRefreshPending = true;
            CharacterModelOwners.Add(this);
        }

        internal void RefreshPending()
        {
            if (!_refreshPending) return;
            RefreshCharacterModels(_graphics);
            if (_refreshPending) _characterModelRefreshPending = true;
        }

        internal void BeginMaterialRefresh()
        {
            if (_materialRefreshDepth++ == 0) _materialRefreshFailed = false;
        }

        internal void EndMaterialRefresh(bool failed)
        {
            if (_materialRefreshDepth <= 0)
                throw new InvalidOperationException("Custom material refresh has no matching owner.");
            _materialRefreshFailed |= failed;
            if (--_materialRefreshDepth != 0) return;
            var pending = _materialRefreshPending;
            _materialRefreshPending = false;
            if (pending && !_materialRefreshFailed) RefreshMaterials();
        }

        private bool TryPreparedJoint(GraphicsCharacter graphics, CharacterModelSkin head, CharacterModelSkin body,
            Dictionary<SkinnedMeshRenderer, CustomModels.SkinMeshSource.SurfaceBinding> bindings,
            GraphicsCharacterDefinitions.BodyPart donorPart, Mesh headBasis, Mesh bodyBasis)
        {
            if (_catalog == null || !_catalog.HasJointAttachments(head.Fingerprint) || head.Source.ClothOwner || body.Source.ClothOwner) return false;
            try
            {
                var selected = _catalog.JointForPart(head.Fingerprint, body.Fingerprint, headBasis, bodyBasis, donorPart);
                var declaration = selected.Declaration; var target = selected.Mesh; if (declaration == null || !target || !JointPeriodicSamplers(head.Renderer)) return false;
                var hb = head.Renderer.bones; var bb = body.Renderer.bones;
                var hp = headBasis.bindposes; var bp = bodyBasis.bindposes;
                if (hb.Length != hp.Length || bb.Length != bp.Length) return false;
                var frame = new Matrix4x4(); for (var i = 0; i < 16; i++) frame[i] = declaration.headToBody[i];
                var palette = hb.Concat(declaration.donorBones.Select(i => bb[i])).ToArray();
                var common = 0;
                for (var i = 0; i < declaration.bodyToHead.Length; i++)
                {
                    var index = declaration.bodyToHead[i]; if (index < 0) continue;
                    if (index >= palette.Length || !bb[i] || !ReferenceEquals(palette[index], bb[i]))
                        throw new InvalidOperationException("A coupled joint's live bone reference differs from its source palette index.");
                    if (index < hp.Length)
                    {
                        if (!JointPose(hp[index], bp[i] * frame)) throw new InvalidOperationException("A coupled joint has a different live common bind frame.");
                        common++;
                    }
                }
                if (common == 0 || palette.Any(v => !v)) return false;
                var palettePoses = target.bindposes;
                if (palettePoses.Length != palette.Length) return false;
                // One live transform can have different native bind poses in the two
                // source meshes. Their distinct skinning bindings need separate slots.
                for (var index = 0; index < palette.Length; index++)
                for (var previous = 0; previous < index; previous++)
                    if (ReferenceEquals(palette[index], palette[previous]) &&
                        JointPose(palettePoses[index], palettePoses[previous])) return false;
                // Protected native samples may border the owned cut, but may not be
                // consumed inside its removed patch. PN samples are derived geometry.
                var native = ReadBodyShapeRest(headBasis);
                var original = ReadBodyShapeRest(head.Source.Original);
                var protectedBoundary = new BodyShapeCut(original.Vertices, original.Triangles).Points.ToHashSet();
                var removed = declaration.removedTriangles.ToHashSet();
                var retained = native.Triangles.Where((_, index) => !removed.Contains(index / 3)).ToArray();
                protectedBoundary.UnionWith(new BodyShapeCut(native.Vertices, retained).Points);
                foreach (var triangle in declaration.removedTriangles)
                for (var corner = 0; corner < 3; corner++)
                {
                    var vertex = native.Triangles[triangle * 3 + corner];
                    if (vertex >= original.Vertices.Length || protectedBoundary.Contains(native.Vertices[vertex])) continue;
                    var weight = native.Weights[vertex];
                    for (var slot = 0; slot < 4; slot++)
                        if (BodyShapeWeight(weight, slot) > 0f && IsProtectedBodyBone(hb[BodyShapeWeightIndex(weight, slot)]))
                            throw new InvalidOperationException("A coupled joint consumes a protected native articulation.");
                }
                var count = headBasis.vertexCount; var normals = target.normals; var tangents = target.tangents;
                var canonical = ReadJointCanonicalWeights(target);
                bindings.TryGetValue(head.Renderer, out var headBinding);
                headBinding ??= _catalog.BaseBinding(head.Source.Original, head.Fingerprint);
                if (headBinding != null && headBinding.Vertices.Length != count ||
                    count != head.Source.Original.vertexCount && headBinding == null)
                    throw new InvalidOperationException("A coupled receiver has no complete native-to-authored base binding.");
                var vertices = new CustomModels.SkinMeshSource.SurfaceVertex[target.vertexCount];
                var overrides = new Dictionary<int, (BoneWeight Weights, Vector3 Normal, Vector4 Tangent)>();
                for (var i = 0; i < vertices.Length; i++)
                {
                    var origin = declaration.origins[i]; var anchor = origin >= 0 ? origin : 0;
                    vertices[i] = origin >= 0 && headBinding != null ? headBinding.Vertices[origin] :
                        new CustomModels.SkinMeshSource.SurfaceVertex(anchor, Vector3.zero, Vector3.forward, Vector3.right, origin >= 0);
                    if (i >= count && vertices[i].Native && vertices[i].Sources != null)
                    {
                        var source = vertices[i];
                        vertices[i] = new CustomModels.SkinMeshSource.SurfaceVertex(source.Anchor, source.Offset,
                            source.Normal, source.Tangent, false, source.Sources, source.TangentW, source.SourceUv,
                            source.SourceSlot, source.SurfaceTriangle, source.SurfaceBary, source.GeometryTriangle, source.GeometryBary);
                    }
                    if (i >= count) overrides.Add(i, (canonical[i], normals[i], tangents[i]));
                }
                var additions = declaration.donorBones.Select(i => (body.Renderer, body.Source, i, frame)).ToArray();
                var identity = "skin-joint:coupled:1:" + head.Fingerprint + ":" + body.Fingerprint + ":" +
                    declaration.headFingerprint + ":" + declaration.bodyFingerprint + ":" + declaration.authoredFingerprint;
                var attachment = new CustomModels.SkinMeshSource.SurfaceBinding(identity, Matrix4x4.identity,
                    headBinding?.SourceTangents ?? ReadNativeRestMesh(head.Source.Original, m => m.tangents), vertices, target, overrides,
                    paletteAdditions: additions, paletteSource: (body.Renderer, body.Source, frame, declaration.bodyToHead), skinEncodingSource: headBinding, authoredBase: headBinding?.AuthoredBase);
                _catalog.RetainBundle(_packs, declaration.bundleFile);
                bindings[head.Renderer] = attachment;
                head.SetAuthored(target);
                return true;
            }
            catch (Exception e) { ReportCharacterModelError("joint-presentation", e); return false; }
        }
        private static BoneWeight[] ReadJointCanonicalWeights(Mesh mesh)
        {
            var canonical = CustomModels.SkinMeshSource.ReadSkinWeights(mesh, Enumerable.Range(0, mesh.vertexCount));
            return Enumerable.Range(0, mesh.vertexCount).Select(index => canonical[index]).ToArray();
        }

        private void PrepareJoints(GraphicsCharacter graphics,
            Dictionary<SkinnedMeshRenderer, CustomModels.SkinMeshSource.SurfaceBinding> bindings)
        {
            const float weld = 0.0001f;
            var slots = graphics.ShapeSkinnedMeshRenderersPerBodySlot;
            if (slots == null) return;
            var heads = Part(slots, GraphicsCharacterDefinitions.BodyPart.Head).ToHashSet();
            var bodies = Part(slots, GraphicsCharacterDefinitions.BodyPart.Body)
                .Concat(Part(graphics.ArmorSkinnedMeshRenderersPerBodySlot, GraphicsCharacterDefinitions.BodyPart.Body)).ToHashSet();
            var lodGroup = AccessTools.Field(graphics.GetType(), "sharedLodGroup")?.GetValue(graphics) as LODGroup;
            var lods = lodGroup ? lodGroup.GetLODs() : [];
            var changes = new Dictionary<CharacterModelSkin, Dictionary<int,
                (BoneWeight Weights, Vector3 Normal, Vector4 Tangent, Vector3? DeltaNormal, Vector3? DeltaTangent)>>();
            var positions = new Dictionary<CharacterModelSkin, Dictionary<int,
                (Vector3 Original, Vector3 Position, float MaximumDistance)>>();
            var blendShapePositions = new Dictionary<CharacterModelSkin, Dictionary<int, Vector3>>();
            var dependencies = new List<string>();
            var coupled = new HashSet<CharacterModelSkin>();
            // Resolve every declaration against the immutable initial authored bases.
            // A Body receiver may also be the donor of a Head joint in this refresh.
            var jointBases = _skins.ToDictionary(skin => skin, skin => skin.Authored);
            foreach (var receiver in _skins.Where(skin => _catalog?.HasJointAttachments(skin.Fingerprint) == true && IsJointSkin(skin.Renderer)))
            foreach (var donorPart in _catalog.JointDonorParts(receiver.Fingerprint))
            {
                var donors = Part(slots, donorPart).Concat(Part(graphics.ArmorSkinnedMeshRenderersPerBodySlot, donorPart)).ToHashSet();
                foreach (var donor in _skins.Where(skin => skin != receiver && donors.Contains(skin.Renderer) && IsJointSkin(skin.Renderer)))
                {
                    if (coupled.Contains(receiver)) break;
                    if (lods.Length != 0 && !lods.Any(lod => lod.renderers.Contains(receiver.Renderer) && lod.renderers.Contains(donor.Renderer))) continue;
                    if (TryPreparedJoint(graphics, receiver, donor, bindings, donorPart, jointBases[receiver], jointBases[donor])) coupled.Add(receiver);
                }
            }
            foreach (var head in _skins.Where(skin => heads.Contains(skin.Renderer) && IsJointSkin(skin.Renderer)))
            foreach (var body in _skins.Where(skin => bodies.Contains(skin.Renderer) && skin != head && IsJointSkin(skin.Renderer)))
            {
                if (lods.Length != 0 && !lods.Any(lod => lod.renderers.Contains(head.Renderer) && lod.renderers.Contains(body.Renderer))) continue;
                if (coupled.Contains(head)) continue;
                // Retopology requires its original source map. A joint must not
                // invent authored-count identity anchors for a smaller native source.
                if (head.Authored.vertexCount != head.Source.Original.vertexCount && !bindings.ContainsKey(head.Renderer) ||
                    body.Authored.vertexCount != body.Source.Original.vertexCount && !bindings.ContainsKey(body.Renderer)) continue;
                var hBones = head.Renderer.bones; var bBones = body.Renderer.bones;
                var hRest = ReadBodyShapeRest(head.Authored); var bRest = ReadBodyShapeRest(body.Authored);
                if (hBones.Length < hRest.BindPoses.Length || bBones.Length < bRest.BindPoses.Length) continue;
                var bToH = Enumerable.Range(0, bRest.BindPoses.Length).Select(index => bBones[index] ? Array.IndexOf(hBones, bBones[index]) : -1).ToArray();
                var hToB = Enumerable.Range(0, hRest.BindPoses.Length).Select(index => hBones[index] ? Array.IndexOf(bBones, hBones[index]) : -1).ToArray();
                var anchor = Array.FindIndex(bToH, index => index >= 0 && index < hRest.BindPoses.Length);
                if (anchor < 0) continue;
                var hToBody = bRest.BindPoses[anchor].inverse * hRest.BindPoses[bToH[anchor]];
                if (Enumerable.Range(0, bToH.Length).Any(index => bToH[index] >= 0 &&
                    !SameMatrix(hRest.BindPoses[bToH[index]], bRest.BindPoses[index] * hToBody))) continue;
                var normalToHead = hToBody.transpose;
                var toHead = hToBody.inverse;
                var headDistanceScale = Mathf.Sqrt(((Vector3)toHead.GetColumn(0)).sqrMagnitude +
                    ((Vector3)toHead.GetColumn(1)).sqrMagnitude + ((Vector3)toHead.GetColumn(2)).sqrMagnitude);
                var paired = false;
                foreach (var correspondence in BodyShapeCut.Match(hRest, bRest, hToBody, weld))
                {
                    var vertex = correspondence.Key;
                    var matches = correspondence.Value.Vertices;
                    var first = matches[0];
                    // Prefer the body's existing influences; both palettes must already contain every selected bone.
                    BoneWeight hWeight, bWeight;
                    if (Remap(bRest.Weights[first], bToH, hRest.BindPoses.Length, out hWeight)) bWeight = bRest.Weights[first];
                    else if (Remap(hRest.Weights[vertex], hToB, bRest.BindPoses.Length, out bWeight)) hWeight = hRest.Weights[vertex];
                    else continue;
                    var bNormal = bRest.Normals[first];
                    var hNormal = normalToHead.MultiplyVector(bNormal);
                    var hDelta = hRest.Muscular == null ? (Vector3?)null : hRest.MuscularNormals[vertex];
                    var bDelta = bRest.Muscular == null ? (Vector3?)null : bRest.MuscularNormals[first];
                    if (hDelta.HasValue && bDelta.HasValue) hDelta = normalToHead.MultiplyVector(bDelta.Value);
                    Add(head, vertex, hWeight, hNormal, hDelta);
                    if (hRest.Muscular != null && bRest.Muscular != null)
                    {
                        var muscular = toHead.MultiplyVector(bRest.Muscular[first]);
                        if (!muscular.Equals(hRest.Muscular[vertex]))
                        {
                            if (!blendShapePositions.TryGetValue(head, out var overrides)) blendShapePositions.Add(head, overrides = []);
                            if (overrides.TryGetValue(vertex, out var old))
                            {
                                if ((old - muscular).sqrMagnitude > weld * weld * headDistanceScale * headDistanceScale)
                                    throw new InvalidOperationException("A shared skin boundary has conflicting adjacent muscle positions.");
                            }
                            else overrides.Add(vertex, muscular);
                        }
                    }
                    var position = toHead.MultiplyPoint3x4(bRest.Vertices[first]);
                    if (!position.Equals(hRest.Vertices[vertex]))
                    {
                        if (!positions.TryGetValue(head, out var overrides)) positions.Add(head, overrides = []);
                        var declaration = (hRest.Vertices[vertex], position, correspondence.Value.MaximumDistance * headDistanceScale);
                        if (overrides.TryGetValue(vertex, out var old))
                        {
                            if ((old.Position - position).sqrMagnitude > weld * weld * headDistanceScale * headDistanceScale)
                                throw new InvalidOperationException("A shared skin boundary has conflicting adjacent positions.");
                        }
                        else overrides.Add(vertex, declaration);
                    }
                    foreach (var target in matches) Add(body, target, bWeight, bNormal, bDelta);
                    paired = true;
                }
                if (paired) dependencies.Add(head.Fingerprint + ":" + CharacterModelFingerprint(head.Authored) + ":" +
                    body.Fingerprint + ":" + CharacterModelFingerprint(body.Authored) + ":" + string.Join(",", bToH) + ":" + string.Join(",", hToB));
            }
            if (dependencies.Count == 0) return;
            var dependency = string.Join("|", dependencies.OrderBy(value => value, StringComparer.Ordinal));
            foreach (var row in changes)
            {
                var skin = row.Key;
                var baseline = skin.Authored;
                var rest = ReadBodyShapeRest(baseline);
                positions.TryGetValue(skin, out var positionOverrides);
                blendShapePositions.TryGetValue(skin, out var blendShapePositionOverrides);
                if (positionOverrides == null && blendShapePositionOverrides == null && !row.Value.Any(pair => !rest.Weights[pair.Key].Equals(pair.Value.Weights) ||
                    !rest.Normals[pair.Key].Equals(pair.Value.Normal) || rest.Tangents.Length == rest.Vertices.Length && !rest.Tangents[pair.Key].Equals(pair.Value.Tangent) ||
                    pair.Value.DeltaNormal.HasValue && !rest.MuscularNormals[pair.Key].Equals(pair.Value.DeltaNormal.Value) ||
                    pair.Value.DeltaTangent.HasValue && !rest.MuscularTangents[pair.Key].Equals(pair.Value.DeltaTangent.Value))) continue;
                var retained = RetainDerivedData("skin-joint:3:" + skin.Fingerprint + ":" + CharacterModelFingerprint(baseline) + ":" + dependency, () =>
                {
                    Mesh readable = null, owned = null;
                    try
                    {
                        readable = baseline.isReadable ? baseline : ReadNativeRestMesh(baseline, native =>
                        {
                            var clone = UnityEngine.Object.Instantiate(native);
                            clone.name = baseline.name;
                            clone.bounds = baseline.bounds;
                            clone.bindposes = baseline.bindposes;
                            clone.boneWeights = baseline.boneWeights;
                            CopyFrames(baseline, clone, null);
                            return clone;
                        });
                        owned = UnityEngine.Object.Instantiate(readable);
                        owned.name = baseline.name;
                        var weights = readable.boneWeights; var normals = readable.normals; var tangents = readable.tangents;
                        foreach (var pair in row.Value)
                        {
                            weights[pair.Key] = pair.Value.Weights; normals[pair.Key] = pair.Value.Normal;
                            if (tangents.Length == weights.Length) tangents[pair.Key] = pair.Value.Tangent;
                        }
                        owned.boneWeights = weights; owned.normals = normals;
                        if (tangents.Length == weights.Length) owned.tangents = tangents;
                        if (positionOverrides != null)
                        {
                            var positionArray = readable.vertices;
                            var bounds = owned.bounds;
                            foreach (var pair in positionOverrides)
                            { positionArray[pair.Key] = pair.Value.Position; bounds.Encapsulate(pair.Value.Position); }
                            owned.vertices = positionArray;
                            owned.bounds = bounds;
                        }
                        CopyFrames(readable, owned, row.Value, blendShapePositionOverrides);
                        CustomModels.SkinMeshSource.VerifySkinEncoding(owned, row.Value.ToDictionary(pair => pair.Key, pair => pair.Value.Weights));
                        return baseline.isReadable ? [owned] : [owned, readable];
                    }
                    catch
                    {
                        if (owned) UnityEngine.Object.Destroy(owned);
                        if (readable && readable != baseline) UnityEngine.Object.Destroy(readable);
                        throw;
                    }
                });
                var replacement = (Mesh)retained[0];
                var boundarySource = baseline.isReadable ? baseline : (Mesh)retained[1];
                bindings.TryGetValue(skin.Renderer, out var binding);
                binding ??= _catalog?.BaseBinding(skin.Source.Original, skin.Fingerprint);
                var vertices = binding?.Vertices ?? Enumerable.Range(0, baseline.vertexCount)
                    .Select(index => new CustomModels.SkinMeshSource.SurfaceVertex(index, Vector3.zero, Vector3.forward, Vector3.right, true)).ToArray();
                var canonicalWeights = CustomModels.SkinMeshSource.ReadSkinWeights(replacement, row.Value.Keys);
                var declared = row.Value.ToDictionary(pair => pair.Key,
                    pair => (canonicalWeights[pair.Key], pair.Value.Normal, pair.Value.Tangent));
                var declaredBlendShapePositions = blendShapePositionOverrides?.ToDictionary(pair => pair.Key,
                    pair => (ReadBodyShapeRest(boundarySource).Muscular[pair.Key], pair.Value));
                bindings[skin.Renderer] = new CustomModels.SkinMeshSource.SurfaceBinding(binding?.Fingerprint ?? skin.Fingerprint,
                    binding?.RendererToPrefab ?? Matrix4x4.identity, binding?.SourceTangents ?? ReadNativeRestMesh(skin.Source.Original, mesh => mesh.tangents), vertices,
                    boundarySource, declared, "Muscular", positionOverrides, null, declaredBlendShapePositions, skinEncodingSource: binding, authoredBase: binding?.AuthoredBase);
                skin.SetAuthored(replacement);
            }
            return;

            static IEnumerable<SkinnedMeshRenderer> Part(SkinnedMeshRenderer[][] parts, GraphicsCharacterDefinitions.BodyPart part) =>
                parts != null && (int)part < parts.Length ? parts[(int)part] ?? [] : [];
            static bool SameMatrix(Matrix4x4 first, Matrix4x4 second) => Enumerable.Range(0, 16).All(index =>
                BodyShapeFinite(first[index]) && BodyShapeFinite(second[index]) && Mathf.Abs(first[index] - second[index]) <= weld);
            static bool Remap(BoneWeight source, int[] indices, int targetCount, out BoneWeight result)
            {
                result = default;
                var mapped = new int[4];
                for (var influence = 0; influence < 4; influence++)
                {
                    if (BodyShapeWeight(source, influence) <= 0f) continue;
                    var index = BodyShapeWeightIndex(source, influence);
                    if (index < 0 || index >= indices.Length || indices[index] < 0 || indices[index] >= targetCount) return false;
                    mapped[influence] = indices[index];
                }
                result = new BoneWeight { boneIndex0 = mapped[0], boneIndex1 = mapped[1], boneIndex2 = mapped[2], boneIndex3 = mapped[3],
                    weight0 = source.weight0, weight1 = source.weight1, weight2 = source.weight2, weight3 = source.weight3 };
                return true;
            }
            void Add(CharacterModelSkin skin, int vertex, BoneWeight weight, Vector3 normal, Vector3? deltaNormal)
            {
                if (!changes.TryGetValue(skin, out var values)) changes.Add(skin, values = []);
                var rest = ReadBodyShapeRest(skin.Authored);
                var tangent = rest.Tangents.Length == rest.Vertices.Length ? rest.Tangents[vertex] : Vector4.zero;
                var oldTangent = (Vector3)tangent;
                var newTangent = Quaternion.FromToRotation(rest.Normals[vertex], normal) * oldTangent;
                tangent = new Vector4(newTangent.x, newTangent.y, newTangent.z, tangent.w);
                Vector3? deltaTangent = null;
                if (deltaNormal.HasValue && rest.Muscular != null)
                    deltaTangent = Quaternion.FromToRotation(rest.Normals[vertex] + rest.MuscularNormals[vertex], normal + deltaNormal.Value) *
                        (oldTangent + rest.MuscularTangents[vertex]) - newTangent;
                if (values.TryGetValue(vertex, out var existing))
                {
                    if (!existing.Weights.Equals(weight) || (existing.Normal - normal).sqrMagnitude > weld * weld ||
                        existing.DeltaNormal.HasValue != deltaNormal.HasValue || deltaNormal.HasValue &&
                        (existing.DeltaNormal.Value - deltaNormal.Value).sqrMagnitude > weld * weld)
                        throw new InvalidOperationException("A shared skin boundary has conflicting adjacent surface attributes.");
                    return;
                }
                values.Add(vertex, (weight, normal, tangent, deltaNormal, deltaTangent));
            }


            static void CopyFrames(Mesh source, Mesh target, Dictionary<int,
                (BoneWeight Weights, Vector3 Normal, Vector4 Tangent, Vector3? DeltaNormal, Vector3? DeltaTangent)> overrides,
                IReadOnlyDictionary<int, Vector3> musclePositions = null)
            {
                target.ClearBlendShapes();
                var positions = new Vector3[source.vertexCount]; var normals = new Vector3[source.vertexCount]; var tangents = new Vector3[source.vertexCount];
                for (var shape = 0; shape < source.blendShapeCount; shape++)
                for (var frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                {
                    var name = source.GetBlendShapeName(shape); var value = source.GetBlendShapeFrameWeight(shape, frame);
                    source.GetBlendShapeFrameVertices(shape, frame, positions, normals, tangents);
                    if (name == "Muscular" && overrides != null) foreach (var pair in overrides)
                    {
                        if (pair.Value.DeltaNormal.HasValue) normals[pair.Key] = pair.Value.DeltaNormal.Value * (value / 100f);
                        if (pair.Value.DeltaTangent.HasValue) tangents[pair.Key] = pair.Value.DeltaTangent.Value * (value / 100f);
                    }
                    if (name == "Muscular" && musclePositions != null)
                        foreach (var pair in musclePositions) positions[pair.Key] = pair.Value * (value / 100f);
                    target.AddBlendShapeFrame(name, value, positions, normals, tangents);
                }
            }
        }

        internal void Refresh(GraphicsCharacter graphics, CharacterModelCatalog catalog)
        {
            if (_busy || IsInitializing || !isActiveAndEnabled) return;
            if (!EnsurePreparedSources(graphics, catalog)) return;
            var published = false;
            var preparedSources = _preparedSources; _preparedSources = null;
            _busy = true;
            _refreshPending = false;
            CharacterModelOwners.Add(this);
            try
            {
                var renderers = CharacterModelRenderers(graphics);
                if (renderers.Count == 0)
                {
                    if (HasSources) Release(false, true);
                    else CharacterModelOwners.Remove(this);
                    return;
                }
                var sources = CustomModels.SkinMeshSource.Capture(graphics, renderers);
                if (sources == null) throw new InvalidOperationException("Native face source ownership could not be resolved.");
                if (preparedSources == null && ReferenceEquals(_actor, graphics.RulesetCharacter) && _catalog == catalog &&
                    renderers.Count == _skins.Count && _skins.All(skin => sources.TryGetValue(skin.Renderer, out var source) && skin.IsCurrent(graphics, source)))
                {
                    RefreshMaterials();
                    return;
                }
                Release(false, false);
                if (HasSources) throw new InvalidOperationException("Previous custom character model sources have not finished restoring.");
                // Release removes the previous registration before this owner acquires new sources.
                CharacterModelOwners.Add(this);
                _actor = graphics.RulesetCharacter;
                _catalog = catalog;
                ReleaseBodyShape(graphics);
                if (graphics.GetComponent<BodyShapePresentation>() is { HasSources: true })
                    throw new InvalidOperationException("Body overlays have not finished restoring before a face replacement.");
                sources = CustomModels.SkinMeshSource.Capture(graphics, renderers);
                if (sources == null) throw new InvalidOperationException("Restored native character model sources could not be resolved.");
                // Resolve and validate every source before changing any renderer.
                foreach (var renderer in renderers)
                {
                    var source = sources[renderer];
                    var fingerprint = CharacterModelFingerprint(source.Original);
                    string bundleFile = null;
                    var authored = catalog?.Mesh(source.Original, fingerprint, _packs, out bundleFile) ?? source.Original;
                    _skins.Add(new CharacterModelSkin(graphics, renderer, source, fingerprint, authored, bundleFile, catalog?.HasJointAttachments(fingerprint) == true));
                }
                var bindings = new Dictionary<SkinnedMeshRenderer, CustomModels.SkinMeshSource.SurfaceBinding>();
                foreach (var skin in _skins.Where(skin => skin.ChangesSource))
                    if (_catalog?.Binding(skin.Source.Original, skin.Fingerprint) is { } binding) bindings.Add(skin.Renderer, binding);
                PrepareJoints(graphics, bindings);
                foreach (var skin in _skins) skin.ValidateSource(bindings.ContainsKey(skin.Renderer));
                var changes = _skins.Where(skin => skin.ChangesSource).ToDictionary(skin => skin.Renderer,
                    skin => (skin.Source, skin.Authored));
                if (changes.Count != 0)
                {
                    _catalog?.RetainNativeSources(_graphics, _nativeSources, _requiredNativeSources);
                    _sourceGraph = CustomModels.SkinMeshSource.PrepareMeshes(graphics, changes, bindings,
                        RetainDerivedData, _catalog == null ? null : _catalog.NativeDataFingerprint,
                        _catalog == null ? null : _catalog.PreparedSurfacePacket);
                    _sourceGraph.Apply();
                }
                foreach (var skin in _skins) skin.Apply(_sourceGraph?.Installed);
                RefreshMaterials();
                RefreshBodyShape(graphics);
                published = true;
            }
            catch
            {
                Release(false, false);
                if (_graphics) RefreshBodyShape(_graphics);
                throw;
            }
            finally
            {
                _busy = false;
                try
                {
                    if (published && preparedSources != null) _sourcePreparationReceipt = preparedSources;
                    preparedSources?.Dispose();
                }
                finally { SynchronizeCharacterModelRefreshPending(); }
            }
        }

        internal void RefreshMaterials()
        {
            if (IsInitializing || _suspended || !isActiveAndEnabled || !_graphics || _catalog == null) return;
            if (_materialRefreshDepth != 0)
            {
                _materialRefreshPending = true;
                return;
            }
            var renderers = new HashSet<Renderer>(_graphics.Renderers ?? []);
            var nativeMaterials = NativeCharacterModelMaterialOwners(_graphics);
            var declarations = new Dictionary<Material, (CharacterModelMaterialAsset Declaration, Shader Shader, CharacterModelMaterial.NativeProfile Native, string Body)>();
            Dictionary<Renderer, string> bodyContexts = null;
            var nativeProfiles = new Dictionary<Material, CharacterModelMaterial.NativeProfile>();
            foreach (var skin in _skins)
            {
                if (!skin.Renderer || !renderers.Contains(skin.Renderer)) continue;
                var materials = skin.Renderer.sharedMaterials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (!material || !nativeMaterials.Contains(material)) continue;
                    CharacterModelMaterial.TryGet(material, out var existing);
                    var shader = existing?.SourceShader ?? material.shader;
                    var declaration = _catalog.Material(skin.Fingerprint, slot, shader);
                    if (declaration == null) continue;
                    if (_graphics.EyeMaterials.Contains(material) && declaration.properties.Any(property => property.name == "_MainTex" && property.kind == "texture"))
                        throw new InvalidOperationException("A custom appearance cannot replace a native eye-style texture.");
                    var body = _catalog.HasBodyTextureConditions && declaration.UsesBodyTexture ? BodyTextureFingerprint(skin.Renderer, bodyContexts ??= []) : null;
                    if (declarations.TryGetValue(material, out var previous) &&
                        !CharacterModelMaterial.SameSelection(material, shader, _catalog, previous.Declaration, previous.Body, declaration, body))
                        throw new InvalidOperationException("A shared native character model material has conflicting authored properties.");
                    CharacterModelMaterial.NativeProfile native = null;
                    if (declaration.authoredProfile != null)
                        if (!nativeProfiles.TryGetValue(material, out native))
                            nativeProfiles.Add(material, native = CharacterModelMaterial.CaptureNativeProfile(material));
                    declarations[material] = (declaration, shader, native, body);
                }
            }
            // Geometry and material scope differ: body surfaces can own separate skin atlases.
            // Explicit mesh-slot profiles take precedence over shared texture declarations.
            foreach (var renderer in renderers)
            {
                if (!renderer) continue;
                var materials = renderer.sharedMaterials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (!material || !nativeMaterials.Contains(material) || declarations.ContainsKey(material)) continue;
                    CharacterModelMaterial.TryGet(material, out var existing);
                    var shader = existing?.SourceShader ?? material.shader;
                    if (_catalog.Material(null, slot, shader) is { } declaration)
                        declarations.Add(material, (declaration, shader, null, _catalog.HasBodyTextureConditions && declaration.UsesBodyTexture ? BodyTextureFingerprint(renderer, bodyContexts ??= []) : null));
                }
            }
            if (declarations.Count != 0) _catalog.RetainNativeSources(_graphics, _nativeSources, _requiredNativeSources);
            else _catalog.ReleaseNativeSources(_nativeSources);
            var requiredPacks = new HashSet<string>(_skins.Select(skin => skin.BundleFile).Where(file => file != null), StringComparer.Ordinal);
            foreach (var owner in _materials.ToArray())
            {
                if (declarations.TryGetValue(owner.Material, out var current) && CharacterModelMaterial.SameDeclaration(owner.Declaration, current.Declaration)) continue;
                owner.Restore();
                _materials.Remove(owner);
            }
            foreach (var row in declarations)
            {
                var owner = _materials.FirstOrDefault(item => item.Material == row.Key);
                if (owner == null)
                {
                    owner = CharacterModelMaterial.Acquire(row.Key, row.Value.Shader, row.Value.Declaration, _catalog, row.Value.Native, row.Value.Body);
                    _materials.Add(owner);
                }
                owner.Apply(row.Value.Body);
            }
            _sourceGraph?.RefreshNativeSurfaceMaterials();
            _catalog.ReleaseBundles(_packs, requiredPacks);
        }

        private string BodyTextureFingerprint(Renderer target, Dictionary<Renderer, string> resolved)
        {
            if (!_catalog.HasBodyTextureConditions) return null;
            if (resolved.TryGetValue(target, out var cached)) return cached;
            var group = AccessTools.Field(_graphics.GetType(), "sharedLodGroup")?.GetValue(_graphics) as LODGroup;
            var lods = group ? group.GetLODs() : [];
            var id = Shader.PropertyToID("_MainTex");
            string fingerprint = null;
            var ambiguous = false;
            foreach (var slots in new[] { _graphics.ShapeSkinnedMeshRenderersPerBodySlot, _graphics.ArmorSkinnedMeshRenderersPerBodySlot })
            {
                if (slots == null) continue;
                for (var index = 0; index < slots.Length; index++)
                {
                    if (index == (int)GraphicsCharacterDefinitions.BodyPart.Head || slots[index] == null) continue;
                    foreach (var renderer in slots[index])
                    {
                        if (!renderer ||
                            lods.Length != 0 && !lods.Any(lod => lod.renderers.Contains(target) && lod.renderers.Contains(renderer))) continue;
                        foreach (var material in renderer.sharedMaterials)
                        {
                            if (!material) continue;
                            CharacterModelMaterial.TryGet(material, out var owner);
                            var shader = owner?.SourceShader ?? material.shader;
                            if (!shader || !shader.name.StartsWith("Lux/Human/Skin", StringComparison.Ordinal) || !material.HasProperty(id)) continue;
                            var texture = CharacterModelMaterial.NativeTexture(material, id);
                            if (!texture || texture.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) continue;
                            var current = CharacterModelTextureFingerprint(texture);
                            // An exposed limb is a body context only by exact content
                            // equality with a body source registered by this catalog.
                            if (!_catalog.IsBodyTextureFingerprint(current)) continue;
                            if (fingerprint != null && fingerprint != current) ambiguous = true;
                            fingerprint = current;
                        }
                    }
                }
            }
            resolved.Add(target, ambiguous ? null : fingerprint);
            return ambiguous ? null : fingerprint;
        }

        internal void Suspend()
        {
            CancelSourcePreparation();
            if (++_initializationDepth != 1) return;
            _initializationFailed = false;
            if (!HasSources) return;
            _suspended = true;
            try
            {
                if (_graphics && _graphics.GetComponent<BodyShapePresentation>() is { HasSources: true })
                    throw new InvalidOperationException("Body overlays still reference a custom character model source before animator initialization.");
                // Pin assets only until this synchronous native initialization has completed.
                foreach (var owner in _materials) owner.RetainBundles(_packs);
                foreach (var owner in _materials) owner.Restore();
                _materials.Clear();
                _sourceGraph?.Restore(true);
                foreach (var skin in _skins)
                {
                    skin.SourceRestored();
                    skin.Restore(false);
                }
            }
            catch (Exception failure)
            {
                _initializationDepth--;
                try { Release(false, false); }
                catch (Exception rollback) { throw new AggregateException("Custom appearance suspension and rollback failed.", failure, rollback); }
                throw;
            }
        }

        internal void Resume(bool failed)
        {
            if (_initializationDepth <= 0)
                throw new InvalidOperationException("Custom character model initialization has no matching suspend owner.");
            _initializationFailed |= failed;
            if (--_initializationDepth != 0) return;
            if (_initializationFailed || _characterModelsUnloading || !isActiveAndEnabled || !_graphics ||
                !UsesCharacterModelPresentation(_graphics))
            {
                Release(false, false);
                return;
            }
            if (!_suspended)
            {
                RefreshCharacterModels(_graphics);
                return;
            }
            _busy = true;
            try
            {
                var renderers = CharacterModelRenderers(_graphics);
                var sources = CustomModels.SkinMeshSource.Capture(_graphics, renderers);
                var same = ReferenceEquals(_actor, _graphics.RulesetCharacter) &&
                    ReferenceEquals(_catalog, SettingsContext.GuiModManagerInstance.EnableCustomFaces ? _characterModelCatalog : null) && sources != null &&
                    renderers.Count == _skins.Count && _skins.All(skin =>
                        sources.TryGetValue(skin.Renderer, out var source) && skin.IsNativeCurrent(source)) &&
                    (_sourceGraph == null || _sourceGraph.CanReapply());
                if (!same)
                {
                    ReleaseCore(false, false, true);
                    _busy = false;
                    RefreshCharacterModels(_graphics);
                    return;
                }
                _sourceGraph?.Apply();
                foreach (var skin in _skins) skin.Apply(_sourceGraph?.Installed);
                _suspended = false;
                RefreshMaterials();
            }
            catch (Exception failure)
            {
                try { Release(false, false); }
                catch (Exception rollback) { throw new AggregateException("Custom appearance resumption and rollback failed.", failure, rollback); }
                throw;
            }
            finally { _busy = false; }
        }

        internal void Release(bool destroying, bool reapplyBody) => ReleaseCore(destroying, reapplyBody, false);

        private void ReleaseCore(bool destroying, bool reapplyBody, bool preservePreparedSources)
        {
            CancelSourcePreparation();
            _sourcePreparationReceipt = null;
            try
            {
            _refreshPending = false;
            _suspended = false;
            if (_graphics) ReleaseBodyShape(_graphics);
            if (_graphics && _graphics.GetComponent<BodyShapePresentation>() is { HasSources: true })
                throw new InvalidOperationException("Body overlays still reference a custom character model source.");
            if (_sourceGraph != null)
            {
                _sourceGraph.Restore(!destroying);
                _sourceGraph = null;
                foreach (var skin in _skins) skin.SourceRestored();
            }
            // Keep every asset lease until the native source graph has restored.
            foreach (var owner in _materials) owner.Restore();
            _materials.Clear();
            ReleaseDerivedData();
            foreach (var skin in _skins.ToArray())
            {
                skin.Restore(destroying);
                _skins.Remove(skin);
            }
            if (_packs.Count != 0)
                (_catalog ?? throw new InvalidOperationException("Owned custom character model packs lost their catalog.")).ReleaseBundles(_packs);
            if (_nativeSources.Count != 0)
                (_catalog ?? throw new InvalidOperationException("Owned native appearance sources lost their catalog.")).ReleaseNativeSources(_nativeSources);
            _requiredNativeSources.Clear();
            if (!HasSources) { _catalog = null; _actor = null; CharacterModelOwners.Remove(this); }
            if (reapplyBody && !destroying && _graphics) RefreshBodyShape(_graphics);
            }
            finally
            {
                try { if (!preservePreparedSources) ReleasePreparedSources(); }
                finally { SynchronizeCharacterModelRefreshPending(); }
            }
        }

        private void OnEnable() { if (!_characterModelsUnloading && _graphics && !_busy) RefreshCharacterModels(_graphics); }
        private void OnDisable() { try { Release(false, false); } catch (Exception exception) { ReportCharacterModelError("disable", exception); } }
        private void OnDestroy() { try { Release(true, false); } catch (Exception exception) { ReportCharacterModelError("destroy", exception); } }
    }

    private static HashSet<Material> NativeCharacterModelMaterialOwners(GraphicsCharacter graphics)
    {
        var result = new HashSet<Material>(graphics.MaterialsToDestroy);
        // Native monster/proxy instancing runs in RefreshRenderers, after the initial animator rebind.
        if (AccessTools.Field(graphics.GetType(), "materialInstances")?.GetValue(graphics) is IEnumerable<Material> instances)
            result.UnionWith(instances);
        return result;
    }

    private static HashSet<SkinnedMeshRenderer> CharacterModelRenderers(GraphicsCharacter graphics)
    {
        var result = new HashSet<SkinnedMeshRenderer>();
        var slots = graphics.ShapeSkinnedMeshRenderersPerBodySlot;
        // Native slot arrays define source ownership for every body surface and wearable.
        // Keep the same complete set for refresh, source counts and animator suspension.
        foreach (var collection in new[] { slots, graphics.ArmorSkinnedMeshRenderersPerBodySlot })
        {
            if (collection == null) continue;
            foreach (var part in collection)
            {
                if (part == null) continue;
                foreach (var renderer in part) if (renderer && renderer.sharedMesh) result.Add(renderer);
            }
        }
        if (slots != null && slots.Any(part => part is { Length: > 0 })) return result;
        // Classic models own an entire skin hierarchy. The catalog explicitly
        // distinguishes authored surfaces from native ones; anatomy is not guessed.
        var wielded = graphics.WieldedInstantiatedItems.Values.Where(value => value).Select(value => value.transform).ToArray();
        foreach (var renderer in graphics.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer && renderer.sharedMesh && !wielded.Any(root => renderer.transform == root || renderer.transform.IsChildOf(root)))
                result.Add(renderer);
        return result;
    }

    private static bool IsJointSkin(SkinnedMeshRenderer renderer) => renderer.sharedMaterials.Any(material => material && material.shader &&
        material.shader.name.StartsWith("Lux/Human/Skin", StringComparison.Ordinal));

    private sealed class CharacterModelSkin(GraphicsCharacter graphics, SkinnedMeshRenderer renderer, CustomModels.SkinMeshSource source, string fingerprint, Mesh authored, string bundleFile, bool jointSamplerCondition = false)
    {
        internal readonly SkinnedMeshRenderer Renderer = renderer;
        internal readonly string Fingerprint = fingerprint;
        internal readonly string BundleFile = bundleFile;
        private readonly Mesh _original = source.Original;
        private readonly bool _jointSkin = IsJointSkin(renderer);
        private readonly bool _trackJointSampler = jointSamplerCondition;
        private readonly bool _jointSamplerState = !jointSamplerCondition || JointPeriodicSamplers(renderer);
        private readonly Transform[] _bones = renderer.bones;
        private Transform[] _appliedBones = renderer.bones;
        private readonly Bounds _nativeBounds = renderer.localBounds;
        private Bounds _appliedBounds;
        private CustomModels.SkinMeshSource _source = source;
        private bool _clothInstalled;
        internal bool IsCurrent(GraphicsCharacter owner, CustomModels.SkinMeshSource current) => Renderer &&
            _jointSkin == IsJointSkin(Renderer) && (!_trackJointSampler || _jointSamplerState == JointPeriodicSamplers(Renderer)) &&
            current.MatchesBones(_appliedBones) && current.OwnerCurrent && current.AcceptsCurrentMesh() &&
            (current.Original == authored || owner.GetComponent<BodyShapePresentation>()?.IsBodyOverlayOf(Renderer, authored, current) == true);
        internal bool IsNativeCurrent(CustomModels.SkinMeshSource current) => Renderer && _original && authored &&
            _jointSkin == IsJointSkin(Renderer) &&
            current.Original == _original && current.OwnerCurrent && current.AcceptsCurrentMesh() &&
            (!_trackJointSampler || _jointSamplerState == JointPeriodicSamplers(Renderer)) && current.MatchesBones(_bones);
        internal bool ChangesSource => authored != _original;
        internal CustomModels.SkinMeshSource Source => _source;
        internal Mesh Authored => authored;
        internal void SetAuthored(Mesh mesh) => authored = mesh;
        internal void ValidateSource(bool hasBinding)
        {
            if (authored == _original) return;
            if (_source.ClothOwner && !hasBinding)
            {
                if (_original.vertexCount != authored.vertexCount || _original.subMeshCount != authored.subMeshCount ||
                    !_original.boneWeights.SequenceEqual(authored.boneWeights) ||
                    Enumerable.Range(0, _original.subMeshCount).Any(index => !_original.GetIndices(index).SequenceEqual(authored.GetIndices(index))))
                    throw new InvalidOperationException("An authored cloth surface changed native vertex, index or skin-weight correspondence.");
            }
        }
        internal void Apply(IReadOnlyDictionary<SkinnedMeshRenderer, CustomModels.SkinMeshSource> sources)
        {
            if (authored == _original) return;
            if (sources == null || !sources.TryGetValue(Renderer, out var source) || source.Original != authored)
                throw new InvalidOperationException("A native character model changed before its authored source could be installed.");
            _source = source;
            _appliedBones = Renderer.bones;
            _clothInstalled = source.ClothOwner;
            _appliedBounds = _nativeBounds;
            _appliedBounds.Encapsulate(authored.bounds);
            Renderer.localBounds = _appliedBounds;
        }
        private CustomModels.SkinMeshSource.SourceGraph _restoreGraph;
        internal void SourceRestored() { _clothInstalled = false; _appliedBones = _bones; }
        internal void Restore(bool destroying)
        {
            if (authored == _original) return;
            if (_restoreGraph != null)
            {
                _restoreGraph.Restore(!destroying);
                _restoreGraph = null;
            }
            if (!Renderer)
            {
                if (_clothInstalled && _source.ClothOwner && _source.OwnerCurrent)
                    throw new InvalidOperationException("A native cloth owner still references an authored character model after renderer destruction.");
                return;
            }
            var sources = CustomModels.SkinMeshSource.Capture(graphics, new[] { Renderer });
            CustomModels.SkinMeshSource current = null;
            sources?.TryGetValue(Renderer, out current);
            if (current != null && current.Original == authored)
            {
                if (!SwitchSkinSource(graphics, Renderer, current, _original, !destroying, false, ref _source, ref _clothInstalled, ref _restoreGraph))
                    throw new InvalidOperationException("An authored character model has not finished restoring its native source.");
                _restoreGraph = null;
                _clothInstalled = false;
            }
            else if (Renderer.sharedMesh == authored || _clothInstalled && _source.ClothOwner && _source.OwnerCurrent)
                throw new InvalidOperationException("An authored character model source is still referenced by a native skin owner.");
            if (Renderer.localBounds.Equals(_appliedBounds)) Renderer.localBounds = _nativeBounds;
        }
    }

    private sealed class CharacterModelMaterial
    {
        private static readonly Dictionary<Material, CharacterModelMaterial> Shared = [];
        internal readonly Material Material;
        internal readonly Shader SourceShader;
        internal readonly CharacterModelMaterialAsset Declaration;
        private readonly CharacterModelCatalog _catalog;
        private readonly NativeProfile _nativeProfile;
        private readonly NativeProfile _authoredProfile;
        private readonly string[] _profileProperties = [], _profileKeywords = [];
        private bool _profileApplied;
        private int _appliedQueue;
        private readonly List<CharacterModelMaterialProperty> _properties = [];
        private readonly HashSet<string> _packs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _requiredPacks = new(StringComparer.Ordinal);
        private int _leases;
        private string _bodyFingerprint;
        internal static bool TryGet(Material material, out CharacterModelMaterial owner)
        {
            owner = null;
            return material && Shared.TryGetValue(material, out owner);
        }
        internal static CharacterModelMaterial Acquire(Material material, Shader shader, CharacterModelMaterialAsset declaration, CharacterModelCatalog catalog, NativeProfile native, string bodyFingerprint)
        {
            if (!Shared.TryGetValue(material, out var owner))
            {
                owner = new CharacterModelMaterial(material, shader, declaration, catalog, native, bodyFingerprint);
                Shared.Add(material, owner);
            }
            else if (!ReferenceEquals(owner._catalog, catalog) || owner.SourceShader != shader || !SameSelection(material, shader, catalog, owner.Declaration, owner._bodyFingerprint, declaration, bodyFingerprint))
                throw new InvalidOperationException("A shared native material has incompatible custom character model owners.");
            owner._leases++;
            return owner;
        }
        private CharacterModelMaterial(Material material, Shader shader, CharacterModelMaterialAsset declaration, CharacterModelCatalog catalog, NativeProfile native, string bodyFingerprint)
        {
            _bodyFingerprint = bodyFingerprint;
            Material = material; SourceShader = shader; Declaration = declaration; _catalog = catalog;
            _nativeProfile = native;
            try
            {
                if (declaration.authoredProfile is { } profile)
                {
                    if (native == null || !ReferenceEquals(native.Material, material) || native.Shader != shader || material.shader != shader)
                        throw new InvalidOperationException("An authored material profile has no original native source snapshot.");
                    _catalog.RetainBundle(_packs, profile.bundleFile);
                    _authoredProfile = new NativeProfile(_catalog.Profile(profile));
                    native.ValidateTarget(_authoredProfile);
                    _profileProperties = _authoredProfile.Names.Where(name => !native.Contains(name)).ToArray();
                    _profileKeywords = _authoredProfile.Keywords.Except(native.Keywords, StringComparer.Ordinal).ToArray();
                    if (declaration.properties.Any(property => native.Contains(property.name)))
                        throw new InvalidOperationException("An authored material profile cannot replace a retained native property.");
                }
                var effectiveShader = _authoredProfile?.Shader ?? shader;
                foreach (var group in declaration.properties.GroupBy(property => property.name, StringComparer.Ordinal))
                {
                    var property = group.First();
                    if (group.Any(value => value.kind == "float") && group.Count() != 1)
                        throw new InvalidOperationException("A face material property has incompatible value declarations.");
                    var index = -1;
                    for (var candidate = 0; candidate < effectiveShader.GetPropertyCount(); candidate++)
                        if (effectiveShader.GetPropertyName(candidate) == property.name) { index = candidate; break; }
                    if (index < 0 || _authoredProfile == null && !material.HasProperty(property.name) ||
                        property.kind != "float" && effectiveShader.GetPropertyType(index) != UnityEngine.Rendering.ShaderPropertyType.Texture ||
                        property.kind == "float" && effectiveShader.GetPropertyType(index) is not (UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range))
                        throw new InvalidOperationException("An authored character model property does not match its native shader: " + property.name);
                    _properties.Add(new CharacterModelMaterialProperty(group.ToArray()));
                }
            }
            catch { _catalog.ReleaseBundles(_packs); throw; }
        }
        internal static bool SameDeclaration(CharacterModelMaterialAsset first, CharacterModelMaterialAsset second) =>
            ReferenceEquals(first, second) || first.AssetBundleFile == second.AssetBundleFile &&
            (first.authoredProfile == null && second.authoredProfile == null || first.authoredProfile != null && second.authoredProfile != null &&
             first.authoredProfile.bundleFile == second.authoredProfile.bundleFile && first.authoredProfile.materialAsset == second.authoredProfile.materialAsset &&
             first.authoredProfile.shader == second.authoredProfile.shader && first.authoredProfile.layout.sourceUv == second.authoredProfile.layout.sourceUv &&
             first.authoredProfile.layout.groomUv == second.authoredProfile.layout.groomUv &&
             first.authoredProfile.layout.groomCodec == second.authoredProfile.layout.groomCodec &&
             first.authoredProfile.layout.preservedUv.SequenceEqual(second.authoredProfile.layout.preservedUv)) &&
            first.properties.Length == second.properties.Length &&
            first.properties.Zip(second.properties, (a, b) => a.name == b.name && a.kind == b.kind && a.value.Equals(b.value) &&
                a.textureAsset == b.textureAsset && a.sourceTextureFingerprint == b.sourceTextureFingerprint && a.sourceBodyTextureFingerprint == b.sourceBodyTextureFingerprint &&
                a.sourceWidth == b.sourceWidth && a.sourceHeight == b.sourceHeight && a.bundleFile == b.bundleFile).All(value => value);
        internal static Texture NativeTexture(Material material, int id)
        {
            if (TryGet(material, out var owner))
                foreach (var property in owner._properties)
                    if (property.Id == id) return property.NativeTexture(material);
            return material.GetTexture(id);
        }
        internal static bool SameSelection(Material material, Shader shader, CharacterModelCatalog catalog,
            CharacterModelMaterialAsset first, string firstBody, CharacterModelMaterialAsset second, string secondBody)
        {
            if (!SameDeclaration(first, second)) return false;
            if (firstBody == secondBody || !first.UsesBodyTexture) return true;
            HashSet<string> retained = null;
            try
            {
                foreach (var group in first.properties.GroupBy(property => property.name, StringComparer.Ordinal))
                {
                    var properties = group.ToArray();
                    if (properties[0].kind == "float") continue;
                    var id = Shader.PropertyToID(group.Key);
                    var native = material.HasProperty(id) ? NativeTexture(material, id) : null;
                    if (!material.HasProperty(id) && first.authoredProfile is { } profile)
                    {
                        catalog.RetainBundle(retained ??= [], profile.bundleFile);
                        var authored = catalog.Profile(profile);
                        if (authored.HasProperty(id)) native = authored.GetTexture(id);
                    }
                    var a = CharacterModelMaterialProperty.SelectTexture(properties, native, catalog, shader.name, first.AssetBundleFile, firstBody);
                    var b = CharacterModelMaterialProperty.SelectTexture(properties, native, catalog, shader.name, second.AssetBundleFile, secondBody);
                    if (a.Property?.textureAsset != b.Property?.textureAsset || a.Bundle != b.Bundle) return false;
                }
                return true;
            }
            finally { if (retained != null) catalog.ReleaseBundles(retained); }
        }
        internal void Apply(string bodyFingerprint)
        {
            if (!Material) return;
            if (_leases > 1 && !SameSelection(Material, SourceShader, _catalog, Declaration, _bodyFingerprint, Declaration, bodyFingerprint))
                throw new InvalidOperationException("A shared native material has incompatible body-dependent texture owners.");
            _bodyFingerprint = bodyFingerprint;
            if (_authoredProfile != null)
            {
                if (!_profileApplied)
                {
                    if (Material.shader != SourceShader)
                        throw new InvalidOperationException("A different shader owns the authored material profile target.");
                    // Shader changes retain the live native property sheet; only added profile values are written.
                    var current = new NativeProfile(Material);
                    Material.shader = _authoredProfile.Shader;
                    _profileApplied = true;
                    _appliedQueue = current.RawQueue < 0 && Material.shader.renderQueue != current.EffectiveQueue ? current.EffectiveQueue : current.RawQueue;
                    current.CopyState(Material, current.Keywords.Concat(_profileKeywords), _appliedQueue);
                    _authoredProfile.Copy(Material, _profileProperties);
                }
                else if (Material.shader != _authoredProfile.Shader)
                    throw new InvalidOperationException("A different shader prevents reapplying the owned authored material profile.");
            }
            else if (Material.shader != SourceShader) return;
            var required = _requiredPacks;
            required.Clear();
            if (Declaration.authoredProfile is { } profile) required.Add(profile.bundleFile);
            foreach (var property in _properties)
                if (property.Resolve(Material, _catalog, SourceShader.name, Declaration.AssetBundleFile, _bodyFingerprint) is { } file) required.Add(file);
            foreach (var file in required) _catalog.RetainBundle(_packs, file);
            foreach (var property in _properties) property.Apply(Material, _catalog);
            _catalog.ReleaseBundles(_packs, required);
        }
        internal void RetainBundles(HashSet<string> retained)
        {
            foreach (var file in _packs) _catalog.RetainBundle(retained, file);
        }
        internal void Restore()
        {
            if (_leases <= 0) return;
            if (_leases > 1) { _leases--; return; }
            if (Material)
            {
                if (_profileApplied && Material.shader != _authoredProfile.Shader)
                    throw new InvalidOperationException("A different shader prevents releasing the owned authored material profile.");
                foreach (var property in _properties) property.Restore(Material);
                if (_profileApplied)
                {
                    var current = new NativeProfile(Material);
                    foreach (var name in _profileProperties)
                        _authoredProfile.Clear(Material, name);
                    Material.shader = SourceShader;
                    current.CopyState(Material, current.Keywords.Except(_profileKeywords, StringComparer.Ordinal),
                        current.RawQueue == _appliedQueue ? _nativeProfile.RawQueue : current.RawQueue);
                    _profileApplied = false;
                }
            }
            _catalog.ReleaseBundles(_packs);
            _leases = 0;
            Shared.Remove(Material);
        }

        internal static NativeProfile CaptureNativeProfile(Material material) =>
            TryGet(material, out var owner)
                ? owner._nativeProfile ?? throw new InvalidOperationException("A declared native profile source lost its pre-application snapshot.")
                : new NativeProfile(material);

        internal sealed class NativeProfile
        {
            internal readonly Material Material;
            internal readonly Shader Shader;
            internal readonly int RawQueue;
            internal readonly int EffectiveQueue;
            internal readonly string[] Keywords;
            internal IEnumerable<string> Names => _values.Keys;
            private readonly bool _instancing, _doubleSided;
            private readonly MaterialGlobalIlluminationFlags _illumination;
            private readonly Dictionary<string, bool> _passes = new(StringComparer.Ordinal);
            private readonly Dictionary<string, UnityEngine.Rendering.TextureDimension> _dimensions = new(StringComparer.Ordinal);
            private readonly Dictionary<string, (UnityEngine.Rendering.ShaderPropertyType Kind, object Value, Vector2 Scale, Vector2 Offset)> _values = new(StringComparer.Ordinal);
            internal NativeProfile(Material material)
            {
                Material = material; Shader = material.shader; RawQueue = RawRenderQueue(material); EffectiveQueue = material.renderQueue;
                Keywords = material.shaderKeywords.ToArray(); _instancing = material.enableInstancing;
                _doubleSided = material.doubleSidedGI; _illumination = material.globalIlluminationFlags;
                for (var pass = 0; pass < material.passCount; pass++)
                {
                    var name = material.GetPassName(pass);
                    if (!string.IsNullOrEmpty(name)) _passes[name] = material.GetShaderPassEnabled(name);
                }
                for (var index = 0; index < Shader.GetPropertyCount(); index++)
                {
                    var name = Shader.GetPropertyName(index); var kind = Shader.GetPropertyType(index);
                    if (kind == UnityEngine.Rendering.ShaderPropertyType.Texture) _dimensions.Add(name, Shader.GetPropertyTextureDimension(index));
                    object value = kind switch
                    {
                        UnityEngine.Rendering.ShaderPropertyType.Texture => material.GetTexture(name),
                        UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range => material.GetFloat(name),
                        UnityEngine.Rendering.ShaderPropertyType.Color => material.GetColor(name),
                        UnityEngine.Rendering.ShaderPropertyType.Vector => material.GetVector(name),
                        _ => throw new InvalidOperationException("A native material has an unsupported shader property kind: " + name)
                    };
                    _values.Add(name, (kind, value,
                        kind == UnityEngine.Rendering.ShaderPropertyType.Texture ? material.GetTextureScale(name) : Vector2.one,
                        kind == UnityEngine.Rendering.ShaderPropertyType.Texture ? material.GetTextureOffset(name) : Vector2.zero));
                }
            }
            internal bool Contains(string name) => _values.ContainsKey(name);
            internal bool Matches(Material material, string name)
            {
                if (!_values.TryGetValue(name, out var row) || !material.HasProperty(name)) return false;
                return row.Kind switch
                {
                    UnityEngine.Rendering.ShaderPropertyType.Texture => material.GetTexture(name) == (Texture)row.Value &&
                        material.GetTextureScale(name).Equals(row.Scale) && material.GetTextureOffset(name).Equals(row.Offset),
                    UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range => material.GetFloat(name).Equals((float)row.Value),
                    UnityEngine.Rendering.ShaderPropertyType.Color => material.GetColor(name).Equals((Color)row.Value),
                    UnityEngine.Rendering.ShaderPropertyType.Vector => material.GetVector(name).Equals((Vector4)row.Value),
                    _ => false
                };
            }
            internal static int RawRenderQueue(Material material) =>
                (int)(AccessTools.Property(typeof(Material), "rawRenderQueue") ??
                    throw new MissingMemberException(typeof(Material).FullName, "rawRenderQueue")).GetValue(material, null);
            internal void ValidateTarget(NativeProfile target)
            {
                foreach (var pair in _values)
                    if (!target._values.TryGetValue(pair.Key, out var row) || !Compatible(pair.Value.Kind, row.Kind) ||
                        pair.Value.Kind == UnityEngine.Rendering.ShaderPropertyType.Texture && _dimensions[pair.Key] != target._dimensions[pair.Key])
                        throw new InvalidOperationException("An authored material profile does not retain a native property and kind: " + pair.Key);
            }
            private static bool Compatible(UnityEngine.Rendering.ShaderPropertyType first, UnityEngine.Rendering.ShaderPropertyType second) =>
                first == second || first is UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range &&
                    second is UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range;
            internal void CopyState(Material target, IEnumerable<string> keywords, int queue)
            {
                target.shaderKeywords = keywords.Distinct(StringComparer.Ordinal).ToArray();
                target.renderQueue = queue; target.enableInstancing = _instancing;
                target.doubleSidedGI = _doubleSided; target.globalIlluminationFlags = _illumination;
                for (var pass = 0; pass < target.passCount; pass++)
                {
                    var name = target.GetPassName(pass);
                    if (_passes.TryGetValue(name, out var enabled)) target.SetShaderPassEnabled(name, enabled);
                }
            }
            internal void Clear(Material target, string name)
            {
                var row = _values[name];
                switch (row.Kind)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        if (target.GetTexture(name) == (Texture)row.Value) target.SetTexture(name, null);
                        if (target.GetTextureScale(name).Equals(row.Scale)) target.SetTextureScale(name, Vector2.one);
                        if (target.GetTextureOffset(name).Equals(row.Offset)) target.SetTextureOffset(name, Vector2.zero);
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color: if (Matches(target, name)) target.SetColor(name, default); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector: if (Matches(target, name)) target.SetVector(name, default); break;
                    default: if (Matches(target, name)) target.SetFloat(name, 0f); break;
                }
            }
            internal void Copy(Material target, IEnumerable<string> names)
            {
                foreach (var name in names)
                {
                    var row = _values[name];
                    if (!target.HasProperty(name)) throw new InvalidOperationException("An authored profile target has no declared property: " + name);
                    if (row.Kind == UnityEngine.Rendering.ShaderPropertyType.Texture)
                    {
                        target.SetTexture(name, (Texture)row.Value);
                        target.SetTextureScale(name, row.Scale); target.SetTextureOffset(name, row.Offset);
                    }
                    else if (row.Kind == UnityEngine.Rendering.ShaderPropertyType.Color) target.SetColor(name, (Color)row.Value);
                    else if (row.Kind == UnityEngine.Rendering.ShaderPropertyType.Vector) target.SetVector(name, (Vector4)row.Value);
                    else target.SetFloat(name, (float)row.Value);
                }
            }
        }

    }

    private sealed class CharacterModelMaterialProperty(CharacterModelProperty[] declarations)
    {
        private readonly int _id = Shader.PropertyToID(declarations[0].name);
        internal int Id => _id;
        internal Texture NativeTexture(Material material)
        {
            var current = material.GetTexture(_id);
            return _applied && current == _appliedTexture ? _nativeTexture : current;
        }
        private bool _applied;
        private Texture _nativeTexture, _appliedTexture;
        private float _nativeFloat, _appliedFloat;
        private bool _resolved;
        private Texture _resolvedNativeTexture;
        private string _resolvedBodyFingerprint;
        private CharacterModelProperty _selected;
        private string _selectedBundle;
        private static bool HasSourceDimensions(CharacterModelProperty[] declarations, int width, int height)
        {
            foreach (var declaration in declarations)
            {
                if (declaration.kind == "texture-remap" && declaration.MatchesSourceDimensions(width, height)) return true;
                if (declaration.SourceCandidates is { } candidates)
                    foreach (var candidate in candidates)
                        if (candidate.MatchesSourceDimensions(width, height)) return true;
            }
            return false;
        }
        internal string Resolve(Material material, CharacterModelCatalog catalog, string shader, string bundle, string bodyFingerprint)
        {
            var declaration = declarations[0];
            if (declaration.kind == "float") return null;
            var current = material.GetTexture(_id);
            if (!_applied || current != _appliedTexture) _nativeTexture = current;
            if (_resolved && ReferenceEquals(_resolvedNativeTexture, _nativeTexture) && _resolvedBodyFingerprint == bodyFingerprint) return _selectedBundle;
            (_selected, _selectedBundle) = SelectTexture(declarations, _nativeTexture, catalog, shader, bundle, bodyFingerprint);
            _resolvedNativeTexture = _nativeTexture;
            _resolvedBodyFingerprint = bodyFingerprint;
            _resolved = true;
            return _selectedBundle;
        }
        internal static (CharacterModelProperty Property, string Bundle) SelectTexture(CharacterModelProperty[] declarations,
            Texture native, CharacterModelCatalog catalog, string shader, string bundle, string bodyFingerprint)
        {
            var selected = declarations.FirstOrDefault(value => value.kind == "texture");
            var selectedBundle = selected == null ? null : selected.bundleFile ?? bundle;
            if (selected == null)
            {
                var fingerprint = native && native.dimension == UnityEngine.Rendering.TextureDimension.Tex2D &&
                    HasSourceDimensions(declarations, native.width, native.height)
                    ? CharacterModelTextureFingerprint(native) : null;
                selected = bodyFingerprint == null ? null : declarations.FirstOrDefault(value => value.kind == "texture-remap" &&
                    value.sourceTextureFingerprint == fingerprint && value.sourceBodyTextureFingerprint == bodyFingerprint);
                selected ??= declarations.FirstOrDefault(value => value.kind == "texture-remap" &&
                    value.sourceTextureFingerprint == fingerprint && value.sourceBodyTextureFingerprint == null);
                if (selected != null) selectedBundle = selected.bundleFile ?? bundle;
                else if (declarations.Any(value => value.kind == "texture-index"))
                {
                    selected = catalog.TextureRemap(shader, declarations[0].name, fingerprint, bodyFingerprint);
                    selectedBundle = selected?.bundleFile;
                }
            }
            return (selected, selectedBundle);
        }
        internal void Apply(Material material, CharacterModelCatalog catalog)
        {
            var declaration = declarations[0];
            if (declaration.kind != "float")
            {
                var current = material.GetTexture(_id);
                _appliedTexture = _selected == null ? _nativeTexture : catalog.Texture(_selectedBundle, _selected);
                if (current != _appliedTexture) material.SetTexture(_id, _appliedTexture);
            }
            else
            {
                var current = material.GetFloat(_id);
                if (!_applied || !current.Equals(_appliedFloat)) _nativeFloat = current;
                _appliedFloat = declaration.value;
                material.SetFloat(_id, _appliedFloat);
            }
            _applied = true;
        }
        internal void Restore(Material material)
        {
            if (!_applied) return;
            if (!material.HasProperty(_id))
                throw new InvalidOperationException("A native material must restore its source shader before an owned custom appearance property can be released.");
            if (declarations[0].kind != "float") { if (material.GetTexture(_id) == _appliedTexture) material.SetTexture(_id, _nativeTexture); }
            else if (material.GetFloat(_id).Equals(_appliedFloat)) material.SetFloat(_id, _nativeFloat);
            _applied = false;
        }

    }

    // Authored native muscle references. Target selection uses the actual skin, not race or item names.
    private static readonly string[][] BodyShapeSampleKeys =
    [
        ["f710eacb3971f134d8fc4789d28eaf1c", "7dff7203cd76ba44fa32480f6493b741"],
        ["915364237588fd14a91ed6ccaf75097c", "3fbf74040c64ee14fa50d9ec1c4f56d9"]
    ];

    private static void ApplySupplementalBodyShape(GraphicsCharacter graphics, float value, float defaultValue, bool supplemental)
    {
        if (_bodyShapesUnloading) return;
        var owner = graphics.GetComponent<BodyShapePresentation>() ?? graphics.gameObject.AddComponent<BodyShapePresentation>();
        owner.Bind(graphics);
        if (!owner.isActiveAndEnabled) return;
        if (DeferCharacterModelRefresh)
        {
            owner.DeferRefresh();
            return;
        }
        var field = supplemental ? GetBodyShapeField(graphics.RulesetCharacter.Sex == CreatureSex.Female ? 1 : 0) : null;
        owner.Refresh(graphics, field, value, defaultValue, supplemental);
    }

    private static BodyShapeField GetBodyShapeField(int sex)
    {
        if (_bodyShapesUnloading) return null;
        if (BodyShapeFields[sex] != null || BodyShapeFieldLoading[sex]) return BodyShapeFields[sex];
        BodyShapeFieldLoading[sex] = true;
        var first = Addressables.LoadAssetAsync<GameObject>(BodyShapeSampleKeys[sex][0]);
        var second = Addressables.LoadAssetAsync<GameObject>(BodyShapeSampleKeys[sex][1]);
        var finished = false;
        first.Completed += _ => Complete();
        second.Completed += _ => Complete();
        return BodyShapeFields[sex];
        void Complete()
        {
            if (finished || !first.IsDone || !second.IsDone) return;
            finished = true;
            try
            {
                if (!_bodyShapesUnloading)
                {
                    if (!first.Result || !second.Result) throw new InvalidOperationException("Native body-shape calibration did not load.");
                    BodyShapeFields[sex] = BodyShapeField.Create(first.Result, second.Result);
                }
            }
            catch (Exception exception) { ReportBodyShapeError("calibration-" + sex, exception); }
            finally { BodyShapeFieldLoading[sex] = false; Addressables.Release(first); Addressables.Release(second); }
            if (_bodyShapesUnloading || BodyShapeFields[sex] == null || !SettingsContext.GuiModManagerInstance.EnableAllRaceBodyShapes) return;
            var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>();
            if (factory == null) return;
            foreach (var character in factory.GraphicsCharacters.ToArray()) RefreshBodyShape(character);
        }
    }

    private static void ReportBodyShapeError(string key, Exception exception)
    {
        if (BodyShapeErrors.Add(key)) Main.Error("Unable to apply a body shape (" + key + "): " + exception);
    }

    private static bool IsProtectedBodyBone(Transform bone)
    {
        for (var current = bone; current; current = current.parent)
        {
            if (current.name is "Bip001 Head" or "Bip001 L Hand" or "Bip001 R Hand" or
                "Bip001 L Foot" or "Bip001 R Foot" || current.name.StartsWith("Bip001 Tail", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static float BodyShapeBoneLength(Transform bone)
    {
        for (var current = bone; current; current = current.parent)
        {
            if (current.localPosition.magnitude > 0.0001f) return current.localPosition.magnitude;
        }
        throw new InvalidOperationException("A native muscle bone has no measurable anchor spacing: " + bone.name);
    }

    private static int BodyShapeWeightIndex(BoneWeight weight, int index) => index switch
    { 0 => weight.boneIndex0, 1 => weight.boneIndex1, 2 => weight.boneIndex2, _ => weight.boneIndex3 };
    private static float BodyShapeWeight(BoneWeight weight, int index) => index switch
    { 0 => weight.weight0, 1 => weight.weight1, 2 => weight.weight2, _ => weight.weight3 };
    private static bool BodyShapeFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool BodyShapeFinite(Vector3 value) => BodyShapeFinite(value.x) && BodyShapeFinite(value.y) && BodyShapeFinite(value.z);

    private static CustomModels.SkinMeshSource.SurfaceBinding CreateAuthoredBaseBinding(Mesh original, Mesh authored, string identity)
    {
        if (!original || !authored || original == authored || original.vertexCount != authored.vertexCount) return null;
        var native = ReadBodyShapeRest(original); var target = ReadBodyShapeRest(authored);
        var count = original.vertexCount;
        if (native.Tangents.Length != count || target.Tangents.Length != count ||
            !native.BindPoses.SequenceEqual(target.BindPoses) || !native.Weights.SequenceEqual(target.Weights) ||
            original.blendShapeCount != authored.blendShapeCount)
            throw new InvalidOperationException("An authored base changed its native skin, palette or physical layout.");
        var changed = Enumerable.Range(0, count).Select(index =>
            !native.Vertices[index].Equals(target.Vertices[index]) || !native.Normals[index].Equals(target.Normals[index]) ||
            !native.Tangents[index].Equals(target.Tangents[index])).ToArray();
        var sp = new Vector3[count]; var sn = new Vector3[count]; var st = new Vector3[count];
        var tp = new Vector3[count]; var tn = new Vector3[count]; var tt = new Vector3[count];
        for (var channel = 0; channel < original.blendShapeCount; channel++)
        {
            if (original.GetBlendShapeName(channel) != authored.GetBlendShapeName(channel) ||
                original.GetBlendShapeFrameCount(channel) != authored.GetBlendShapeFrameCount(channel))
                throw new InvalidOperationException("An authored base changed a native physical channel.");
            for (var frame = 0; frame < original.GetBlendShapeFrameCount(channel); frame++)
            {
                if (original.GetBlendShapeFrameWeight(channel, frame) != authored.GetBlendShapeFrameWeight(channel, frame))
                    throw new InvalidOperationException("An authored base changed a native physical frame range.");
                original.GetBlendShapeFrameVertices(channel, frame, sp, sn, st);
                authored.GetBlendShapeFrameVertices(channel, frame, tp, tn, tt);
                for (var index = 0; index < count; index++)
                    changed[index] |= !sp[index].Equals(tp[index]) || !sn[index].Equals(tn[index]) || !st[index].Equals(tt[index]);
            }
        }
        if (!changed.Any(value => value)) return null;
        var vertices = new CustomModels.SkinMeshSource.SurfaceVertex[count];
        for (var index = 0; index < count; index++)
        {
            if (!changed[index])
            {
                vertices[index] = new CustomModels.SkinMeshSource.SurfaceVertex(index, Vector3.zero, Vector3.forward, Vector3.right, true);
                continue;
            }
            var normal = native.Normals[index].normalized; var tangent = (Vector3)native.Tangents[index];
            tangent = (tangent - normal * Vector3.Dot(normal, tangent)).normalized;
            var handedness = native.Tangents[index].w; var bitangent = Vector3.Cross(normal, tangent) * handedness;
            if (normal.sqrMagnitude < 0.99f || tangent.sqrMagnitude < 0.99f || bitangent.sqrMagnitude < 0.99f ||
                handedness is not (-1f or 1f) || target.Tangents[index].w != handedness)
                throw new InvalidOperationException("An authored base has no compatible native tangent frame.");
            Vector3 Coordinates(Vector3 value) => new(Vector3.Dot(value, tangent), Vector3.Dot(value, bitangent), Vector3.Dot(value, normal));
            vertices[index] = new CustomModels.SkinMeshSource.SurfaceVertex(index,
                Coordinates(target.Vertices[index] - native.Vertices[index]), Coordinates(target.Normals[index]),
                Coordinates((Vector3)target.Tangents[index]), false);
        }
        return new CustomModels.SkinMeshSource.SurfaceBinding(identity, Matrix4x4.identity, native.Tangents, vertices, authoredBase: authored);
    }

    private sealed class BodyShapeRest
    {
        internal Vector3[] Vertices, Normals, Muscular, MuscularNormals, MuscularTangents;
        internal Vector4[] Tangents;
        internal Matrix4x4[] BindPoses;
        internal BoneWeight[] Weights;
        internal int[] WeightedBones;
        internal bool AllVerticesWeighted;
        internal int[] Triangles;
        internal bool[] Boundary;
        private BodyShapeCut _cut;
        internal BodyShapeCut Cut => _cut ??= new BodyShapeCut(Vertices, Triangles);
    }

    private sealed class BodyShapeCut
    {
        internal readonly Vector3[] Points;
        internal readonly int[][] Vertices;
        internal readonly int[] Groups, Components;
        internal readonly bool[] Closed;
        internal readonly (int First, int Second)[] Edges;

        internal BodyShapeCut(Vector3[] vertices, int[] triangles)
        {
            var indices = new Dictionary<Vector3, int>();
            var points = new List<Vector3>();
            var groups = new int[vertices.Length];
            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                if (!indices.TryGetValue(vertices[vertex], out var group))
                { indices.Add(vertices[vertex], group = points.Count); points.Add(vertices[vertex]); }
                groups[vertex] = group;
            }
            var boundary = BodyShapeEdges(triangles, groups).Where(pair => pair.Value == 1).Select(pair => pair.Key).ToArray();
            var members = boundary.SelectMany(edge => new[] { edge.Item1, edge.Item2 }).Distinct().OrderBy(index => index).ToArray();
            var remap = members.Select((group, index) => (group, index)).ToDictionary(pair => pair.group, pair => pair.index);
            Points = members.Select(group => points[group]).ToArray();
            var aliases = members.Select(_ => new List<int>()).ToArray();
            Groups = Enumerable.Repeat(-1, vertices.Length).ToArray();
            for (var vertex = 0; vertex < vertices.Length; vertex++)
                if (remap.TryGetValue(groups[vertex], out var group)) { Groups[vertex] = group; aliases[group].Add(vertex); }
            Vertices = aliases.Select(list => list.ToArray()).ToArray();
            Edges = boundary.Select(edge => (remap[edge.Item1], remap[edge.Item2])).ToArray();
            var neighbours = Points.Select(_ => new List<int>()).ToArray();
            foreach (var edge in Edges) { neighbours[edge.First].Add(edge.Second); neighbours[edge.Second].Add(edge.First); }
            Components = Enumerable.Repeat(-1, Points.Length).ToArray();
            var closed = new List<bool>();
            for (var seed = 0; seed < Points.Length; seed++)
            {
                if (Components[seed] >= 0) continue;
                var pending = new Stack<int>(); pending.Push(seed);
                var isClosed = true;
                while (pending.Count != 0)
                {
                    var point = pending.Pop();
                    if (Components[point] >= 0) continue;
                    Components[point] = closed.Count;
                    isClosed &= neighbours[point].Count == 2;
                    foreach (var neighbour in neighbours[point]) if (Components[neighbour] < 0) pending.Push(neighbour);
                }
                closed.Add(isClosed);
            }
            Closed = closed.ToArray();
        }

        internal float[] MinimumEdges(Vector3[] points)
        {
            var result = Enumerable.Repeat(float.PositiveInfinity, points.Length).ToArray();
            foreach (var edge in Edges)
            {
                var length = (points[edge.First] - points[edge.Second]).magnitude;
                result[edge.First] = Mathf.Min(result[edge.First], length);
                result[edge.Second] = Mathf.Min(result[edge.Second], length);
            }
            return result;
        }

        internal static Dictionary<int, (List<int> Vertices, float MaximumDistance)> Match(
            BodyShapeRest head, BodyShapeRest body, Matrix4x4 headToBody, float tolerance)
        {
            var result = new Dictionary<int, (List<int> Vertices, float MaximumDistance)>();
            var cells = new Dictionary<(int, int, int), List<int>>();
            for (var vertex = 0; vertex < body.Vertices.Length; vertex++)
            {
                if (!body.Boundary[vertex]) continue;
                var key = Cell(body.Vertices[vertex]);
                if (!cells.TryGetValue(key, out var list)) cells.Add(key, list = []);
                list.Add(vertex);
            }
            for (var vertex = 0; vertex < head.Vertices.Length; vertex++)
            {
                if (!head.Boundary[vertex]) continue;
                var point = headToBody.MultiplyPoint3x4(head.Vertices[vertex]);
                var cell = Cell(point);
                var matches = new List<int>();
                for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
                    if (cells.TryGetValue((cell.Item1 + x, cell.Item2 + y, cell.Item3 + z), out var candidates))
                        foreach (var candidate in candidates)
                            if ((body.Vertices[candidate] - point).sqrMagnitude <= tolerance * tolerance) matches.Add(candidate);
                if (matches.Count != 0) result.Add(vertex, (matches, tolerance));
            }
            var h = head.Cut; var b = body.Cut;
            var anchors = new HashSet<(int, int)>();
            foreach (var pair in result)
            {
                var group = h.Groups[pair.Key];
                if (group < 0) continue;
                foreach (var vertex in pair.Value.Vertices)
                {
                    var target = b.Groups[vertex];
                    if (target >= 0 && h.Closed[h.Components[group]] && b.Closed[b.Components[target]])
                        anchors.Add((h.Components[group], b.Components[target]));
                }
            }
            var ambiguousHeads = anchors.GroupBy(pair => pair.Item1).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            var ambiguousBodies = anchors.GroupBy(pair => pair.Item2).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            anchors.RemoveWhere(pair => ambiguousHeads.Contains(pair.Item1) || ambiguousBodies.Contains(pair.Item2));
            if (h.Points.Length != 0 && b.Points.Length != 0)
            {
                var hPoints = h.Points.Select(headToBody.MultiplyPoint3x4).ToArray();
                var hEdges = h.MinimumEdges(hPoints); var bEdges = b.MinimumEdges(b.Points);
                var hNearest = Enumerable.Repeat(-1, hPoints.Length).ToArray();
                var bNearest = Enumerable.Repeat(-1, b.Points.Length).ToArray();
                var hDistances = Enumerable.Repeat(float.PositiveInfinity, hPoints.Length).ToArray();
                var bDistances = Enumerable.Repeat(float.PositiveInfinity, b.Points.Length).ToArray();
                var hTies = new bool[hPoints.Length]; var bTies = new bool[b.Points.Length];
                for (var source = 0; source < hPoints.Length; source++) for (var target = 0; target < b.Points.Length; target++)
                {
                    var distance = (hPoints[source] - b.Points[target]).sqrMagnitude;
                    if (distance < hDistances[source]) { hDistances[source] = distance; hNearest[source] = target; hTies[source] = false; }
                    else if (distance == hDistances[source]) hTies[source] = true;
                    if (distance < bDistances[target]) { bDistances[target] = distance; bNearest[target] = source; bTies[target] = false; }
                    else if (distance == bDistances[target]) bTies[target] = true;
                }
                var cycles = new Dictionary<(int, int), Dictionary<int, int>>();
                for (var source = 0; source < hPoints.Length; source++)
                {
                    var target = hNearest[source];
                    if (target < 0 || bNearest[target] != source || hTies[source] || bTies[target] ||
                        !h.Closed[h.Components[source]] || !b.Closed[b.Components[target]]) continue;
                    var maximum = Mathf.Min(hEdges[source], bEdges[target]) * 0.5f;
                    if (!BodyShapeFinite(maximum) || maximum <= 0f || hDistances[source] >= maximum * maximum) continue;
                    var key = (h.Components[source], b.Components[target]);
                    if (!cycles.TryGetValue(key, out var cycle)) cycles.Add(key, cycle = []);
                    cycle.Add(source, target);
                }
                var bodyEdges = b.Edges.Select(edge => Edge(edge.First, edge.Second)).ToHashSet();
                var complete = cycles.Where(pair => pair.Value.Count == h.Components.Count(value => value == pair.Key.Item1) &&
                    pair.Value.Count == b.Components.Count(value => value == pair.Key.Item2) &&
                    h.Edges.Where(edge => h.Components[edge.First] == pair.Key.Item1).All(edge =>
                        bodyEdges.Contains(Edge(pair.Value[edge.First], pair.Value[edge.Second])))).Select(pair => pair.Key).ToHashSet();
                for (var source = 0; source < hPoints.Length; source++)
                {
                    var target = hNearest[source];
                    if (target < 0 || bNearest[target] != source || hTies[source] || bTies[target]) continue;
                    var key = (h.Components[source], b.Components[target]);
                    // A complete reciprocal cycle identifies a cut whose source vertices all have an offset.
                    var full = complete.Contains(key);
                    if (!full && !anchors.Contains(key)) continue;
                    var maximum = Mathf.Min(hEdges[source], bEdges[target]) * (full ? 0.5f : 0.1f);
                    if (!BodyShapeFinite(maximum) || maximum <= 0f ||
                        (full ? hDistances[source] >= maximum * maximum : hDistances[source] > maximum * maximum)) continue;
                    foreach (var vertex in h.Vertices[source])
                    {
                        if (!result.TryGetValue(vertex, out var current)) current = ([], tolerance);
                        foreach (var member in b.Vertices[target]) if (!current.Vertices.Contains(member)) current.Vertices.Add(member);
                        result[vertex] = (current.Vertices, Mathf.Max(current.MaximumDistance, maximum));
                    }
                }
            }
            foreach (var pair in result) pair.Value.Vertices.Sort();
            return result;

            (int, int, int) Cell(Vector3 point) => (Mathf.FloorToInt(point.x / tolerance),
                Mathf.FloorToInt(point.y / tolerance), Mathf.FloorToInt(point.z / tolerance));
            static (int, int) Edge(int first, int second) => first < second ? (first, second) : (second, first);
        }
    }

    private static BodyShapeRest ReadBodyShapeRest(Mesh source)
    {
        return BodyShapeRestCache.GetValue(source, static mesh => ReadNativeRestMesh(mesh, readable =>
        {
            var result = new BodyShapeRest { BindPoses = mesh.bindposes, Weights = mesh.boneWeights };
            result.Vertices = readable.vertices;
            result.Normals = readable.normals;
            result.Tangents = readable.tangents;
            result.Triangles = readable.triangles;
            if (result.Vertices.Length != mesh.vertexCount || result.Normals.Length != mesh.vertexCount ||
                result.Weights.Length != mesh.vertexCount || !result.Vertices.All(BodyShapeFinite) ||
                !result.Normals.All(BodyShapeFinite) || result.Triangles.Length % 3 != 0 ||
                result.Triangles.Any(index => index < 0 || index >= mesh.vertexCount))
                throw new InvalidOperationException("Native rest channels are incomplete: " + mesh.name);
            var weightedBones = new HashSet<int>();
            result.AllVerticesWeighted = true;
            foreach (var weights in result.Weights)
            {
                var total = 0f;
                for (var influence = 0; influence < 4; influence++)
                {
                    var weight = BodyShapeWeight(weights, influence);
                    var index = BodyShapeWeightIndex(weights, influence);
                    if (!BodyShapeFinite(weight) || weight < 0f || weight > 0f && (index < 0 || index >= result.BindPoses.Length))
                        throw new InvalidOperationException("A body skin weight references an invalid bindpose: " + mesh.name);
                    if (weight <= 0f) continue;
                    weightedBones.Add(index);
                    total += weight;
                }
                result.AllVerticesWeighted &= BodyShapeFinite(total) && total > 0f;
            }
            result.WeightedBones = weightedBones.ToArray();
            result.Boundary = BodyShapeBoundary(result.Vertices.Length, result.Triangles);
            var muscular = mesh.GetBlendShapeIndex("Muscular");
            if (muscular >= 0)
            {
                if (mesh.GetBlendShapeFrameCount(muscular) != 1 || mesh.GetBlendShapeFrameWeight(muscular, 0) <= 0f)
                    throw new InvalidOperationException("The native Muscular frame range is unsupported: " + mesh.name);
                result.Muscular = new Vector3[mesh.vertexCount];
                result.MuscularNormals = new Vector3[mesh.vertexCount];
                result.MuscularTangents = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(muscular, 0, result.Muscular, result.MuscularNormals, result.MuscularTangents);
                var multiplier = 100f / mesh.GetBlendShapeFrameWeight(muscular, 0);
                for (var index = 0; index < result.Muscular.Length; index++)
                {
                    result.Muscular[index] *= multiplier;
                    result.MuscularNormals[index] *= multiplier;
                    result.MuscularTangents[index] *= multiplier;
                    if (!BodyShapeFinite(result.Muscular[index]) || !BodyShapeFinite(result.MuscularNormals[index]) ||
                        !BodyShapeFinite(result.MuscularTangents[index]))
                        throw new InvalidOperationException("Native muscle channels are nonfinite: " + mesh.name);
                }
            }
            return result;
        }));
    }

    internal static T ReadNativeRestMesh<T>(Mesh mesh, Func<Mesh, T> read)
    {
        GameObject host = null;
        Mesh baked = null;
        try
        {
            var readable = mesh;
            var bindPoses = mesh.bindposes;
            if (!mesh.isReadable)
            {
                host = new GameObject("Body shape rest proxy") { hideFlags = HideFlags.HideAndDontSave };
                host.SetActive(false);
                var skin = host.AddComponent<SkinnedMeshRenderer>();
                skin.enabled = false;
                var bones = new Transform[bindPoses.Length];
                for (var index = 0; index < bones.Length; index++)
                {
                    var matrix = bindPoses[index].inverse;
                    var bone = new GameObject("Bind proxy").transform;
                    bone.SetParent(host.transform, false);
                    var x = (Vector3)matrix.GetColumn(0);
                    var y = (Vector3)matrix.GetColumn(1);
                    var z = (Vector3)matrix.GetColumn(2);
                    var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
                    if (scale.x < 1e-8f || scale.y < 1e-8f || scale.z < 1e-8f) throw new InvalidOperationException("A body bindpose is singular.");
                    if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f) scale.x = -scale.x;
                    bone.localPosition = matrix.GetColumn(3);
                    bone.localRotation = Quaternion.LookRotation(z / scale.z, y / scale.y);
                    bone.localScale = scale;
                    var identity = bone.localToWorldMatrix * bindPoses[index];
                    for (var component = 0; component < 16; component++)
                    {
                        if (!BodyShapeFinite(identity[component]) || Mathf.Abs(identity[component] - Matrix4x4.identity[component]) > 0.0001f)
                            throw new InvalidOperationException("A body bindpose did not form an identity skin matrix.");
                    }
                    bones[index] = bone;
                }
                if (bones.Length == 0) throw new InvalidOperationException("A body mesh has no bind bones.");
                skin.sharedMesh = mesh;
                skin.bones = bones;
                skin.rootBone = bones[0];
                for (var index = 0; index < mesh.blendShapeCount; index++) skin.SetBlendShapeWeight(index, 0f);
                baked = new Mesh();
                skin.BakeMesh(baked);
                readable = baked;
            }

            return read(readable);
        }
        finally
        {
            if (host) { host.SetActive(false); UnityEngine.Object.Destroy(host); }
            if (baked) UnityEngine.Object.Destroy(baked);
        }
    }


    private static bool[] BodyShapeBoundary(int vertices, int[] triangles)
    {
        var result = new bool[vertices];
        foreach (var pair in BodyShapeEdges(triangles).Where(pair => pair.Value == 1))
        { result[pair.Key.Item1] = true; result[pair.Key.Item2] = true; }
        return result;
    }

    private static Dictionary<(int, int), int> BodyShapeEdges(int[] triangles, int[] groups = null)
    {
        var edges = new Dictionary<(int, int), int>();
        for (var index = 0; index < triangles.Length; index += 3)
        {
            var first = groups == null ? triangles[index] : groups[triangles[index]];
            var second = groups == null ? triangles[index + 1] : groups[triangles[index + 1]];
            var third = groups == null ? triangles[index + 2] : groups[triangles[index + 2]];
            if (first == second || second == third || first == third) continue;
            Add(first, second); Add(second, third); Add(third, first);
        }
        return edges;
        void Add(int first, int second)
        {
            var key = first < second ? (first, second) : (second, first);
            edges.TryGetValue(key, out var count);
            edges[key] = count + 1;
        }
    }

    private sealed class BodyShapeSample(Vector3 point, Vector3 delta, float weight)
    {
        internal readonly Vector3 Point = point;
        internal readonly Vector3 Delta = delta;
        internal readonly float Weight = weight;
    }

    private sealed class BodyShapeField
    {
        private readonly Dictionary<string, BodyShapeSample[]> _samples;
        private readonly Dictionary<string, float> _support;
        private readonly BodyShapeField _parent;
        private BodyShapeField(Dictionary<string, List<BodyShapeSample>> samples, BodyShapeField parent = null)
        {
            _parent = parent;
            _samples = samples.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            _support = [];
            foreach (var pair in _samples)
            {
                var values = pair.Value;
                var minimum = values[0].Point;
                var maximum = minimum;
                foreach (var sample in values)
                {
                    minimum = Vector3.Min(minimum, sample.Point);
                    maximum = Vector3.Max(maximum, sample.Point);
                }
                var spread = maximum - minimum;
                var axis = spread.y > spread.x ? 1 : 0;
                if (spread.z > spread[axis]) axis = 2;
                var order = Enumerable.Range(0, values.Length).ToArray();
                Array.Sort(order, (first, second) =>
                {
                    var comparison = values[first].Point[axis].CompareTo(values[second].Point[axis]);
                    return comparison != 0 ? comparison : first.CompareTo(second);
                });
                var distances = new List<float>();
                for (var position = 0; position < order.Length; position++)
                {
                    var sample = values[order[position]];
                    var distance = float.MaxValue;
                    for (var direction = -1; direction <= 1; direction += 2)
                    for (var next = position + direction; next >= 0 && next < order.Length; next += direction)
                    {
                        var other = values[order[next]];
                        var along = other.Point[axis] - sample.Point[axis];
                        if (along * along >= distance) break;
                        var squared = (other.Point - sample.Point).sqrMagnitude;
                        if (squared > 1e-8f) distance = Mathf.Min(distance, squared);
                    }
                    if (distance < float.MaxValue) distances.Add(Mathf.Sqrt(distance));
                }
                if (distances.Count == 0) throw new InvalidOperationException("A native muscle field has no spatial support: " + pair.Key);
                distances.Sort();
                _support.Add(pair.Key, distances[(distances.Count - 1) * 3 / 4] * 3f);
            }
        }
        internal static BodyShapeField Create(params GameObject[] prefabs)
        {
            var samples = new Dictionary<string, List<BodyShapeSample>>(StringComparer.Ordinal);
            var seen = new HashSet<Mesh>();
            foreach (var skin in prefabs.SelectMany(prefab => prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)))
            {
                var mesh = skin.sharedMesh;
                if (!mesh || !mesh.name.EndsWith("LOD0", StringComparison.Ordinal) || !seen.Add(mesh) || mesh.GetBlendShapeIndex("Muscular") < 0) continue;
                var rest = ReadBodyShapeRest(mesh);
                var bones = skin.bones.Take(rest.BindPoses.Length).ToArray();
                if (bones.Length < rest.BindPoses.Length || rest.Muscular == null) throw new InvalidOperationException("A native muscle calibration skin is incomplete.");
                var protectedBones = bones.Select(IsProtectedBodyBone).ToArray();
                var lengths = bones.Select(BodyShapeBoneLength).ToArray();
                for (var vertex = 0; vertex < rest.Vertices.Length; vertex++)
                {
                    var weights = rest.Weights[vertex];
                    for (var influence = 0; influence < 4; influence++)
                    {
                        var weight = BodyShapeWeight(weights, influence);
                        if (weight <= 0f) continue;
                        var index = BodyShapeWeightIndex(weights, influence);
                        if (protectedBones[index]) continue;
                        var name = bones[index].name;
                        if (!samples.TryGetValue(name, out var list)) samples.Add(name, list = []);
                        list.Add(new BodyShapeSample(rest.BindPoses[index].MultiplyPoint3x4(rest.Vertices[vertex]) / lengths[index],
                            rest.BindPoses[index].MultiplyVector(rest.Muscular[vertex]) / lengths[index], weight));
                    }
                }
            }
            if (samples.Count == 0) throw new InvalidOperationException("The native muscle field contains no samples.");
            return new BodyShapeField(samples);
        }
        private bool Contains(string bone) => _samples.ContainsKey(bone) || _parent != null && _parent.Contains(bone);
        internal int ResolveAnchor(Transform[] bones, int influence)
        {
            if (Contains(bones[influence].name)) return influence;
            for (var bone = bones[influence].parent; bone; bone = bone.parent)
            {
                if (!Contains(bone.name)) continue;
                var index = Array.IndexOf(bones, bone);
                if (index >= 0) return index;
            }
            throw new InvalidOperationException("The native muscle field has no mapped ancestor for: " + bones[influence].name);
        }
        internal static BodyShapeField Complete(BodyShapeField parent, List<BodyShapeRenderer> rows)
        {
            if (parent == null) return null;
            var samples = new Dictionary<string, List<BodyShapeSample>>(StringComparer.Ordinal);
            var seen = new List<(Mesh Original, Transform[] Bones)>();
            foreach (var row in rows)
            {
                if (row.Identity || !row.UseNativeFrame || !row.Original.name.EndsWith("LOD0", StringComparison.Ordinal) ||
                    row.Original.GetBlendShapeIndex("Muscular") < 0) continue;
                if (row.Original.GetBlendShapeIndex(BodyVolumePositive) >= 0 ||
                    row.Original.GetBlendShapeIndex(BodyVolumeNegative) >= 0)
                    throw new InvalidOperationException("A native muscle source still contains an owned body frame.");
                var sourceBones = row.Renderer.bones;
                if (seen.Any(source => source.Original == row.Original && source.Bones.SequenceEqual(sourceBones))) continue;
                var rest = ReadBodyShapeRest(row.Original);
                var bones = sourceBones.Take(rest.BindPoses.Length).ToArray();
                if (bones.Length < rest.BindPoses.Length || rest.Muscular == null)
                    throw new InvalidOperationException("An actor's native muscle source is incomplete.");
                seen.Add((row.Original, sourceBones));
                var eligible = bones.Select(bone => !IsProtectedBodyBone(bone)).ToArray();
                if (!eligible.Any(value => value)) continue;
                var lengths = bones.Select(BodyShapeBoneLength).ToArray();
                for (var vertex = 0; vertex < rest.Vertices.Length; vertex++)
                {
                    var weights = rest.Weights[vertex];
                    for (var influence = 0; influence < 4; influence++)
                    {
                        var weight = BodyShapeWeight(weights, influence);
                        if (weight <= 0f) continue;
                        var index = BodyShapeWeightIndex(weights, influence);
                        if (!eligible[index]) continue;
                        var name = bones[index].name;
                        if (!samples.TryGetValue(name, out var list)) samples.Add(name, list = []);
                        list.Add(new BodyShapeSample(rest.BindPoses[index].MultiplyPoint3x4(rest.Vertices[vertex]) / lengths[index],
                            rest.BindPoses[index].MultiplyVector(rest.Muscular[vertex]) / lengths[index], weight));
                    }
                }
            }
            return samples.Count == 0 ? parent : new BodyShapeField(samples, parent);
        }
        internal Vector3 Evaluate(string bone, Vector3 point)
        {
            if (!_samples.TryGetValue(bone, out var samples))
            {
                if (_parent != null) return _parent.Evaluate(bone, point);
                throw new InvalidOperationException("The native muscle field has no bone: " + bone);
            }
            var distances = new[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
            var closest = new BodyShapeSample[4];
            foreach (var sample in samples)
            {
                var distance = (sample.Point - point).sqrMagnitude;
                if (distance < 1e-10f) return sample.Delta;
                for (var index = 0; index < 4; index++)
                {
                    if (distance >= distances[index]) continue;
                    for (var next = 3; next > index; next--) { distances[next] = distances[next - 1]; closest[next] = closest[next - 1]; }
                    distances[index] = distance;
                    closest[index] = sample;
                    break;
                }
            }
            var support = _support[bone];
            var confidence = _parent == null ? Mathf.Clamp01(1f - distances[0] / (support * support)) : 1f;
            if (_parent == null && confidence == 0f) return Vector3.zero;
            var blend = _parent == null ? 0f : Mathf.Clamp01((Mathf.Sqrt(distances[0]) - support) / support);
            blend = blend * blend * (3f - 2f * blend);
            if (blend == 1f) return _parent.Contains(bone) ? _parent.Evaluate(bone, point) : Vector3.zero;
            var result = Vector3.zero;
            var total = 0f;
            for (var index = 0; index < 4 && closest[index] != null; index++)
            {
                var weight = closest[index].Weight / distances[index];
                result += closest[index].Delta * weight;
                total += weight;
            }
            var local = result / total;
            if (_parent == null) return local * confidence * confidence;
            if (blend == 0f) return local;
            return Vector3.Lerp(local, _parent.Contains(bone) ? _parent.Evaluate(bone, point) : Vector3.zero, blend);
        }
    }

    private sealed class BodyShapeRenderer
    {
        internal SkinnedMeshRenderer Renderer;
        internal CustomModels.SkinMeshSource Source;
        internal Mesh Original;
        internal bool PreserveNativeWeight;
        internal bool UseNativeFrame;
        internal bool Identity;
    }

    private sealed class BodyShapeSeam(Vector3 point, Vector3 delta, Vector3 normal, Vector3 tangent, bool native,
        Matrix4x4? derivative = null)
    {
        internal readonly Vector3 Point = point;
        internal readonly Vector3 Delta = delta, Normal = normal, Tangent = tangent;
        internal readonly bool Native = native;
        internal readonly Matrix4x4? Derivative = derivative;
    }

    private sealed class BodyShapeSeams
    {
        private const float Weld = 0.0001f;
        private readonly Dictionary<(string, int, int, int), List<BodyShapeSeam>> _points = [];
        internal void Add(string bone, Vector3 point, Vector3 delta, Vector3 normal, Vector3 tangent, bool native,
            Matrix4x4? derivative = null)
        {
            var key = Key(bone, point);
            if (!_points.TryGetValue(key, out var list)) _points.Add(key, list = []);
            list.Add(new BodyShapeSeam(point, delta, normal, tangent, native, derivative));
        }
        internal bool TryGet(string bone, Vector3 point, out BodyShapeSeam seam)
        {
            seam = null;
            var key = Key(bone, point);
            BodyShapeSeam match = null;
            var minimum = Weld * Weld;
            for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
            {
                if (!_points.TryGetValue((bone, key.Item2 + x, key.Item3 + y, key.Item4 + z), out var list)) continue;
                foreach (var candidate in list)
                {
                    var distance = (candidate.Point - point).sqrMagnitude;
                    if (distance > Weld * Weld || match != null && match.Native && !candidate.Native) continue;
                    if (match == null || candidate.Native && !match.Native || distance < minimum) { minimum = distance; match = candidate; }
                }
            }
            if (match == null) return false;
            seam = match;
            return true;
        }
        private static (string, int, int, int) Key(string bone, Vector3 point) =>
            (bone, Mathf.FloorToInt(point.x / Weld), Mathf.FloorToInt(point.y / Weld), Mathf.FloorToInt(point.z / Weld));
    }

    private static bool IsBodyIdentityPart(GraphicsCharacterDefinitions.BodyPart part) => part is
        GraphicsCharacterDefinitions.BodyPart.Helmet or GraphicsCharacterDefinitions.BodyPart.Hair or
        GraphicsCharacterDefinitions.BodyPart.Beard or
        GraphicsCharacterDefinitions.BodyPart.Horns or GraphicsCharacterDefinitions.BodyPart.Tail;

    private static bool IsProtectedBodyPart(GraphicsCharacterDefinitions.BodyPart part) =>
        part == GraphicsCharacterDefinitions.BodyPart.Head || IsBodyIdentityPart(part);

    private static bool IsProtectedBodyRenderer(GraphicsCharacter graphics, SkinnedMeshRenderer renderer)
    {
        foreach (var slots in new[] { graphics.ShapeSkinnedMeshRenderersPerBodySlot, graphics.ArmorSkinnedMeshRenderersPerBodySlot })
        {
            if (slots == null) continue;
            for (var slot = 0; slot < slots.Length; slot++)
            {
                if (IsProtectedBodyPart((GraphicsCharacterDefinitions.BodyPart)slot) &&
                    slots[slot] != null && slots[slot].Contains(renderer)) return true;
            }
        }
        return false;
    }

    private static List<BodyShapeRenderer> CaptureBodyShapeRenderers(GraphicsCharacter graphics)
    {
        var result = new Dictionary<SkinnedMeshRenderer, BodyShapeRenderer>();
        // Native frame eligibility follows the presentation's original musculature capability.
        var useNativeFrame = !TryGetRaceIdentity(graphics.RulesetCharacter, out var race, out var subRace) ||
            HasNativeBodyShape(subRace?.RacePresentation ?? race.RacePresentation);
        foreach (var slots in new[] { graphics.ShapeSkinnedMeshRenderersPerBodySlot, graphics.ArmorSkinnedMeshRenderersPerBodySlot })
        {
            if (slots == null) continue;
            for (var slot = 0; slot < slots.Length; slot++)
            {
                if (slots[slot] == null) continue;
                var protectedPart = IsProtectedBodyPart((GraphicsCharacterDefinitions.BodyPart)slot);
                var identityPart = IsBodyIdentityPart((GraphicsCharacterDefinitions.BodyPart)slot);
                foreach (var renderer in slots[slot])
                {
                    if (!renderer || !renderer.sharedMesh) continue;
                    if (result.TryGetValue(renderer, out var row))
                    {
                        row.PreserveNativeWeight |= protectedPart;
                        row.Identity |= identityPart;
                    }
                    else result.Add(renderer, new BodyShapeRenderer
                    { Renderer = renderer, PreserveNativeWeight = protectedPart, Identity = identityPart,
                        UseNativeFrame = useNativeFrame });
                }
            }
        }
        var sources = CustomModels.SkinMeshSource.Capture(graphics, result.Keys.ToHashSet());
        foreach (var row in result.Values)
        {
            var single = sources ?? CustomModels.SkinMeshSource.Capture(graphics, new HashSet<SkinnedMeshRenderer> { row.Renderer });
            if (single != null && single.TryGetValue(row.Renderer, out var source) && source.AcceptsCurrentMesh())
            { row.Source = source; row.Original = source.Original; }
        }
        return result.Values.Where(row => row.Original).ToList();
    }

    private static int BodyShapeDominantBone(BoneWeight weights, Transform[] bones)
    {
        var result = -1;
        var maximum = 0f;
        for (var influence = 0; influence < 4; influence++)
        {
            var weight = BodyShapeWeight(weights, influence);
            var index = BodyShapeWeightIndex(weights, influence);
            if (weight > maximum || weight > 0f && weight == maximum && result >= 0 && string.CompareOrdinal(bones[index].name, bones[result].name) < 0)
            { maximum = weight; result = index; }
        }
        return result;
    }

    private static float BodyShapeBodyWeight(BoneWeight weights, bool[] protectedBones, out float total)
    {
        total = 0f;
        var body = 0f;
        for (var influence = 0; influence < 4; influence++)
        {
            var weight = BodyShapeWeight(weights, influence);
            if (weight <= 0f) continue;
            var index = BodyShapeWeightIndex(weights, influence);
            if (index < 0 || index >= protectedBones.Length)
                throw new InvalidOperationException("A skin weight references a missing bindpose.");
            total += weight;
            if (!protectedBones[index]) body += weight;
        }
        return total > 0f ? Mathf.Clamp01(body / total) : 0f;
    }

    private sealed class BodyShapePresentation : MonoBehaviour
    {
        private GraphicsCharacter _graphics;
        private BodyShapeField _baseField, _field;
        private Animator _fieldRig;
        private Avatar _fieldAvatar;
        private RulesetCharacter _fieldActor;
        private readonly List<FieldSource> _fieldSources = [];
        private bool _fieldSourcesReady;
        private bool _supplemental;
        private readonly List<BodyShapePiece> _pieces = [];
        private float _value;
        private float _default;
        private bool _busy, _refreshPending;
        internal bool IsPending => _refreshPending;
        internal void Bind(GraphicsCharacter graphics) => _graphics = graphics;
        internal void DeferRefresh()
        {
            _refreshPending = _characterModelRefreshPending = true;
            BodyShapeOwners.Add(this);
        }
        internal void RefreshPending()
        {
            if (!_refreshPending) return;
            RefreshBodyShape(_graphics);
            if (_refreshPending) _characterModelRefreshPending = true;
        }
        private sealed class FieldSource(BodyShapeRenderer row)
        {
            private readonly SkinnedMeshRenderer _renderer = row.Renderer;
            private readonly Mesh _original = row.Original;
            private readonly Transform[] _bones = row.Renderer.bones;
            private readonly bool _preserveNativeWeight = row.PreserveNativeWeight;
            private readonly bool _useNativeFrame = row.UseNativeFrame;
            internal bool Matches(BodyShapeRenderer current) => current.Renderer == _renderer &&
                current.Original == _original && current.PreserveNativeWeight == _preserveNativeWeight &&
                current.UseNativeFrame == _useNativeFrame &&
                current.Source != null && current.Source.MatchesBones(_bones);
        }
        private bool FieldSourcesCurrent(GraphicsCharacter graphics, BodyShapeField field, List<BodyShapeRenderer> sources, bool supplemental) =>
            _fieldSourcesReady && _supplemental == supplemental && _baseField == field && _fieldRig == graphics.Animator &&
            _fieldAvatar == (graphics.Animator ? graphics.Animator.avatar : null) && ReferenceEquals(_fieldActor, graphics.RulesetCharacter) &&
            sources.Count == _fieldSources.Count && _fieldSources.All(source => sources.Any(source.Matches));
        internal bool HasSources => _pieces.Count != 0;
        internal bool IsBodyOverlayOf(SkinnedMeshRenderer renderer, Mesh original, CustomModels.SkinMeshSource source) =>
            _pieces.Any(piece => piece.Renderer == renderer && piece.IsOverlayOf(original, source));
        internal void Refresh(GraphicsCharacter graphics, BodyShapeField field, float value, float defaultValue, bool supplemental)
        {
            if (_busy || _bodyShapesUnloading) return;
            _refreshPending = false;
            BodyShapeOwners.Add(this);
            _busy = true;
            try
            {
                _graphics = graphics;
                _value = value;
                _default = defaultValue;
                var rows = CaptureBodyShapeRenderers(graphics);
                var restored = false;
                foreach (var piece in _pieces.ToArray())
                {
                    var row = rows.FirstOrDefault(item => item.Renderer == piece.Renderer);
                    if (row != null && piece.IsCurrent(row)) row.Original = piece.Original;
                    else
                    {
                        if (!piece.Restore(false)) throw new InvalidOperationException("A changed body target has not finished restoring.");
                        _pieces.Remove(piece);
                        restored = true;
                    }
                }
                if (restored)
                {
                    rows = CaptureBodyShapeRenderers(graphics);
                    foreach (var piece in _pieces)
                    {
                        var row = rows.FirstOrDefault(item => item.Renderer == piece.Renderer);
                        if (row == null || !piece.IsCurrent(row))
                            throw new InvalidOperationException("A body source changed while restoring a target.");
                        row.Original = piece.Original;
                    }
                }
                var sources = rows.Where(row => !row.Identity && row.UseNativeFrame && row.Original.GetBlendShapeIndex("Muscular") >= 0).ToList();
                if (!FieldSourcesCurrent(graphics, field, sources, supplemental))
                {
                    Release(false);
                    if (_pieces.Count != 0) throw new InvalidOperationException("Native body sources have not finished restoring.");
                    rows = CaptureBodyShapeRenderers(graphics);
                    sources = rows.Where(row => !row.Identity && row.UseNativeFrame && row.Original.GetBlendShapeIndex("Muscular") >= 0).ToList();
                    try { _field = supplemental ? BodyShapeField.Complete(field, sources) : null; }
                    catch (Exception exception)
                    {
                        // Native rows use their own frames; unavailable calibration only defers supplemental rows.
                        _field = null;
                        ReportBodyShapeError("actor-native-field", exception);
                    }
                    _baseField = field;
                    _supplemental = supplemental;
                    _fieldRig = graphics.Animator;
                    _fieldAvatar = _fieldRig ? _fieldRig.avatar : null;
                    _fieldActor = graphics.RulesetCharacter;
                    _fieldSources.AddRange(sources.Select(row => new FieldSource(row)));
                    _fieldSourcesReady = true;
                }
                BodyShapeSeams seams = null;
                var readingSeams = false;
                BodyShapeSeams GetSeams()
                {
                    if (seams != null) return seams;
                    readingSeams = true;
                    seams = new BodyShapeSeams();
                    foreach (var row in rows)
                    {
                        if (row.Identity || !row.UseNativeFrame || row.Original.GetBlendShapeIndex("Muscular") < 0) continue;
                        var bones = row.Renderer.bones;
                        var rest = ReadBodyShapeRest(row.Original);
                        var protectedBones = bones.Select(IsProtectedBodyBone).ToArray();
                        for (var vertex = 0; vertex < rest.Vertices.Length; vertex++)
                        {
                            if (!rest.Boundary[vertex]) continue;
                            var dominant = BodyShapeDominantBone(rest.Weights[vertex], bones);
                            if (dominant < 0) continue;
                            var body = BodyShapeBodyWeight(rest.Weights[vertex], protectedBones, out _);
                            seams.Add(bones[dominant].name, rest.BindPoses[dominant].MultiplyPoint3x4(rest.Vertices[vertex]),
                                rest.BindPoses[dominant].MultiplyVector(rest.Muscular[vertex] * body),
                                rest.BindPoses[dominant].inverse.transpose.MultiplyVector(rest.MuscularNormals[vertex] * body),
                                rest.BindPoses[dominant].MultiplyVector(rest.MuscularTangents[vertex] * body), true);
                        }
                    }
                    readingSeams = false;
                    return seams;
                }
                foreach (var row in rows)
                {
                    if (_pieces.Any(piece => piece.Renderer == row.Renderer)) continue;
                    if (!supplemental)
                    {
                        var native = row.UseNativeFrame ? row.Original.GetBlendShapeIndex("Muscular") : -1;
                        if (native < 0) continue;
                        var baseline = row.PreserveNativeWeight ? row.Renderer.GetBlendShapeWeight(native) : defaultValue;
                        if (value == baseline) continue;
                    }
                    try { BodyShapePiece.Create(graphics, row, _field, GetSeams, _pieces, supplemental); }
                    catch (Exception exception)
                    {
                        if (readingSeams) throw;
                        ReportBodyShapeError(row.Original.name, exception);
                    }
                }
                foreach (var piece in _pieces) piece.SetValue(value, defaultValue);
            }
            catch (Exception exception) { ReportBodyShapeError("presentation", exception); }
            finally { _busy = false; SynchronizeCharacterModelRefreshPending(); }
        }
        internal void Release(bool destroying)
        {
            _refreshPending = false;
            foreach (var piece in _pieces.ToArray())
            {
                try { if (piece.Restore(destroying)) _pieces.Remove(piece); }
                catch (Exception exception) { ReportBodyShapeError(piece.Original.name + "-restore", exception); }
            }
            _baseField = _field = null;
            _fieldRig = null;
            _fieldAvatar = null;
            _fieldActor = null;
            _fieldSources.Clear();
            _fieldSourcesReady = false;
            _supplemental = false;
            if (_pieces.Count == 0 && _bodyShapesUnloading) BodyShapeOwners.Remove(this);
            SynchronizeCharacterModelRefreshPending();
        }
        private void OnEnable() { if (!_bodyShapesUnloading && _graphics && !_busy) RefreshBodyShape(_graphics); }
        private void OnDisable() { Release(false); }
        private void OnDestroy() { Release(true); if (_pieces.Count == 0) BodyShapeOwners.Remove(this); }
        private void LateUpdate()
        {
            if (_bodyShapesUnloading || !_graphics || _busy) return;
            foreach (var piece in _pieces) piece.SetValue(_value, _default);
        }
    }

    private static bool SwitchSkinSource(GraphicsCharacter graphics, SkinnedMeshRenderer renderer,
        CustomModels.SkinMeshSource source, Mesh replacement, bool restart, bool ownedReplacement,
        ref CustomModels.SkinMeshSource capturedSource, ref bool clothSourceInstalled,
        ref CustomModels.SkinMeshSource.SourceGraph graph)
    {
        if (graph != null) throw new InvalidOperationException("A previous native skin transition still has an owner.");
        if (!renderer || !replacement || !source.OwnerCurrent || !source.AcceptsCurrentMesh()) return false;
        var changes = new Dictionary<SkinnedMeshRenderer, (CustomModels.SkinMeshSource Source, Mesh Mesh)>
            { [renderer] = (source, replacement) };
        graph = CustomModels.SkinMeshSource.PrepareMeshes(graphics, changes);
        graph.Apply(restart);
        capturedSource = graph.Installed[renderer];
        clothSourceInstalled = source.ClothOwner && ownedReplacement;
        return true;
    }

    private sealed class BodyShapePiece
    {
        internal readonly SkinnedMeshRenderer Renderer;
        internal readonly Mesh Original;
        private readonly GraphicsCharacter _graphics;
        private readonly Mesh _owned;
        private readonly Bounds _originalBounds;
        private readonly Bounds _appliedBounds;
        private readonly bool _identity, _preserveNativeWeight, _useNativeFrame;
        private readonly Transform[] _bones;
        private CustomModels.SkinMeshSource _source;
        private CustomModels.SkinMeshSource.SourceGraph _sourceGraph;
        private bool _clothSourceInstalled;
        private readonly int _positive;
        private readonly int _negative;
        private readonly float? _preservedNativeWeight;
        private BodyShapePiece(GraphicsCharacter graphics, BodyShapeRenderer row, Mesh owned, float expansion)
        {
            _graphics = graphics;
            Renderer = row.Renderer;
            Original = row.Original;
            _owned = owned;
            _source = row.Source;
            _identity = row.Identity;
            _preserveNativeWeight = row.PreserveNativeWeight;
            _useNativeFrame = row.UseNativeFrame;
            _bones = row.Renderer.bones;
            _positive = owned.GetBlendShapeIndex(BodyVolumePositive);
            _negative = owned.GetBlendShapeIndex(BodyVolumeNegative);
            var native = Original.GetBlendShapeIndex("Muscular");
            if (row.PreserveNativeWeight && native >= 0) _preservedNativeWeight = Renderer.GetBlendShapeWeight(native);
            _originalBounds = Renderer.localBounds;
            var bounds = _originalBounds;
            bounds.Expand(expansion * 2f);
            _appliedBounds = bounds;
        }
        internal static void Create(GraphicsCharacter graphics, BodyShapeRenderer row, BodyShapeField field, Func<BodyShapeSeams> getSeams,
            List<BodyShapePiece> pieces, bool supplemental)
        {
            var hasNativeFrame = row.UseNativeFrame && row.Original.GetBlendShapeIndex("Muscular") >= 0;
            if ((row.Identity || !supplemental || field == null) && !hasNativeFrame) return;
            if ((row.PreserveNativeWeight || !hasNativeFrame) &&
                row.Renderer.bones.All(IsProtectedBodyBone)) return;
            var rest = ReadBodyShapeRest(row.Original);
            var bones = row.Renderer.bones.Take(rest.BindPoses.Length).ToArray();
            if (bones.Length < rest.BindPoses.Length) throw new InvalidOperationException("The body's original bone mapping is incomplete.");
            var protectedBones = bones.Select(IsProtectedBodyBone).ToArray();
            // With no protected positive influences, native correction has body / total = 1 everywhere.
            if (hasNativeFrame && !row.PreserveNativeWeight && rest.AllVerticesWeighted &&
                rest.WeightedBones is { } usedBones && usedBones.All(index => !protectedBones[index])) return;
            var lengths = !hasNativeFrame ? bones.Select(BodyShapeBoneLength).ToArray() : null;
            var inverse = !hasNativeFrame ? rest.BindPoses.Select(matrix => matrix.inverse).ToArray() : null;
            var anchors = !hasNativeFrame ? Enumerable.Repeat(-1, bones.Length).ToArray() : null;
            var delta = new Vector3[rest.Vertices.Length];
            var protectedVertices = new bool[rest.Vertices.Length];
            var nativeCorrection = hasNativeFrame && !row.PreserveNativeWeight;
            var normalCorrection = hasNativeFrame ? new Vector3[delta.Length] : null;
            var tangentCorrection = hasNativeFrame ? new Vector3[delta.Length] : null;
            var seamNormals = !hasNativeFrame ? new Dictionary<int,
                (Vector3 Normal, Vector3 Tangent, Vector3 NegativeNormal, Vector3 NegativeTangent)>() : null;
            var baseline = row.PreserveNativeWeight && rest.Muscular != null
                ? row.Renderer.GetBlendShapeWeight(row.Original.GetBlendShapeIndex("Muscular")) / 100f : 0f;
            var maximum = 0f;
            var changed = false;
            for (var vertex = 0; vertex < delta.Length; vertex++)
            {
                var weights = rest.Weights[vertex];
                var body = BodyShapeBodyWeight(weights, protectedBones, out var total);
                protectedVertices[vertex] = body == 0f;
                if (hasNativeFrame)
                {
                    // Keep native frames intact; mask their contribution through an owned additive frame.
                    var factor = nativeCorrection ? body - 1f : body;
                    delta[vertex] = rest.Muscular[vertex] * factor;
                    normalCorrection[vertex] = rest.MuscularNormals[vertex] * factor;
                    tangentCorrection[vertex] = rest.MuscularTangents[vertex] * factor;
                    changed |= normalCorrection[vertex].sqrMagnitude > 0f || tangentCorrection[vertex].sqrMagnitude > 0f;
                }
                else if (body > 0f)
                {
                    delta[vertex] = Evaluate(rest.Vertices[vertex], weights, total);
                }
                if (!hasNativeFrame && body > 0f && rest.Boundary[vertex])
                {
                    var dominant = BodyShapeDominantBone(weights, bones);
                    if (dominant >= 0)
                    {
                        var basis = rest.BindPoses[dominant];
                        var point = basis.MultiplyPoint3x4(rest.Vertices[vertex]);
                        var seams = getSeams();
                        if (!seams.TryGet(bones[dominant].name, point, out var seam))
                        {
                            // Different pieces share this deformation derivative at their physical cut.
                            var step = lengths[dominant] * 0.001f;
                            var derivative = Matrix4x4.zero;
                            Vector3 At(Vector3 position) => basis.MultiplyVector(
                                Evaluate(inverse[dominant].MultiplyPoint3x4(position), weights, total));
                            for (var axis = 0; axis < 3; axis++)
                            {
                                var offset = Vector3.zero; offset[axis] = step;
                                var column = (At(point + offset) - At(point - offset)) / (2f * step);
                                if (!BodyShapeFinite(column)) throw new InvalidOperationException("A body field has a nonfinite surface derivative.");
                                derivative.SetColumn(axis, new Vector4(column.x, column.y, column.z, 0f));
                            }
                            seams.Add(bones[dominant].name, point, basis.MultiplyVector(delta[vertex]),
                                Vector3.zero, Vector3.zero, false, derivative);
                            if (!seams.TryGet(bones[dominant].name, point, out seam))
                                throw new InvalidOperationException("A body field lost its shared surface derivative.");
                        }
                        delta[vertex] = inverse[dominant].MultiplyVector(seam.Delta);
                        if (seam.Derivative is { } derivativeFrame && rest.Tangents.Length == delta.Length)
                        {
                            var nativeNormal = rest.Normals[vertex] + (baseline == 0f ? Vector3.zero : rest.MuscularNormals[vertex] * baseline);
                            var nativeTangent = (Vector3)rest.Tangents[vertex] + (baseline == 0f ? Vector3.zero : rest.MuscularTangents[vertex] * baseline);
                            var positive = SurfaceFrame(derivativeFrame, basis, nativeNormal, nativeTangent, 1f);
                            var negative = SurfaceFrame(derivativeFrame, basis, nativeNormal, nativeTangent, -1f);
                            seamNormals[vertex] = (positive.Normal, positive.Tangent, negative.Normal, negative.Tangent);
                            changed |= positive.Normal.sqrMagnitude > 0f || positive.Tangent.sqrMagnitude > 0f ||
                                negative.Normal.sqrMagnitude > 0f || negative.Tangent.sqrMagnitude > 0f;
                        }
                        else
                        {
                            var normal = basis.transpose.MultiplyVector(seam.Normal);
                            var tangent = inverse[dominant].MultiplyVector(seam.Tangent);
                            seamNormals[vertex] = (normal, tangent, -normal, -tangent);
                            changed |= normal.sqrMagnitude > 0f || tangent.sqrMagnitude > 0f;
                        }
                    }
                }
                if (!BodyShapeFinite(delta[vertex])) throw new InvalidOperationException("A body-shape delta is nonfinite.");
                maximum = Mathf.Max(maximum, delta[vertex].magnitude);
                changed |= delta[vertex].sqrMagnitude > 0f;
            }
            if (!changed) return;
            var owned = UnityEngine.Object.Instantiate(row.Original);
            owned.name = row.Original.name;
            BodyShapePiece piece = null;
            try
            {
                AddFrame(owned, BodyVolumePositive, rest, delta, protectedVertices, 1f, normalCorrection, tangentCorrection, baseline, seamNormals);
                AddFrame(owned, BodyVolumeNegative, rest, delta, protectedVertices, -1f, normalCorrection, tangentCorrection, baseline, seamNormals);
                piece = new BodyShapePiece(graphics, row, owned, maximum);
                pieces.Add(piece);
                if (!piece.SwitchSource(row.Source, owned)) throw new InvalidOperationException("The renderer changed before its body morph was installed.");
                row.Renderer.localBounds = piece._appliedBounds;
            }
            catch (Exception failure)
            {
                try
                {
                    if (piece == null) UnityEngine.Object.Destroy(owned);
                    else if (piece.Restore(false)) pieces.Remove(piece);
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException("Body source installation and restoration failed.", failure, restoreFailure);
                }
                throw;
            }
            return;

            Vector3 Evaluate(Vector3 position, BoneWeight weights, float total)
            {
                var result = Vector3.zero;
                for (var influence = 0; influence < 4; influence++)
                {
                    var weight = BodyShapeWeight(weights, influence);
                    if (weight <= 0f) continue;
                    var index = BodyShapeWeightIndex(weights, influence);
                    if (protectedBones[index]) continue;
                    if (anchors[index] < 0) anchors[index] = field.ResolveAnchor(bones, index);
                    var anchor = anchors[index];
                    var point = rest.BindPoses[anchor].MultiplyPoint3x4(position) / lengths[anchor];
                    result += inverse[anchor].MultiplyVector(field.Evaluate(bones[anchor].name, point) * lengths[anchor]) * (weight / total);
                }
                return result;
            }
        }

        private static (Vector3 Normal, Vector3 Tangent) SurfaceFrame(Matrix4x4 derivative, Matrix4x4 basis,
            Vector3 normal, Vector3 tangent, float sign)
        {
            var transform = Matrix4x4.identity;
            for (var row = 0; row < 3; row++) for (var column = 0; column < 3; column++)
                transform[row, column] += derivative[row, column] * sign;
            if (!BodyShapeFinite(transform.determinant) || transform.determinant == 0f)
                throw new InvalidOperationException("A body field has a singular surface derivative.");
            var inverseBasis = basis.inverse;
            var transformedNormal = transform.inverse.transpose.MultiplyVector(inverseBasis.transpose.MultiplyVector(normal)).normalized;
            var transformedTangent = transform.MultiplyVector(basis.MultiplyVector(tangent));
            transformedTangent = (transformedTangent - transformedNormal * Vector3.Dot(transformedNormal, transformedTangent)).normalized;
            var targetNormal = basis.transpose.MultiplyVector(transformedNormal).normalized;
            var targetTangent = inverseBasis.MultiplyVector(transformedTangent);
            targetTangent = (targetTangent - targetNormal * Vector3.Dot(targetNormal, targetTangent)).normalized;
            if (!BodyShapeFinite(targetNormal) || !BodyShapeFinite(targetTangent) ||
                targetNormal.sqrMagnitude < 0.99f || targetTangent.sqrMagnitude < 0.99f)
                throw new InvalidOperationException("A body field has an invalid surface frame.");
            return (targetNormal - normal, targetTangent - tangent);
        }
        internal bool IsOverlayOf(Mesh original, CustomModels.SkinMeshSource source) =>
            _sourceGraph?.IsApplied == true && Original == original && Renderer && source != null && source.MatchesBones(_bones) &&
            source.Original == _owned && source.OwnerCurrent && source.AcceptsCurrentMesh();
        internal bool IsCurrent(BodyShapeRenderer row)
        {
            var source = row.Source;
            if (_sourceGraph?.IsApplied != true || row.Renderer != Renderer || row.Identity != _identity || row.PreserveNativeWeight != _preserveNativeWeight ||
                row.UseNativeFrame != _useNativeFrame ||
                source == null || !source.MatchesBones(_bones) || source.Original != _owned || !source.AcceptsCurrentMesh()) return false;
            _source = source;
            return true;
        }
        internal void SetValue(float value, float defaultValue)
        {
            if (_sourceGraph?.IsApplied != true || !Renderer || !_owned || !_source.OwnerCurrent || !_source.AcceptsCurrentMesh()) return;
            value -= _preservedNativeWeight ?? defaultValue;
            Renderer.SetBlendShapeWeight(_positive, Mathf.Max(0f, value));
            Renderer.SetBlendShapeWeight(_negative, Mathf.Max(0f, -value));
        }
        internal bool Restore(bool destroying)
        {
            if (_sourceGraph != null)
            {
                _sourceGraph.Restore(!destroying);
                _sourceGraph = null;
                _clothSourceInstalled = false;
            }
            if (Renderer && _owned)
            {
                var sources = CustomModels.SkinMeshSource.Capture(_graphics, new HashSet<SkinnedMeshRenderer> { Renderer });
                CustomModels.SkinMeshSource source = null;
                sources?.TryGetValue(Renderer, out source);
                if (source != null && source.Original == _owned)
                {
                    return false;
                }
                else if (_clothSourceInstalled && _source.ClothOwner && (source == null || _source.OwnerCurrent)) return false;
                else if (Renderer.sharedMesh == _owned) Renderer.sharedMesh = Original;
                if (Renderer.localBounds.Equals(_appliedBounds)) Renderer.localBounds = _originalBounds;
            }
            if (_clothSourceInstalled && _source.ClothOwner && _source.OwnerCurrent) return false;
            if (_owned) UnityEngine.Object.Destroy(_owned);
            return true;
        }
        private bool SwitchSource(CustomModels.SkinMeshSource source, Mesh replacement, bool restart = true) =>
            SwitchSkinSource(_graphics, Renderer, source, replacement, restart, replacement == _owned,
                ref _source, ref _clothSourceInstalled, ref _sourceGraph);
        private static void AddFrame(Mesh owned, string name, BodyShapeRest rest, Vector3[] delta, bool[] protectedVertices, float sign,
            Vector3[] normalCorrection, Vector3[] tangentCorrection, float baseline,
            IReadOnlyDictionary<int, (Vector3 Normal, Vector3 Tangent, Vector3 NegativeNormal, Vector3 NegativeTangent)> seamNormals)
        {
            var source = rest.Vertices;
            if (baseline != 0f)
            {
                source = new Vector3[delta.Length];
                for (var index = 0; index < source.Length; index++) source[index] = rest.Vertices[index] + rest.Muscular[index] * baseline;
            }
            var positions = new Vector3[delta.Length];
            var changes = new Vector3[delta.Length];
            for (var index = 0; index < delta.Length; index++) { changes[index] = delta[index] * sign; positions[index] = source[index] + changes[index]; }
            var before = normalCorrection == null ? BodyShapeNormals(source, rest.Triangles) : null;
            var after = normalCorrection == null ? BodyShapeNormals(positions, rest.Triangles) : null;
            var normals = new Vector3[delta.Length];
            var tangents = rest.Tangents.Length == delta.Length ? new Vector3[delta.Length] : null;
            for (var index = 0; index < delta.Length; index++)
            {
                if (normalCorrection != null)
                {
                    normals[index] = normalCorrection[index] * sign;
                    if (tangents != null) tangents[index] = tangentCorrection[index] * sign;
                    continue;
                }
                if (seamNormals != null && seamNormals.TryGetValue(index, out var seam))
                {
                    normals[index] = sign > 0f ? seam.Normal : seam.NegativeNormal;
                    if (tangents != null) tangents[index] = sign > 0f ? seam.Tangent : seam.NegativeTangent;
                    continue;
                }
                if (protectedVertices[index] || rest.Normals[index].sqrMagnitude < 1e-8f) continue;
                var nativeNormal = rest.Normals[index] + (baseline == 0f ? Vector3.zero : rest.MuscularNormals[index] * baseline);
                var normal = (nativeNormal + after[index] - before[index]).normalized;
                normals[index] = normal - nativeNormal;
                if (tangents == null) continue;
                var tangent = (Vector3)rest.Tangents[index] + (baseline == 0f ? Vector3.zero : rest.MuscularTangents[index] * baseline);
                tangents[index] = Quaternion.FromToRotation(nativeNormal, normal) * tangent - tangent;
            }
            owned.AddBlendShapeFrame(name, 100f, changes, normals, tangents);
        }
    }

    private static Vector3[] BodyShapeNormals(Vector3[] vertices, int[] triangles)
    {
        var result = new Vector3[vertices.Length];
        for (var index = 0; index < triangles.Length; index += 3)
        {
            var first = triangles[index];
            var second = triangles[index + 1];
            var third = triangles[index + 2];
            var normal = Vector3.Cross(vertices[second] - vertices[first], vertices[third] - vertices[first]);
            result[first] += normal; result[second] += normal; result[third] += normal;
        }
        for (var index = 0; index < result.Length; index++) result[index].Normalize();
        return result;
    }

    private static void LoadRace([NotNull] CharacterRaceDefinition characterRaceDefinition)
    {
        Races.Add(characterRaceDefinition);
        UpdateRaceVisibility(characterRaceDefinition);
    }

    private static void LoadSubrace([NotNull] CharacterRaceDefinition characterRaceDefinition)
    {
        Subraces.Add(characterRaceDefinition);
        UpdateSubraceVisibility(characterRaceDefinition);
    }

    private static void UpdateRaceVisibility([NotNull] CharacterRaceDefinition characterRaceDefinition)
    {
        characterRaceDefinition.GuiPresentation.hidden =
            !Main.Settings.RaceEnabled.Contains(characterRaceDefinition.Name);

        characterRaceDefinition.SubRaces.ForEach(x => x.GuiPresentation.hidden =
            !Main.Settings.RaceEnabled.Contains(characterRaceDefinition.Name));
    }

    private static void UpdateSubraceVisibility([NotNull] CharacterRaceDefinition characterRaceDefinition)
    {
        characterRaceDefinition.GuiPresentation.hidden =
            !Main.Settings.SubraceEnabled.Contains(characterRaceDefinition.Name);

        if (RaceHalfElfBuilder.RaceHalfElfVariant.SubRaces.Contains(characterRaceDefinition))
        {
            var hidden = RaceHalfElfBuilder.RaceHalfElfVariant.SubRaces.All(x => x.GuiPresentation.Hidden);

            RaceHalfElfBuilder.RaceHalfElfVariant.GuiPresentation.hidden = hidden;
        }
        else if (RaceTieflingBuilder.RaceTiefling.SubRaces.Contains(characterRaceDefinition))
        {
            var hidden = RaceTieflingBuilder.RaceTiefling.SubRaces.All(x => x.GuiPresentation.Hidden);

            RaceTieflingBuilder.RaceTiefling.GuiPresentation.hidden = hidden;
        }
    }

    internal static void Switch(CharacterRaceDefinition characterRaceDefinition, bool active)
    {
        var name = characterRaceDefinition.Name;

        if (active)
        {
            Main.Settings.RaceEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.RaceEnabled.Remove(name);
        }

        UpdateRaceVisibility(characterRaceDefinition);
    }

    internal static void SwitchSubrace(CharacterRaceDefinition characterRaceDefinition, bool active)
    {
        var name = characterRaceDefinition.Name;

        if (active)
        {
            Main.Settings.SubraceEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.SubraceEnabled.Remove(name);
        }

        UpdateSubraceVisibility(characterRaceDefinition);
    }

    private static void LoadVision()
    {
        if (Main.Settings.DisableSenseDarkVisionFromAllRaces)
        {
            foreach (var featureUnlocks in DatabaseRepository.GetDatabase<CharacterRaceDefinition>()
                         .Select(crd => crd.FeatureUnlocks))
            {
                featureUnlocks.RemoveAll(x => x.FeatureDefinition == SenseDarkvision);
                // Half-orcs have a different darkvision.
                featureUnlocks.RemoveAll(x => x.FeatureDefinition == SenseDarkvision12);
            }
        }

        // ReSharper disable once InvertIf
        if (Main.Settings.DisableSenseSuperiorDarkVisionFromAllRaces)
        {
            foreach (var featureUnlocks in DatabaseRepository.GetDatabase<CharacterRaceDefinition>()
                         .Select(crd => crd.FeatureUnlocks))
            {
                featureUnlocks.RemoveAll(x => x.FeatureDefinition == SenseSuperiorDarkvision);
            }
        }
    }

    internal static void SwitchDarknessPerceptive()
    {
        var races = new List<CharacterRaceDefinition>
        {
            RaceKoboldBuilder.SubraceDarkKobold,
            SubraceDarkelfBuilder.SubraceDarkelf,
            SubraceGrayDwarfBuilder.SubraceGrayDwarf
        };

        if (Main.Settings.AddDarknessPerceptiveToDarkRaces)
        {
            foreach (var characterRaceDefinition in races
                         .Where(a => !a.FeatureUnlocks.Exists(x =>
                             x.Level == 1 && x.FeatureDefinition == AbilityCheckAffinityDarknessPerceptive)))
            {
                characterRaceDefinition.FeatureUnlocks.Add(
                    new FeatureUnlockByLevel(AbilityCheckAffinityDarknessPerceptive, 1));
            }
        }
        else
        {
            foreach (var characterRaceDefinition in races
                         .Where(a => a.FeatureUnlocks.Exists(x =>
                             x.Level == 1 && x.FeatureDefinition == AbilityCheckAffinityDarknessPerceptive)))
            {
                characterRaceDefinition.FeatureUnlocks.RemoveAll(x =>
                    x.Level == 1 && x.FeatureDefinition == AbilityCheckAffinityDarknessPerceptive);
            }
        }
    }

    internal static void SwitchDragonbornElementalBreathUsages()
    {
        var powers = DatabaseRepository.GetDatabase<FeatureDefinitionPower>()
            .Where(x =>
                x.Name.StartsWith("PowerDragonbornBreathWeapon") ||
                x.Name == "PowerFeatDragonFear");

        foreach (var power in powers)
        {
            if (Main.Settings.ChangeDragonbornElementalBreathUsages)
            {
                power.usesAbilityScoreName = AttributeDefinitions.Constitution;
                power.usesDetermination = UsesDetermination.AbilityBonusPlusFixed;
                power.fixedUsesPerRecharge = 0;
            }
            else
            {
                power.usesAbilityScoreName = AttributeDefinitions.Charisma;
                power.usesDetermination = UsesDetermination.Fixed;
                power.fixedUsesPerRecharge = 1;
            }
        }
    }

    internal static void HandleSmallRaces(BattleDefinitions.AttackEvaluationParams evaluationParams)
    {
        if (!Main.Settings.UseOfficialSmallRacesDisWithHeavyWeapons)
        {
            return;
        }

        var rulesetCharacter = evaluationParams.attacker.RulesetCharacter;

        if (IsSmallSizeIdentity(rulesetCharacter) &&
            evaluationParams.attackMode is { SourceDefinition: ItemDefinition { IsWeapon: true } itemDefinition } &&
            ValidatorsWeapon.IsHeavyWeapon(itemDefinition))
        {
            evaluationParams.attackModifier.AttackAdvantageTrends.Add(
                new TrendInfo(-1, FeatureSourceType.Unknown, "Feedback/&SmallRace", null));
        }
    }

    internal static void ApplySpeciesBaseWalkSpeed(RulesetCharacter rulesetCharacter)
    {
        var walkMode = (int)MoveMode.Walk;

        if (!Main.Settings.EnableSpeciesBaseWalkSpeed2024 ||
            rulesetCharacter is not (RulesetCharacterHero or RulesetCharacterSimulacrum) ||
            !HasFiveCellBaseWalkSpeed(rulesetCharacter) ||
            !rulesetCharacter.MoveModes.TryGetValue(walkMode, out var currentSpeed) ||
            currentSpeed >= SpeciesBaseWalkSpeed2024)
        {
            return;
        }

        rulesetCharacter.MoveModes[walkMode] = SpeciesBaseWalkSpeed2024;
        SynchronizeWalkLinkedModes(rulesetCharacter, SpeciesBaseWalkSpeed2024);
    }

    internal static bool TryFormatSpeciesBaseWalkSpeedDescription(
        FeatureDefinition feature,
        out string description,
        out int displayedSpeed)
    {
        description = null;
        displayedSpeed = 0;

        if (feature is not FeatureDefinitionMoveMode
            {
                MoveMode: MoveMode.Walk,
                Speed: 5
            } moveMode)
        {
            return false;
        }

        displayedSpeed = Main.Settings.EnableSpeciesBaseWalkSpeed2024
            ? SpeciesBaseWalkSpeed2024
            : moveMode.Speed;
        description = Gui.Format(
            moveMode.GuiPresentation.Description,
            Gui.FormatDistance(displayedSpeed));

        return true;
    }

    internal static void RefreshSpeciesBaseWalkSpeeds()
    {
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characterService == null)
        {
            return;
        }

        var rulesetCharacters = characterService.AllValidEntities
            .Select(x => x.RulesetActor)
            .Concat(characterService.PartyCharacters.Select(x => x.RulesetCharacter))
            .Concat(characterService.GuestCharacters.Select(x => x.RulesetCharacter))
            .OfType<RulesetCharacter>()
            .Where(x => x is RulesetCharacterHero or RulesetCharacterSimulacrum)
            .Distinct();

        foreach (var rulesetCharacter in rulesetCharacters)
        {
            rulesetCharacter.RefreshMoveModes();
        }
    }

    private static bool HasFiveCellBaseWalkSpeed(RulesetCharacter rulesetCharacter)
    {
        return TryGetRaceIdentity(rulesetCharacter, out var race, out var subRace) &&
               (DefinesFiveCellWalkSpeed(race) || DefinesFiveCellWalkSpeed(subRace));
    }

    private static bool DefinesFiveCellWalkSpeed(CharacterRaceDefinition race)
    {
        return race?.FeatureUnlocks.Any(x =>
            x.FeatureDefinition is FeatureDefinitionMoveMode moveMode &&
            moveMode.MoveMode == MoveMode.Walk &&
            moveMode.Speed == 5) == true;
    }

    private static bool IsSmallSizeIdentity(RulesetCharacter rulesetCharacter)
    {
        return TryGetRaceIdentity(rulesetCharacter, out var race, out _) &&
               race.SizeDefinition == DatabaseHelper.CharacterSizeDefinitions.Small;
    }

    private static void SynchronizeWalkLinkedModes(
        RulesetCharacter rulesetCharacter,
        int walkSpeed)
    {
        var implementationService =
            ServiceRepository.GetService<IRulesetImplementationService>();

        foreach (var feature in rulesetCharacter.GetMovementModifiers())
        {
            if (feature is not IMovementAffinityProvider provider ||
                !IsMovementAffinityActive(
                    rulesetCharacter,
                    feature,
                    provider,
                    implementationService))
            {
                continue;
            }

            if (provider.CanMoveOnWalls)
            {
                RaiseModeToWalkSpeed(rulesetCharacter, MoveMode.Climb, walkSpeed);
            }

            if (provider.CanFlyWithWalkSpeed)
            {
                RaiseModeToWalkSpeed(rulesetCharacter, MoveMode.Fly, walkSpeed);
            }
        }
    }

    private static bool IsMovementAffinityActive(
        RulesetCharacter rulesetCharacter,
        FeatureDefinition feature,
        IMovementAffinityProvider provider,
        IRulesetImplementationService implementationService)
    {
        if (provider.SituationalContext == SituationalContext.None)
        {
            return true;
        }

        if (implementationService == null)
        {
            return false;
        }

        var contextParams = new RulesetImplementationDefinitions.SituationalContextParams(
            provider.SituationalContext,
            rulesetCharacter,
            null,
            implementationService.FindSourceIdOfFeature(rulesetCharacter, feature),
            null,
            false,
            null);

        return implementationService.IsSituationalContextValid(contextParams);
    }

    private static void RaiseModeToWalkSpeed(
        RulesetCharacter rulesetCharacter,
        MoveMode moveMode,
        int walkSpeed)
    {
        var mode = (int)moveMode;

        if (!rulesetCharacter.MoveModes.TryGetValue(mode, out var currentSpeed) ||
            currentSpeed < walkSpeed)
        {
            rulesetCharacter.MoveModes[mode] = walkSpeed;
        }
    }

    private static bool TryGetRaceIdentity(
        RulesetCharacter rulesetCharacter,
        out CharacterRaceDefinition race,
        out CharacterRaceDefinition subRace)
    {
        if (rulesetCharacter is RulesetCharacterSimulacrum duplicate)
        {
            return SimulacrumBehavior.TryGetHumanoidIdentity(duplicate, out race, out subRace);
        }

        var hero = rulesetCharacter.GetOriginalHero();

        race = hero?.RaceDefinition;
        subRace = hero?.SubRaceDefinition;

        return race != null;
    }
}
