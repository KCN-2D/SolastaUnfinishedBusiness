using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Builders;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;

namespace SolastaUnfinishedBusiness.CustomUI;

public static class CustomModels
{
    internal static bool RequiresWieldedEquipmentRefresh(ItemDefinition definition) =>
        definition.IsWeapon ||
        definition.IsFocusItem &&
        (definition.SlotsWhereActive.Contains(EquipmentDefinitions.SlotTypeMainHand) ||
         definition.SlotsWhereActive.Contains(EquipmentDefinitions.SlotTypeOffHand));

    internal sealed class SphericalGrip(Vector3 modelCenter, Vector3 palmOffset, Vector3 upperArm,
        Vector2 forearm, Vector2 wrist, float fingerExtension)
    {
        internal Vector3 ModelCenter { get; } = modelCenter;
        internal Vector3 PalmOffset { get; } = palmOffset;
        internal float FingerExtension { get; } = fingerExtension;
        internal float[] ArmMuscles { get; } =
            [upperArm.x, upperArm.y, upperArm.z, forearm.x, forearm.y, wrist.x, wrist.y];
    }

    internal static void ApplyWieldedItemPresentation(GraphicsCharacter graphics, RulesetItem item, string slot)
    {
        var grip = item.ItemDefinition.GetFirstSubFeatureOfType<SphericalGrip>();
        if (grip == null ||
            slot != EquipmentDefinitions.SlotTypeMainHand && slot != EquipmentDefinitions.SlotTypeOffHand ||
            !graphics.WieldedInstantiatedItems.TryGetValue(slot, out var model) || !model)
        {
            return;
        }

        var presentation = model.GetComponent<SphericalGripPresentation>() ??
                           model.AddComponent<SphericalGripPresentation>();
        presentation.Bind(graphics, slot, grip);
    }

    internal static void RefreshWieldedItemPresentations(GraphicsCharacter graphics)
    {
        foreach (var model in graphics.WieldedInstantiatedItems.Values)
        {
            if (model)
            {
                model.GetComponent<SphericalGripPresentation>()?.RequestRefresh();
            }
        }
    }

    private sealed class SphericalGripPresentation : MonoBehaviour
    {
        private static readonly string[] ArmMuscleNames =
        [
            "Arm Down-Up", "Arm Front-Back", "Arm Twist In-Out", "Forearm Stretch",
            "Forearm Twist In-Out", "Hand Down-Up", "Hand In-Out"
        ];

        private GraphicsCharacter _graphics;
        private string _slot;
        private SphericalGrip _grip;
        private Transform _hand;
        private Transform _index;
        private Transform _pinky;
        private Transform[] _posedBones;
        private Quaternion[] _poseCorrections;
        private Quaternion[] _animationRotations;
        private Quaternion[] _appliedRotations;
        private int _layer;
        private bool _leftHand;
        private bool _refreshRequested;
        private bool _resolutionAttempted;
        private bool _poseApplied;
        internal int ResolutionAttempts { get; private set; }
        internal int ApplyCount { get; private set; }
        internal bool IsConfigured { get; private set; }
        internal float ClosedWeight { get; private set; }
        internal float PalmUpDot { get; private set; }
        internal Vector3 TargetCenter { get; private set; }

        internal void Bind(GraphicsCharacter graphics, string slot, SphericalGrip grip)
        {
            _graphics = graphics;
            _slot = slot;
            _grip = grip;
            _leftHand = slot == EquipmentDefinitions.SlotTypeOffHand;
            IsConfigured = false;
            _refreshRequested = false;
        }

        internal void RequestRefresh()
        {
            RestoreAnimationPose();
            IsConfigured = false;
            _resolutionAttempted = false;
            _refreshRequested = true;
        }

        [UsedImplicitly]
        private void LateUpdate()
        {
            if (!_refreshRequested || !_graphics ||
                !_graphics.WieldedInstantiatedItems.TryGetValue(_slot, out var current) ||
                current != gameObject || !_graphics.Animator || !_graphics.Animator.enabled ||
                !_graphics.Animator.isInitialized)
            {
                return;
            }

            var animator = _graphics.Animator;
            if (!IsConfigured && !_resolutionAttempted)
            {
                _resolutionAttempted = true;
                ResolutionAttempts++;
                IsConfigured = ConfigurePose(animator);
            }

            if (!IsConfigured)
            {
                return;
            }

            for (var i = 0; i < _posedBones.Length; i++)
            {
                var rotation = _posedBones[i].localRotation;
                if (!_poseApplied || rotation != _appliedRotations[i])
                {
                    _animationRotations[i] = rotation;
                }
                _appliedRotations[i] = _poseCorrections[i] * _animationRotations[i];
                _posedBones[i].localRotation = _appliedRotations[i];
            }
            _poseApplied = true;
            ApplyCount++;
            ClosedWeight = animator.GetLayerWeight(_layer);
            var knuckles = (_index.position + _pinky.position) * 0.5f;
            var palm = (_hand.position + knuckles) * 0.5f;
            var along = (knuckles - _hand.position).normalized;
            var across = (_pinky.position - _index.position).normalized;
            var normal = Vector3.Cross(along, across).normalized * (_leftHand ? 1 : -1);
            across = Vector3.Cross(normal, along).normalized * (_leftHand ? 1 : -1);
            var offset = _grip.PalmOffset;
            var scale = transform.lossyScale;
            var itemScale = Mathf.Abs(scale.x);
            TargetCenter = palm + (along * offset.x + across * offset.y + normal * offset.z) * itemScale;
            transform.rotation = Quaternion.LookRotation(along, normal);
            transform.position += TargetCenter - transform.TransformPoint(_grip.ModelCenter);
            PalmUpDot = Vector3.Dot(normal, _graphics.transform.up);

        }

