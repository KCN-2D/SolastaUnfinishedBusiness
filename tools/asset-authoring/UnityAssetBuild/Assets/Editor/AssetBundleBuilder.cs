using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SolastaAssetAuthoring
{
    // Editor-only authoring tool. No custom runtime component is included in the bundle.
    public static class AssetBundleBuilder
    {
        private const string EditorVersion = "2019.4.37f1";
        private const string ProjectMarker = "SolastaUBAssetAuthoring:UnityProject:1";
        private const string BundleName = "ub_authoring_sample";
        private const int SampleCount = 25;
        private const string JobMarker = "SolastaUBAssetAuthoring:UnityJob:1";
        private const string JobFolder = ".asset-authoring-jobs";
        private static double nextPoll;
        private static bool workerBusy;
        private static string loadedSourceHash;
        internal static string SavingFolder;

        [Serializable]
        private sealed class Job
        {
            public int schemaVersion = 0;
            public string jobId = null;
            public string inputSha256 = null;
            public string kind = null;
        }

        [Serializable] private sealed class InputAsset { public string path = null; public string sha256 = null; }
        [Serializable] private sealed class MaterialProperty
        {
            public string name = null;
            public string kind = null;
            public float value = 0;
            public float[] rgba = null;
            public string texture = null;
        }
        [Serializable] private sealed class MaterialSpec
        {
            public string name = null;
            public string shader = null;
            public string[] requiredPasses = null;
            public MaterialProperty[] properties = null;
            public int renderQueue = -1;
        }
        [Serializable] private sealed class MaterialRecipe
        {
            public int schemaVersion = 0;
            public string bundleName = null;
            public InputAsset[] assets = null;
            public MaterialSpec[] materials = null;
        }
        [Serializable] private sealed class MaterialValidation
        {
            public string name;
            public string shader;
            public bool shaderSupported;
            public string[] passes;
            public int renderQueue;
        }
        [Serializable] private sealed class MaterialIdentityContract
        {
            public string kind, expectedAsset, alternativeAsset, expectedName, alternativeName, rejection;
            public int expectedInstanceId, alternativeInstanceId;
            public bool rejected;
        }

        [Serializable]
        private sealed class WorkerReceipt
        {
            public int schemaVersion = 2;
            public int processId;
            public int mainThread;
            public string processStartedUtc;
            public string projectPath;
            public string unityVersion;
            public string sourceSha256;
            public string updatedUtc;
            public string desktop, inputDesktop, ownerId;
            public bool desktopQuerySucceeded, desktopReceivesInput;
            public uint nativeMainThread;
        }

        [Serializable]
        private sealed class ShutdownRequest
        {
            public int processId;
            public string processStartedUtc, ownerId;
        }

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DesktopName(IntPtr desktop, int information, StringBuilder value, uint size, out uint needed);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
        private static extern bool DesktopInput(IntPtr desktop, int information, out int value, uint size, out uint needed);

        private static string ReadDesktopName(IntPtr desktop)
        {
            if (desktop == IntPtr.Zero) { throw new InvalidOperationException("A desktop handle is unavailable."); }
            var name = new StringBuilder(1024);
            uint needed;
            Require(DesktopName(desktop, 2, name, (uint)(name.Capacity * 2), out needed), "The desktop name cannot be read.");
            return name.ToString();
        }

        private static void RecordWorkerDesktop(WorkerReceipt receipt)
        {
            receipt.nativeMainThread = GetCurrentThreadId();
            var desktop = GetThreadDesktop(receipt.nativeMainThread);
            receipt.desktop = ReadDesktopName(desktop);
            int receivesInput;
            uint needed;
            Require(DesktopInput(desktop, 6, out receivesInput, 4, out needed) && needed == 4, "The desktop input state cannot be read.");
            receipt.desktopReceivesInput = receivesInput != 0;
            var input = OpenInputDesktop(0, false, 1);
            try { receipt.inputDesktop = ReadDesktopName(input); }
            finally { if (input != IntPtr.Zero) { Require(CloseDesktop(input), "The input desktop handle cannot be released."); } }
            receipt.desktopQuerySucceeded = true;
            receipt.ownerId = GetArgument("-ubAuthoringOwner", false);
        }

        private static bool ShutdownOwnedWorker(string directory, WorkerReceipt receipt)
        {
            var path = Path.Combine(directory, "shutdown-request.json");
            if (string.IsNullOrEmpty(receipt.ownerId) || !File.Exists(path)) { return false; }
            RejectReparsePoints(path);
            Require(new FileInfo(path).Length <= 4096, "The worker shutdown request is too large.");
            var request = JsonUtility.FromJson<ShutdownRequest>(File.ReadAllText(path));
            DateTime requestedStart;
            DateTime currentStart;
            if (request == null || request.ownerId != receipt.ownerId || request.processId != receipt.processId ||
                !DateTime.TryParse(request.processStartedUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out requestedStart) ||
                !DateTime.TryParse(receipt.processStartedUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out currentStart) ||
                requestedStart.ToUniversalTime() != currentStart.ToUniversalTime() || !receipt.desktopQuerySucceeded || receipt.desktopReceivesInput ||
                receipt.desktop == receipt.inputDesktop) { return false; }
            File.Delete(path);
            EditorApplication.Exit(0);
            return true;
        }

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
            public string validationScope = "rig";
            public InputAsset[] inputAssets;
            public MaterialValidation[] materials;
            public MeshValidation[] meshes;
            public MaterialIdentityContract[] identityContracts;
            public string transport;
            public string jobId;
            public int processId;
            public int mainThread;
            public string activeScenePathBefore;
            public string activeScenePathAfter;
            public bool activeSceneDirtyBefore;
            public bool activeSceneDirtyAfter;
            public int sceneCountBefore;
            public int sceneCountAfter;
            public string gameValidation = "not_run";
        }

        public static void Build()
        {
            var materialJob = Environment.GetCommandLineArgs().Contains("-materialRecipe");
            var meshJob = Environment.GetCommandLineArgs().Contains("-meshRecipe");
            var success = meshJob ? BuildMeshTemplates(GetArgument("-meshRecipe"), GetArgument("-bundleOutput"), null, null)
                : materialJob ? BuildMaterials(GetArgument("-materialRecipe"), GetArgument("-bundleOutput"), null, null)
                : BuildValidated(GetArgument("-sampleFbx"), GetArgument("-bundleOutput"), null, null);
            if (!success)
            {
                EditorApplication.Exit(1);
            }
        }

        private static bool BuildValidated(string input, string destination, string jobId, string expectedHash)
        {
            var report = new Report
            {
                unityVersion = Application.unityVersion,
                startedUtc = DateTime.UtcNow.ToString("o"),
                transport = jobId == null ? "batch" : "gui_worker",
                jobId = jobId,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                mainThread = Thread.CurrentThread.ManagedThreadId,
                activeScenePathBefore = SceneManager.GetActiveScene().path,
                activeSceneDirtyBefore = SceneManager.GetActiveScene().isDirty,
                sceneCountBefore = SceneManager.sceneCount
            };
            string reportPath = null;
            try
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var marker = Path.Combine(projectRoot, ".solasta-asset-authoring-project");
                Require(File.Exists(marker) && File.ReadAllText(marker).Trim() == ProjectMarker,
                    "Use Invoke-UnityAssetBuild.ps1 to create an isolated, marked project copy.");
                RejectReparsePoints(projectRoot);
                var modelPath = jobId == null ? "Assets/Input/SampleRig.fbx" : "Assets/AuthoringJobs/" + jobId + "/Input/SampleRig.fbx";
                var generatedFolder = jobId == null ? "Assets/Generated" : "Assets/AuthoringJobs/" + jobId + "/Generated";
                var clipPath = generatedFolder + "/SampleMotion.anim";
                var controllerPath = generatedFolder + "/SampleRig.controller";
                var prefabPath = generatedFolder + "/SampleRig.prefab";
                var sampleFbx = Path.GetFullPath(input);
                var output = Path.GetFullPath(destination);
                if (jobId != null) { RequireJob(projectRoot, jobId); }
                Require(SamePath(sampleFbx, Path.Combine(projectRoot, modelPath)), "The FBX is outside this build's input directory.");
                var expectedOutput = jobId == null ? Path.Combine(projectRoot, "BuildOutput") : Path.Combine(projectRoot, JobFolder, jobId, "Output");
                Require(SamePath(output, expectedOutput), "The output is outside this build's isolated directory.");
                RejectReparsePoints(sampleFbx);
                RejectReparsePoints(output);
                Directory.CreateDirectory(output);
                reportPath = Path.Combine(output, "unity-report.json");
                Require(!File.Exists(reportPath), "This project has already run. Create a new run with the launcher.");
                Require(Application.unityVersion == EditorVersion, "Unity " + EditorVersion + " is required.");
                Require(File.Exists(sampleFbx), "The copied FBX does not exist.");
                report.inputSha256 = Sha256(sampleFbx);
                Require(expectedHash == null || report.inputSha256 == expectedHash, "The copied FBX changed after submission.");

                report.stage = "import";
                if (jobId != null)
                {
                    AssetDatabase.ImportAsset("Assets/AuthoringJobs/" + jobId,
                        ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);
                }
                AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
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
                importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
                var takes = importer.defaultClipAnimations;
                Require(takes != null && takes.Length == 1, "The sample FBX must contain exactly one animation take.");
                takes[0].name = "SampleMotion";
                takes[0].loopTime = true;
                importer.clipAnimations = takes;
                importer.SaveAndReimport();

                var importedClips = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>()
                    .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
                Require(importedClips.Length == 1, "Expected exactly one imported, non-preview clip.");
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                Require(model != null, "The FBX model could not be loaded.");
                report.imported = Validate(model, importedClips[0]);

                report.stage = "create_assets";
                Require(!AssetDatabase.IsValidFolder(generatedFolder), "Generated assets already exist; use a fresh run.");
                var parentFolder = generatedFolder.Substring(0, generatedFolder.LastIndexOf('/'));
                Require(AssetDatabase.IsValidFolder(parentFolder), "The build's asset parent directory was not imported.");
                AssetDatabase.CreateFolder(parentFolder, "Generated");
                var clipAsset = Object.Instantiate(importedClips[0]);
                clipAsset.name = "SampleMotion";
                AssetDatabase.CreateAsset(clipAsset, clipPath);
                var controller = AnimatorController.CreateAnimatorControllerAtPathWithClip(controllerPath, clipAsset);
                Require(controller != null, "AnimatorController creation failed.");
                var shader = Shader.Find("Standard");
                Require(shader != null, "The built-in Standard shader is unavailable.");
                var material = new Material(shader) { name = "SampleMaterial", color = new Color(0.15f, 0.55f, 0.8f) };
                AssetDatabase.CreateAsset(material, generatedFolder + "/SampleMaterial.mat");
                var preview = EditorSceneManager.NewPreviewScene();
                GameObject instance = null;
                try
                {
                    instance = Object.Instantiate(model);
                    SceneManager.MoveGameObjectToScene(instance, preview);
                    instance.name = "SampleRig";
                    var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
                    animator.runtimeAnimatorController = controller;
                    animator.applyRootMotion = false;
                    foreach (var renderer in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMesh.subMeshCount).ToArray();
                    }
                    bool saved;
                    var prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out saved);
                    Require(saved && prefab != null, "Prefab creation failed.");
                }
                finally
                {
                    if (instance != null) { Object.DestroyImmediate(instance); }
                    EditorSceneManager.ClosePreviewScene(preview);
                }
                SavingFolder = generatedFolder + "/";
                try { AssetDatabase.SaveAssets(); }
                finally { SavingFolder = null; }

                var explicitAssets = new[] { prefabPath, clipPath, controllerPath };
                var bundle = BuildAndReload(output, BundleName, explicitAssets, generatedFolder, report);
                try
                {
                    var prefab = bundle.LoadAsset<GameObject>(prefabPath);
                    var reloadedClip = bundle.LoadAsset<AnimationClip>(clipPath);
                    var reloadedController = bundle.LoadAsset<RuntimeAnimatorController>(controllerPath);
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
                report.activeScenePathAfter = SceneManager.GetActiveScene().path;
                report.activeSceneDirtyAfter = SceneManager.GetActiveScene().isDirty;
                report.sceneCountAfter = SceneManager.sceneCount;
                Require(report.activeScenePathBefore == report.activeScenePathAfter &&
                        report.activeSceneDirtyBefore == report.activeSceneDirtyAfter &&
                        report.sceneCountBefore == report.sceneCountAfter, "The build changed the open scene state.");
                report.success = true;
                report.completedUtc = DateTime.UtcNow.ToString("o");
                WriteJson(reportPath, report);
                Debug.Log("Asset authoring validation succeeded: " + reportPath);
                return true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString();
                report.completedUtc = DateTime.UtcNow.ToString("o");
                // Write failures only after the marked output path has passed validation.
                if (reportPath != null && !File.Exists(reportPath))
                {
                    WriteJson(reportPath, report);
                }
                Debug.LogException(exception);
                return false;
            }
        }

        private static AssetBundle BuildAndReload(string output, string bundleName, string[] assets, string generatedFolder, Report report)
        {
            report.stage = "build_bundle";
            AssetBundleManifest manifest;
            SavingFolder = generatedFolder + "/";
            try
            {
                manifest = BuildPipeline.BuildAssetBundles(output,
                    new[] { new AssetBundleBuild { assetBundleName = bundleName, assetNames = assets } },
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
            }
            finally { SavingFolder = null; }
            Require(manifest != null, "BuildAssetBundles failed.");
            report.bundlePath = Path.Combine(output, bundleName);
            Require(File.Exists(report.bundlePath), "The expected AssetBundle file was not produced.");
            report.bundleSha256 = Sha256(report.bundlePath);
            report.stage = "reload_bundle";
            var bundle = AssetBundle.LoadFromFile(report.bundlePath);
            Require(bundle != null, "AssetBundle.LoadFromFile failed.");
            try
            {
                report.assetNames = bundle.GetAllAssetNames();
                foreach (var asset in assets)
                {
                    Require(report.assetNames.Contains(asset.ToLowerInvariant()), "Missing bundle asset: " + asset);
                }
                return bundle;
            }
            catch { bundle.Unload(true); throw; }
        }

        [DataContract] private sealed class MeshRecipe
        {
            [DataMember(IsRequired = true)] public int schemaVersion;
            [DataMember(IsRequired = true)] public string bundleName;
            [DataMember(IsRequired = true)] public MeshSpec[] meshes;
        }
        [DataContract] private sealed class MeshBounds
        {
            [DataMember(IsRequired = true)] public float[] center;
            [DataMember(IsRequired = true)] public float[] size;
        }
        [DataContract] private sealed class MeshUv
        {
            [DataMember(IsRequired = true)] public int channel;
            [DataMember(IsRequired = true)] public int dimension;
            [DataMember(IsRequired = true)] public float[] values;
        }
        [DataContract] private sealed class MeshSubmesh
        {
            [DataMember(IsRequired = true)] public string topology;
            [DataMember(IsRequired = true)] public int[] indices;
        }
        [DataContract] private sealed class MeshSpec
        {
            [DataMember(IsRequired = true)] public string name;
            [DataMember(IsRequired = true)] public float[] vertices;
            [DataMember(IsRequired = true)] public float[] normals;
            [DataMember(IsRequired = true)] public float[] tangents;
            [DataMember] public float[] colors;
            [DataMember(IsRequired = true)] public MeshUv[] uvChannels;
            [DataMember(IsRequired = true)] public MeshSubmesh[] subMeshes;
            [DataMember(IsRequired = true)] public MeshBounds bounds;
            [DataMember(IsRequired = true)] public string indexFormat;
        }
        [Serializable] private sealed class MeshChannelValidation
        {
            public string attribute, expectedSha256, reloadedSha256;
            public int dimension, elements;
            public bool exact;
        }
        [Serializable] private sealed class MeshValidation
        {
            public string name, indexFormat;
            public int vertexCount, subMeshCount;
            public bool exact, boundsExact;
            public MeshChannelValidation[] channels;
            public string[] indexSha256;
        }

        private static bool BuildMeshTemplates(string input, string destination, string jobId, string expectedHash)
        {
            var report = new Report
            {
                unityVersion = Application.unityVersion, startedUtc = DateTime.UtcNow.ToString("o"),
                transport = jobId == null ? "batch" : "gui_worker", jobId = jobId, validationScope = "mesh_templates",
                processId = System.Diagnostics.Process.GetCurrentProcess().Id, mainThread = Thread.CurrentThread.ManagedThreadId,
                activeScenePathBefore = SceneManager.GetActiveScene().path, activeSceneDirtyBefore = SceneManager.GetActiveScene().isDirty,
                sceneCountBefore = SceneManager.sceneCount
            };
            string reportPath = null;
            try
            {
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                RejectReparsePoints(root);
                var marker = Path.Combine(root, ".solasta-asset-authoring-project");
                Require(File.Exists(marker) && File.ReadAllText(marker).Trim() == ProjectMarker, "The mesh authoring project is unmarked.");
                if (jobId != null) { RequireJob(root, jobId); }
                var assetRoot = jobId == null ? "Assets/Input" : "Assets/AuthoringJobs/" + jobId + "/Input";
                var generated = jobId == null ? "Assets/Generated" : "Assets/AuthoringJobs/" + jobId + "/Generated";
                var recipePath = Path.GetFullPath(input);
                var output = Path.GetFullPath(destination);
                Require(SamePath(recipePath, Path.Combine(root, assetRoot, "mesh-recipe.json")), "The mesh recipe is outside its owned input directory.");
                Require(SamePath(output, jobId == null ? Path.Combine(root, "BuildOutput") : Path.Combine(root, JobFolder, jobId, "Output")), "The mesh output is outside its isolated directory.");
                RejectReparsePoints(recipePath); RejectReparsePoints(output);
                Require(File.Exists(recipePath) && new FileInfo(recipePath).Length <= 67108864, "The mesh recipe must be at most 64 MiB.");
                Directory.CreateDirectory(output); reportPath = Path.Combine(output, "unity-report.json");
                Require(!File.Exists(reportPath), "This mesh job has already run.");
                Require(Application.unityVersion == EditorVersion, "Unity " + EditorVersion + " is required.");
                var bytes = File.ReadAllBytes(recipePath);
                report.inputSha256 = HashBytes(bytes);
                Require(expectedHash == null || expectedHash == report.inputSha256, "The mesh recipe changed after submission.");
                MeshRecipe recipe;
                using (var stream = new MemoryStream(bytes)) { recipe = (MeshRecipe)new DataContractJsonSerializer(typeof(MeshRecipe)).ReadObject(stream); }
                Require(recipe != null && recipe.schemaVersion == 1 && recipe.bundleName != null && Regex.IsMatch(recipe.bundleName, "^[a-z0-9][a-z0-9_.-]{0,127}$"), "Invalid mesh recipe version or bundle name.");
                Require(recipe.meshes != null && recipe.meshes.Length > 0 && recipe.meshes.Length <= 128, "Invalid mesh list.");
                Require(recipe.meshes.All(mesh => mesh != null && mesh.name != null && Regex.IsMatch(mesh.name, "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")) &&
                        recipe.meshes.Select(mesh => mesh.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == recipe.meshes.Length, "Invalid or duplicate mesh name.");
                foreach (var spec in recipe.meshes) { ValidateMeshSpec(spec); }
                Require(Sha256(recipePath) == report.inputSha256, "The mesh input changed during validation.");
                report.stage = "create_assets";
                AssetDatabase.ImportAsset(assetRoot, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);
                Require(!AssetDatabase.IsValidFolder(generated), "Generated mesh assets already exist.");
                var parent = generated.Substring(0, generated.LastIndexOf('/'));
                Require(AssetDatabase.IsValidFolder(parent), "The mesh job parent was not imported.");
                AssetDatabase.CreateFolder(parent, "Generated");
                var paths = recipe.meshes.Select(mesh => generated + "/" + mesh.name + ".asset").ToArray();
                for (var index = 0; index < recipe.meshes.Length; index++)
                {
                    var mesh = CreateTemplateMesh(recipe.meshes[index]);
                    try { ValidateTemplateMesh(mesh, recipe.meshes[index]); AssetDatabase.CreateAsset(mesh, paths[index]); }
                    finally { if (mesh != null && !EditorUtility.IsPersistent(mesh)) { Object.DestroyImmediate(mesh); } }
                }
                SavingFolder = generated + "/";
                try { AssetDatabase.SaveAssets(); }
                finally { SavingFolder = null; }
                var bundle = BuildAndReload(output, recipe.bundleName, paths, generated, report);
                try
                {
                    Require(bundle.LoadAllAssets().Length == recipe.meshes.Length && bundle.LoadAllAssets().All(asset => asset is Mesh), "The mesh bundle contains unexpected assets.");
                    report.meshes = recipe.meshes.Select((spec, index) => ValidateTemplateMesh(bundle.LoadAsset<Mesh>(paths[index]), spec)).ToArray();
                }
                finally { bundle.Unload(true); }
                Require(Sha256(recipePath) == report.inputSha256, "The mesh input changed during the build.");
                report.activeScenePathAfter = SceneManager.GetActiveScene().path;
                report.activeSceneDirtyAfter = SceneManager.GetActiveScene().isDirty;
                report.sceneCountAfter = SceneManager.sceneCount;
                Require(report.activeScenePathBefore == report.activeScenePathAfter && report.activeSceneDirtyBefore == report.activeSceneDirtyAfter &&
                        report.sceneCountBefore == report.sceneCountAfter, "The mesh build changed the open scene state.");
                report.stage = "complete"; report.success = true; report.completedUtc = DateTime.UtcNow.ToString("o");
                WriteJson(reportPath, report); return true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString(); report.completedUtc = DateTime.UtcNow.ToString("o");
                if (reportPath != null && !File.Exists(reportPath)) { WriteJson(reportPath, report); }
                Debug.LogException(exception); return false;
            }
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static void MeshNumbers(float[] values, int count, string channel)
        {
            Require(values != null && values.Length == count && values.All(Finite), "Invalid mesh " + channel + " numeric data/length.");
        }
        private static Vector3 MeshVector3(float[] values) { return new Vector3(values[0], values[1], values[2]); }
        private static Vector3[] MeshVectors3(float[] values)
        {
            return Enumerable.Range(0, values.Length / 3).Select(i => new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2])).ToArray();
        }
        private static Vector4[] MeshVectors4(float[] values)
        {
            return Enumerable.Range(0, values.Length / 4).Select(i => new Vector4(values[i * 4], values[i * 4 + 1], values[i * 4 + 2], values[i * 4 + 3])).ToArray();
        }
        private static void ValidateMeshSpec(MeshSpec spec)
        {
            Require(spec.vertices != null && spec.vertices.Length >= 9 && spec.vertices.Length <= 3000000 && spec.vertices.Length % 3 == 0, "Invalid mesh vertex count.");
            var count = spec.vertices.Length / 3;
            MeshNumbers(spec.vertices, count * 3, "vertices");
            Require(spec.normals != null && (spec.normals.Length == 0 || spec.normals.Length == count * 3), "Invalid normal count.");
            Require(spec.tangents != null && (spec.tangents.Length == 0 || spec.tangents.Length == count * 4), "Invalid tangent count.");
            MeshNumbers(spec.normals, spec.normals.Length, "normals"); MeshNumbers(spec.tangents, spec.tangents.Length, "tangents");
            Require(spec.colors == null || spec.colors.Length == 0 || spec.colors.Length == count * 4, "Invalid color count.");
            if (spec.colors != null) { MeshNumbers(spec.colors, spec.colors.Length, "colors"); }
            Require(spec.indexFormat == "UInt16" || spec.indexFormat == "UInt32", "Invalid mesh index format.");
            Require(spec.indexFormat != "UInt16" || count <= 65535, "The mesh exceeds UInt16 vertex capacity.");
            Require(spec.bounds != null, "Missing mesh bounds."); MeshNumbers(spec.bounds.center, 3, "bounds center"); MeshNumbers(spec.bounds.size, 3, "bounds size");
            Require(spec.bounds.size.All(value => value >= 0), "Negative mesh bounds size.");
            var bounds = new Bounds(MeshVector3(spec.bounds.center), MeshVector3(spec.bounds.size));
            Require(new[] { bounds.min.x, bounds.min.y, bounds.min.z, bounds.max.x, bounds.max.y, bounds.max.z }.All(Finite), "Mesh bounds overflow.");
            for (var axis = 0; axis < 3; axis++)
            {
                var center = (double)spec.bounds.center[axis]; var size = (double)spec.bounds.size[axis];
                // Two float32 rounding steps in center/size are permitted for enclosure;
                // the declared bounds themselves must still roundtrip bit-for-bit.
                var margin = (Math.Abs(center) + Math.Abs(size)) * 2.384185791015625e-7 + 2.802596928649634e-45;
                for (var vertex = 0; vertex < count; vertex++) { Require(Math.Abs(spec.vertices[vertex * 3 + axis] - center) <= size / 2 + margin, "Mesh bounds do not enclose the vertices within float32 rounding."); }
            }
            Require(spec.uvChannels != null && spec.uvChannels.Length <= 8 && spec.uvChannels.All(uv => uv != null && uv.channel >= 0 && uv.channel <= 7 && uv.dimension >= 2 && uv.dimension <= 4) &&
                    spec.uvChannels.Select(uv => uv.channel).Distinct().Count() == spec.uvChannels.Length, "Invalid or duplicate UV channel/dimension.");
            foreach (var uv in spec.uvChannels) { MeshNumbers(uv.values, count * uv.dimension, "UV"); }
            Require(spec.subMeshes != null && spec.subMeshes.Length > 0 && spec.subMeshes.Length <= 256, "Invalid submesh count.");
            foreach (var submesh in spec.subMeshes)
            {
                Require(submesh != null && submesh.topology == "Triangles" && submesh.indices != null && submesh.indices.Length >= 3 && submesh.indices.Length <= 6000000 && submesh.indices.Length % 3 == 0 &&
                        submesh.indices.All(index => index >= 0 && index < count), "Invalid mesh topology or index range.");
            }
        }
        private static Mesh CreateTemplateMesh(MeshSpec spec)
        {
            var mesh = new Mesh { name = spec.name, indexFormat = spec.indexFormat == "UInt16" ? UnityEngine.Rendering.IndexFormat.UInt16 : UnityEngine.Rendering.IndexFormat.UInt32 };
            try
            {
                mesh.vertices = MeshVectors3(spec.vertices);
                if (spec.normals.Length > 0) { mesh.normals = MeshVectors3(spec.normals); }
                if (spec.tangents.Length > 0) { mesh.tangents = MeshVectors4(spec.tangents); }
                if (spec.colors != null && spec.colors.Length > 0) { mesh.colors = Enumerable.Range(0, spec.colors.Length / 4).Select(i => new Color(spec.colors[i * 4], spec.colors[i * 4 + 1], spec.colors[i * 4 + 2], spec.colors[i * 4 + 3])).ToArray(); }
                foreach (var uv in spec.uvChannels)
                {
                    if (uv.dimension == 2) { mesh.SetUVs(uv.channel, Enumerable.Range(0, uv.values.Length / 2).Select(i => new Vector2(uv.values[i * 2], uv.values[i * 2 + 1])).ToList()); }
                    else if (uv.dimension == 3) { mesh.SetUVs(uv.channel, MeshVectors3(uv.values).ToList()); }
                    else { mesh.SetUVs(uv.channel, MeshVectors4(uv.values).ToList()); }
                }
                mesh.subMeshCount = spec.subMeshes.Length;
                for (var i = 0; i < spec.subMeshes.Length; i++) { mesh.SetIndices(spec.subMeshes[i].indices, MeshTopology.Triangles, i, false); }
                mesh.bounds = new Bounds(MeshVector3(spec.bounds.center), MeshVector3(spec.bounds.size));
                return mesh;
            }
            catch { Object.DestroyImmediate(mesh); throw; }
        }
        private static string HashBytes(byte[] bytes)
        {
            using (var sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        }
        private static string MeshFloatHash(float[] values)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream)) { foreach (var value in values) { writer.Write(value); } writer.Flush(); return HashBytes(stream.ToArray()); }
        }
        private static string MeshIndexHash(int[] values)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream)) { foreach (var value in values) { writer.Write(value); } writer.Flush(); return HashBytes(stream.ToArray()); }
        }
        private static MeshValidation ValidateTemplateMesh(Mesh mesh, MeshSpec spec)
        {
            Require(mesh != null && mesh.isReadable && mesh.name == spec.name && mesh.vertexCount == spec.vertices.Length / 3 && mesh.subMeshCount == spec.subMeshes.Length && mesh.indexFormat.ToString() == spec.indexFormat, "The mesh identity/format changed.");
            Require(mesh.blendShapeCount == 0 && mesh.bindposes.Length == 0 && mesh.boneWeights.Length == 0, "Unexpected skinning data in a static template.");
            var report = new MeshValidation { name = mesh.name, indexFormat = mesh.indexFormat.ToString(), vertexCount = mesh.vertexCount, subMeshCount = mesh.subMeshCount };
            var channels = new List<MeshChannelValidation>();
            Action<UnityEngine.Rendering.VertexAttribute, int, float[], float[]> channel = (attribute, dimension, expected, actual) =>
            {
                var exists = mesh.HasVertexAttribute(attribute);
                var actualDimension = exists ? mesh.GetVertexAttributeDimension(attribute) : 0;
                var expectedDimension = expected.Length == 0 ? 0 : dimension;
                var value = new MeshChannelValidation { attribute = attribute.ToString(), dimension = actualDimension, elements = actual.Length,
                    expectedSha256 = MeshFloatHash(expected), reloadedSha256 = MeshFloatHash(actual) };
                value.exact = actual.Length == expected.Length && actualDimension == expectedDimension && value.expectedSha256 == value.reloadedSha256 &&
                    (!exists || mesh.GetVertexAttributeFormat(attribute) == UnityEngine.Rendering.VertexAttributeFormat.Float32);
                channels.Add(value); Require(value.exact, "The mesh channel bits/dimension changed: " + attribute);
            };
            channel(UnityEngine.Rendering.VertexAttribute.Position, 3, spec.vertices, mesh.vertices.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray());
            channel(UnityEngine.Rendering.VertexAttribute.Normal, 3, spec.normals, mesh.normals.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray());
            channel(UnityEngine.Rendering.VertexAttribute.Tangent, 4, spec.tangents, mesh.tangents.SelectMany(v => new[] { v.x, v.y, v.z, v.w }).ToArray());
            channel(UnityEngine.Rendering.VertexAttribute.Color, 4, spec.colors ?? new float[0], mesh.colors.SelectMany(v => new[] { v.r, v.g, v.b, v.a }).ToArray());
            for (var index = 0; index < 8; index++)
            {
                var uv = spec.uvChannels.SingleOrDefault(value => value.channel == index); var values = new List<Vector4>(); mesh.GetUVs(index, values);
                var dimension = uv == null ? 0 : uv.dimension;
                var flat = values.SelectMany(v => new[] { v.x, v.y, v.z, v.w }.Take(dimension)).ToArray();
                channel((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + index), dimension, uv == null ? new float[0] : uv.values, flat);
            }
            report.channels = channels.ToArray(); report.indexSha256 = new string[spec.subMeshes.Length];
            for (var index = 0; index < spec.subMeshes.Length; index++)
            {
                var actual = mesh.GetIndices(index); report.indexSha256[index] = MeshIndexHash(actual);
                Require(mesh.GetTopology(index) == MeshTopology.Triangles && mesh.GetBaseVertex(index) == 0 && actual.SequenceEqual(spec.subMeshes[index].indices), "The mesh submesh indices/topology changed.");
            }
            var box = mesh.bounds;
            report.boundsExact = MeshFloatHash(new[] { box.center.x, box.center.y, box.center.z, box.size.x, box.size.y, box.size.z }) == MeshFloatHash(spec.bounds.center.Concat(spec.bounds.size).ToArray());
            Require(report.boundsExact, "The mesh bounds bits changed."); report.exact = true; return report;
        }

        private static bool BuildMaterials(string input, string destination, string jobId, string expectedHash,
            bool identityContracts = false)
        {
            var report = new Report
            {
                unityVersion = Application.unityVersion, startedUtc = DateTime.UtcNow.ToString("o"),
                transport = jobId == null ? "batch" : "gui_worker", jobId = jobId,
                validationScope = identityContracts ? "material_identity_contracts" : "materials",
                processId = System.Diagnostics.Process.GetCurrentProcess().Id, mainThread = Thread.CurrentThread.ManagedThreadId,
                activeScenePathBefore = SceneManager.GetActiveScene().path, activeSceneDirtyBefore = SceneManager.GetActiveScene().isDirty,
                sceneCountBefore = SceneManager.sceneCount
            };
            string reportPath = null;
            try
            {
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var marker = Path.Combine(root, ".solasta-asset-authoring-project");
                Require(File.Exists(marker) && File.ReadAllText(marker).Trim() == ProjectMarker, "The authoring project is unmarked.");
                if (jobId != null) { RequireJob(root, jobId); }
                var assetRoot = jobId == null ? "Assets/Input" : "Assets/AuthoringJobs/" + jobId + "/Input";
                var generatedFolder = jobId == null ? "Assets/Generated" : "Assets/AuthoringJobs/" + jobId + "/Generated";
                var recipePath = Path.GetFullPath(input);
                var output = Path.GetFullPath(destination);
                Require(SamePath(recipePath, Path.Combine(root, assetRoot, "recipe.json")), "The material recipe is outside its input directory.");
                Require(SamePath(output, jobId == null ? Path.Combine(root, "BuildOutput") : Path.Combine(root, JobFolder, jobId, "Output")),
                    "The material output is outside its isolated directory.");
                RejectReparsePoints(recipePath); RejectReparsePoints(output);
                Directory.CreateDirectory(output);
                reportPath = Path.Combine(output, "unity-report.json");
                Require(!File.Exists(reportPath), "This material job has already run.");
                Require(Application.unityVersion == EditorVersion, "Unity " + EditorVersion + " is required.");
                report.inputSha256 = Sha256(recipePath);
                Require(expectedHash == null || report.inputSha256 == expectedHash, "The material recipe changed after submission.");
                var recipe = JsonUtility.FromJson<MaterialRecipe>(File.ReadAllText(recipePath));
                Require(recipe != null && recipe.schemaVersion == 1 && recipe.bundleName != null &&
                        Regex.IsMatch(recipe.bundleName, "^[a-z0-9][a-z0-9_.-]{0,127}$"), "Invalid material recipe version or bundle name.");
                Require(recipe.assets != null && recipe.assets.Length > 0 && recipe.materials != null && recipe.materials.Length > 0,
                    "The material recipe must declare input assets and materials.");
                Require(recipe.assets.Select(asset => asset.path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == recipe.assets.Length,
                    "The recipe contains duplicate input paths.");
                foreach (var asset in recipe.assets)
                {
                    var path = MaterialInputPath(root, assetRoot, asset.path);
                    Require(asset.sha256 != null && Regex.IsMatch(asset.sha256, "^[a-f0-9]{64}$") && Sha256(path) == asset.sha256,
                        "The material input hash differs: " + asset.path);
                }
                report.inputAssets = recipe.assets;
                report.stage = "import";
                AssetDatabase.ImportAsset(assetRoot, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);
                Require(!AssetDatabase.IsValidFolder(generatedFolder), "Generated material assets already exist.");
                var parent = generatedFolder.Substring(0, generatedFolder.LastIndexOf('/'));
                Require(AssetDatabase.IsValidFolder(parent), "The material job parent was not imported.");
                AssetDatabase.CreateFolder(parent, "Generated");
                Require(recipe.materials.All(spec => spec.name != null && Regex.IsMatch(spec.name, "^[A-Za-z0-9_-]+$")) &&
                        recipe.materials.Select(spec => spec.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == recipe.materials.Length,
                    "Material names must be unique file-safe names.");
                var paths = recipe.materials.Select(spec => generatedFolder + "/" + spec.name + ".mat").ToArray();
                report.stage = "create_assets";
                for (var index = 0; index < recipe.materials.Length; index++)
                {
                    var spec = recipe.materials[index];
                    Require(recipe.assets.Any(asset => asset.path == spec.shader) && Path.GetExtension(spec.shader) == ".shader",
                        "The material shader must be a declared .shader input.");
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetRoot + "/" + spec.shader);
                    Require(shader != null && shader.isSupported && !ShaderUtil.ShaderHasError(shader), "The imported shader failed compilation: " + spec.shader);
                    var material = new Material(shader) { name = spec.name };
                    try
                    {
                        foreach (var property in spec.properties ?? new MaterialProperty[0])
                        {
                            Require(!string.IsNullOrEmpty(property.name) && material.HasProperty(property.name), "Missing material property: " + property.name);
                            var propertyIndex = Enumerable.Range(0, ShaderUtil.GetPropertyCount(shader))
                                .Single(indexInShader => ShaderUtil.GetPropertyName(shader, indexInShader) == property.name);
                            var propertyType = ShaderUtil.GetPropertyType(shader, propertyIndex);
                            Require((property.kind == "float" && (propertyType == ShaderUtil.ShaderPropertyType.Float || propertyType == ShaderUtil.ShaderPropertyType.Range)) ||
                                    (property.kind == "color" && propertyType == ShaderUtil.ShaderPropertyType.Color) ||
                                    (property.kind == "texture" && propertyType == ShaderUtil.ShaderPropertyType.TexEnv), "The material property type differs from its recipe: " + property.name);
                            if (property.kind == "float")
                            {
                                Require(!float.IsNaN(property.value) && !float.IsInfinity(property.value), "Non-finite material value.");
                                material.SetFloat(property.name, property.value);
                            }
                            else if (property.kind == "color")
                            {
                                Require(property.rgba != null && property.rgba.Length == 4 && property.rgba.All(value => !float.IsNaN(value) && !float.IsInfinity(value)),
                                    "A material color must have four finite components.");
                                material.SetColor(property.name, new Color(property.rgba[0], property.rgba[1], property.rgba[2], property.rgba[3]));
                            }
                            else if (property.kind == "texture")
                            {
                                Require(recipe.assets.Any(asset => asset.path == property.texture), "The texture must be a declared input.");
                                var texture = AssetDatabase.LoadAssetAtPath<Texture>(assetRoot + "/" + property.texture);
                                Require(texture != null, "The texture did not import: " + property.texture);
                                material.SetTexture(property.name, texture);
                            }
                            else { throw new InvalidOperationException("Unknown material property kind: " + property.kind); }
                        }
                        Require(spec.renderQueue >= -1 && spec.renderQueue <= 5000, "Invalid material render queue.");
                        material.renderQueue = spec.renderQueue;
                        ValidateMaterial(material, spec, assetRoot);
                        AssetDatabase.CreateAsset(material, paths[index]);
                    }
                    finally
                    {
                        if (material != null && !EditorUtility.IsPersistent(material)) { Object.DestroyImmediate(material); }
                    }
                }
                SavingFolder = generatedFolder + "/";
                try { AssetDatabase.SaveAssets(); }
                finally { SavingFolder = null; }
                var referencedAssets = recipe.materials.Select(spec => assetRoot + "/" + spec.shader)
                    .Concat(recipe.materials.SelectMany(spec => spec.properties ?? new MaterialProperty[0])
                        .Where(property => property.kind == "texture").Select(property => assetRoot + "/" + property.texture));
                var bundleAssets = paths.Concat(referencedAssets).Distinct(StringComparer.Ordinal).ToArray();
                var bundle = BuildAndReload(output, recipe.bundleName, bundleAssets, generatedFolder, report);
                try
                {
                    report.materials = recipe.materials.Select((spec, index) =>
                    {
                        var material = bundle.LoadAsset<Material>(paths[index]);
                        Require(material != null && material.name == spec.name, "The bundled material name changed.");
                        return ValidateMaterial(material, spec, assetRoot, bundle);
                    }).ToArray();
                    if (identityContracts)
                    {
                        report.identityContracts = ValidateMaterialIdentityContracts(bundle, recipe, paths, assetRoot);
                    }
                }
                finally { bundle.Unload(true); }
                Require(Sha256(recipePath) == report.inputSha256 && recipe.assets.All(asset =>
                    Sha256(MaterialInputPath(root, assetRoot, asset.path)) == asset.sha256), "Material inputs changed during the build.");
                report.activeScenePathAfter = SceneManager.GetActiveScene().path;
                report.activeSceneDirtyAfter = SceneManager.GetActiveScene().isDirty;
                report.sceneCountAfter = SceneManager.sceneCount;
                Require(report.activeScenePathBefore == report.activeScenePathAfter && report.activeSceneDirtyBefore == report.activeSceneDirtyAfter &&
                        report.sceneCountBefore == report.sceneCountAfter, "The material build changed the open scene state.");
                report.stage = "complete"; report.success = true; report.completedUtc = DateTime.UtcNow.ToString("o");
                WriteJson(reportPath, report);
                return true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString(); report.completedUtc = DateTime.UtcNow.ToString("o");
                if (reportPath != null && !File.Exists(reportPath)) { WriteJson(reportPath, report); }
                Debug.LogException(exception); return false;
            }
        }

        private static string MaterialInputPath(string root, string assetRoot, string relative)
        {
            Require(!string.IsNullOrEmpty(relative) && Regex.IsMatch(relative, "^[A-Za-z0-9_./-]+$") && !Path.IsPathRooted(relative) &&
                    relative.Split('/').All(segment => segment != "" && segment != "." && segment != ".."), "Invalid material input path.");
            Require(new[] { ".shader", ".cginc", ".hlsl", ".png", ".jpg", ".jpeg", ".tga" }.Contains(Path.GetExtension(relative)), "Unsupported material input extension.");
            var path = Path.Combine(root, assetRoot, relative);
            RejectReparsePoints(path);
            Require(File.Exists(path), "The material input is missing: " + relative);
            return path;
        }

        private static MaterialValidation ValidateMaterial(Material material, MaterialSpec spec, string assetRoot,
            AssetBundle bundle = null)
        {
            Require(material.shader != null && material.shader.isSupported && spec.requiredPasses != null && spec.requiredPasses.Length > 0 &&
                    spec.requiredPasses.All(pass => !string.IsNullOrEmpty(pass) && material.FindPass(pass) >= 0), "A required material shader pass is missing: " + spec.name);
            var shaderPath = assetRoot + "/" + spec.shader;
            var expectedShader = bundle != null ? bundle.LoadAsset<Shader>(shaderPath) : AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            Require(expectedShader != null && material.shader == expectedShader, "The material's shader reference changed: " + spec.shader);
            foreach (var property in spec.properties ?? new MaterialProperty[0])
            {
                Require(material.HasProperty(property.name), "A bundled material property is missing: " + property.name);
                if (property.kind == "float") { Require(Mathf.Abs(material.GetFloat(property.name) - property.value) < 0.00001f, "A float material value changed."); }
                else if (property.kind == "color")
                {
                    var actual = material.GetColor(property.name);
                    Require(Mathf.Abs(actual.r - property.rgba[0]) < 0.00001f && Mathf.Abs(actual.g - property.rgba[1]) < 0.00001f &&
                            Mathf.Abs(actual.b - property.rgba[2]) < 0.00001f && Mathf.Abs(actual.a - property.rgba[3]) < 0.00001f, "A material color changed.");
                }
                else if (property.kind == "texture")
                {
                    var texturePath = assetRoot + "/" + property.texture;
                    var expected = bundle != null ? bundle.LoadAsset<Texture>(texturePath) : AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                    var actual = material.GetTexture(property.name);
                    Require(expected != null && actual == expected, "A material texture reference changed: " + property.texture);
                }
            }
            Require(spec.renderQueue == -1 || material.renderQueue == spec.renderQueue, "The material render queue changed.");
            return new MaterialValidation { name = material.name, shader = material.shader.name, shaderSupported = material.shader.isSupported,
                renderQueue = material.renderQueue, passes = Enumerable.Range(0, material.passCount).Select(material.GetPassName).ToArray() };
        }

        private static MaterialIdentityContract[] ValidateMaterialIdentityContracts(AssetBundle bundle,
            MaterialRecipe recipe, string[] materialPaths, string assetRoot)
        {
            Require(recipe.materials.Length == 2, "The identity contract requires two owned materials.");
            var expected = recipe.materials[0];
            var alternative = recipe.materials[1];
            var material = bundle.LoadAsset<Material>(materialPaths[0]);
            var expectedShader = bundle.LoadAsset<Shader>(assetRoot + "/" + expected.shader);
            var otherShader = bundle.LoadAsset<Shader>(assetRoot + "/" + alternative.shader);
            var expectedProperty = (expected.properties ?? new MaterialProperty[0]).Single(property => property.kind == "texture");
            var otherProperty = (alternative.properties ?? new MaterialProperty[0]).Single(property => property.kind == "texture");
            var expectedTexture = bundle.LoadAsset<Texture>(assetRoot + "/" + expectedProperty.texture);
            var otherTexture = bundle.LoadAsset<Texture>(assetRoot + "/" + otherProperty.texture);
            Require(expectedShader && otherShader && expectedShader != otherShader && expectedShader.name == otherShader.name,
                "The native bundle must retain distinct shaders with the same internal name.");
            Require(expectedTexture && otherTexture && expectedTexture != otherTexture && expectedTexture.name == otherTexture.name &&
                    expectedTexture.width == otherTexture.width && expectedTexture.height == otherTexture.height,
                "The native bundle must retain distinct, equally sized textures with the same name.");
            var contracts = new[] {
                new MaterialIdentityContract { kind = "shader", expectedAsset = expected.shader, alternativeAsset = alternative.shader,
                    expectedName = expectedShader.name, alternativeName = otherShader.name,
                    expectedInstanceId = expectedShader.GetInstanceID(), alternativeInstanceId = otherShader.GetInstanceID() },
                new MaterialIdentityContract { kind = "texture", expectedAsset = expectedProperty.texture, alternativeAsset = otherProperty.texture,
                    expectedName = expectedTexture.name, alternativeName = otherTexture.name,
                    expectedInstanceId = expectedTexture.GetInstanceID(), alternativeInstanceId = otherTexture.GetInstanceID() }
            };
            foreach (var contract in contracts)
            {
                var clone = new Material(material);
                try
                {
                    if (contract.kind == "shader") { clone.shader = otherShader; }
                    else { clone.SetTexture(expectedProperty.name, otherTexture); }
                    try { ValidateMaterial(clone, expected, assetRoot, bundle); }
                    catch (InvalidOperationException error)
                    {
                        contract.rejection = error.Message;
                        contract.rejected = error.Message.Contains(contract.kind == "shader"
                            ? "shader reference changed" : "texture reference changed");
                    }
                    Require(contract.rejected, "A same-name " + contract.kind + " reference substitution was accepted.");
                }
                finally { Object.DestroyImmediate(clone); }
            }
            return contracts;
        }

        [InitializeOnLoadMethod]
        private static void InitializeWorker()
        {
            if (Application.isBatchMode) { return; }
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var marker = Path.Combine(root, ".solasta-asset-authoring-project");
            if (Application.unityVersion != EditorVersion || !File.Exists(marker) || File.ReadAllText(marker).Trim() != ProjectMarker) { return; }
            loadedSourceHash = Sha256(Path.Combine(root, "Assets/Editor/AssetBundleBuilder.cs"));
            EditorApplication.update -= PollJobs;
            EditorApplication.update += PollJobs;
        }

        private static void PollJobs()
        {
            if (workerBusy || EditorApplication.timeSinceStartup < nextPoll || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) { return; }
            nextPoll = EditorApplication.timeSinceStartup + 1.0;
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            try
            {
                RejectReparsePoints(root);
                var source = Path.Combine(root, "Assets/Editor/AssetBundleBuilder.cs");
                RejectReparsePoints(source);
                if (Sha256(source) != loadedSourceHash)
                {
                    EditorApplication.update -= PollJobs;
                    AssetDatabase.ImportAsset("Assets/Editor/AssetBundleBuilder.cs", ImportAssetOptions.ForceSynchronousImport);
                    return;
                }
                var directory = Path.Combine(root, JobFolder);
                RejectReparsePoints(directory);
                Directory.CreateDirectory(directory);
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    var receipt = new WorkerReceipt
                    {
                        processId = process.Id, processStartedUtc = process.StartTime.ToUniversalTime().ToString("o"),
                        mainThread = Thread.CurrentThread.ManagedThreadId, projectPath = root,
                        unityVersion = Application.unityVersion, updatedUtc = DateTime.UtcNow.ToString("o"),
                        sourceSha256 = loadedSourceHash
                    };
                    RecordWorkerDesktop(receipt);
                    WriteJson(Path.Combine(directory, "worker.json"), receipt);
                    if (ShutdownOwnedWorker(directory, receipt)) { return; }
                }
                foreach (var jobDirectory in Directory.GetDirectories(directory).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var jobId = Path.GetFileName(jobDirectory);
                    if (!Regex.IsMatch(jobId, "^[a-f0-9]{32}$")) { continue; }
                    var requestPath = Path.Combine(jobDirectory, "request.json");
                    var claimedPath = Path.Combine(jobDirectory, "started.json");
                    if (!File.Exists(requestPath)) { continue; }
                    RequireJob(root, jobId);
                    var reportPath = Path.Combine(jobDirectory, "Output/unity-report.json");
                    if (File.Exists(claimedPath))
                    {
                        if (!File.Exists(reportPath)) { WriteJobFailure(reportPath, jobId, "The previous job was interrupted; submit a new job."); }
                        continue;
                    }
                    using (var claim = new FileStream(claimedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(claim)) { writer.Write(DateTime.UtcNow.ToString("o")); }
                    workerBusy = true;
                    try
                    {
                        RejectReparsePoints(requestPath);
                        Require(new FileInfo(requestPath).Length <= 4096, "The job request is too large.");
                        var job = JsonUtility.FromJson<Job>(File.ReadAllText(requestPath));
                        Require(job != null && job.schemaVersion == 1 && job.jobId == jobId &&
                                job.inputSha256 != null && Regex.IsMatch(job.inputSha256, "^[a-f0-9]{64}$"), "The job request is invalid.");
                        Require(job.kind == null || job.kind == "rig" || job.kind == "materials" ||
                                job.kind == "material-identity-contracts" || job.kind == "mesh-templates", "Unknown authoring job kind.");
                        if (job.kind == "mesh-templates")
                        {
                            BuildMeshTemplates(Path.Combine(root, "Assets/AuthoringJobs", jobId, "Input/mesh-recipe.json"),
                                Path.Combine(jobDirectory, "Output"), jobId, job.inputSha256);
                        }
                        else if (job.kind == "materials" || job.kind == "material-identity-contracts")
                        {
                            BuildMaterials(Path.Combine(root, "Assets/AuthoringJobs", jobId, "Input/recipe.json"),
                                Path.Combine(jobDirectory, "Output"), jobId, job.inputSha256,
                                job.kind == "material-identity-contracts");
                        }
                        else
                        {
                            BuildValidated(Path.Combine(root, "Assets/AuthoringJobs", jobId, "Input/SampleRig.fbx"),
                                Path.Combine(jobDirectory, "Output"), jobId, job.inputSha256);
                        }
                    }
                    catch (Exception exception)
                    {
                        WriteJobFailure(reportPath, jobId, exception.ToString());
                        Debug.LogException(exception);
                    }
                    finally { workerBusy = false; }
                    break;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private static void RequireJob(string root, string jobId)
        {
            Require(Regex.IsMatch(jobId, "^[a-f0-9]{32}$"), "Invalid job identifier.");
            var directory = Path.Combine(root, JobFolder, jobId);
            RejectReparsePoints(directory);
            var marker = Path.Combine(directory, ".solasta-asset-authoring-job");
            RejectReparsePoints(marker);
            Require(File.Exists(marker) && File.ReadAllText(marker).Trim() == JobMarker, "The job directory is not owned by the launcher.");
        }

        private static void WriteJobFailure(string path, string jobId, string error)
        {
            RejectReparsePoints(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteJson(path, new Report { unityVersion = Application.unityVersion, transport = "gui_worker", jobId = jobId,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id, mainThread = Thread.CurrentThread.ManagedThreadId,
                error = error, completedUtc = DateTime.UtcNow.ToString("o") });
        }

        private static void WriteJson(string path, object value)
        {
            var temporary = path + ".writing";
            File.WriteAllText(temporary, JsonUtility.ToJson(value, true));
            if (File.Exists(path)) { File.Replace(temporary, path, null); }
            else { File.Move(temporary, path); }
        }

        private static Validation Validate(GameObject prefab, AnimationClip clip)
        {
            Require(clip != null && clip.length >= 0.1f && clip.length <= 10f,
                "A short animation clip between 0.1 and 10 seconds is required.");
            var preview = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            try
            {
                instance = Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(instance, preview);
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
                if (instance != null) { Object.DestroyImmediate(instance); }
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private static Vector3[] Snapshot(SkinnedMeshRenderer renderer)
        {
            var mesh = new Mesh();
            try { renderer.BakeMesh(mesh); return mesh.vertices; }
            finally { Object.DestroyImmediate(mesh); }
        }

        private static string GetArgument(string name, bool required = true)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index < arguments.Length - 1; index++)
            {
                if (arguments[index] == name) { return arguments[index + 1]; }
            }
            if (required) { throw new ArgumentException("Missing command-line argument " + name); }
            return string.Empty;
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

    internal sealed class AuthoringSaveFilter : UnityEditor.AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            return AssetBundleBuilder.SavingFolder == null ? paths : paths.Where(path =>
                path.StartsWith(AssetBundleBuilder.SavingFolder, StringComparison.Ordinal)).ToArray();
        }
    }
}
