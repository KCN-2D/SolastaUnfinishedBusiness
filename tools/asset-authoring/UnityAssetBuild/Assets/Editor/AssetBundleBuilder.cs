using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolastaAssetAuthoring
{
    // Editor-only authoring tool. No custom runtime component is included in the bundle.
    public static class AssetBundleBuilder
    {
        private const string EditorVersion = "2019.4.37f1";
        private const string ProjectMarker = "SolastaUBAssetAuthoring:UnityProject:1";
        private const string ModelPath = "Assets/Input/SampleRig.fbx";
        private const string GeneratedFolder = "Assets/Generated";
        private const string ClipPath = GeneratedFolder + "/SampleMotion.anim";
        private const string ControllerPath = GeneratedFolder + "/SampleRig.controller";
        private const string PrefabPath = GeneratedFolder + "/SampleRig.prefab";
        private const string BundleName = "ub_authoring_sample";
        private const int SampleCount = 25;

        [Serializable]
        private sealed class Validation
        {
            public int skinnedMeshCount;
            public int boneCount;
            public int vertexCount;
            public float clipSeconds;
            public int sampledFrames;
            public int deformedFrames;
            public float maxVertexDisplacement;
            public float maxBoneAngleDegrees;
            public float maxBonePositionDisplacement;
            public Vector3 boundsSize;
        }

        [Serializable]
        private sealed class Report
        {
            public int schemaVersion = 1;
            public bool success;
            public string stage = "initializing";
            public string unityVersion;
            public string startedUtc;
            public string completedUtc;
            public string inputSha256;
            public string bundleSha256;
            public string bundlePath;
            public string[] assetNames;
            public Validation imported;
            public Validation reloaded;
            public string error;
            public string gameValidation = "not_run";
        }

        public static void Build()
        {
            var report = new Report
            {
                unityVersion = Application.unityVersion,
                startedUtc = DateTime.UtcNow.ToString("o")
            };
            string reportPath = null;
            try
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var marker = Path.Combine(projectRoot, ".solasta-asset-authoring-project");
                Require(File.Exists(marker) && File.ReadAllText(marker).Trim() == ProjectMarker,
                    "Use Invoke-UnityAssetBuild.ps1 to create an isolated, marked project copy.");
                RejectReparsePoints(projectRoot);
                var sampleFbx = Path.GetFullPath(GetArgument("-sampleFbx"));
                var output = Path.GetFullPath(GetArgument("-bundleOutput"));
                Require(SamePath(sampleFbx, Path.Combine(projectRoot, ModelPath)),
                    "-sampleFbx must be the copied Assets/Input/SampleRig.fbx.");
                Require(SamePath(output, Path.Combine(projectRoot, "BuildOutput")),
                    "-bundleOutput must be this project copy's BuildOutput directory.");
                RejectReparsePoints(sampleFbx);
                RejectReparsePoints(output);
                Directory.CreateDirectory(output);
                reportPath = Path.Combine(output, "unity-report.json");
                Require(!File.Exists(reportPath), "This project has already run. Create a new run with the launcher.");
                Require(Application.unityVersion == EditorVersion, "Unity " + EditorVersion + " is required.");
                Require(File.Exists(sampleFbx), "The copied FBX does not exist.");
                report.inputSha256 = Sha256(sampleFbx);

                report.stage = "import";
                AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
                Require(importer != null, "FBX ModelImporter was not created.");
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.globalScale = 1f;
                importer.useFileScale = true;
                importer.importAnimation = true;
                importer.optimizeGameObjects = false;
                importer.isReadable = true;
                importer.addCollider = false;
                importer.importCameras = false;
                importer.importLights = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.animationCompression = ModelImporterAnimationCompression.Off;
                importer.SaveAndReimport();
                importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
                var takes = importer.defaultClipAnimations;
                Require(takes != null && takes.Length == 1, "The sample FBX must contain exactly one animation take.");
                takes[0].name = "SampleMotion";
                takes[0].loopTime = true;
                importer.clipAnimations = takes;
                importer.SaveAndReimport();

                var importedClips = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<AnimationClip>()
                    .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
                Require(importedClips.Length == 1, "Expected exactly one imported, non-preview clip.");
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                Require(model != null, "The FBX model could not be loaded.");
                report.imported = Validate(model, importedClips[0]);

                report.stage = "create_assets";
                Require(!AssetDatabase.IsValidFolder(GeneratedFolder), "Generated assets already exist; use a fresh run.");
                AssetDatabase.CreateFolder("Assets", "Generated");
                var clipAsset = Object.Instantiate(importedClips[0]);
                clipAsset.name = "SampleMotion";
                AssetDatabase.CreateAsset(clipAsset, ClipPath);
                var controller = AnimatorController.CreateAnimatorControllerAtPathWithClip(ControllerPath, clipAsset);
                Require(controller != null, "AnimatorController creation failed.");
                var shader = Shader.Find("Standard");
                Require(shader != null, "The built-in Standard shader is unavailable.");
                var material = new Material(shader) { name = "SampleMaterial", color = new Color(0.15f, 0.55f, 0.8f) };
                AssetDatabase.CreateAsset(material, GeneratedFolder + "/SampleMaterial.mat");
                var instance = Object.Instantiate(model);
                try
                {
                    instance.name = "SampleRig";
                    var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
                    animator.runtimeAnimatorController = controller;
                    animator.applyRootMotion = false;
                    foreach (var renderer in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMesh.subMeshCount).ToArray();
                    }
                    bool saved;
                    var prefab = PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath, out saved);
                    Require(saved && prefab != null, "Prefab creation failed.");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
                AssetDatabase.SaveAssets();

                report.stage = "build_bundle";
                var explicitAssets = new[] { PrefabPath, ClipPath, ControllerPath };
                var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = explicitAssets };
                var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build },
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
                    BuildTarget.StandaloneWindows64);
                Require(manifest != null, "BuildAssetBundles failed.");
                report.bundlePath = Path.Combine(output, BundleName);
                Require(File.Exists(report.bundlePath), "The expected AssetBundle file was not produced.");
                report.bundleSha256 = Sha256(report.bundlePath);

                report.stage = "reload_bundle";
                var bundle = AssetBundle.LoadFromFile(report.bundlePath);
                Require(bundle != null, "AssetBundle.LoadFromFile failed.");
                try
                {
                    report.assetNames = bundle.GetAllAssetNames();
                    foreach (var path in explicitAssets)
                    {
                        Require(report.assetNames.Contains(path.ToLowerInvariant()), "Missing bundle asset: " + path);
                    }
                    var prefab = bundle.LoadAsset<GameObject>(PrefabPath);
                    var reloadedClip = bundle.LoadAsset<AnimationClip>(ClipPath);
                    var reloadedController = bundle.LoadAsset<RuntimeAnimatorController>(ControllerPath);
                    Require(prefab != null && reloadedClip != null && reloadedController != null,
                        "The bundle must reload a prefab, clip and runtime animator controller.");
                    var animator = prefab.GetComponent<Animator>();
                    Require(animator != null && animator.runtimeAnimatorController == reloadedController,
                        "The reloaded prefab does not refer to the bundled animator controller.");
                    Require(animator.avatar != null && animator.avatar.isValid && !animator.avatar.isHuman,
                        "The reloaded prefab must retain its valid Generic avatar.");
                    Require(reloadedController.animationClips.Contains(reloadedClip),
                        "The reloaded controller does not refer to the bundled animation clip.");
                    report.reloaded = Validate(prefab, reloadedClip);
                    Require(Mathf.Abs(report.reloaded.clipSeconds - report.imported.clipSeconds) < 0.001f,
                        "The reloaded clip duration changed.");
                    Require(report.reloaded.vertexCount == report.imported.vertexCount &&
                            report.reloaded.boneCount == report.imported.boneCount,
                        "Reload changed the mesh or skeleton counts.");
                }
                finally
                {
                    bundle.Unload(true);
                }
                report.stage = "complete";
                report.success = true;
                report.completedUtc = DateTime.UtcNow.ToString("o");
                File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
                Debug.Log("Asset authoring validation succeeded: " + reportPath);
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                report.completedUtc = DateTime.UtcNow.ToString("o");
                // Write failures only after the marked output path has passed validation.
                if (reportPath != null && !File.Exists(reportPath))
                {
                    File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
                }
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static Validation Validate(GameObject prefab, AnimationClip clip)
        {
            Require(clip != null && clip.length >= 0.1f && clip.length <= 10f,
                "A short animation clip between 0.1 and 10 seconds is required.");
            var instance = Object.Instantiate(prefab);
            try
            {
                Require(instance.GetComponentsInChildren<MonoBehaviour>(true).Length == 0,
                    "No custom runtime scripts are permitted in this authoring sample.");
                var rootAnimator = instance.GetComponent<Animator>();
                Require(rootAnimator != null && rootAnimator.avatar != null && rootAnimator.avatar.isValid &&
                        !rootAnimator.avatar.isHuman, "The imported model must have a valid Generic avatar.");
                foreach (var animator in instance.GetComponentsInChildren<Animator>(true))
                {
                    animator.enabled = false;
                }
                var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Require(renderers.Length > 0, "No SkinnedMeshRenderer was imported.");
                foreach (var renderer in renderers)
                {
                    Require(renderer.sharedMesh != null && renderer.sharedMesh.vertexCount > 0,
                        "A skinned mesh is empty.");
                    Require(renderer.bones.Length > 0 && renderer.bones.All(bone => bone != null),
                        "A skinned mesh has missing bones.");
                    Require(renderer.sharedMesh.bindposes.Length == renderer.bones.Length,
                        "The bind pose and bone counts differ.");
                    Require(renderer.sharedMesh.boneWeights.Any(weight =>
                            weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3 > 0f),
                        "The mesh has no bone weights.");
                }
                var bones = renderers.SelectMany(renderer => renderer.bones).Distinct().ToArray();
                Require(bones.Length >= 2, "The sample must have at least two weighted bones.");
                clip.SampleAnimation(instance, 0f);
                var baseline = renderers.Select(Snapshot).ToArray();
                var rotations = bones.Select(bone => bone.localRotation).ToArray();
                var positions = bones.Select(bone => bone.localPosition).ToArray();
                var bounds = new Bounds();
                var hasPoint = false;
                for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    foreach (var vertex in baseline[rendererIndex])
                    {
                        var point = instance.transform.InverseTransformPoint(renderers[rendererIndex].transform.TransformPoint(vertex));
                        if (!hasPoint) { bounds = new Bounds(point, Vector3.zero); hasPoint = true; }
                        else { bounds.Encapsulate(point); }
                    }
                }
                var size = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                Require(size >= 0.01f && size <= 10f, "The small sample bounds are outside 0.01..10 Unity units. Check FBX units.");
                var result = new Validation
                {
                    skinnedMeshCount = renderers.Length, boneCount = bones.Length,
                    vertexCount = baseline.Sum(vertices => vertices.Length), clipSeconds = clip.length,
                    sampledFrames = SampleCount, boundsSize = bounds.size
                };
                for (var frame = 0; frame < SampleCount; frame++)
                {
                    clip.SampleAnimation(instance, clip.length * frame / (SampleCount - 1));
                    var frameMax = 0f;
                    for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                    {
                        var current = Snapshot(renderers[rendererIndex]);
                        Require(current.Length == baseline[rendererIndex].Length, "Animation changed the vertex count.");
                        for (var vertex = 0; vertex < current.Length; vertex++)
                        {
                            var delta = Vector3.Distance(current[vertex], baseline[rendererIndex][vertex]);
                            Require(!float.IsNaN(delta) && !float.IsInfinity(delta), "Non-finite skinned vertex.");
                            frameMax = Mathf.Max(frameMax, delta);
                        }
                    }
                    if (frameMax > 0.00001f) { result.deformedFrames++; }
                    result.maxVertexDisplacement = Mathf.Max(result.maxVertexDisplacement, frameMax);
                    for (var bone = 0; bone < bones.Length; bone++)
                    {
                        result.maxBoneAngleDegrees = Mathf.Max(result.maxBoneAngleDegrees,
                            Quaternion.Angle(rotations[bone], bones[bone].localRotation));
                        result.maxBonePositionDisplacement = Mathf.Max(result.maxBonePositionDisplacement,
                            Vector3.Distance(positions[bone], bones[bone].localPosition));
                    }
                }
                Require(result.deformedFrames > 0, "Sampling the clip did not deform any skinned vertices.");
                Require(result.maxBoneAngleDegrees > 0.01f || result.maxBonePositionDisplacement > 0.00001f,
                    "Sampling the clip did not animate any weighted bone transforms.");
                return result;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static Vector3[] Snapshot(SkinnedMeshRenderer renderer)
        {
            var mesh = new Mesh();
            try { renderer.BakeMesh(mesh); return mesh.vertices; }
            finally { Object.DestroyImmediate(mesh); }
        }

        private static string GetArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index < arguments.Length - 1; index++)
            {
                if (arguments[index] == name) { return arguments[index + 1]; }
            }
            throw new ArgumentException("Missing command-line argument " + name);
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectReparsePoints(string path)
        {
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                        "Reparse-point paths are not accepted: " + current);
                }
            }
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