        private bool ConfigurePose(Animator animator)
        {
            if (!animator.isHuman || !animator.avatar || !animator.avatar.isValid)
            {
                return false;
            }

            _hand = animator.GetBoneTransform(_leftHand ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            _index = animator.GetBoneTransform(_leftHand ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            _pinky = animator.GetBoneTransform(_leftHand ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            _layer = animator.GetLayerIndex(_leftHand ? "LeftHandClosed" : "RightHandClosed");
            if (!_hand || !_index || !_pinky || _layer < 0)
            {
                return false;
            }

            var side = _leftHand ? "Left" : "Right";
            var names = HumanTrait.MuscleName;
            var indices = new int[ArmMuscleNames.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = Array.IndexOf(names, side + " " + ArmMuscleNames[i]);
                if (indices[i] < 0)
                {
                    return false;
                }
            }

            var bones = new List<Transform>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                var name = bone.ToString();
                if (!name.StartsWith(side, StringComparison.Ordinal) ||
                    !(name.Contains("Arm") || name.Contains("Hand") || name.Contains("Thumb") ||
                      name.Contains("Index") || name.Contains("Middle") || name.Contains("Ring") ||
                      name.Contains("Little")))
                {
                    continue;
                }
                var target = animator.GetBoneTransform(bone);
                if (target)
                {
                    bones.Add(target);
                }
            }
            _posedBones = bones.ToArray();
            _poseCorrections = new Quaternion[_posedBones.Length];
            _animationRotations = new Quaternion[_posedBones.Length];
            _appliedRotations = new Quaternion[_posedBones.Length];

            var transforms = animator.GetComponentsInChildren<Transform>(true);
            var positions = new Vector3[transforms.Length];
            var rotations = new Quaternion[transforms.Length];
            var scales = new Vector3[transforms.Length];
            for (var i = 0; i < transforms.Length; i++)
            {
                positions[i] = transforms[i].localPosition;
                rotations[i] = transforms[i].localRotation;
                scales[i] = transforms[i].localScale;
            }
            for (var i = 0; i < _posedBones.Length; i++)
            {
                _animationRotations[i] = _posedBones[i].localRotation;
            }

            using var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var original = new HumanPose();
            poseHandler.GetHumanPose(ref original);
            var pose = new HumanPose
            {
                bodyPosition = original.bodyPosition,
                bodyRotation = original.bodyRotation,
                muscles = (float[])original.muscles.Clone()
            };
            for (var i = 0; i < indices.Length; i++)
            {
                pose.muscles[indices[i]] = _grip.ArmMuscles[i];
            }
            for (var i = 0; i < names.Length; i++)
            {
                if (!names[i].StartsWith(side + " ", StringComparison.Ordinal))
                {
                    continue;
                }
                if (names[i].EndsWith("Stretched", StringComparison.Ordinal))
                {
                    pose.muscles[i] = _grip.FingerExtension;
                }
                else if (names[i].EndsWith("Spread", StringComparison.Ordinal))
                {
                    pose.muscles[i] = 0;
                }
            }
            try
            {
                poseHandler.SetHumanPose(ref pose);
                for (var i = 0; i < _posedBones.Length; i++)
                {
                    _poseCorrections[i] = _posedBones[i].localRotation * Quaternion.Inverse(_animationRotations[i]);
                }
            }
            finally
            {
                try
                {
                    poseHandler.SetHumanPose(ref original);
                }
                finally
                {
                    for (var i = 0; i < transforms.Length; i++)
                    {
                        transforms[i].localPosition = positions[i];
                        transforms[i].localRotation = rotations[i];
                        transforms[i].localScale = scales[i];
                    }
                }
            }
            _poseApplied = false;
            return true;
        }

        private void RestoreAnimationPose()
        {
            if (!_poseApplied)
            {
                return;
            }
            for (var i = 0; i < _posedBones.Length; i++)
            {
                if (_posedBones[i] && _posedBones[i].localRotation == _appliedRotations[i])
                {
                    _posedBones[i].localRotation = _animationRotations[i];
                }
            }
            _poseApplied = false;
        }

        [UsedImplicitly]
        private void OnDisable() => RestoreAnimationPose();

        [UsedImplicitly]
        private void OnDestroy() => RestoreAnimationPose();
    }

    internal sealed class MonsterModel(string name, float scale = 1f,
        Action<GraphicsCharacterMonster, Transform> configure = null,
        Func<GraphicsCharacterMonster, Transform> effectOrigin = null,
        Func<GraphicsCharacterMonster,
            IReadOnlyDictionary<AnimationDefinitions.BoneType, (Transform Transform, Vector3 LocalPosition)>> bones = null,
        bool nativeAnimationAudioEnabled = true,
        Action<GraphicsCharacterMonster, bool> hitReaction = null)
    {
        internal string Name { get; } = name;
        internal float Scale { get; } = scale;
        internal Action<GraphicsCharacterMonster, Transform> Configure { get; } = configure;
        internal Func<GraphicsCharacterMonster, Transform> EffectOrigin { get; } = effectOrigin;
        internal Func<GraphicsCharacterMonster,
            IReadOnlyDictionary<AnimationDefinitions.BoneType, (Transform Transform, Vector3 LocalPosition)>> Bones { get; } = bones;
        internal bool NativeAnimationAudioEnabled { get; } = nativeAnimationAudioEnabled;
        internal Action<GraphicsCharacterMonster, bool> HitReaction { get; } = hitReaction;
    }

    internal static void ReactToMonsterHit(GraphicsCharacter character, bool blocking)
    {
        if (character is GraphicsCharacterMonster monster)
        {
            monster.RulesetCharacterMonster?.MonsterDefinition.GetFirstSubFeatureOfType<MonsterModel>()?
                .HitReaction?.Invoke(monster, blocking);
        }
    }

    internal static bool UsesNativeAnimationAudio(GraphicsCharacter graphics) =>
        graphics is not GraphicsCharacterMonster monster ||
        monster.RulesetCharacterMonster?.MonsterDefinition.GetFirstSubFeatureOfType<MonsterModel>()?
            .NativeAnimationAudioEnabled != false;

    internal static void ApplyMonsterModel(GraphicsCharacterMonster graphics)
    {
        var actor = graphics?.RulesetCharacterMonster;
        var model = actor?.MonsterDefinition.GetFirstSubFeatureOfType<MonsterModel>();
        if (model == null)
        {
            return;
        }

        var prefab = GetPrefabByGuid(GetBlenderModel(model.Name).AssetGUID);
        if (!prefab)
        {
            return;
        }

        var name = "UB_MonsterModel_" + model.Name;
        var body = graphics.transform.Find(name);
        if (!body)
        {
            body = UnityEngine.Object.Instantiate(prefab, graphics.transform, false).transform;
            body.name = name;
        }

        // Retain the native skeleton, animator and spell anchors. Only the authored
        // visible body is replaced, so the normal movement and casting lifecycle remains intact.
        foreach (var renderer in graphics.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var particle = false;
            for (var parent = renderer.transform; parent && parent != graphics.transform; parent = parent.parent)
            {
                if (parent.GetComponent("ParticleSystem"))
                {
                    particle = true;
                    break;
                }
            }

            if (!renderer.transform.IsChildOf(body) && !particle)
            {
                renderer.forceRenderingOff = true;
            }
        }

        body.localPosition = Vector3.zero;
        body.localRotation = Quaternion.identity;
        var scale = graphics.transform.lossyScale;
        body.localScale = new Vector3(model.Scale / Mathf.Abs(scale.x), model.Scale / Mathf.Abs(scale.y),
            model.Scale / Mathf.Abs(scale.z));
        model.Configure?.Invoke(graphics, body);
        var bones = model.Bones?.Invoke(graphics);
        if (bones != null)
        {
            foreach (var entry in bones)
            {
                var bone = entry.Value;
                if (!bone.Transform)
                {
                    continue;
                }
                graphics.BonesDictionary[entry.Key] = bone.Transform;
                // Native deterministic targeting applies logical position/surface/orientation
                // to an initial unrotated offset, rather than sampling the animated bone.
                graphics.InitialBonesLocalPositionDictionary[entry.Key] = bone.LocalPosition * model.Scale;
            }
        }
        foreach (var renderer in body.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
            renderer.gameObject.layer = graphics.gameObject.layer;
        }
        graphics.RefreshRenderers();
    }

    internal static Transform GetMonsterEffectOrigin(GraphicsCharacter character)
    {
        if (character is not GraphicsCharacterMonster monster)
        {
            return null;
        }
        return monster.RulesetCharacterMonster.MonsterDefinition.GetFirstSubFeatureOfType<MonsterModel>()?
            .EffectOrigin?.Invoke(monster);
    }

    internal static void RefreshMaterialOverlays(GraphicsCharacter character)
    {
        if (!character)
        {
            return;
        }

        foreach (var filter in character.GetComponentsInChildren<MeshFilter>(true))
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            var mesh = filter.sharedMesh;
            if (!renderer || !mesh)
            {
                continue;
            }

            var source = OverlayMeshSources.TryGetValue(mesh, out var original) ? original : mesh;
            if (!LoadedMeshes.Contains(source))
            {
                continue;
            }
            var overlays = renderer.sharedMaterials.Length - source.subMeshCount;
            if (overlays <= 0 || source.subMeshCount == 1)
            {
                filter.sharedMesh = source;
                continue;
            }

            var key = (source, overlays);
            if (!OverlayMeshes.TryGetValue(key, out var overlayMesh))
            {
                // Native character overlays append materials. Unity otherwise draws
                // each of those passes over only the final material's submesh.
                overlayMesh = UnityEngine.Object.Instantiate(source);
                overlayMesh.name = source.name + " Material Overlays";
                overlayMesh.subMeshCount = source.subMeshCount + overlays;
                var triangles = source.triangles;
                for (var index = source.subMeshCount; index < overlayMesh.subMeshCount; index++)
                {
                    overlayMesh.SetTriangles(triangles, index, false);
                }
                OverlayMeshes.Add(key, overlayMesh);
                OverlayMeshSources.Add(overlayMesh, source);
            }
            filter.sharedMesh = overlayMesh;
        }
    }

