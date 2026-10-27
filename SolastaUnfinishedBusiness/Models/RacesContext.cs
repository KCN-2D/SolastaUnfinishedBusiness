using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
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
        if (!_bodyShapesUnloading && graphics && graphics.RulesetCharacter != null && graphics.Animator)
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
        const MorphotypeElementDefinition.ElementCategory category = MorphotypeElementDefinition.ElementCategory.Musculature;
        foreach (var original in OriginalBodyShapeCategories)
        {
            if (original.Key.IsAvailable(category) == original.Value) continue;
            BodyShapeCategories(original.Key) = original.Value
                ? [.. original.Key.AvailableMorphotypeCategories, category]
                : original.Key.AvailableMorphotypeCategories.Where(value => value != category).ToArray();
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
        return BodyShapeRestCache.GetValue(source, Read);
        static BodyShapeRest Read(Mesh mesh)
        {
            GameObject host = null;
            Mesh baked = null;
            try
            {
                var result = new BodyShapeRest { BindPoses = mesh.bindposes, Weights = mesh.boneWeights };
                var readable = mesh;
                if (!mesh.isReadable)
                {
                    host = new GameObject("Body shape rest proxy") { hideFlags = HideFlags.HideAndDontSave };
                    host.SetActive(false);
                    var skin = host.AddComponent<SkinnedMeshRenderer>();
                    skin.enabled = false;
                    var bones = new Transform[result.BindPoses.Length];
                    for (var index = 0; index < bones.Length; index++)
                    {
                        var matrix = result.BindPoses[index].inverse;
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
                        var identity = bone.localToWorldMatrix * result.BindPoses[index];
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
            }
            finally
            {
                if (host) { host.SetActive(false); UnityEngine.Object.Destroy(host); }
                if (baked) UnityEngine.Object.Destroy(baked);
            }
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
                _bones.SequenceEqual(current.Renderer.bones);
        }
        private bool FieldSourcesCurrent(GraphicsCharacter graphics, BodyShapeField field, List<BodyShapeRenderer> sources) =>
            _fieldSourcesReady && _baseField == field && _fieldRig == graphics.Animator &&
            _fieldAvatar == (graphics.Animator ? graphics.Animator.avatar : null) && ReferenceEquals(_fieldActor, graphics.RulesetCharacter) &&
            sources.Count == _fieldSources.Count && _fieldSources.All(source => sources.Any(source.Matches));
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
                foreach (var row in rows)
                {
                    if (_pieces.Any(piece => piece.Renderer == row.Renderer)) continue;
                    try { BodyShapePiece.Create(graphics, row, _field, seams, _pieces); }
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
        internal static void Create(GraphicsCharacter graphics, BodyShapeRenderer row, BodyShapeField field, BodyShapeSeams seams, List<BodyShapePiece> pieces)
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
            var delta = new Vector3[rest.Vertices.Length];
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
                var baseline = row.PreserveNativeWeight && rest.Muscular != null
                    ? row.Renderer.GetBlendShapeWeight(row.Original.GetBlendShapeIndex("Muscular")) / 100f : 0f;
                AddFrame(owned, BodyVolumePositive, rest, delta, protectedVertices, 1f, normalCorrection, tangentCorrection, baseline);
                AddFrame(owned, BodyVolumeNegative, rest, delta, protectedVertices, -1f, normalCorrection, tangentCorrection, baseline);
                piece = new BodyShapePiece(graphics, row, owned, maximum);
                pieces.Add(piece);
                if (!piece.SwitchSource(row.Source, owned)) throw new InvalidOperationException("The renderer changed before its body morph was installed.");
                row.Renderer.localBounds = piece._appliedBounds;
            }
            catch
            {
                if (piece == null) UnityEngine.Object.Destroy(owned);
                else if (piece.Restore(false)) pieces.Remove(piece);
                throw;
            }
        }
        internal bool IsCurrent(BodyShapeRenderer row)
        {
            var source = row.Source;
            if (row.Renderer != Renderer || row.Identity != _identity || row.PreserveNativeWeight != _preserveNativeWeight ||
                !_bones.SequenceEqual(Renderer.bones) || source == null || source.Original != _owned || !source.AcceptsCurrentMesh()) return false;
            _source = source;
            return true;
        }
        internal void SetValue(float value, float defaultValue)
        {
            if (!Renderer || !_owned || !_source.OwnerCurrent || !_source.AcceptsCurrentMesh()) return;
            value -= _preservedNativeWeight ?? defaultValue;
            Renderer.SetBlendShapeWeight(_positive, Mathf.Max(0f, value));
            Renderer.SetBlendShapeWeight(_negative, Mathf.Max(0f, -value));
        }
        internal bool Restore(bool destroying)
        {
            if (Renderer && _owned)
            {
                var sources = CustomModels.SkinMeshSource.Capture(_graphics, new HashSet<SkinnedMeshRenderer> { Renderer });
                CustomModels.SkinMeshSource source = null;
                sources?.TryGetValue(Renderer, out source);
                if (source != null && source.Original == _owned)
                {
                    if (!SwitchSource(source, Original, !destroying)) return false;
                }
                else if (_clothSourceInstalled && _source.ClothOwner && (source == null || _source.OwnerCurrent)) return false;
                else if (Renderer.sharedMesh == _owned) Renderer.sharedMesh = Original;
                if (Renderer.localBounds.Equals(_appliedBounds)) Renderer.localBounds = _originalBounds;
            }
            if (_clothSourceInstalled && _source.ClothOwner && _source.OwnerCurrent) return false;
            if (_owned) UnityEngine.Object.Destroy(_owned);
            return true;
        }
        private bool SwitchSource(CustomModels.SkinMeshSource source, Mesh replacement, bool restart = true)
        {
            if (!Renderer || !replacement || !source.OwnerCurrent || !source.AcceptsCurrentMesh()) return false;
            var nativeWeights = Enumerable.Range(0, Original.blendShapeCount).Select(Renderer.GetBlendShapeWeight).ToArray();
            if (source.ClothOwner)
            {
                AccessTools.Method(source.ClothOwner.GetType(), "TacticalDispose").Invoke(source.ClothOwner, null);
                if (!source.TrySetClothSourceMesh(replacement))
                {
                    AccessTools.Method(source.ClothOwner.GetType(), "TacticalInit").Invoke(source.ClothOwner, null);
                    return false;
                }
                _clothSourceInstalled = replacement == _owned;
            }
            Renderer.sharedMesh = replacement;
            var pending = CustomModels.SkinMeshSource.Capture(_graphics, new HashSet<SkinnedMeshRenderer> { Renderer });
            if (pending != null && pending.TryGetValue(Renderer, out var pendingSource)) _source = pendingSource;
            if (source.ClothOwner && restart) AccessTools.Method(source.ClothOwner.GetType(), "TacticalInit").Invoke(source.ClothOwner, null);
            for (var index = 0; index < nativeWeights.Length; index++) Renderer.SetBlendShapeWeight(index, nativeWeights[index]);
            var current = CustomModels.SkinMeshSource.Capture(_graphics, new HashSet<SkinnedMeshRenderer> { Renderer });
            if (current != null && current.TryGetValue(Renderer, out var captured)) _source = captured;
            return true;
        }
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
