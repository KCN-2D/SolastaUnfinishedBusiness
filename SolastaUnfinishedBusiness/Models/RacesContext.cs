using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        _customFacesUnloading = false;
        if (_customFaceCatalog == null) _customFaceCatalogAttempted = false;
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
            graphics.GetComponent<CustomFacePresentation>() is not { IsInitializing: true })
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
        if (character is not RulesetCharacterHero &&
            (character is not RulesetCharacterSimulacrum duplicate ||
             !SimulacrumBehavior.UsesInventoryAppearance(duplicate)))
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

        var originallyAvailable = !OriginalBodyShapeCategories.TryGetValue(presentation, out var available) || available;
        var value = (float)presentation.DefaultMusculature;
        if ((enabled || originallyAvailable) && character.MorphotypeElementAdditionalValues.TryGetValue(
                MorphotypeElementDefinition.ElementCategory.Musculature, out var savedValue) &&
            BodyShapeFinite(savedValue)) value = Mathf.Clamp(savedValue, 0f, 100f);
        foreach (var renderer in graphics.BlendShapeRenderers)
        {
            if (IsProtectedBodyRenderer(graphics, renderer)) continue;
            var mesh = renderer ? renderer.sharedMesh : null;
            var index = mesh ? mesh.GetBlendShapeIndex("Muscular") : -1;
            if (index >= 0) renderer.SetBlendShapeWeight(index, value);
        }
        if (enabled) ApplySupplementalBodyShape(graphics, value, presentation.DefaultMusculature);
        else ReleaseBodyShape(graphics);
        return false;
    }

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
        UnloadCustomFaces();
        const MorphotypeElementDefinition.ElementCategory category = MorphotypeElementDefinition.ElementCategory.Musculature;
        foreach (var original in OriginalBodyShapeCategories)
        {
            if (original.Key.IsAvailable(category) == original.Value) continue;
            BodyShapeCategories(original.Key) = original.Value
                ? [.. original.Key.AvailableMorphotypeCategories, category]
                : original.Key.AvailableMorphotypeCategories.Where(value => value != category).ToArray();
        }
    }
    private static readonly ConditionalWeakTable<Mesh, string> CustomFaceFingerprints = new();
    private static readonly ConditionalWeakTable<Texture, string> CustomFaceTextureFingerprints = new();
    private static readonly HashSet<CustomFacePresentation> CustomFaceOwners = [];
    private static readonly HashSet<string> CustomFaceErrors = [];
    private static CustomFaceCatalog _customFaceCatalog;
    private static bool _customFacesUnloading, _customFaceCatalogAttempted;

    internal static void SwitchCustomFaces()
    {
        if (_customFacesUnloading) return;
        if (_customFaceCatalog == null) _customFaceCatalogAttempted = false;
        var factory = ServiceRepository.GetService<IGraphicsCharacterFactoryService>();
        if (factory == null) return;
        foreach (var graphics in factory.GraphicsCharacters.ToArray()) RefreshCustomFaces(graphics);
        if (!SettingsContext.GuiModManagerInstance.EnableCustomFaces && !CustomFaceOwners.Any(owner => owner && owner.HasSources))
        {
            _customFaceCatalog?.Dispose();
            _customFaceCatalog = null;
            _customFaceCatalogAttempted = false;
        }
    }

    internal static void RefreshCustomFaces(GraphicsCharacter graphics)
    {
        if (_customFacesUnloading || !graphics || graphics.RulesetCharacter == null) return;
        var owner = graphics.GetComponent<CustomFacePresentation>();
        try
        {
            if (!SettingsContext.GuiModManagerInstance.EnableCustomFaces)
            {
                if (owner && owner.HasSources) owner.Release(false, true);
                return;
            }
            owner ??= graphics.gameObject.AddComponent<CustomFacePresentation>();
            owner.Bind(graphics);
            if (!owner.isActiveAndEnabled || owner.IsInitializing) return;
            if (_customFaceCatalog == null)
            {
                if (_customFaceCatalogAttempted) return;
                _customFaceCatalogAttempted = true;
                _customFaceCatalog = CustomFaceCatalog.Load();
            }
            owner.Refresh(graphics, _customFaceCatalog);
        }
        catch (Exception exception) { ReportCustomFaceError("refresh", exception); }
    }

    internal static void RefreshCustomFaceMaterials(GraphicsCharacter graphics)
    {
        if (_customFacesUnloading || !graphics || !SettingsContext.GuiModManagerInstance.EnableCustomFaces) return;
        try { graphics.GetComponent<CustomFacePresentation>()?.RefreshMaterials(); }
        catch (Exception exception) { ReportCustomFaceError("materials", exception); }
    }

    internal static object SuspendCustomFaces(GraphicsCharacter graphics)
    {
        if (!graphics) return null;
        ReleaseBodyShape(graphics);
        var owner = graphics.GetComponent<CustomFacePresentation>();
        if (!owner && !_customFacesUnloading && graphics.RulesetCharacter != null &&
            SettingsContext.GuiModManagerInstance.EnableCustomFaces)
        {
            owner = graphics.gameObject.AddComponent<CustomFacePresentation>();
            owner.Bind(graphics);
        }
        if (!owner) return null;
        owner.Suspend();
        return owner;
    }

    internal static void ResumeCustomFaces(GraphicsCharacter graphics, object state, bool failed)
    {
        try
        {
            if (state is CustomFacePresentation owner && owner) owner.Resume(failed);
            else if (!failed) RefreshCustomFaces(graphics);
            if (!failed) RefreshBodyShape(graphics);
        }
        catch (Exception exception) { ReportCustomFaceError("resume", exception); }
    }

    internal static void ReleaseCustomFaces(GraphicsCharacter graphics)
    {
        if (!graphics) return;
        try { graphics.GetComponent<CustomFacePresentation>()?.Release(false, false); }
        catch (Exception exception) { ReportCustomFaceError("release", exception); }
    }

    internal static void ReleaseCustomFacesForBodyPart(GraphicsCharacter graphics, GraphicsCharacterDefinitions.BodyPart part)
    {
        if (part is GraphicsCharacterDefinitions.BodyPart.Head or GraphicsCharacterDefinitions.BodyPart.Hair or GraphicsCharacterDefinitions.BodyPart.Beard && graphics &&
            graphics.GetComponent<CustomFacePresentation>() is { HasSources: true }) ReleaseCustomFaces(graphics);
    }

    private static void UnloadCustomFaces()
    {
        _customFacesUnloading = true;
        foreach (var owner in CustomFaceOwners.ToArray())
        {
            try { owner.Release(Main.IsApplicationQuitting, false); }
            catch (Exception exception) { ReportCustomFaceError("unload-restore", exception); }
        }
        if (CustomFaceOwners.Any(owner => owner && owner.HasSources))
        {
            ReportCustomFaceError("unload", new InvalidOperationException("Custom face sources are still owned by a renderer."));
            return;
        }
        _customFaceCatalog?.Dispose();
        _customFaceCatalog = null;
        _customFaceCatalogAttempted = false;
    }

    private static void ReportCustomFaceError(string stage, Exception exception)
    {
        var text = stage + ": " + exception;
        if (CustomFaceErrors.Add(text)) Main.Error("Unable to apply a custom face (" + text + ").");
    }

    private static string CustomFaceFingerprint(Mesh source) => CustomFaceFingerprints.GetValue(source,
        static mesh => ReadNativeRestMesh(mesh, readable =>
        {
            var positions = readable.vertices;
            var sourceNormals = readable.normals;
            var sourceTangents = readable.tangents;
            var colors = readable.colors;
            if (positions.Length != mesh.vertexCount || sourceNormals.Length != mesh.vertexCount ||
                sourceTangents.Length != 0 && sourceTangents.Length != mesh.vertexCount ||
                colors.Length != 0 && colors.Length != mesh.vertexCount)
                throw new InvalidOperationException("A native face rest channel is incomplete.");
            using var sha = CustomModels.CreateAssetSha256();
            using var hashing = new System.Security.Cryptography.CryptoStream(System.IO.Stream.Null, sha,
                System.Security.Cryptography.CryptoStreamMode.Write);
            using var stream = new System.IO.BufferedStream(hashing, 65536);
            using var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            writer.Write("Solasta.MeshSource.1"); writer.Write(mesh.vertexCount); writer.Write((int)mesh.indexFormat);
            CustomFaceWriteVectors(writer, positions);
            CustomFaceWriteVectors(writer, sourceNormals);
            CustomFaceWriteVectors(writer, sourceTangents);
            writer.Write(checked(colors.Length * 4));
            foreach (var value in colors)
            {
                CustomFaceWriteFloat(writer, value.r); CustomFaceWriteFloat(writer, value.g);
                CustomFaceWriteFloat(writer, value.b); CustomFaceWriteFloat(writer, value.a);
            }
            var poses = mesh.bindposes;
            writer.Write(checked(poses.Length * 16));
            foreach (var matrix in poses)
                for (var index = 0; index < 16; index++) CustomFaceWriteFloat(writer, matrix[index]);
            var weights = mesh.boneWeights;
            writer.Write(checked(weights.Length * 4));
            foreach (var weight in weights)
            {
                CustomFaceWriteFloat(writer, weight.weight0); CustomFaceWriteFloat(writer, weight.weight1);
                CustomFaceWriteFloat(writer, weight.weight2); CustomFaceWriteFloat(writer, weight.weight3);
            }
            writer.Write(checked(weights.Length * 4));
            foreach (var weight in weights)
            {
                writer.Write(weight.boneIndex0); writer.Write(weight.boneIndex1);
                writer.Write(weight.boneIndex2); writer.Write(weight.boneIndex3);
            }
            writer.Write(readable.subMeshCount);
            for (var index = 0; index < readable.subMeshCount; index++) CustomFaceWriteInts(writer, readable.GetIndices(index));
            var uvChannels = new List<(int Channel, int Dimension, List<Vector4> Values)>();
            for (var channel = 0; channel < 8; channel++)
            {
                var values = new List<Vector4>(); readable.GetUVs(channel, values);
                if (values.Count > 0) uvChannels.Add((channel,
                    readable.GetVertexAttributeDimension((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel)), values));
            }
            writer.Write(uvChannels.Count);
            foreach (var uv in uvChannels)
            {
                if (uv.Values.Count != mesh.vertexCount || uv.Dimension < 1 || uv.Dimension > 4)
                    throw new InvalidOperationException("A native face UV channel is incomplete.");
                writer.Write(uv.Channel); writer.Write(uv.Dimension); writer.Write(checked(mesh.vertexCount * uv.Dimension));
                foreach (var value in uv.Values)
                    for (var component = 0; component < uv.Dimension; component++) CustomFaceWriteFloat(writer, value[component]);
            }
            writer.Write(mesh.blendShapeCount);
            Vector3[] vertices = mesh.blendShapeCount == 0 ? [] : new Vector3[mesh.vertexCount];
            Vector3[] normals = mesh.blendShapeCount == 0 ? [] : new Vector3[mesh.vertexCount];
            Vector3[] tangents = mesh.blendShapeCount == 0 ? [] : new Vector3[mesh.vertexCount];
            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                writer.Write(mesh.GetBlendShapeName(shape)); var frames = mesh.GetBlendShapeFrameCount(shape); writer.Write(frames);
                for (var frame = 0; frame < frames; frame++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, frame, vertices, normals, tangents);
                    CustomFaceWriteFloat(writer, mesh.GetBlendShapeFrameWeight(shape, frame));
                    CustomFaceWriteVectors(writer, vertices);
                    CustomFaceWriteVectors(writer, normals);
                    CustomFaceWriteVectors(writer, tangents);
                }
            }
            writer.Flush();
            stream.Flush();
            hashing.FlushFinalBlock();
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }));

    private static void CustomFaceWriteFloat(System.IO.BinaryWriter writer, float value)
    {
        if (!BodyShapeFinite(value)) throw new InvalidOperationException("A native face source contains nonfinite data.");
        writer.Write(value == 0f ? 0f : value);
    }
    private static void CustomFaceWriteVectors(System.IO.BinaryWriter writer, Vector3[] values)
    {
        writer.Write(checked(values.Length * 3));
        foreach (var value in values)
        {
            CustomFaceWriteFloat(writer, value.x); CustomFaceWriteFloat(writer, value.y); CustomFaceWriteFloat(writer, value.z);
        }
    }
    private static void CustomFaceWriteVectors(System.IO.BinaryWriter writer, Vector4[] values)
    {
        writer.Write(checked(values.Length * 4));
        foreach (var value in values)
        {
            CustomFaceWriteFloat(writer, value.x); CustomFaceWriteFloat(writer, value.y);
            CustomFaceWriteFloat(writer, value.z); CustomFaceWriteFloat(writer, value.w);
        }
    }
    private static void CustomFaceWriteInts(System.IO.BinaryWriter writer, int[] values)
    {
        writer.Write(values?.Length ?? 0);
        if (values != null) foreach (var value in values) writer.Write(value);
    }

    private static string CustomFaceTextureFingerprint(Texture source) => CustomFaceTextureFingerprints.GetValue(source,
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
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply(false, false);
                var rgba = copy.GetRawTextureData();
                if (rgba.Length != checked(texture.width * texture.height * 4))
                    throw new InvalidOperationException("A native face texture did not yield RGBA32 linear pixels.");
                using var sha = CustomModels.CreateAssetSha256();
                using var hashing = new System.Security.Cryptography.CryptoStream(System.IO.Stream.Null, sha,
                    System.Security.Cryptography.CryptoStreamMode.Write);
                using var writer = new System.IO.BinaryWriter(hashing, System.Text.Encoding.UTF8, true);
                writer.Write("Solasta.TextureRgba32Linear.1"); writer.Write(texture.width); writer.Write(texture.height); writer.Write(rgba); writer.Flush();
                hashing.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
            finally
            {
                GL.sRGBWrite = srgbWrite;
                RenderTexture.active = previous;
                if (target) RenderTexture.ReleaseTemporary(target);
                if (copy) UnityEngine.Object.Destroy(copy);
            }
        });

    [Serializable] private sealed class CustomFaceManifest
    {
        public int schemaVersion;
        public string recipeVersion;
        public CustomFaceBundle[] bundles;
        public CustomFaceMeshAsset[] meshes;
        public CustomFaceMaterialAsset[] materials;
        public CustomFaceProperty[] textureRemaps;
    }
    [Serializable] private sealed class CustomFaceBundle
    {
        public string file, sha256;
        [NonSerialized] internal AssetBundle Loaded;
        [NonSerialized] internal int Owners;
        [NonSerialized] internal List<CustomFaceMeshAsset> Meshes;
        [NonSerialized] internal List<CustomFaceProperty> Textures;
        [NonSerialized] internal List<CustomFaceMaterialProfile> Profiles;
    }
    [Serializable] private sealed class CustomFaceMeshAsset
    {
        public string sourceFingerprint, mode, authoredMeshAsset, authoredFingerprint, bundleFile, bindingFile, bindingSha256;
        [NonSerialized] internal Mesh Loaded;
        [NonSerialized] internal CustomModels.SkinMeshSource.SurfaceBinding LoadedBinding;
        [NonSerialized] internal CustomFaceSurfaceLayout[] Layouts;
        [NonSerialized] internal (string BundleSha256, string Asset, string Fingerprint) VerifiedContent;
    }
    [Serializable] private sealed class CustomFaceMaterialAsset
    {
        public string sourceFingerprint, sourceShader, bundleFile;
        public int materialSlot;
        public CustomFaceProperty[] properties;
        public CustomFaceMaterialProfile authoredProfile;
        [NonSerialized] internal bool SharedTexturesAdded;
        internal bool UsesTextures
        {
            get
            {
                foreach (var property in properties) if (property.kind is "texture" or "texture-remap") return true;
                return false;
            }
        }
        internal string AssetBundleFile => UsesTextures ? bundleFile : null;
    }
    [Serializable] private sealed class CustomFaceMaterialProfile
    {
        public string bundleFile, materialAsset, shader;
        public CustomFaceSurfaceLayout layout;
        [NonSerialized] internal Material Loaded;
    }
    [Serializable] private sealed class CustomFaceSurfaceLayout
    {
        public int sourceUv, groomUv;
        public int[] preservedUv;
    }
    [Serializable] private sealed class CustomFaceProperty
    {
        public string name, kind, textureAsset, sourceTextureFingerprint, sourceShader, bundleFile;
        public int? sourceWidth, sourceHeight;
        public float value;
        [NonSerialized] internal Texture2D Loaded;
        [NonSerialized] internal CustomFaceProperty[] SourceCandidates;
        internal bool MatchesSourceDimensions(int width, int height) =>
            !sourceWidth.HasValue || sourceWidth.Value == width && sourceHeight.Value == height;
    }

    private sealed class CustomFaceCatalog : IDisposable
    {
        private readonly string _directory;
        private readonly Dictionary<string, CustomFaceBundle> _bundles;
        private readonly Dictionary<string, CustomFaceMeshAsset> _meshes;
        private readonly Dictionary<string, CustomFaceMaterialAsset> _materials;
        private readonly Dictionary<(string Shader, string Property, string Fingerprint), CustomFaceProperty> _textureRemaps = [];
        private readonly Dictionary<string, CustomFaceProperty[]> _textureProperties = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (UnityEngine.Object[] Data, int Owners)> _derivedData = new(StringComparer.Ordinal);
        private CustomFaceCatalog(string directory, CustomFaceManifest manifest)
        {
            _directory = directory;
            _bundles = manifest.bundles.ToDictionary(value => value.file, StringComparer.Ordinal);
            _meshes = manifest.meshes.ToDictionary(value => value.sourceFingerprint, StringComparer.Ordinal);
            _materials = manifest.materials.ToDictionary(value => MaterialKey(value.sourceFingerprint, value.materialSlot, value.sourceShader), StringComparer.Ordinal);
            foreach (var entry in _bundles.Values)
            {
                entry.Meshes = [];
                entry.Textures = [];
                entry.Profiles = [];
            }
            var layouts = manifest.materials.Where(value => value.authoredProfile != null)
                .ToLookup(value => value.sourceFingerprint, value => value.authoredProfile.layout, StringComparer.Ordinal);
            foreach (var mesh in manifest.meshes)
            {
                mesh.Layouts = layouts[mesh.sourceFingerprint].ToArray();
                mesh.VerifiedContent = default;
                if (mesh.mode == "authored") _bundles[mesh.bundleFile].Meshes.Add(mesh);
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
                _textureRemaps.Add((property.sourceShader, property.name, property.sourceTextureFingerprint), property);
                _bundles[property.bundleFile].Textures.Add(property);
            }
            foreach (var group in (manifest.textureRemaps ?? []).GroupBy(property => property.sourceShader, StringComparer.Ordinal))
                _textureProperties.Add(group.Key, group.GroupBy(property => property.name, StringComparer.Ordinal)
                    .OrderBy(properties => properties.Key, StringComparer.Ordinal).Select(properties => new CustomFaceProperty
                    { name = properties.Key, kind = "texture-index", SourceCandidates = properties.ToArray() }).ToArray());
        }
        private static bool IsHash(string value) => value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        private static bool SourceDimensionsValid(CustomFaceProperty property) =>
            property.sourceWidth.HasValue == property.sourceHeight.HasValue &&
            (!property.sourceWidth.HasValue || property.kind == "texture-remap" && property.sourceWidth.Value > 0 && property.sourceHeight.Value > 0);
        private static bool IsBundleFile(string value) => !string.IsNullOrWhiteSpace(value) &&
            value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && System.IO.Path.GetFileName(value) == value && value is not ("." or "..");
        private static string MaterialKey(string fingerprint, int slot, string shader) => fingerprint + "|" + slot + "|" + shader;
        internal static CustomFaceCatalog Load()
        {
            var directory = System.IO.Path.Combine(Main.ModFolder, "BlenderModels", "CharacterFaces");
            var path = System.IO.Path.Combine(directory, "CharacterFaces.manifest.json");
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("The custom face catalog is missing.", path);
            var manifest = Parse(System.IO.File.ReadAllText(path));
            Validate(manifest);
            return new CustomFaceCatalog(directory, manifest);
        }
        private static CustomFaceManifest Parse(string json)
        {
            using var text = new System.IO.StringReader(json);
            using var reader = new JsonTextReader(text)
                { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double };
            var data = JObject.Load(reader,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || data.Properties().Any(property => property.Name is not ("schemaVersion" or "recipeVersion" or "bundles" or "meshes" or "materials" or "textureRemaps")) ||
                !JsonField(data, "schemaVersion", JTokenType.Integer) || !JsonField(data, "recipeVersion", JTokenType.String) ||
                data["bundles"] is not JArray bundles || data["meshes"] is not JArray meshes || data["materials"] is not JArray materials)
                throw new System.IO.InvalidDataException("Invalid custom face catalog JSON schema.");
            var version = (int)data["schemaVersion"];
            if (version is not (1 or 2) || version == 1 && data.Property("textureRemaps") != null || version == 2 && data["textureRemaps"] is not JArray)
                throw new System.IO.InvalidDataException("Invalid custom face shared texture catalog version.");
            foreach (var token in bundles)
                if (token is not JObject bundle || bundle.Properties().Any(property => property.Name is not ("file" or "sha256")) ||
                    !JsonField(bundle, "file", JTokenType.String) || !JsonField(bundle, "sha256", JTokenType.String))
                    throw new System.IO.InvalidDataException("Invalid custom face pack JSON schema.");
            foreach (var token in meshes)
                if (token is not JObject mesh || mesh.Properties().Any(property => property.Name is not ("sourceFingerprint" or "mode" or "authoredMeshAsset" or "authoredFingerprint" or "bundleFile" or "bindingFile" or "bindingSha256")) ||
                    !JsonField(mesh, "sourceFingerprint", JTokenType.String) || !JsonField(mesh, "mode", JTokenType.String) ||
                    !JsonField(mesh, "authoredMeshAsset", JTokenType.String, false) || !JsonField(mesh, "authoredFingerprint", JTokenType.String, false) ||
                    !JsonField(mesh, "bundleFile", JTokenType.String, false) ||
                    !JsonField(mesh, "bindingFile", JTokenType.String, false) || !JsonField(mesh, "bindingSha256", JTokenType.String, false))
                    throw new System.IO.InvalidDataException("Invalid custom face mesh JSON schema.");
            foreach (var token in materials)
            {
                if (token is not JObject material || material.Properties().Any(property => property.Name is not ("sourceFingerprint" or "sourceShader" or "bundleFile" or "materialSlot" or "properties" or "authoredProfile")) ||
                    !JsonField(material, "sourceFingerprint", JTokenType.String) || !JsonField(material, "sourceShader", JTokenType.String) ||
                    !JsonField(material, "bundleFile", JTokenType.String, false) || !JsonField(material, "materialSlot", JTokenType.Integer) ||
                    material["properties"] is not JArray properties)
                    throw new System.IO.InvalidDataException("Invalid custom face material JSON schema.");
                if (material["authoredProfile"] is { Type: not JTokenType.Null } profileToken &&
                    (profileToken is not JObject profile || profile.Properties().Any(field => field.Name is not ("bundleFile" or "materialAsset" or "shader" or "layout")) ||
                     !JsonField(profile, "bundleFile", JTokenType.String) || !JsonField(profile, "materialAsset", JTokenType.String) ||
                     !JsonField(profile, "shader", JTokenType.String) || profile["layout"] is not JObject layout ||
                     layout.Properties().Any(field => field.Name is not ("sourceUv" or "groomUv" or "preservedUv")) ||
                     !JsonField(layout, "sourceUv", JTokenType.Integer) || !JsonField(layout, "groomUv", JTokenType.Integer) ||
                     layout["preservedUv"] is not JArray preserved ||
                     preserved.Any(channel => channel.Type != JTokenType.Integer)))
                    throw new System.IO.InvalidDataException("Invalid authored material profile JSON schema.");
                foreach (var value in properties)
                    if (value is not JObject property || property.Properties().Any(field => field.Name is not ("name" or "kind" or "textureAsset" or "sourceTextureFingerprint" or "sourceWidth" or "sourceHeight" or "bundleFile" or "value")) ||
                        !JsonField(property, "name", JTokenType.String) || !JsonField(property, "kind", JTokenType.String) ||
                        !JsonField(property, "textureAsset", JTokenType.String, false) || !JsonField(property, "sourceTextureFingerprint", JTokenType.String, false) ||
                        !JsonField(property, "sourceWidth", JTokenType.Integer, false) || !JsonField(property, "sourceHeight", JTokenType.Integer, false) ||
                        !JsonField(property, "bundleFile", JTokenType.String, false) ||
                        !JsonField(property, "value", JTokenType.Float, (string)property["kind"] == "float"))
                        throw new System.IO.InvalidDataException("Invalid custom face property JSON schema.");
            }
            foreach (var token in data["textureRemaps"] as JArray ?? [])
                if (token is not JObject property || property.Properties().Any(field => field.Name is not
                        ("sourceShader" or "name" or "kind" or "sourceTextureFingerprint" or "sourceWidth" or "sourceHeight" or "bundleFile" or "textureAsset")) ||
                    !JsonField(property, "sourceShader", JTokenType.String) || !JsonField(property, "name", JTokenType.String) ||
                    !JsonField(property, "kind", JTokenType.String) || !JsonField(property, "sourceTextureFingerprint", JTokenType.String) ||
                    !JsonField(property, "sourceWidth", JTokenType.Integer, false) || !JsonField(property, "sourceHeight", JTokenType.Integer, false) ||
                    !JsonField(property, "bundleFile", JTokenType.String) || !JsonField(property, "textureAsset", JTokenType.String))
                    throw new System.IO.InvalidDataException("Invalid shared custom face texture JSON schema.");
            return data.ToObject<CustomFaceManifest>(JsonSerializer.Create(new JsonSerializerSettings
                { MissingMemberHandling = MissingMemberHandling.Error }));
        }
        private static bool JsonField(JObject data, string name, JTokenType type, bool required = true) =>
            data.TryGetValue(name, StringComparison.Ordinal, out var token)
                ? token.Type == type || type == JTokenType.Float && token.Type == JTokenType.Integer || !required && token.Type == JTokenType.Null
                : !required;
        private static void Validate(CustomFaceManifest manifest)
        {
            if (manifest == null || manifest.schemaVersion is not (1 or 2) || manifest.schemaVersion == 1 && manifest.textureRemaps != null ||
                manifest.schemaVersion == 2 && manifest.textureRemaps == null || string.IsNullOrWhiteSpace(manifest.recipeVersion) ||
                manifest.bundles == null || manifest.meshes == null || manifest.meshes.Length == 0 || manifest.materials == null ||
                manifest.bundles.Any(value => value == null || !IsBundleFile(value.file) || !IsHash(value.sha256)) ||
                manifest.bundles.Select(value => value.file).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.bundles.Length)
                throw new InvalidOperationException("The custom face catalog is incomplete or has duplicate packs.");
            var bundles = new HashSet<string>(manifest.bundles.Select(value => value.file), StringComparer.Ordinal);
            var sourceFingerprints = new HashSet<string>(StringComparer.Ordinal);
            if (manifest.meshes.Any(value => value == null || !IsHash(value.sourceFingerprint) || value.mode is not ("native" or "authored") ||
                    value.mode == "native" && (!string.IsNullOrEmpty(value.authoredMeshAsset) || !string.IsNullOrEmpty(value.authoredFingerprint) || !string.IsNullOrEmpty(value.bundleFile) ||
                        !string.IsNullOrEmpty(value.bindingFile) || !string.IsNullOrEmpty(value.bindingSha256)) ||
                    (!string.IsNullOrEmpty(value.bindingFile) || !string.IsNullOrEmpty(value.bindingSha256)) &&
                        (!IsBundleFile(value.bindingFile) || !IsHash(value.bindingSha256)) ||
                    value.mode == "authored" && (!IsHash(value.authoredFingerprint) || string.IsNullOrWhiteSpace(value.authoredMeshAsset) ||
                        value.bundleFile == null || !bundles.Contains(value.bundleFile))) ||
                manifest.meshes.Any(value => !sourceFingerprints.Add(value.sourceFingerprint)))
                throw new InvalidOperationException("The custom face catalog has invalid or duplicate mesh identities.");
            var authoredSources = new HashSet<string>(manifest.meshes.Where(mesh => mesh.mode == "authored")
                .Select(mesh => mesh.sourceFingerprint), StringComparer.Ordinal);
            var nativeProperties = NativeFaceAppearanceProperties();
            var nativeTextures = manifest.schemaVersion == 2 ? NativeFaceAppearanceProperties(true) : [];
            if ((manifest.textureRemaps ?? []).Any(property => property == null || string.IsNullOrWhiteSpace(property.sourceShader) ||
                    string.IsNullOrWhiteSpace(property.name) || property.kind != "texture-remap" || !IsHash(property.sourceTextureFingerprint) || !SourceDimensionsValid(property) ||
                    string.IsNullOrWhiteSpace(property.textureAsset) || property.bundleFile == null || !bundles.Contains(property.bundleFile) ||
                    nativeProperties.Contains(Shader.PropertyToID(property.name)) && !nativeTextures.Contains(Shader.PropertyToID(property.name))) ||
                (manifest.textureRemaps ?? []).GroupBy(property => (property.sourceShader, property.name, property.sourceTextureFingerprint)).Any(group => group.Count() != 1))
                throw new InvalidOperationException("A shared custom face texture declaration is invalid or replaces a native choice parameter.");
            foreach (var material in manifest.materials)
            {
                if (material?.authoredProfile is { } profile &&
                    (!bundles.Contains(profile.bundleFile ?? "") || string.IsNullOrWhiteSpace(profile.materialAsset) ||
                     string.IsNullOrWhiteSpace(profile.shader) || profile.layout == null || profile.layout.preservedUv == null ||
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
                        (group.Any(value => value.kind != "texture-remap") || group.Select(value => value.sourceTextureFingerprint).Distinct(StringComparer.Ordinal).Count() != group.Count())) ||
                    !string.IsNullOrEmpty(material.bundleFile) && !bundles.Contains(material.bundleFile))
                    throw new InvalidOperationException("A custom face material declaration is invalid or replaces a native appearance property.");
            }
            if (manifest.materials.Select(value => MaterialKey(value.sourceFingerprint, value.materialSlot, value.sourceShader)).Distinct(StringComparer.Ordinal).Count() != manifest.materials.Length)
                throw new InvalidOperationException("The custom face catalog has duplicate material identities.");
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
        private AssetBundle Bundle(string file)
        {
            var entry = _bundles[file];
            if (entry.Owners <= 0) throw new InvalidOperationException("A custom face pack has no active presentation owner.");
            if (entry.Loaded) return entry.Loaded;
            var path = System.IO.Path.Combine(_directory, entry.file);
            using (var stream = System.IO.File.OpenRead(path))
            using (var sha = CustomModels.CreateAssetSha256())
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != entry.sha256)
                    throw new InvalidOperationException("A custom face pack does not match its catalog: " + file);
            entry.Loaded = AssetBundle.LoadFromFile(path);
            if (!entry.Loaded) throw new InvalidOperationException("A custom face pack could not be loaded: " + file);
            return entry.Loaded;
        }
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
                    if (!loaded || !loaded.isReadable || verifyContent && CustomFaceFingerprint(loaded) != entry.authoredFingerprint)
                        throw new InvalidOperationException("An authored face mesh is missing or its content identity changed: " + entry.authoredMeshAsset);
                    ValidateCustomFaceBinding(original, loaded);
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
        private static void ValidateSurfaceLayout(Mesh original, Mesh authored, CustomFaceSurfaceLayout layout, bool validateAuthored)
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
            // The common 4x4 tile codec uses 0 for source/core and tileIndex + 1 for cards.
            if (groom.Any(value => value.z is < 0f or > 16f || value.z != (int)value.z || value.w is not (-1f or 1f)))
                throw new InvalidOperationException("An authored material layout has an invalid surface tile or handedness.");
            for (var slot = 0; slot < authored.subMeshCount; slot++)
            {
                if (authored.GetTopology(slot) != MeshTopology.Triangles)
                    throw new InvalidOperationException("An authored material surface role requires triangle topology.");
                var indices = authored.GetIndices(slot);
                if (indices.Length % 3 != 0) throw new InvalidOperationException("An authored material surface has incomplete triangles.");
                for (var index = 0; index < indices.Length; index += 3)
                    if (groom[indices[index]].z != groom[indices[index + 1]].z || groom[indices[index]].z != groom[indices[index + 2]].z)
                        throw new InvalidOperationException("An authored material surface role changes within a triangle.");
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
            if (entry.LoadedBinding != null) return entry.LoadedBinding;
            var path = System.IO.Path.Combine(_directory, entry.bindingFile);
            var bytes = System.IO.File.ReadAllBytes(path);
            using (var sha = CustomModels.CreateAssetSha256())
                if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != entry.bindingSha256)
                    throw new InvalidOperationException("An authored source attachment does not match its catalog.");
            var tangents = ReadNativeRestMesh(original, readable => readable.tangents);
            var binding = ParseBinding(bytes, fingerprint, fingerprint + ":" + entry.authoredFingerprint + ":" + entry.bindingSha256,
                original.vertexCount, entry.Loaded.vertexCount, entry.Loaded.subMeshCount, tangents);
            return entry.LoadedBinding = binding;
        }
        private static CustomModels.SkinMeshSource.SurfaceBinding ParseBinding(byte[] bytes, string fingerprint, string bindingIdentity,
            int originalVertexCount, int authoredVertexCount, int subMeshCount, Vector4[] tangents)
        {
            if (tangents == null || tangents.Length != originalVertexCount || tangents.Any(tangent => !BodyShapeFinite(tangent.x) || !BodyShapeFinite(tangent.y) ||
                    !BodyShapeFinite(tangent.z) || tangent.w is not (-1f or 1f)))
                throw new InvalidOperationException("The native source has incomplete tangent frames.");
            using var stream = new System.IO.MemoryStream(bytes, false);
            using var compressed = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new System.IO.BinaryReader(compressed, new System.Text.UTF8Encoding(false, true));
            Require(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) == "UBSFB001");
            Require(BitConverter.ToString(reader.ReadBytes(32)).Replace("-", "").ToLowerInvariant() == fingerprint);
            Boolean(); // Retain and validate the authored cloth-generation flag.
            var matrix = new Matrix4x4();
            for (var index = 0; index < 16; index++) matrix[index] = Number();
            var determinant = Vector3.Dot(Vector3.Cross((Vector3)matrix.GetColumn(0), (Vector3)matrix.GetColumn(1)), (Vector3)matrix.GetColumn(2));
            Require(BodyShapeFinite(determinant) && determinant != 0f &&
                matrix[3] == 0f && matrix[7] == 0f && matrix[11] == 0f && matrix[15] == 1f);
            var roleCount = reader.ReadInt32();
            Require(roleCount > 0 && roleCount <= authoredVertexCount);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < roleCount; index++)
            {
                var length = reader.ReadInt32();
                Require(length > 0 && length <= 4096);
                var encoded = reader.ReadBytes(length);
                Require(encoded.Length == length);
                var role = new System.Text.UTF8Encoding(false, true).GetString(encoded);
                Require(!string.IsNullOrWhiteSpace(role) && roles.Add(role));
            }
            Require(reader.ReadInt32() == authoredVertexCount);
            var vertices = new CustomModels.SkinMeshSource.SurfaceVertex[authoredVertexCount];
            for (var index = 0; index < vertices.Length; index++)
            {
                var anchor = reader.ReadInt32(); var native = Boolean();
                var role = reader.ReadInt32(); var slot = reader.ReadInt32();
                Require(role >= 0 && role < roleCount && slot >= 0 && slot < subMeshCount);
                Number(); Number(); // Source UVs remain part of the verified attachment format.
                var offset = Vector(); var normal = Vector(); var tangent = Vector();
                Require(Number() is -1f or 1f);
                Require(anchor >= 0 && anchor < originalVertexCount && normal.sqrMagnitude >= 1e-8f && tangent.sqrMagnitude >= 1e-8f &&
                    Vector3.Cross(normal, tangent).sqrMagnitude >= 1e-8f &&
                    (!native || offset.Equals(Vector3.zero) && normal.Equals(Vector3.forward) && tangent.Equals(Vector3.right)));
                vertices[index] = new CustomModels.SkinMeshSource.SurfaceVertex(anchor, offset, normal, tangent, native);
            }
            Require(compressed.ReadByte() == -1);
            return new CustomModels.SkinMeshSource.SurfaceBinding(bindingIdentity, matrix, tangents, vertices);
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
        internal UnityEngine.Object[] RetainDerivedData(string key, Func<UnityEngine.Object[]> create, HashSet<string> retained)
        {
            if (!_derivedData.TryGetValue(key, out var entry))
            {
                var data = create();
                if (data == null || data.Length == 0 || data.Any(value => !value))
                {
                    foreach (var value in data ?? []) if (value) UnityEngine.Object.Destroy(value);
                    throw new InvalidOperationException("Derived source binding construction did not return owned native data.");
                }
                entry = (data, 0);
            }
            if (retained.Add(key)) entry.Owners++;
            _derivedData[key] = entry;
            return entry.Data;
        }
        internal void ReleaseDerivedData(HashSet<string> retained)
        {
            foreach (var key in retained.ToArray())
            {
                var entry = _derivedData[key];
                if (entry.Owners <= 0) throw new InvalidOperationException("A derived native binding has no active presentation owner.");
                if (--entry.Owners == 0)
                {
                    foreach (var data in entry.Data) if (data) UnityEngine.Object.Destroy(data);
                    _derivedData.Remove(key);
                }
                else _derivedData[key] = entry;
                retained.Remove(key);
            }
        }
        internal CustomFaceMaterialAsset Material(string fingerprint, int slot, Shader shader)
        {
            if (!shader) return null;
            var key = MaterialKey(fingerprint, slot, shader.name);
            _materials.TryGetValue(key, out var entry);
            if (entry?.authoredProfile != null || entry?.SharedTexturesAdded == true || !_textureProperties.TryGetValue(shader.name, out var selectors)) return entry;
            entry = new CustomFaceMaterialAsset
            {
                sourceFingerprint = fingerprint, materialSlot = slot, sourceShader = shader.name, bundleFile = entry?.bundleFile,
                properties = (entry?.properties ?? []).Concat(selectors).ToArray(), SharedTexturesAdded = true
            };
            _materials[key] = entry;
            return entry;
        }
        internal CustomFaceProperty TextureRemap(string shader, string property, string fingerprint) =>
            fingerprint != null && _textureRemaps.TryGetValue((shader, property, fingerprint), out var entry) ? entry : null;
        internal Texture2D Texture(string bundle, CustomFaceProperty property)
        {
            if (_bundles[bundle].Owners <= 0) throw new InvalidOperationException("A custom face texture has no active presentation owner.");
            if (!property.Loaded) property.Loaded = Bundle(bundle).LoadAsset<Texture2D>(property.textureAsset);
            if (!property.Loaded) throw new InvalidOperationException("An authored face texture is missing: " + property.textureAsset);
            return property.Loaded;
        }
        internal Material Profile(CustomFaceMaterialProfile profile)
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
            if (retained.Add(file)) entry.Owners++;
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
                if (entry.Owners <= 0) throw new InvalidOperationException("A custom face pack lease has no matching owner.");
                if (entry.Owners == 1) UnloadBundle(entry);
                entry.Owners--;
                retained.Remove(file);
            }
        }
        private void UnloadBundle(CustomFaceBundle entry)
        {
            if (entry.Loaded) entry.Loaded.Unload(true);
            entry.Loaded = null;
            foreach (var mesh in entry.Meshes) mesh.Loaded = null;
            foreach (var property in entry.Textures) property.Loaded = null;
            foreach (var profile in entry.Profiles) profile.Loaded = null;
        }
        public void Dispose()
        {
            if (_bundles.Values.Any(entry => entry.Owners != 0))
                throw new InvalidOperationException("Custom face presentations still own catalog packs.");
            if (_derivedData.Count != 0)
                throw new InvalidOperationException("Custom face presentations still own derived native bindings.");
            foreach (var entry in _bundles.Values) UnloadBundle(entry);
            foreach (var entry in _meshes.Values)
            {
                entry.LoadedBinding = null;
                entry.VerifiedContent = default;
            }
        }
    }

    private static void ValidateCustomFaceBinding(Mesh original, Mesh authored)
    {
        var originalPoses = original.bindposes; var authoredPoses = authored.bindposes;
        if (originalPoses.Length != authoredPoses.Length || original.blendShapeCount != authored.blendShapeCount)
            throw new InvalidOperationException("An authored face changed the native binding or expression count.");
        for (var bone = 0; bone < originalPoses.Length; bone++)
            for (var element = 0; element < 16; element++)
                if (!BitConverter.GetBytes(originalPoses[bone][element]).SequenceEqual(BitConverter.GetBytes(authoredPoses[bone][element])))
                    throw new InvalidOperationException("An authored face changed a native bind pose.");
        var weights = authored.boneWeights;
        if (weights.Length != authored.vertexCount) throw new InvalidOperationException("An authored face has incomplete skin weights.");
        foreach (var weight in weights)
        {
            var sum = weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3;
            if (!BodyShapeFinite(sum) || Mathf.Abs(sum - 1f) > 0.00001f)
                throw new InvalidOperationException("An authored face skin weight is not normalized.");
            for (var influence = 0; influence < 4; influence++)
                if (!BodyShapeFinite(BodyShapeWeight(weight, influence)) || BodyShapeWeight(weight, influence) < 0f || BodyShapeWeight(weight, influence) > 1f ||
                    BodyShapeWeight(weight, influence) > 0f && (BodyShapeWeightIndex(weight, influence) < 0 || BodyShapeWeightIndex(weight, influence) >= originalPoses.Length))
                    throw new InvalidOperationException("An authored face references an invalid native bone.");
        }
        for (var shape = 0; shape < original.blendShapeCount; shape++)
        {
            if (original.GetBlendShapeName(shape) != authored.GetBlendShapeName(shape) || original.GetBlendShapeFrameCount(shape) != authored.GetBlendShapeFrameCount(shape))
                throw new InvalidOperationException("An authored face changed native expression names or ordering.");
            for (var frame = 0; frame < original.GetBlendShapeFrameCount(shape); frame++)
                if (!BitConverter.GetBytes(original.GetBlendShapeFrameWeight(shape, frame)).SequenceEqual(BitConverter.GetBytes(authored.GetBlendShapeFrameWeight(shape, frame))))
                    throw new InvalidOperationException("An authored face changed a native expression frame weight.");
        }
    }
    private sealed class CustomFacePresentation : MonoBehaviour
    {
        private GraphicsCharacter _graphics;
        private RulesetCharacter _actor;
        private readonly List<CustomFaceSkin> _skins = [];
        private CustomModels.SkinMeshSource.SourceGraph _sourceGraph;
        private readonly HashSet<string> _derivedKeys = new(StringComparer.Ordinal);
        private readonly List<CustomFaceMaterial> _materials = [];
        private readonly HashSet<string> _packs = new(StringComparer.Ordinal);
        private CustomFaceCatalog _catalog;
        private bool _busy, _suspended, _initializationFailed;
        private int _initializationDepth;
        internal bool IsInitializing => _initializationDepth != 0;
        internal bool HasSources => _skins.Count != 0 || _materials.Count != 0 || _packs.Count != 0;

        internal void Bind(GraphicsCharacter graphics) => _graphics = graphics;

        internal void Refresh(GraphicsCharacter graphics, CustomFaceCatalog catalog)
        {
            if (_busy || IsInitializing || !isActiveAndEnabled) return;
            _busy = true;
            CustomFaceOwners.Add(this);
            try
            {
                var renderers = CustomFaceRenderers(graphics);
                if (renderers.Count == 0)
                {
                    if (HasSources) Release(false, true);
                    return;
                }
                var sources = CustomModels.SkinMeshSource.Capture(graphics, renderers);
                if (sources == null) throw new InvalidOperationException("Native face source ownership could not be resolved.");
                if (ReferenceEquals(_actor, graphics.RulesetCharacter) && _catalog == catalog &&
                    renderers.Count == _skins.Count && _skins.All(skin => sources.TryGetValue(skin.Renderer, out var source) && skin.IsCurrent(graphics, source)))
                {
                    RefreshMaterials();
                    return;
                }
                Release(false, false);
                if (HasSources) throw new InvalidOperationException("Previous custom face sources have not finished restoring.");
                _actor = graphics.RulesetCharacter;
                _catalog = catalog;
                ReleaseBodyShape(graphics);
                if (graphics.GetComponent<BodyShapePresentation>() is { HasSources: true })
                    throw new InvalidOperationException("Body overlays have not finished restoring before a face replacement.");
                sources = CustomModels.SkinMeshSource.Capture(graphics, renderers);
                if (sources == null) throw new InvalidOperationException("Restored native face sources could not be resolved.");
                // Resolve and validate every source before changing any renderer.
                foreach (var renderer in renderers)
                {
                    var source = sources[renderer];
                    var fingerprint = CustomFaceFingerprint(source.Original);
                    var authored = catalog.Mesh(source.Original, fingerprint, _packs, out var bundleFile);
                    _skins.Add(new CustomFaceSkin(graphics, renderer, source, fingerprint, authored, bundleFile));
                }
                var bindings = new Dictionary<SkinnedMeshRenderer, CustomModels.SkinMeshSource.SurfaceBinding>();
                foreach (var skin in _skins.Where(skin => skin.ChangesSource))
                    if (_catalog.Binding(skin.Source.Original, skin.Fingerprint) is { } binding) bindings.Add(skin.Renderer, binding);
                foreach (var skin in _skins) skin.ValidateSource(bindings.ContainsKey(skin.Renderer));
                var changes = _skins.Where(skin => skin.ChangesSource).ToDictionary(skin => skin.Renderer,
                    skin => (skin.Source, skin.Authored));
                if (changes.Count != 0)
                {
                    _sourceGraph = CustomModels.SkinMeshSource.PrepareMeshes(graphics, changes, bindings,
                        (key, create) => _catalog.RetainDerivedData(key, create, _derivedKeys));
                    _sourceGraph.Apply();
                }
                foreach (var skin in _skins) skin.Apply(_sourceGraph?.Installed);
                RefreshMaterials();
                RefreshBodyShape(graphics);
            }
            catch
            {
                Release(false, false);
                if (_graphics) RefreshBodyShape(_graphics);
                throw;
            }
            finally { _busy = false; }
        }

        internal void RefreshMaterials()
        {
            if (IsInitializing || _suspended || !isActiveAndEnabled || !_graphics || _catalog == null) return;
            var renderers = new HashSet<Renderer>(_graphics.Renderers ?? []);
            var nativeMaterials = NativeFaceMaterialOwners(_graphics);
            var declarations = new Dictionary<Material, (CustomFaceMaterialAsset Declaration, Shader Shader, CustomFaceMaterial.NativeProfile Native)>();
            var nativeProfiles = new Dictionary<Material, CustomFaceMaterial.NativeProfile>();
            foreach (var skin in _skins)
            {
                if (!skin.Renderer || !renderers.Contains(skin.Renderer)) continue;
                var materials = skin.Renderer.sharedMaterials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (!material || !nativeMaterials.Contains(material)) continue;
                    CustomFaceMaterial.TryGet(material, out var existing);
                    var shader = existing?.SourceShader ?? material.shader;
                    var declaration = _catalog.Material(skin.Fingerprint, slot, shader);
                    if (declaration == null) continue;
                    if (_graphics.EyeMaterials.Contains(material) && declaration.properties.Any(property => property.name == "_MainTex" && property.kind == "texture"))
                        throw new InvalidOperationException("A custom appearance cannot replace a native eye-style texture.");
                    if (declarations.TryGetValue(material, out var previous) &&
                        !CustomFaceMaterial.SameDeclaration(previous.Declaration, declaration))
                        throw new InvalidOperationException("A shared native face material has conflicting authored properties.");
                    CustomFaceMaterial.NativeProfile native = null;
                    if (declaration.authoredProfile != null)
                        if (!nativeProfiles.TryGetValue(material, out native))
                            nativeProfiles.Add(material, native = CustomFaceMaterial.CaptureNativeProfile(material));
                    declarations[material] = (declaration, shader, native);
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
                    CustomFaceMaterial.TryGet(material, out var existing);
                    var shader = existing?.SourceShader ?? material.shader;
                    if (_catalog.Material(null, slot, shader) is { } declaration)
                        declarations.Add(material, (declaration, shader, null));
                }
            }
            var requiredPacks = new HashSet<string>(_skins.Select(skin => skin.BundleFile).Where(file => file != null), StringComparer.Ordinal);
            foreach (var owner in _materials.ToArray())
            {
                if (declarations.TryGetValue(owner.Material, out var current) && CustomFaceMaterial.SameDeclaration(owner.Declaration, current.Declaration)) continue;
                owner.Restore();
                _materials.Remove(owner);
            }
            foreach (var row in declarations)
            {
                var owner = _materials.FirstOrDefault(item => item.Material == row.Key);
                if (owner == null)
                {
                    owner = CustomFaceMaterial.Acquire(row.Key, row.Value.Shader, row.Value.Declaration, _catalog, row.Value.Native);
                    _materials.Add(owner);
                }
                owner.Apply();
            }
            _catalog.ReleaseBundles(_packs, requiredPacks);
        }

        internal void Suspend()
        {
            if (++_initializationDepth != 1) return;
            _initializationFailed = false;
            if (!HasSources) return;
            _suspended = true;
            try
            {
                if (_graphics && _graphics.GetComponent<BodyShapePresentation>() is { HasSources: true })
                    throw new InvalidOperationException("Body overlays still reference a custom face source before animator initialization.");
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
                throw new InvalidOperationException("Custom face initialization has no matching suspend owner.");
            _initializationFailed |= failed;
            if (--_initializationDepth != 0) return;
            if (_initializationFailed || _customFacesUnloading || !isActiveAndEnabled || !_graphics ||
                !SettingsContext.GuiModManagerInstance.EnableCustomFaces)
            {
                Release(false, false);
                return;
            }
            if (!_suspended)
            {
                RefreshCustomFaces(_graphics);
                return;
            }
            _busy = true;
            try
            {
                var renderers = CustomFaceRenderers(_graphics);
                var sources = CustomModels.SkinMeshSource.Capture(_graphics, renderers);
                var same = ReferenceEquals(_actor, _graphics.RulesetCharacter) &&
                    ReferenceEquals(_catalog, _customFaceCatalog) && sources != null &&
                    renderers.Count == _skins.Count && _skins.All(skin =>
                        sources.TryGetValue(skin.Renderer, out var source) && skin.IsNativeCurrent(source)) &&
                    (_sourceGraph == null || _sourceGraph.CanReapply());
                if (!same)
                {
                    Release(false, false);
                    _busy = false;
                    RefreshCustomFaces(_graphics);
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

        internal void Release(bool destroying, bool reapplyBody)
        {
            _suspended = false;
            if (_graphics) ReleaseBodyShape(_graphics);
            if (_graphics && _graphics.GetComponent<BodyShapePresentation>() is { HasSources: true })
                throw new InvalidOperationException("Body overlays still reference a custom face source.");
            foreach (var owner in _materials) owner.Restore();
            _materials.Clear();
            if (_sourceGraph != null)
            {
                _sourceGraph.Restore(!destroying);
                _sourceGraph = null;
                foreach (var skin in _skins) skin.SourceRestored();
            }
            if (_derivedKeys.Count != 0)
                (_catalog ?? throw new InvalidOperationException("Derived native bindings lost their catalog owner.")).ReleaseDerivedData(_derivedKeys);
            foreach (var skin in _skins.ToArray())
            {
                skin.Restore(destroying);
                _skins.Remove(skin);
            }
            if (_packs.Count != 0)
                (_catalog ?? throw new InvalidOperationException("Owned custom face packs lost their catalog.")).ReleaseBundles(_packs);
            if (!HasSources) { _catalog = null; _actor = null; CustomFaceOwners.Remove(this); }
            if (reapplyBody && !destroying && _graphics) RefreshBodyShape(_graphics);
        }
        private void OnEnable() { if (!_customFacesUnloading && _graphics && !_busy) RefreshCustomFaces(_graphics); }
        private void OnDisable() { try { Release(false, false); } catch (Exception exception) { ReportCustomFaceError("disable", exception); } }
        private void OnDestroy() { try { Release(true, false); } catch (Exception exception) { ReportCustomFaceError("destroy", exception); } }
    }

    private static HashSet<Material> NativeFaceMaterialOwners(GraphicsCharacter graphics)
    {
        var result = new HashSet<Material>(graphics.MaterialsToDestroy);
        // Native monster/proxy instancing runs in RefreshRenderers, after the initial animator rebind.
        if (AccessTools.Field(graphics.GetType(), "materialInstances")?.GetValue(graphics) is IEnumerable<Material> instances)
            result.UnionWith(instances);
        return result;
    }

    private static HashSet<SkinnedMeshRenderer> CustomFaceRenderers(GraphicsCharacter graphics)
    {
        var result = new HashSet<SkinnedMeshRenderer>();
        var slots = graphics.ShapeSkinnedMeshRenderersPerBodySlot;
        if (slots != null)
            foreach (var part in new[] { GraphicsCharacterDefinitions.BodyPart.Head, GraphicsCharacterDefinitions.BodyPart.Hair, GraphicsCharacterDefinitions.BodyPart.Beard })
            {
                var slot = (int)part;
                if (slot < slots.Length && slots[slot] != null)
                    foreach (var renderer in slots[slot]) if (renderer && renderer.sharedMesh) result.Add(renderer);
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

    private sealed class CustomFaceSkin(GraphicsCharacter graphics, SkinnedMeshRenderer renderer, CustomModels.SkinMeshSource source, string fingerprint, Mesh authored, string bundleFile)
    {
        internal readonly SkinnedMeshRenderer Renderer = renderer;
        internal readonly string Fingerprint = fingerprint;
        internal readonly string BundleFile = bundleFile;
        private readonly Mesh _original = source.Original;
        private readonly Transform[] _bones = renderer.bones;
        private readonly Bounds _nativeBounds = renderer.localBounds;
        private Bounds _appliedBounds;
        private CustomModels.SkinMeshSource _source = source;
        private bool _clothInstalled;
        internal bool IsCurrent(GraphicsCharacter owner, CustomModels.SkinMeshSource current) => Renderer &&
            current.MatchesBones(_bones) && current.OwnerCurrent && current.AcceptsCurrentMesh() &&
            (current.Original == authored || owner.GetComponent<BodyShapePresentation>()?.IsBodyOverlayOf(Renderer, authored, current) == true);
        internal bool IsNativeCurrent(CustomModels.SkinMeshSource current) => Renderer && _original && authored &&
            current.Original == _original && current.OwnerCurrent && current.AcceptsCurrentMesh() &&
            current.MatchesBones(_bones);
        internal bool ChangesSource => authored != _original;
        internal CustomModels.SkinMeshSource Source => _source;
        internal Mesh Authored => authored;
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
                throw new InvalidOperationException("A native face changed before its authored source could be installed.");
            _source = source;
            _clothInstalled = source.ClothOwner;
            _appliedBounds = _nativeBounds;
            _appliedBounds.Encapsulate(authored.bounds);
            Renderer.localBounds = _appliedBounds;
        }
        private CustomModels.SkinMeshSource.SourceGraph _restoreGraph;
        internal void SourceRestored() => _clothInstalled = false;
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
                    throw new InvalidOperationException("A native cloth owner still references an authored face after renderer destruction.");
                return;
            }
            var sources = CustomModels.SkinMeshSource.Capture(graphics, new[] { Renderer });
            CustomModels.SkinMeshSource current = null;
            sources?.TryGetValue(Renderer, out current);
            if (current != null && current.Original == authored)
            {
                if (!SwitchSkinSource(graphics, Renderer, current, _original, !destroying, false, ref _source, ref _clothInstalled, ref _restoreGraph))
                    throw new InvalidOperationException("An authored face has not finished restoring its native source.");
                _restoreGraph = null;
                _clothInstalled = false;
            }
            else if (Renderer.sharedMesh == authored || _clothInstalled && _source.ClothOwner && _source.OwnerCurrent)
                throw new InvalidOperationException("An authored face source is still referenced by a native skin owner.");
            if (Renderer.localBounds.Equals(_appliedBounds)) Renderer.localBounds = _nativeBounds;
        }
    }

    private sealed class CustomFaceMaterial
    {
        private static readonly Dictionary<Material, CustomFaceMaterial> Shared = [];
        internal readonly Material Material;
        internal readonly Shader SourceShader;
        internal readonly CustomFaceMaterialAsset Declaration;
        private readonly CustomFaceCatalog _catalog;
        private readonly NativeProfile _nativeProfile;
        private readonly NativeProfile _authoredProfile;
        private readonly string[] _profileProperties = [], _profileKeywords = [];
        private bool _profileApplied;
        private int _appliedQueue;
        private readonly List<CustomFaceMaterialProperty> _properties = [];
        private readonly HashSet<string> _packs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _requiredPacks = new(StringComparer.Ordinal);
        private int _leases;
        internal static bool TryGet(Material material, out CustomFaceMaterial owner)
        {
            owner = null;
            return material && Shared.TryGetValue(material, out owner);
        }
        internal static CustomFaceMaterial Acquire(Material material, Shader shader, CustomFaceMaterialAsset declaration, CustomFaceCatalog catalog, NativeProfile native)
        {
            if (!Shared.TryGetValue(material, out var owner))
            {
                owner = new CustomFaceMaterial(material, shader, declaration, catalog, native);
                Shared.Add(material, owner);
            }
            else if (!ReferenceEquals(owner._catalog, catalog) || owner.SourceShader != shader || !SameDeclaration(owner.Declaration, declaration))
                throw new InvalidOperationException("A shared native material has incompatible custom face owners.");
            owner._leases++;
            return owner;
        }
        private CustomFaceMaterial(Material material, Shader shader, CustomFaceMaterialAsset declaration, CustomFaceCatalog catalog, NativeProfile native)
        {
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
                        throw new InvalidOperationException("An authored face property does not match its native shader: " + property.name);
                    _properties.Add(new CustomFaceMaterialProperty(group.ToArray()));
                }
            }
            catch { _catalog.ReleaseBundles(_packs); throw; }
        }
        internal static bool SameDeclaration(CustomFaceMaterialAsset first, CustomFaceMaterialAsset second) =>
            first.AssetBundleFile == second.AssetBundleFile &&
            (first.authoredProfile == null && second.authoredProfile == null || first.authoredProfile != null && second.authoredProfile != null &&
             first.authoredProfile.bundleFile == second.authoredProfile.bundleFile && first.authoredProfile.materialAsset == second.authoredProfile.materialAsset &&
             first.authoredProfile.shader == second.authoredProfile.shader && first.authoredProfile.layout.sourceUv == second.authoredProfile.layout.sourceUv &&
             first.authoredProfile.layout.groomUv == second.authoredProfile.layout.groomUv &&
             first.authoredProfile.layout.preservedUv.SequenceEqual(second.authoredProfile.layout.preservedUv)) &&
            first.properties.Length == second.properties.Length &&
            first.properties.Zip(second.properties, (a, b) => a.name == b.name && a.kind == b.kind && a.value.Equals(b.value) &&
                a.textureAsset == b.textureAsset && a.sourceTextureFingerprint == b.sourceTextureFingerprint &&
                a.sourceWidth == b.sourceWidth && a.sourceHeight == b.sourceHeight && a.bundleFile == b.bundleFile).All(value => value);
        internal void Apply()
        {
            if (!Material) return;
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
                if (property.Resolve(Material, _catalog, SourceShader.name, Declaration.AssetBundleFile) is { } file) required.Add(file);
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

    private sealed class CustomFaceMaterialProperty(CustomFaceProperty[] declarations)
    {
        private readonly int _id = Shader.PropertyToID(declarations[0].name);
        private bool _applied;
        private Texture _nativeTexture, _appliedTexture;
        private float _nativeFloat, _appliedFloat;
        private bool _resolved;
        private Texture _resolvedNativeTexture;
        private CustomFaceProperty _selected;
        private string _selectedBundle;
        private bool HasSourceDimensions(int width, int height)
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
        internal string Resolve(Material material, CustomFaceCatalog catalog, string shader, string bundle)
        {
            var declaration = declarations[0];
            if (declaration.kind == "float") return null;
            var current = material.GetTexture(_id);
            if (!_applied || current != _appliedTexture) _nativeTexture = current;
            if (_resolved && ReferenceEquals(_resolvedNativeTexture, _nativeTexture)) return _selectedBundle;
            _selected = declarations.FirstOrDefault(value => value.kind == "texture");
            _selectedBundle = _selected == null ? null : _selected.bundleFile ?? bundle;
            if (_selected == null)
            {
                var fingerprint = _nativeTexture && _nativeTexture.dimension == UnityEngine.Rendering.TextureDimension.Tex2D &&
                    HasSourceDimensions(_nativeTexture.width, _nativeTexture.height)
                    ? CustomFaceTextureFingerprint(_nativeTexture) : null;
                _selected = declarations.FirstOrDefault(value => value.kind == "texture-remap" && value.sourceTextureFingerprint == fingerprint);
                if (_selected != null) _selectedBundle = _selected.bundleFile ?? bundle;
                else if (declarations.Any(value => value.kind == "texture-index"))
                {
                    _selected = catalog.TextureRemap(shader, declarations[0].name, fingerprint);
                    _selectedBundle = _selected?.bundleFile;
                }
            }
            _resolvedNativeTexture = _nativeTexture;
            _resolved = true;
            return _selectedBundle;
        }
        internal void Apply(Material material, CustomFaceCatalog catalog)
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

    private static void ApplySupplementalBodyShape(GraphicsCharacter graphics, float value, float defaultValue)
    {
        if (_bodyShapesUnloading) return;
        var field = GetBodyShapeField(graphics.RulesetCharacter.Sex == CreatureSex.Female ? 1 : 0);
        var owner = graphics.GetComponent<BodyShapePresentation>() ?? graphics.gameObject.AddComponent<BodyShapePresentation>();
        owner.Refresh(graphics, field, value, defaultValue);
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

    private sealed class BodyShapeRest
    {
        internal Vector3[] Vertices, Normals, Muscular, MuscularNormals, MuscularTangents;
        internal Vector4[] Tangents;
        internal Matrix4x4[] BindPoses;
        internal BoneWeight[] Weights;
        internal int[] Triangles;
        internal bool[] Boundary;
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
            foreach (var weights in result.Weights)
            for (var influence = 0; influence < 4; influence++)
            {
                var weight = BodyShapeWeight(weights, influence);
                var index = BodyShapeWeightIndex(weights, influence);
                if (!BodyShapeFinite(weight) || weight < 0f || weight > 0f && (index < 0 || index >= result.BindPoses.Length))
                    throw new InvalidOperationException("A body skin weight references an invalid bindpose: " + mesh.name);
            }
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

    private static T ReadNativeRestMesh<T>(Mesh mesh, Func<Mesh, T> read)
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
        var edges = new Dictionary<(int, int), int>();
        for (var index = 0; index < triangles.Length; index += 3)
        { Add(triangles[index], triangles[index + 1]); Add(triangles[index + 1], triangles[index + 2]); Add(triangles[index + 2], triangles[index]); }
        var result = new bool[vertices];
        foreach (var pair in edges.Where(pair => pair.Value == 1)) { result[pair.Key.Item1] = true; result[pair.Key.Item2] = true; }
        return result;
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
                if (row.Identity || !row.Original.name.EndsWith("LOD0", StringComparison.Ordinal) ||
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
        internal bool Identity;
    }

    private sealed class BodyShapeShoulders
    {
        private readonly Transform _leftShoulder, _rightShoulder, _leftArm, _rightArm, _leftForearm, _rightForearm;
        private readonly List<(Transform[] Bones, Matrix4x4[] Bind, Vector3 Center, Vector3 Axis)> _frames = [];
        internal static bool Available(GraphicsCharacter graphics) => graphics.Animator && graphics.Animator.avatar &&
            graphics.Animator.avatar.isValid && graphics.Animator.avatar.isHuman;
        internal BodyShapeShoulders(GraphicsCharacter graphics, List<BodyShapeRenderer> rows)
        {
            if (!Available(graphics)) return;
            var human = graphics.Animator.avatar.humanDescription.human;
            if ((human == null || human.Length == 0) && !graphics.Animator.isInitialized)
                throw new InvalidOperationException("The Humanoid body mapping is unavailable.");
            Transform[] nodes = null;
            Transform Find(HumanBodyBones role)
            {
                if (graphics.Animator.isInitialized)
                {
                    var mapped = graphics.Animator.GetBoneTransform(role);
                    if (mapped) return mapped;
                }
                if (human == null) return null;
                var name = human.FirstOrDefault(bone => bone.humanName == role.ToString()).boneName;
                nodes ??= graphics.Animator.transform.GetComponentsInChildren<Transform>(true);
                return string.IsNullOrEmpty(name) ? null : nodes.FirstOrDefault(node => node.name == name &&
                    (node == graphics.Animator.transform || node.IsChildOf(graphics.Animator.transform)));
            }
            _leftShoulder = Find(HumanBodyBones.LeftShoulder);
            _rightShoulder = Find(HumanBodyBones.RightShoulder);
            _leftArm = Find(HumanBodyBones.LeftUpperArm);
            _rightArm = Find(HumanBodyBones.RightUpperArm);
            _leftForearm = Find(HumanBodyBones.LeftLowerArm);
            _rightForearm = Find(HumanBodyBones.RightLowerArm);
            if (!_leftArm || !_rightArm) throw new InvalidOperationException("The Humanoid shoulder rig is incomplete.");
            foreach (var row in rows)
            {
                if (row.Identity) continue;
                var bones = row.Renderer.bones;
                var bind = row.Original.bindposes;
                foreach (var pair in new[] { (_leftShoulder, _rightShoulder), (_leftArm, _rightArm) })
                {
                    if (!pair.Item1 || !pair.Item2) continue;
                    var left = Array.IndexOf(bones, pair.Item1);
                    var right = Array.IndexOf(bones, pair.Item2);
                    if (left < 0 || right < 0 || left >= bind.Length || right >= bind.Length) continue;
                    var a = bind[left].inverse.MultiplyPoint3x4(Vector3.zero);
                    var b = bind[right].inverse.MultiplyPoint3x4(Vector3.zero);
                    if ((b - a).sqrMagnitude <= 1e-8f) continue;
                    _frames.Add((bones, bind, (a + b) * .5f, (b - a).normalized));
                    break;
                }
            }
        }
        internal (Vector3 Center, Vector3 Axis, bool[] Bones) For(Transform[] bones, Matrix4x4[] bind)
        {
            bool Shoulder(Transform bone, Transform shoulder, Transform arm, Transform forearm) => bone &&
                (bone == shoulder || arm && (bone == arm || bone.IsChildOf(arm)) &&
                    (!forearm || bone != forearm && !bone.IsChildOf(forearm)));
            var selected = bones.Select(bone => Shoulder(bone, _leftShoulder, _leftArm, _leftForearm) ||
                Shoulder(bone, _rightShoulder, _rightArm, _rightForearm)).ToArray();
            if (!selected.Any(value => value)) return (Vector3.zero, Vector3.zero, selected);
            foreach (var frame in _frames)
            for (var index = 0; index < bones.Length; index++)
            {
                var common = Array.IndexOf(frame.Bones, bones[index]);
                if (common < 0 || common >= frame.Bind.Length) continue;
                var matrix = bind[index].inverse * frame.Bind[common];
                return (matrix.MultiplyPoint3x4(frame.Center), matrix.MultiplyVector(frame.Axis).normalized, selected);
            }
            throw new InvalidOperationException("The shoulder's original bind frame is unavailable.");
        }
        internal static Vector3 Apply(Vector3 point, Vector3 delta, BoneWeight weights, bool[] selected, Vector3 center, Vector3 axis)
        {
            if (axis.sqrMagnitude == 0f) return delta;
            var shoulder = 0f;
            var total = 0f;
            for (var influence = 0; influence < 4; influence++)
            {
                var weight = BodyShapeWeight(weights, influence);
                if (weight <= 0f) continue;
                total += weight;
                if (selected[BodyShapeWeightIndex(weights, influence)]) shoulder += weight;
            }
            if (shoulder == 0f || total == 0f) return delta;
            var side = Vector3.Dot(point - center, axis);
            if (Mathf.Abs(side) < 1e-6f) return delta;
            var outward = axis * (side > 0f ? 1f : -1f);
            var along = Vector3.Dot(delta, outward);
            if (along >= 0f) return delta;
            // Majority shoulder skin keeps the original displacement magnitude;
            // the smooth influence border joins the unchanged chest surface.
            var coverage = Mathf.Clamp01(shoulder / total * 2f);
            coverage = coverage * coverage * (3f - 2f * coverage);
            return delta - outward * (2f * along * coverage);
        }
    }

    private sealed class BodyShapeSeam(Vector3 point, Vector3 delta, bool native)
    {
        internal readonly Vector3 Point = point;
        internal readonly Vector3 Delta = delta;
        internal readonly bool Native = native;
    }

    private sealed class BodyShapeSeams
    {
        private const float Weld = 0.0001f;
        private readonly Dictionary<(string, int, int, int), List<BodyShapeSeam>> _points = [];
        internal void Add(string bone, Vector3 point, Vector3 delta, bool native)
        {
            var key = Key(bone, point);
            if (!_points.TryGetValue(key, out var list)) _points.Add(key, list = []);
            list.Add(new BodyShapeSeam(point, delta, native));
        }
        internal bool TryGet(string bone, Vector3 point, out Vector3 delta)
        {
            delta = Vector3.zero;
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
            delta = match.Delta;
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
                    { Renderer = renderer, PreserveNativeWeight = protectedPart, Identity = identityPart });
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
        private bool _fieldShouldersReady;
        private readonly List<BodyShapePiece> _pieces = [];
        private float _value;
        private float _default;
        private bool _busy;
        private sealed class FieldSource(BodyShapeRenderer row)
        {
            private readonly SkinnedMeshRenderer _renderer = row.Renderer;
            private readonly Mesh _original = row.Original;
            private readonly Transform[] _bones = row.Renderer.bones;
            private readonly bool _preserveNativeWeight = row.PreserveNativeWeight;
            internal bool Matches(BodyShapeRenderer current) => current.Renderer == _renderer &&
                current.Original == _original && current.PreserveNativeWeight == _preserveNativeWeight &&
                current.Source != null && current.Source.MatchesBones(_bones);
        }
        private bool FieldSourcesCurrent(GraphicsCharacter graphics, BodyShapeField field, List<BodyShapeRenderer> sources) =>
            _fieldSourcesReady && _baseField == field && _fieldRig == graphics.Animator &&
            _fieldShouldersReady == BodyShapeShoulders.Available(graphics) &&
            _fieldAvatar == (graphics.Animator ? graphics.Animator.avatar : null) && ReferenceEquals(_fieldActor, graphics.RulesetCharacter) &&
            sources.Count == _fieldSources.Count && _fieldSources.All(source => sources.Any(source.Matches));
        internal bool HasSources => _pieces.Count != 0;
        internal bool IsBodyOverlayOf(SkinnedMeshRenderer renderer, Mesh original, CustomModels.SkinMeshSource source) =>
            _pieces.Any(piece => piece.Renderer == renderer && piece.IsOverlayOf(original, source));
        internal void Refresh(GraphicsCharacter graphics, BodyShapeField field, float value, float defaultValue)
        {
            if (_busy || _bodyShapesUnloading) return;
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
                var sources = rows.Where(row => !row.Identity && row.Original.GetBlendShapeIndex("Muscular") >= 0).ToList();
                if (!FieldSourcesCurrent(graphics, field, sources))
                {
                    Release(false);
                    if (_pieces.Count != 0) throw new InvalidOperationException("Native body sources have not finished restoring.");
                    rows = CaptureBodyShapeRenderers(graphics);
                    sources = rows.Where(row => !row.Identity && row.Original.GetBlendShapeIndex("Muscular") >= 0).ToList();
                    try { _field = BodyShapeField.Complete(field, sources); }
                    catch (Exception exception)
                    {
                        // Native rows use their own frames; unavailable calibration only defers supplemental rows.
                        _field = null;
                        ReportBodyShapeError("actor-native-field", exception);
                    }
                    _baseField = field;
                    _fieldRig = graphics.Animator;
                    _fieldAvatar = _fieldRig ? _fieldRig.avatar : null;
                    _fieldActor = graphics.RulesetCharacter;
                    _fieldSources.AddRange(sources.Select(row => new FieldSource(row)));
                    _fieldSourcesReady = true;
                    _fieldShouldersReady = BodyShapeShoulders.Available(graphics);
                }
                var seams = new BodyShapeSeams();
                foreach (var row in rows)
                {
                    if (row.Identity || row.Original.GetBlendShapeIndex("Muscular") < 0) continue;
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
                            rest.BindPoses[dominant].MultiplyVector(rest.Muscular[vertex] * body), true);
                    }
                }
                BodyShapeShoulders shoulders = null;
                foreach (var row in rows)
                {
                    if (_pieces.Any(piece => piece.Renderer == row.Renderer)) continue;
                    try { BodyShapePiece.Create(graphics, row, _field, seams, _pieces, shoulders ??= new BodyShapeShoulders(graphics, rows)); }
                    catch (Exception exception) { ReportBodyShapeError(row.Original.name, exception); }
                }
                foreach (var piece in _pieces) piece.SetValue(value, defaultValue);
            }
            catch (Exception exception) { ReportBodyShapeError("presentation", exception); }
            finally { _busy = false; }
        }
        internal void Release(bool destroying)
        {
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
            _fieldShouldersReady = false;
            if (_pieces.Count == 0 && _bodyShapesUnloading) BodyShapeOwners.Remove(this);
        }
        private void OnEnable() { if (!_bodyShapesUnloading && _graphics && !_busy) RefreshBodyShape(_graphics); }
        private void OnDisable() { Release(false); }
        private void OnDestroy() { Release(true); if (_pieces.Count == 0) BodyShapeOwners.Remove(this); }
        private void LateUpdate()
        {
            if (_bodyShapesUnloading || !_graphics || !SettingsContext.GuiModManagerInstance.EnableAllRaceBodyShapes) return;
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
        private readonly bool _identity, _preserveNativeWeight;
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
        internal static void Create(GraphicsCharacter graphics, BodyShapeRenderer row, BodyShapeField field, BodyShapeSeams seams,
            List<BodyShapePiece> pieces, BodyShapeShoulders shoulders)
        {
            if (row.Identity) return;
            if (field == null && row.Original.GetBlendShapeIndex("Muscular") < 0) return;
            if ((row.PreserveNativeWeight || row.Original.GetBlendShapeIndex("Muscular") < 0) &&
                row.Renderer.bones.All(IsProtectedBodyBone)) return;
            var rest = ReadBodyShapeRest(row.Original);
            var bones = row.Renderer.bones.Take(rest.BindPoses.Length).ToArray();
            if (bones.Length < rest.BindPoses.Length) throw new InvalidOperationException("The body's original bone mapping is incomplete.");
            var protectedBones = bones.Select(IsProtectedBodyBone).ToArray();
            var lengths = rest.Muscular == null ? bones.Select(BodyShapeBoneLength).ToArray() : null;
            var inverse = rest.Muscular == null ? rest.BindPoses.Select(matrix => matrix.inverse).ToArray() : null;
            var anchors = rest.Muscular == null ? Enumerable.Repeat(-1, bones.Length).ToArray() : null;
            var shoulderFrame = shoulders.For(bones, rest.BindPoses);
            var delta = new Vector3[rest.Vertices.Length];
            var shoulderCorrection = new Vector3[delta.Length];
            var protectedVertices = new bool[rest.Vertices.Length];
            var nativeCorrection = rest.Muscular != null && !row.PreserveNativeWeight;
            var normalCorrection = nativeCorrection ? new Vector3[delta.Length] : null;
            var tangentCorrection = nativeCorrection ? new Vector3[delta.Length] : null;
            var maximum = 0f;
            var changed = false;
            for (var vertex = 0; vertex < delta.Length; vertex++)
            {
                var weights = rest.Weights[vertex];
                var body = BodyShapeBodyWeight(weights, protectedBones, out var total);
                protectedVertices[vertex] = body == 0f;
                if (rest.Muscular != null)
                {
                    // Keep native frames intact; mask their contribution through an owned additive frame.
                    var factor = nativeCorrection ? body - 1f : body;
                    delta[vertex] = rest.Muscular[vertex] * factor;
                    if (nativeCorrection)
                    {
                        normalCorrection[vertex] = rest.MuscularNormals[vertex] * factor;
                        tangentCorrection[vertex] = rest.MuscularTangents[vertex] * factor;
                        changed |= normalCorrection[vertex].sqrMagnitude > 0f || tangentCorrection[vertex].sqrMagnitude > 0f;
                    }
                }
                else if (body > 0f)
                {
                    for (var influence = 0; influence < 4; influence++)
                    {
                        var weight = BodyShapeWeight(weights, influence);
                        if (weight <= 0f) continue;
                        var index = BodyShapeWeightIndex(weights, influence);
                        if (protectedBones[index]) continue;
                        if (anchors[index] < 0) anchors[index] = field.ResolveAnchor(bones, index);
                        var anchor = anchors[index];
                        var point = rest.BindPoses[anchor].MultiplyPoint3x4(rest.Vertices[vertex]) / lengths[anchor];
                        delta[vertex] += inverse[anchor].MultiplyVector(field.Evaluate(bones[anchor].name, point) * lengths[anchor]) * (weight / total);
                    }
                }
                if (rest.Muscular == null && body > 0f && rest.Boundary[vertex])
                {
                    var dominant = BodyShapeDominantBone(weights, bones);
                    if (dominant >= 0 && seams.TryGet(bones[dominant].name, rest.BindPoses[dominant].MultiplyPoint3x4(rest.Vertices[vertex]), out var seam))
                        delta[vertex] = inverse[dominant].MultiplyVector(seam);
                }
                if (body > 0f)
                {
                    var desired = nativeCorrection ? delta[vertex] + rest.Muscular[vertex] : delta[vertex];
                    var corrected = BodyShapeShoulders.Apply(rest.Vertices[vertex], desired, weights,
                        shoulderFrame.Bones, shoulderFrame.Center, shoulderFrame.Axis);
                    shoulderCorrection[vertex] = corrected - desired;
                    delta[vertex] += shoulderCorrection[vertex];
                }
                if (!BodyShapeFinite(delta[vertex])) throw new InvalidOperationException("A body-shape delta is nonfinite.");
                maximum = Mathf.Max(maximum, delta[vertex].magnitude);
                changed |= delta[vertex].sqrMagnitude > 0f;
            }
            if (!changed) return;
            if (nativeCorrection && shoulderCorrection.Any(correction => correction.sqrMagnitude > 0f))
            {
                var before = BodyShapeNormals(rest.Vertices.Select((point, index) => point + delta[index] -
                    shoulderCorrection[index] + rest.Muscular[index]).ToArray(), rest.Triangles);
                var after = BodyShapeNormals(rest.Vertices.Select((point, index) => point + delta[index] +
                    rest.Muscular[index]).ToArray(), rest.Triangles);
                for (var index = 0; index < delta.Length; index++)
                {
                    if (protectedVertices[index] || (after[index] - before[index]).sqrMagnitude == 0f) continue;
                    var body = BodyShapeBodyWeight(rest.Weights[index], protectedBones, out _);
                    var nativeNormal = rest.Normals[index] + rest.MuscularNormals[index] * body;
                    var normal = (nativeNormal + after[index] - before[index]).normalized;
                    normalCorrection[index] += normal - nativeNormal;
                    if (rest.Tangents.Length == delta.Length)
                    {
                        var tangent = (Vector3)rest.Tangents[index] + rest.MuscularTangents[index] * body;
                        tangentCorrection[index] += Quaternion.FromToRotation(nativeNormal, normal) * tangent - tangent;
                    }
                }
            }
            var owned = UnityEngine.Object.Instantiate(row.Original);
            owned.name = row.Original.name;
            BodyShapePiece piece = null;
            try
            {
                var baseline = row.PreserveNativeWeight && rest.Muscular != null
                    ? row.Renderer.GetBlendShapeWeight(row.Original.GetBlendShapeIndex("Muscular")) / 100f : 0f;
                AddFrame(owned, BodyVolumePositive, rest, delta, protectedVertices, 1f, normalCorrection, tangentCorrection, baseline);
                AddFrame(owned, BodyVolumeNegative, rest, delta, protectedVertices, -1f, normalCorrection, tangentCorrection, baseline);
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
        }
        internal bool IsOverlayOf(Mesh original, CustomModels.SkinMeshSource source) =>
            _sourceGraph?.IsApplied == true && Original == original && Renderer && source != null && source.MatchesBones(_bones) &&
            source.Original == _owned && source.OwnerCurrent && source.AcceptsCurrentMesh();
        internal bool IsCurrent(BodyShapeRenderer row)
        {
            var source = row.Source;
            if (_sourceGraph?.IsApplied != true || row.Renderer != Renderer || row.Identity != _identity || row.PreserveNativeWeight != _preserveNativeWeight ||
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
            Vector3[] normalCorrection, Vector3[] tangentCorrection, float baseline)
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