    internal static Transform GetMonsterPhotoTarget(GraphicsCharacter graphics, AnimationDefinitions.BoneType boneType,
        ref GraphicsCharacterPhotoManager.PhotoParameters parameters)
    {
        var monster = graphics as GraphicsCharacterMonster;
        var model = monster?.RulesetCharacterMonster.MonsterDefinition
            .GetFirstSubFeatureOfType<MonsterModel>();
        if (model != null)
        {
            ApplyMonsterModel(monster);
        }
        var body = model == null ? null : graphics.transform.Find("UB_MonsterModel_" + model.Name);
        var mesh = body ? body.GetComponent<MeshFilter>()?.sharedMesh : null;
        if (!mesh)
        {
            return graphics.GetBoneTransform(boneType);
        }

        // Portraits follow the authored silhouette without changing the model's attack anchors.
        var bounds = new Bounds(body.TransformPoint(mesh.bounds.center), Vector3.zero);
        foreach (var renderer in body.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.enabled && renderer.gameObject.activeInHierarchy)
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        var target = graphics.transform.Find("UB_MonsterPhotoTarget");
        if (!target)
        {
            target = new GameObject("UB_MonsterPhotoTarget").transform;
            target.SetParent(graphics.transform, false);
        }
        target.position = bounds.center;

        const float fieldOfView = 30f;
        var radius = bounds.extents.magnitude;
        var distance = radius * 1.2f / Mathf.Sin(fieldOfView * Mathf.Deg2Rad / 2f);
        parameters.ForceFollowOffset = true;
        parameters.FollowOffset = new Vector3(0, radius * 0.5f, -distance);
        parameters.ForceFieldOfView = true;
        parameters.FieldOfView = fieldOfView;
        parameters.ForceScreenOffset = true;
        parameters.ScreenX = parameters.ScreenY = 0.5f;
        parameters.ForceLightingOffset = true;
        parameters.LightingOffset = Vector3.zero;
        return target;
    }

    // Gets or creates a prefab from a Blender Model on local disk, and returns an asset reference
    [NotNull]
    internal static AssetReference GetBlenderModel(string name)
    {
        var prefabGuid = GetGuid(name);

        if (!PrefabsByGuid.ContainsKey(prefabGuid))
        {
            BlenderModelLoader.Shared.LoadModel(name);
        }

        return new AssetReference(prefabGuid);
    }

    internal static void AlertIfModelsNotFound()
    {
        var blenderModels = Path.Combine(Main.ModFolder, "BlenderModels");

        string[] expectedModels = ["ArcaneFocusOrb", "EldritchCannon", "EldritchCannonCradle", "EldritchCannonBarrel", "EldritchCannonLegUpper",
            "EldritchCannonLegLower", "EldritchCannonFoot", "Katana", "LightningLauncher", "LongMace", "Pike", "ThunderGauntlet"];
        var missingModel = false;

        foreach (var expectedModel in expectedModels)
        {
            if (!Directory.Exists(Path.Combine(blenderModels, expectedModel)))
            {
                missingModel = true;
                break;
            }
        }

        if (missingModel)
        {
            Gui.GuiService.ShowMessage(
                MessageModal.Severity.Attention2,
                "Message/&MessageModWelcomeTitle",
                "ModUi/&MissingBlenderModelsDescription",
                "Message/&MessageOkTitle",
                "Message/&MessageCancelTitle",
                () => { },
                () => { });
        }
    }

    private static string GetGuid(string name)
    {
        return GuidHelper.Create(DefinitionBuilder.CeNamespaceGuid, name).ToString();
    }

    #region Blender Model Loader

    internal sealed class BlenderModelLoader : MonoBehaviour
    {
        private static readonly int Alpha = Shader.PropertyToID("_Alpha");
        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int SpecColor = Shader.PropertyToID("_SpecColor");
        private static readonly int AmbientColor = Shader.PropertyToID("_AmbientColor");
        private static readonly int Glossiness = Shader.PropertyToID("_Glossiness");
        private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
        private static BlenderModelLoader _shared;
        private readonly List<AssetBundle> _materialBundles = [];
        private Transform _prefabRoot;
        private GameObject _template;
        public string directoryPath;

        [NotNull]
        internal static BlenderModelLoader Shared
        {
            get
            {
                if (_shared)
                {
                    return _shared;
                }

                _shared = new GameObject().AddComponent<BlenderModelLoader>();
                DontDestroyOnLoad(_shared.gameObject);
                var prefabRoot = new GameObject("Blender Model Prefabs");
                prefabRoot.SetActive(false);
                prefabRoot.transform.SetParent(_shared.transform, false);
                _shared._prefabRoot = prefabRoot.transform;
                _shared._template = new GameObject("BlenderModelLoader", typeof(MeshFilter), typeof(MeshRenderer));
                _shared._template.transform.SetParent(_shared._prefabRoot, false);

                return _shared;
            }
        }

        public void LoadModel(string filename)
        {
            // should get better performance by instantiating the prefab outside the coroutine
            var prefab = Instantiate(_template, _prefabRoot, false);
            var meshFilter = prefab.GetComponent<MeshFilter>();
            var meshRenderer = prefab.GetComponent<MeshRenderer>();

            directoryPath = Path.Combine(Path.Combine(Main.ModFolder, "BlenderModels"), filename);
            StartCoroutine(ConstructModel(filename, prefab, meshFilter, meshRenderer));
        }

        internal static void Unload()
        {
            if (!_shared)
            {
                return;
            }

            _shared.StopAllCoroutines();
            foreach (var bundle in _shared._materialBundles)
            {
                if (bundle)
                {
                    bundle.Unload(true);
                }
            }
            _shared._materialBundles.Clear();
            Destroy(_shared.gameObject);
            _shared = null;
        }

        private IEnumerator ConstructModel(
            string filename, GameObject prefab, MeshFilter meshFilter, MeshRenderer meshRenderer)
        {
            if (!TryReadObjectAndCorrespondingMaterialFiles(directoryPath, filename, out var obj))
            {
                yield break;
            }

            var prefabGuid = GetGuid(filename);

            meshFilter.mesh = PopulateMesh(obj);
            meshRenderer.materials = DefineMaterial(filename, obj);
            PrefabsByGuid[prefabGuid] = prefab;

            Main.Info($"Model {filename} loaded.");
        }

        private static Mesh PopulateMesh(ObjectFile obj)
        {
            var mesh = new Mesh();
            LoadedMeshes.Add(mesh);
            var triplets = new List<int[]>();
            var subMeshes = new List<int>();

            for (var i = 0; i < obj.f.Count; i += 1)
            {
                for (var j = 0; j < obj.f[i].Count; j += 1)
                {
                    triplets.Add(obj.f[i][j]);
                }

                subMeshes.Add(obj.f[i].Count);
            }

            var vertices = new Vector3[triplets.Count];
            var normals = new Vector3[triplets.Count];
            var uvs = new Vector2[triplets.Count];

            for (var i = 0; i < triplets.Count; i += 1)
            {
                vertices[i] = obj.v[triplets[i][0] - 1];
                normals[i] = obj.vn[triplets[i][2] - 1];

                if (triplets[i][1] > 0)
                {
                    uvs[i] = obj.vt[triplets[i][1] - 1];
                }
            }

            mesh.name = obj.o;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subMeshes.Count;

            var vertex = 0;

            for (var i = 0; i < subMeshes.Count; i += 1)
            {
                var triangles = new int[subMeshes[i]];

                for (var j = 0; j < subMeshes[i]; j += 1)
                {
                    triangles[j] = vertex;
                    vertex += 1;
                }

                mesh.SetTriangles(triangles, i);
            }

            mesh.RecalculateBounds();
            mesh.Optimize();

            mesh.RecalculateTangents();
            return mesh;
        }

        [CanBeNull]
        private static Texture2D LoadTexture(string modelName, string name, bool linear = false)
        {
            var directoryPath = Path.Combine(Path.Combine(Main.ModFolder, "BlenderModels"), modelName);
            var filename = Path.GetFullPath(Path.Combine(directoryPath, name));
            var key = filename + (linear ? "|linear" : "|srgb");
            if (TexturesByPath.TryGetValue(key, out var existing) && existing)
            {
                return existing;
            }

            try
            {
                var textureData = File.ReadAllBytes(filename);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);

                if (!texture.LoadImage(textureData))
                {
                    Main.Error($"Failed to load texture from {filename}");
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                LoadedTextures.Add(texture);
                TexturesByPath[key] = texture;

                return texture;
            }
            catch
            {
                Main.Error($"Failed to open file {filename}");
            }

            return null;
        }

        private Dictionary<string, Material> LoadMaterialTemplates(string modelName)
        {
            var bundlePath = Path.Combine(Main.ModFolder, "BlenderModels", modelName,
                modelName + ".materials.bundle");
            var templates = new Dictionary<string, Material>(StringComparer.Ordinal);
            if (!File.Exists(bundlePath))
            {
                return templates;
            }

            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (!bundle)
            {
                throw new InvalidDataException($"Cannot load model material bundle {bundlePath}.");
            }

            try
            {
                foreach (var material in bundle.LoadAllAssets<Material>())
                {
                    if (!material || !material.shader || !material.shader.isSupported)
                    {
                        throw new InvalidDataException($"Unsupported material in {bundlePath}.");
                    }

                    templates.Add(material.name, material);
                }

                _materialBundles.Add(bundle);
                return templates;
            }
            catch
            {
                bundle.Unload(true);
                throw;
            }
        }

        private Material[] DefineMaterial(string modelName, ObjectFile obj)
        {
            var materials = new Material[obj.usemtl.Count];
            var materialByName = new Dictionary<string, Material>(StringComparer.Ordinal);
            var templates = LoadMaterialTemplates(modelName);

            for (var i = 0; i < obj.usemtl.Count; i += 1)
            {
                var materialName = obj.usemtl[i];
                if (materialByName.TryGetValue(materialName, out var existing))
                {
                    materials[i] = existing;
                    continue;
                }

                NewMtl newMtl = null;
                var hasTemplate = templates.TryGetValue(materialName, out var template);
                var material = hasTemplate ? new Material(template) : new Material(Shader.Find("Standard"));
                LoadedMaterials.Add(material);
                material.name = materialName;

                foreach (var materialDescription in obj.Materials)
                {
                    if (materialDescription.newmtl != materialName)
                    {
                        continue;
                    }

                    newMtl = materialDescription;
                    break;
                }

                if (newMtl != null)
                {
                    if (string.IsNullOrEmpty(newMtl.map_Kd))
                    {
                        var albedo = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                        {
                            name = materialName + " Albedo"
                        };
                        albedo.SetPixel(0, 0, new Color(newMtl.Kd.x, newMtl.Kd.y, newMtl.Kd.z));
                        albedo.Apply();
                        LoadedTextures.Add(albedo);
                        if (material.HasProperty(MainTex))
                        {
                            material.SetTexture(MainTex, albedo);
                        }
                    }
                    if (material.HasProperty(ColorProperty))
                    {
                        material.SetColor(ColorProperty, new Color(1f, 1f, 1f, newMtl.d));
                    }
                    if (material.HasProperty(Alpha))
                    {
                        material.SetFloat(Alpha, newMtl.d);
                    }
                    if (material.HasProperty(Glossiness))
                    {
                        material.SetFloat(Glossiness, Mathf.Clamp01(newMtl.Ns / 1000f));
                    }
                    if (material.HasProperty(AmbientColor))
                    {
                        material.SetColor(AmbientColor, new Color(newMtl.Ka.x, newMtl.Ka.y, newMtl.Ka.z));
                    }
                    if (material.HasProperty(SpecColor))
                    {
                        material.SetColor(SpecColor, new Color(newMtl.Ks.x, newMtl.Ks.y, newMtl.Ks.z));
                    }
                    if (material.HasProperty(EmissionColor))
                    {
                        material.SetColor(EmissionColor, new Color(newMtl.Ke.x, newMtl.Ke.y, newMtl.Ke.z));
                    }
                    if (!hasTemplate)
                    {
                        if (newMtl.Ke.sqrMagnitude > 0)
                        {
                            material.EnableKeyword("_EMISSION");
                        }
                        else
                        {
                            material.DisableKeyword("_EMISSION");
                        }
                        var translucent = newMtl.d < 1f;
                        material.SetInt(SrcBlend, translucent ? (int)BlendMode.SrcAlpha : (int)BlendMode.One);
                        material.SetInt(DstBlend, translucent ? (int)BlendMode.OneMinusSrcAlpha : (int)BlendMode.Zero);
                        material.SetInt(ZWrite, translucent ? 0 : 1);
                        material.DisableKeyword("_ALPHATEST_ON");
                        if (translucent)
                        {
                            material.EnableKeyword("_ALPHABLEND_ON");
                        }
                        else
                        {
                            material.DisableKeyword("_ALPHABLEND_ON");
                        }
                        material.renderQueue = translucent ? (int)RenderQueue.Transparent : (int)RenderQueue.Geometry;
                    }

                    if (material.HasProperty(MainTex) && !string.IsNullOrEmpty(newMtl.map_Kd))
                    {
                        var mainTex = LoadTexture(modelName, newMtl.map_Kd);

                        if (mainTex)
                        {
                            material.SetTexture(MainTex, mainTex);
                        }
                    }

                    if (material.HasProperty(BumpMap) && !string.IsNullOrEmpty(newMtl.map_Bump))
                    {
                        var bumpMap = LoadTexture(modelName, newMtl.map_Bump, true);

                        if (bumpMap)
                        {
                            material.SetTexture(BumpMap, bumpMap);
                            if (!hasTemplate)
                            {
                                material.EnableKeyword("_NORMALMAP");
                            }
                        }
                    }
                }
                else
                {
                    Main.Error($"Cannot find material {materialName} in {obj.mtllib}.");
                }

                materials[i] = material;
                materialByName.Add(materialName, material);

                var materialGuid = GetGuid(modelName + "/" + materialName);

                MaterialsByGuid[materialGuid] = material;
            }

            return materials;
        }
    }

    #endregion

    #region Patch Helpers

    private static readonly Dictionary<string, GameObject> PrefabsByGuid = [];
    private static readonly HashSet<Mesh> LoadedMeshes = [];
    private static readonly Dictionary<string, Material> MaterialsByGuid = [];
    private static readonly HashSet<Material> LoadedMaterials = [];
    private static readonly List<Texture2D> LoadedTextures = [];
    private static readonly Dictionary<string, Texture2D> TexturesByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(Mesh Mesh, int Overlays), Mesh> OverlayMeshes = [];
    private static readonly Dictionary<Mesh, Mesh> OverlayMeshSources = [];

    internal static void Unload()
    {
        foreach (var mesh in OverlayMeshes.Values)
        {
            UnityEngine.Object.Destroy(mesh);
        }
        OverlayMeshes.Clear();
        OverlayMeshSources.Clear();
        foreach (var mesh in LoadedMeshes)
        {
            if (mesh)
            {
                UnityEngine.Object.Destroy(mesh);
            }
        }
        LoadedMeshes.Clear();
        var destroyedPrefabs = new HashSet<GameObject>();

        foreach (var prefab in PrefabsByGuid.Values)
        {
            if (!prefab || !destroyedPrefabs.Add(prefab))
            {
                continue;
            }

            UnityEngine.Object.Destroy(prefab);
        }

        PrefabsByGuid.Clear();

        foreach (var material in LoadedMaterials)
        {
            if (!material)
            {
                continue;
            }

            UnityEngine.Object.Destroy(material);
        }

        LoadedMaterials.Clear();
        MaterialsByGuid.Clear();
        var destroyedTextures = new HashSet<Texture2D>();

        foreach (var texture in LoadedTextures)
        {
            if (!texture || !destroyedTextures.Add(texture))
            {
                continue;
            }

            UnityEngine.Object.Destroy(texture);
        }

        LoadedTextures.Clear();
        TexturesByPath.Clear();
        BlenderModelLoader.Unload();
    }

    [CanBeNull]
    internal static GameObject GetPrefabByGuid([NotNull] string guid)
    {
        return PrefabsByGuid.TryGetValue(guid, out var prefab) ? prefab : null;
    }

    internal static Material GetMaterialByGuid([NotNull] string guid)
    {
        return MaterialsByGuid.TryGetValue(guid, out var material) ? material : null;
    }

    #endregion

    #region Blender Model Parser

    [Serializable]
    private sealed class ObjectFile
    {
        internal const int CurrentSchemaVersion = 2;

        [OptionalField] public int SchemaVersion = CurrentSchemaVersion;

        // ReSharper disable file InconsistentNaming
        public string o;
        public string mtllib;
        public List<string> usemtl;
        public List<Vector3> v;
        public List<Vector3> vn;
        public List<Vector2> vt;
        public List<List<int[]>> f;
        public List<NewMtl> Materials = [];
    }
    // ReSharper enable file InconsistentNaming

    [Serializable]
    private sealed class NewMtl
    {
        // ReSharper disable file InconsistentNaming
        public string newmtl;
        public float Ns;

        public Vector3 Ka;

        [OptionalField] public Vector3 Kd = Vector3.one;
        public Vector3 Ks;

        public Vector3 Ke;

        //[UsedImplicitly] public float Ni ;
        public float d = 1f;

        //[UsedImplicitly] public float illum ;
        public string map_Kd;
        public string map_Bump;
    }
    // ReSharper enable file InconsistentNaming

    private static bool TryReadObjectAndCorrespondingMaterialFiles(
        string pathModel, string filename, out ObjectFile objFile)
    {
        objFile = new ObjectFile();

        var pathCache = Path.Combine(pathModel, "../../../ModelsCache");

        if (!Directory.Exists(pathCache))
        {
            Directory.CreateDirectory(pathCache);
        }

        var pathObjFile = Path.Combine(pathModel, $"{filename}.obj");
        var pathMtlFile = Path.Combine(pathModel, $"{filename}.mtl");
        var pathCacheFile = Path.Combine(pathCache, $"{filename}.cache");

        if (File.Exists(pathObjFile) &&
            File.Exists(pathCacheFile) &&
            File.GetLastWriteTime(pathCacheFile) >= File.GetLastWriteTime(pathObjFile) &&
            File.GetLastWriteTime(pathCacheFile) >= File.GetLastWriteTime(pathMtlFile) &&
            Deserialize(pathCacheFile, ref objFile))
        {
            Main.Info($"Read {filename} from cache.");

            return true;
        }

        try
        {
            var lines = File.ReadAllLines(pathObjFile);

            objFile.usemtl = [];
            objFile.v = [];
            objFile.vn = [];
            objFile.vt = [];
            objFile.f = [];

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                {
                    continue;
                }

                var token = line.Split(' ');

                switch (token[0])
                {
                    case "o":
                        objFile.o = token[1];
                        break;
                    case "mtllib":
                        objFile.mtllib = token[1];
                        break;
                    case "usemtl":
                        objFile.usemtl.Add(token[1]);
                        objFile.f.Add([]);
                        break;
                    case "v":
                        objFile.v.Add(new Vector3(
                            ParseNumber(token[1]),
                            ParseNumber(token[2]),
                            ParseNumber(token[3])));
                        break;
                    case "vn":
                        objFile.vn.Add(new Vector3(
                            ParseNumber(token[1]),
                            ParseNumber(token[2]),
                            ParseNumber(token[3])));
                        break;
                    case "vt":
                        objFile.vt.Add(new Vector3(
                            ParseNumber(token[1]),
                            ParseNumber(token[2])));
                        break;
                    case "f":
                        for (var i = 1; i < 4; i += 1)
                        {
                            var triplet = Array.ConvertAll(token[i].Split('/'),
                                x => string.IsNullOrEmpty(x) ? 0 : int.Parse(x));
                            objFile.f[objFile.f.Count - 1].Add(triplet);
                        }

                        break;
                }
            }

            if (!TryReadMaterialFile(pathModel, filename, objFile.Materials))
            {
                return false;
            }

            Serialize(objFile, pathCacheFile);

            return true;
        }
        catch
        {
            Main.Error($"Failed to read object file {pathObjFile}");

            return false;
        }
    }

    private static bool TryReadMaterialFile(string modelPath, string filename, List<NewMtl> materials)
    {
        var pathMtl = Path.Combine(modelPath, $"{filename}.mtl");

        try
        {
            var lines = File.ReadAllLines(pathMtl);

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                {
                    continue;
                }

                var token = line.Split(' ');

                switch (token[0])
                {
                    case "newmtl":
                        materials.Add(new NewMtl { newmtl = token[1] });
                        break;
                    case "Ns":
                        materials[materials.Count - 1].Ns = ParseNumber(token[1]);
                        break;
                    case "Ka":
                        materials[materials.Count - 1].Ka =
                            new Vector3(ParseNumber(token[1]), ParseNumber(token[2]), ParseNumber(token[3]));
                        break;
                    case "Kd":
                        materials[materials.Count - 1].Kd =
                            new Vector3(ParseNumber(token[1]), ParseNumber(token[2]), ParseNumber(token[3]));
                        break;
                    case "Ks":
                        materials[materials.Count - 1].Ks =
                            new Vector3(ParseNumber(token[1]), ParseNumber(token[2]), ParseNumber(token[3]));
                        break;
                    case "Ke":
                        materials[materials.Count - 1].Ke =
                            new Vector3(ParseNumber(token[1]), ParseNumber(token[2]), ParseNumber(token[3]));
                        break;
                    case "Ni":
                        // materials[materials.Count - 1].Ni = float.Parse(token[1]);
                        break;
                    case "d":
                        materials[materials.Count - 1].d = ParseNumber(token[1]);
                        break;
                    case "ileum":
                        // materials[materials.Count - 1].illum = float.Parse(token[1]);
                        break;
                    case "map_Kd":
                        materials[materials.Count - 1].map_Kd = token[1];
                        break;
                    case "map_Bump":
                        materials[materials.Count - 1].map_Bump = token[1];
                        break;
                }
            }

            return true;
        }
        catch
        {
            Main.Error($"Failed to read material file {pathMtl}");

            return false;
        }
    }

    private static float ParseNumber(string value) => float.Parse(value, CultureInfo.InvariantCulture);

    private static void Serialize(ObjectFile objFile, string path)
    {
        try
        {
            using var file = File.Create(path);
            var bf = GetBinaryFormatter();

            bf.Serialize(file, objFile);
        }
        catch (Exception e)
        {
            Main.Error($"Failed to serialize {path} {e.Message}");
        }
    }

    private static bool Deserialize(string path, ref ObjectFile objFile)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open);
            var bf = GetBinaryFormatter();

            var cached = (ObjectFile)bf.Deserialize(file);
            if (cached.SchemaVersion != ObjectFile.CurrentSchemaVersion)
            {
                return false;
            }

            objFile = cached;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BinaryFormatter GetBinaryFormatter()
    {
        var bf = new BinaryFormatter();
        var surrogateSelector = new SurrogateSelector();
        var vector2SS = new Vector2SerializationSurrogate();
        var vector3SS = new Vector3SerializationSurrogate();

        surrogateSelector.AddSurrogate(
            typeof(Vector2), new StreamingContext(StreamingContextStates.All), vector2SS);
        surrogateSelector.AddSurrogate(
            typeof(Vector3), new StreamingContext(StreamingContextStates.All), vector3SS);

        bf.SurrogateSelector = surrogateSelector;

        return bf;
    }

    private sealed class Vector2SerializationSurrogate : ISerializationSurrogate
    {
        public void GetObjectData(object obj, SerializationInfo info, StreamingContext context)
        {
            var v2 = (Vector2)obj;

            info.AddValue("x", v2.x);
            info.AddValue("y", v2.y);
        }

        public object SetObjectData(
            object obj, SerializationInfo info, StreamingContext context, ISurrogateSelector selector)
        {
            var v2 = (Vector2)obj;

            v2.x = (float)info.GetValue("x", typeof(float));
            v2.y = (float)info.GetValue("y", typeof(float));
            obj = v2;

            return obj;
        }
    }

    private sealed class Vector3SerializationSurrogate : ISerializationSurrogate
    {
        public void GetObjectData(object obj, SerializationInfo info, StreamingContext context)
        {
            var v3 = (Vector3)obj;

            info.AddValue("x", v3.x);
            info.AddValue("y", v3.y);
            info.AddValue("z", v3.z);
        }

        public object SetObjectData(
            object obj, SerializationInfo info, StreamingContext context, ISurrogateSelector selector)
        {
            var v3 = (Vector3)obj;

            v3.x = (float)info.GetValue("x", typeof(float));
            v3.y = (float)info.GetValue("y", typeof(float));
            v3.z = (float)info.GetValue("z", typeof(float));
            obj = v3;

            return obj;
        }
    }

    #endregion
}
