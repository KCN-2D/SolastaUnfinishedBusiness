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
#if !MESH_INPUT_ONLY
using static SolastaAssetAuthoring.MeshRecipeInput;
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
            public string colorSpace = "Linear";
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
            public MeshValidation[] createdMeshes, savedMeshes;
            public MeshValidationContract[] meshValidationContracts;
            public TextureValidation[] createdTextures, savedTextures, textures;
            public SourceAssetValidation[] createdSourceAssets, savedSourceAssets, sourceAssets;
            public int sourceBundlesLoaded, sourceBundlesReleased;
            public MeshValidationContract[] textureValidationContracts;
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
            public string requestedColorSpace, previousColorSpace, actualColorSpace;
        }

        private static void ApplyColorSpace(Report report, string requested)
        {
            requested = string.IsNullOrEmpty(requested) ? "Linear" : requested;
            Require(requested == "Linear" || requested == "Gamma", "Unknown authoring color space.");
            report.requestedColorSpace = requested;
            report.previousColorSpace = PlayerSettings.colorSpace.ToString();
            var target = requested == "Linear" ? ColorSpace.Linear : ColorSpace.Gamma;
            // This marked project owns its authoring target. Keep it between
            // jobs so a multi-pack session does not repeatedly reimport shaders.
            if (PlayerSettings.colorSpace != target) { PlayerSettings.colorSpace = target; }
            report.actualColorSpace = QualitySettings.activeColorSpace.ToString();
            Require(report.actualColorSpace == requested, "The Editor did not apply the authoring color space.");
        }

        public static void Build()
        {
            var materialJob = Environment.GetCommandLineArgs().Contains("-materialRecipe");
            var meshJob = Environment.GetCommandLineArgs().Contains("-meshRecipe");
            var colorSpace = GetArgument("-colorSpace", false);
            var success = meshJob ? BuildMeshTemplates(GetArgument("-meshRecipe"), GetArgument("-bundleOutput"), null, null, false, colorSpace)
                : materialJob ? BuildMaterials(GetArgument("-materialRecipe"), GetArgument("-bundleOutput"), null, null, false, colorSpace)
                : BuildValidated(GetArgument("-sampleFbx"), GetArgument("-bundleOutput"), null, null, colorSpace);
            if (!success)
            {
                EditorApplication.Exit(1);
            }
        }

        private static bool BuildValidated(string input, string destination, string jobId, string expectedHash, string colorSpace = "Linear")
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
                ApplyColorSpace(report, colorSpace);
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
            Require(PlayerSettings.colorSpace.ToString() == report.requestedColorSpace &&
                    QualitySettings.activeColorSpace.ToString() == report.requestedColorSpace,
                    "The authoring color space changed before bundle compilation.");
            report.actualColorSpace = QualitySettings.activeColorSpace.ToString();
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
            Require(manifest.GetAllAssetBundles().Length == 1 && manifest.GetAllDependencies(bundleName).Length == 0,
                "The bundle has an undeclared output or external bundle dependency.");
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

        [Serializable] private sealed class TextureValidation
        {
            public string name, path, inputSha256, graphicsFormat, textureFormat, rawSha256, compression;
            public int width, height, mipCount, rawBytes, compressionQuality;
            public bool normalMap, sRGB, mipmaps, heightConversion, readable, crunched, exact, importSettingsExact;
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
            public int vertexCount, subMeshCount, boneCount, blendShapeCount;
            public bool exact, boundsExact, skinExact, blendShapesExact;
            public MeshChannelValidation[] channels;
            public string[] indexSha256;
            public MeshBlendShapeValidation[] blendShapes;
        }
        [Serializable] private sealed class MeshBlendShapeValidation
        {
            public string name;
            public bool exact, channelIdentityExact, firstLookupExact;
            public int index, frameIndex, frameCount, expectedFirstIndex, actualFirstIndex;
            public long expectedNameHash, nameHash;
            public MeshBlendFrameValidation[] frames;
        }
        [Serializable] private sealed class MeshBlendFrameValidation
        {
            public float weight;
            public bool weightExact, exact;
            public MeshChannelValidation[] channels;
        }
        [Serializable] private sealed class MeshValidationContract
        {
            public string kind, rejection, notApplicableReason;
            public bool rejected, applicable = true;
        }

        [Serializable] private sealed class SourceAssetValidation
        {
            public string sourcePath, sourceSha256, asset, sourceName, name, type, dataSha256, physicalSha256;
            public bool exact, physicalLayoutExact;
            public string[] physicalFields;
        }
        private sealed class SourceAssetState
        {
            public Type assetType;
            public SourceBundleSpec bundle;
            public SourceAssetSpec spec;
            public string sourceName, outputPath, dataSha256, physicalSha256;
            public string[] physicalFields;
        }

        private static string SourceAssetDataHash(Object asset)
        {
            using (var hash = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(stream))
            {
                var mesh = asset as Mesh;
                if (mesh != null)
                {
                    Require(mesh.isReadable, "The source mesh has no readable channel data.");
                    writer.Write("Mesh"); writer.Write(mesh.vertexCount); writer.Write((int)mesh.indexFormat);
                    var descriptors = mesh.GetVertexAttributes(); writer.Write(descriptors.Length);
                    foreach (var descriptor in descriptors)
                    { writer.Write((int)descriptor.attribute); writer.Write((int)descriptor.format); writer.Write(descriptor.dimension); writer.Write(descriptor.stream); }
                    Action<Vector3[]> vector3 = values => { writer.Write(values.Length); foreach (var value in values) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); } };
                    vector3(mesh.vertices); vector3(mesh.normals);
                    var tangents = mesh.tangents; writer.Write(tangents.Length);
                    foreach (var value in tangents) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
                    var colors = mesh.colors; writer.Write(colors.Length);
                    foreach (var value in colors) { writer.Write(value.r); writer.Write(value.g); writer.Write(value.b); writer.Write(value.a); }
                    for (var channel = 0; channel < 8; channel++)
                    {
                        var values = new List<Vector4>(); mesh.GetUVs(channel, values); writer.Write(values.Count);
                        foreach (var value in values) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
                    }
                    var poses = mesh.bindposes; writer.Write(poses.Length);
                    foreach (var pose in poses) for (var i = 0; i < 16; i++) writer.Write(pose[i]);
                    var weights = mesh.boneWeights; writer.Write(weights.Length);
                    foreach (var value in weights)
                    { writer.Write(value.weight0); writer.Write(value.weight1); writer.Write(value.weight2); writer.Write(value.weight3);
                        writer.Write(value.boneIndex0); writer.Write(value.boneIndex1); writer.Write(value.boneIndex2); writer.Write(value.boneIndex3); }
                    writer.Write(mesh.subMeshCount);
                    for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    { writer.Write((int)mesh.GetTopology(submesh)); writer.Write(mesh.GetBaseVertex(submesh));
                        var indices = mesh.GetIndices(submesh); writer.Write(indices.Length); foreach (var index in indices) writer.Write(index); }
                    var bounds = mesh.bounds;
                    vector3(new[] { bounds.center, bounds.size }); writer.Write(mesh.blendShapeCount);
                    var vertices = new Vector3[mesh.vertexCount]; var normals = new Vector3[mesh.vertexCount]; var deltas = new Vector3[mesh.vertexCount];
                    for (var shape = 0; shape < mesh.blendShapeCount; shape++)
                    {
                        var name = mesh.GetBlendShapeName(shape); writer.Write(name); writer.Write(mesh.GetBlendShapeIndex(name));
                        var frames = mesh.GetBlendShapeFrameCount(shape); writer.Write(frames);
                        for (var frame = 0; frame < frames; frame++)
                        {
                            writer.Write(mesh.GetBlendShapeFrameWeight(shape, frame));
                            Array.Clear(vertices, 0, vertices.Length); Array.Clear(normals, 0, normals.Length); Array.Clear(deltas, 0, deltas.Length);
                            mesh.GetBlendShapeFrameVertices(shape, frame, vertices, normals, deltas);
                            vector3(vertices); vector3(normals); vector3(deltas);
                        }
                    }
                }
                else
                {
                    var texture = asset as Texture2D;
                    Require(texture != null && texture.isReadable, "The source texture has no readable mip data.");
                    writer.Write("Texture2D"); writer.Write(texture.width); writer.Write(texture.height); writer.Write(texture.mipmapCount);
                    writer.Write((int)texture.format); writer.Write(texture.graphicsFormat.ToString());
                    writer.Write((int)texture.filterMode); writer.Write(texture.anisoLevel); writer.Write(texture.mipMapBias);
                    writer.Write((int)texture.wrapModeU); writer.Write((int)texture.wrapModeV); writer.Write((int)texture.wrapModeW);
                    var bytes = texture.GetRawTextureData(); writer.Write(bytes.Length); writer.Write(bytes);
                }
                writer.Flush(); stream.FlushFinalBlock(); return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string SourcePhysicalLayoutHash(Object asset, out string[] fields)
        {
            var recorded = new List<string>();
            using (var hash = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(stream))
            using (var serialized = new SerializedObject(asset))
            {
                if (asset is Mesh)
                {
                    var channels = MeshShapeChannels(serialized); recorded.Add(channels.propertyPath); writer.Write(channels.arraySize);
                    for (var index = 0; index < channels.arraySize; index++)
                    {
                        var channel = channels.GetArrayElementAtIndex(index);
                        writer.Write(channel.FindPropertyRelative("name").stringValue);
                        foreach (var field in new[] { "nameHash", "frameIndex", "frameCount" })
                            writer.Write(channel.FindPropertyRelative(field).longValue);
                    }
                    var shapes = serialized.FindProperty("m_Shapes.shapes");
                    Require(shapes != null && shapes.isArray, "The source mesh physical frame layout is unavailable.");
                    recorded.Add(shapes.propertyPath); writer.Write(shapes.arraySize);
                    for (var index = 0; index < shapes.arraySize; index++)
                    {
                        var shape = shapes.GetArrayElementAtIndex(index);
                        foreach (var field in new[] { "firstVertex", "vertexCount" }) writer.Write(shape.FindPropertyRelative(field).longValue);
                        foreach (var field in new[] { "hasNormals", "hasTangents" }) writer.Write(shape.FindPropertyRelative(field).boolValue);
                    }
                }
                else foreach (var name in new[] { "m_ColorSpace", "m_LightmapFormat", "m_StreamingMipmaps", "m_StreamingMipmapsPriority", "m_IgnoreMasterTextureLimit", "m_ForcedFallbackFormat", "m_DownscaleFallback" })
                {
                    var property = serialized.FindProperty(name); writer.Write(name); writer.Write(property != null);
                    if (property == null) continue;
                    recorded.Add(name);
                    if (property.propertyType == SerializedPropertyType.Boolean) writer.Write(property.boolValue);
                    else { Require(property.propertyType == SerializedPropertyType.Integer, "Unsupported source texture layout field: " + name); writer.Write(property.longValue); }
                }
                fields = recorded.ToArray(); writer.Flush(); stream.FlushFinalBlock();
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static SourceAssetValidation ValidateSourceAsset(SourceAssetState state, Object actual)
        {
            Require(actual != null && actual.name == state.spec.name && actual.GetType() == state.assetType,
                "The cloned source asset identity/type changed.");
            var fingerprint = SourceAssetDataHash(actual);
            Require(fingerprint == state.dataSha256, "The cloned source numeric bits or texture mip payload changed: " + state.spec.name);
            string[] fields;
            var physical = SourcePhysicalLayoutHash(actual, out fields);
            Require(physical == state.physicalSha256 && fields.SequenceEqual(state.physicalFields), "The cloned source physical frame layout or texture metadata changed: " + state.spec.name);
            return new SourceAssetValidation { sourcePath = state.bundle.path, sourceSha256 = state.bundle.sha256,
                asset = state.spec.asset, sourceName = state.sourceName, name = state.spec.name, type = state.spec.type,
                dataSha256 = fingerprint, physicalSha256 = physical, physicalFields = fields, physicalLayoutExact = true, exact = true };
        }

        private static bool BuildMeshTemplates(string input, string destination, string jobId, string expectedHash, bool validationContracts = false, string colorSpace = "Linear")
        {
            var report = new Report
            {
                unityVersion = Application.unityVersion, startedUtc = DateTime.UtcNow.ToString("o"),
                transport = jobId == null ? "batch" : "gui_worker", jobId = jobId,
                validationScope = validationContracts ? "mesh_template_contracts" : "mesh_templates",
                processId = System.Diagnostics.Process.GetCurrentProcess().Id, mainThread = Thread.CurrentThread.ManagedThreadId,
                activeScenePathBefore = SceneManager.GetActiveScene().path, activeSceneDirtyBefore = SceneManager.GetActiveScene().isDirty,
                sceneCountBefore = SceneManager.sceneCount
            };
            string reportPath = null;
            var sourceBundles = new List<AssetBundle>();
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
                ApplyColorSpace(report, colorSpace);
                var bytes = File.ReadAllBytes(recipePath);
                report.inputSha256 = HashBytes(bytes);
                Require(expectedHash == null || expectedHash == report.inputSha256, "The mesh recipe changed after submission.");
                var recipe = MeshRecipeInput.Read(bytes, recipePath);
                var textures = recipe.textures ?? new TextureSpec[0];
                var sources = recipe.sourceBundles ?? new SourceBundleSpec[0];
                ValidateTextureSpecs(textures, root, assetRoot);
                report.inputAssets = textures.Select(spec => new InputAsset { path = spec.path, sha256 = spec.sha256 })
                    .Concat(recipe.numericPayload == null ? new InputAsset[0] : new[] { new InputAsset { path = recipe.numericPayload.path, sha256 = recipe.numericPayload.sha256 } })
                    .Concat(sources.Select(spec => new InputAsset { path = spec.path, sha256 = spec.sha256 })).ToArray();
                Require(Sha256(recipePath) == report.inputSha256, "The mesh input changed during validation.");
                report.stage = "create_assets";
                AssetDatabase.ImportAsset(assetRoot, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive);
                var texturePaths = textures.Select(spec => assetRoot + "/" + spec.path).ToArray();
                report.createdTextures = textures.Select((spec, index) => {
                    ConfigureTexture(texturePaths[index], spec);
                    return ValidateTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(texturePaths[index]), spec, texturePaths[index], null);
                }).ToArray();
                Require(!AssetDatabase.IsValidFolder(generated), "Generated mesh assets already exist.");
                var parent = generated.Substring(0, generated.LastIndexOf('/'));
                Require(AssetDatabase.IsValidFolder(parent), "The mesh job parent was not imported.");
                AssetDatabase.CreateFolder(parent, "Generated");
                var paths = recipe.meshes.Select(mesh => generated + "/" + mesh.name + ".asset").ToArray();
                report.createdMeshes = new MeshValidation[recipe.meshes.Length];
                for (var index = 0; index < recipe.meshes.Length; index++)
                {
                    var mesh = CreateTemplateMesh(recipe.meshes[index]);
                    try { report.createdMeshes[index] = ValidateTemplateMesh(mesh, recipe.meshes[index]); AssetDatabase.CreateAsset(mesh, paths[index]); }
                    finally { if (mesh != null && !EditorUtility.IsPersistent(mesh)) { Object.DestroyImmediate(mesh); } }
                }
                var clones = new List<SourceAssetState>(); var created = new List<SourceAssetValidation>();
                foreach (var spec in sources)
                {
                    var sourcePath = InputPath(root, assetRoot, spec.path, ".bundle");
                    Require(Sha256(sourcePath) == spec.sha256, "The source bundle input hash differs: " + spec.path);
                    var sourceBundle = AssetBundle.LoadFromFile(sourcePath);
                    Require(sourceBundle != null, "The declared source bundle did not load: " + spec.path); sourceBundles.Add(sourceBundle); report.sourceBundlesLoaded++;
                    var available = sourceBundle.GetAllAssetNames();
                    foreach (var selected in spec.assets)
                    {
                        Require(available.Count(name => string.Equals(name, selected.asset, StringComparison.Ordinal)) == 1,
                            "The source asset selector is missing or ambiguous: " + selected.asset);
                        var type = selected.type == "Mesh" ? typeof(Mesh) : typeof(Texture2D);
                        var original = sourceBundle.LoadAsset(selected.asset, type);
                        Require(original != null && original.GetType() == type, "The source asset type differs: " + selected.asset);
                        var state = new SourceAssetState { assetType = type, sourceName = original.name, bundle = spec, spec = selected,
                            outputPath = generated + "/" + selected.name + ".asset", dataSha256 = SourceAssetDataHash(original) };
                        state.physicalSha256 = SourcePhysicalLayoutHash(original, out state.physicalFields);
                        Object clone = null;
                        try
                        {
                            clone = Object.Instantiate(original); clone.name = selected.name;
                            created.Add(ValidateSourceAsset(state, clone)); AssetDatabase.CreateAsset(clone, state.outputPath); clones.Add(state);
                        }
                        finally { if (clone != null && !EditorUtility.IsPersistent(clone)) Object.DestroyImmediate(clone); }
                    }
                }
                report.createdSourceAssets = created.ToArray();
                SavingFolder = generated + "/";
                try { AssetDatabase.SaveAssets(); }
                finally { SavingFolder = null; }
                report.stage = "reload_saved_meshes";
                report.savedMeshes = new MeshValidation[recipe.meshes.Length];
                for (var index = 0; index < recipe.meshes.Length; index++)
                {
                    Resources.UnloadAsset(AssetDatabase.LoadAssetAtPath<Mesh>(paths[index]));
                    AssetDatabase.ImportAsset(paths[index], ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    report.savedMeshes[index] = ValidateTemplateMesh(AssetDatabase.LoadAssetAtPath<Mesh>(paths[index]), recipe.meshes[index]);
                }
                report.savedTextures = textures.Select((spec, index) => {
                    Resources.UnloadAsset(AssetDatabase.LoadAssetAtPath<Texture2D>(texturePaths[index]));
                    return ValidateTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(texturePaths[index]), spec, texturePaths[index], report.createdTextures[index]);
                }).ToArray();
                report.savedSourceAssets = clones.Select(state => {
                    Resources.UnloadAsset(AssetDatabase.LoadAssetAtPath(state.outputPath, state.assetType));
                    AssetDatabase.ImportAsset(state.outputPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    return ValidateSourceAsset(state, AssetDatabase.LoadAssetAtPath(state.outputPath, state.assetType));
                }).ToArray();
                var bundle = BuildAndReload(output, recipe.bundleName, paths.Concat(texturePaths).Concat(clones.Select(state => state.outputPath)).ToArray(), generated, report);
                try
                {
                    var all = bundle.LoadAllAssets();
                    Require(all.Length == recipe.meshes.Length + textures.Length + clones.Count &&
                        all.Count(asset => asset is Mesh) == recipe.meshes.Length + clones.Count(state => state.spec.type == "Mesh") &&
                        all.Count(asset => asset is Texture2D) == textures.Length + clones.Count(state => state.spec.type == "Texture2D"), "The mesh bundle contains unexpected assets.");
                    report.meshes = recipe.meshes.Select((spec, index) => ValidateTemplateMesh(bundle.LoadAsset<Mesh>(paths[index]), spec)).ToArray();
                    report.textures = textures.Select((spec, index) => ValidateTexture(bundle.LoadAsset<Texture2D>(texturePaths[index]), spec, texturePaths[index], report.createdTextures[index])).ToArray();
                    report.sourceAssets = clones.Select(state => ValidateSourceAsset(state, bundle.LoadAsset(state.outputPath, state.assetType))).ToArray();
                    if (validationContracts)
                    {
                        var index = Array.FindIndex(recipe.meshes, spec => spec.skin != null && spec.skin.bindPoses.Length >= 32 &&
                            spec.blendShapes != null && spec.blendShapes.Length >= 2 && spec.blendShapes[0].frames.Length >= 2);
                        Require(index >= 0, "The mesh validation contract requires two bones and multiple blend shapes/frames.");
                        report.meshValidationContracts = VerifyMeshValidationContracts(bundle.LoadAsset<Mesh>(paths[index]), recipe.meshes[index]);
                        if (textures.Length > 0)
                            report.textureValidationContracts = VerifyTextureValidationContracts(bundle.LoadAsset<Texture2D>(texturePaths[0]), textures[0], texturePaths[0], report.createdTextures[0]);
                    }
                }
                finally { bundle.Unload(true); }
                Require(Sha256(recipePath) == report.inputSha256 && textures.All(spec => Sha256(MaterialInputPath(root, assetRoot, spec.path)) == spec.sha256) &&
                    sources.All(spec => Sha256(InputPath(root, assetRoot, spec.path, ".bundle")) == spec.sha256), "The mesh, texture or source bundle input changed during the build.");
                if (recipe.numericPayload != null) MeshRecipeInput.VerifyPayload(recipePath, recipe.numericPayload);
                report.activeScenePathAfter = SceneManager.GetActiveScene().path;
                report.activeSceneDirtyAfter = SceneManager.GetActiveScene().isDirty;
                report.sceneCountAfter = SceneManager.sceneCount;
                Require(report.activeScenePathBefore == report.activeScenePathAfter && report.activeSceneDirtyBefore == report.activeSceneDirtyAfter &&
                        report.sceneCountBefore == report.sceneCountAfter, "The mesh build changed the open scene state.");
                foreach (var sourceBundle in sourceBundles) { if (sourceBundle != null) sourceBundle.Unload(true); report.sourceBundlesReleased++; }
                sourceBundles.Clear();
                report.stage = "complete"; report.success = true; report.completedUtc = DateTime.UtcNow.ToString("o");
                WriteJson(reportPath, report); return true;
            }
            catch (Exception exception)
            {
                report.error = exception.ToString(); report.completedUtc = DateTime.UtcNow.ToString("o");
                if (reportPath != null && !File.Exists(reportPath)) { WriteJson(reportPath, report); }
                Debug.LogException(exception); return false;
            }
            finally { foreach (var bundle in sourceBundles) if (bundle != null) bundle.Unload(true); }
        }

        private static void ValidateMeshTextureJson(byte[] bytes) { MeshRecipeInput.ValidateJson(bytes); }

        private static void ValidateTextureSpecs(TextureSpec[] specs, string root, string assetRoot)
        {
            Require(specs.Length <= 128 && specs.All(spec => spec != null && spec.name != null &&
                Regex.IsMatch(spec.name, "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")) &&
                specs.Select(spec => spec.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == specs.Length &&
                specs.Select(spec => spec.path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == specs.Length, "Invalid or duplicate mesh texture name/path.");
            foreach (var spec in specs)
            {
                var path = MaterialInputPath(root, assetRoot, spec.path);
                Require(new[] { ".png", ".jpg", ".jpeg", ".tga" }.Contains(Path.GetExtension(spec.path)) &&
                    Path.GetFileNameWithoutExtension(spec.path) == spec.name && !(spec.normalMap && spec.sRGB), "Texture basename or normal-map color space is invalid.");
                Require(spec.sha256 != null && Regex.IsMatch(spec.sha256, "^[a-f0-9]{64}$") && Sha256(path) == spec.sha256, "The mesh texture input hash differs: " + spec.path);
            }
        }

        private static void ConfigureTexture(string path, TextureSpec spec)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Require(importer != null, "The declared texture did not create a TextureImporter: " + path);
            importer.textureType = spec.normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.convertToNormalmap = false;
            importer.sRGBTexture = spec.sRGB;
            importer.mipmapEnabled = spec.mipmaps;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.crunchedCompression = spec.crunchedCompression;
            importer.compressionQuality = spec.compressionQuality ?? 50;
            // The reload contract reads the compressed CPU payload, including every mip.
            importer.isReadable = true;
            importer.SaveAndReimport();
        }

        private static TextureValidation ValidateTexture(Texture2D texture, TextureSpec spec, string path, TextureValidation expected)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Require(texture != null && importer != null && texture.name == spec.name && texture.isReadable && texture.width > 0 && texture.height > 0,
                "The texture identity, readable payload or dimensions changed: " + spec.name);
            var value = new TextureValidation { name = texture.name, path = spec.path, inputSha256 = spec.sha256,
                width = texture.width, height = texture.height, mipCount = texture.mipmapCount, graphicsFormat = texture.graphicsFormat.ToString(), textureFormat = texture.format.ToString(),
                normalMap = importer.textureType == TextureImporterType.NormalMap, sRGB = importer.sRGBTexture, mipmaps = importer.mipmapEnabled,
                heightConversion = importer.convertToNormalmap, readable = importer.isReadable, compression = importer.textureCompression.ToString(),
                crunched = importer.crunchedCompression, compressionQuality = importer.compressionQuality };
            value.importSettingsExact = importer.textureType == (spec.normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default) &&
                importer.textureShape == TextureImporterShape.Texture2D && value.sRGB == spec.sRGB && value.mipmaps == spec.mipmaps &&
                !value.heightConversion && value.readable && value.crunched == spec.crunchedCompression && importer.textureCompression == TextureImporterCompression.Compressed &&
                value.compressionQuality == (spec.compressionQuality ?? 50) && importer.npotScale == TextureImporterNPOTScale.None;
            Require(value.importSettingsExact, "The declared texture import settings changed: " + spec.name);
            Require(!spec.crunchedCompression || texture.format == TextureFormat.DXT1Crunched || texture.format == TextureFormat.DXT5Crunched,
                "The requested desktop Crunch format was not produced: " + spec.name);
            var raw = texture.GetRawTextureData();
            value.rawBytes = raw.Length; value.rawSha256 = HashBytes(raw);
            var mipCount = spec.mipmaps ? 1 + (int)Math.Floor(Math.Log(Math.Max(texture.width, texture.height), 2d)) : 1;
            Require(raw.Length > 0 && value.mipCount == mipCount, "The texture mip chain/payload changed: " + spec.name);
            value.exact = expected == null || value.name == expected.name && value.path == expected.path && value.width == expected.width &&
                value.height == expected.height && value.mipCount == expected.mipCount && value.graphicsFormat == expected.graphicsFormat && value.textureFormat == expected.textureFormat &&
                value.rawBytes == expected.rawBytes && value.rawSha256 == expected.rawSha256;
            Require(value.exact, "The texture compressed payload/format changed: " + spec.name);
            return value;
        }

        private static MeshValidationContract[] VerifyTextureValidationContracts(Texture2D source, TextureSpec spec, string path, TextureValidation expected)
        {
            var contracts = new List<MeshValidationContract>();
            foreach (var kind in new[] { "texture-name", "texture-payload", "texture-format", "texture-mips", "texture-import-type", "texture-import-srgb",
                "texture-height-conversion", "texture-import-mips", "texture-compression", "texture-crunch", "texture-quality", "texture-readable" })
            {
                if (kind == "texture-mips" && source.width == 1 && source.height == 1) continue;
                Texture2D clone = null;
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                try
                {
                    clone = Object.Instantiate(source); clone.name = spec.name;
                    switch (kind)
                    {
                        case "texture-name": clone.name += "_Changed"; break;
                        case "texture-payload": var raw = clone.GetRawTextureData(); raw[0] ^= 1; clone.LoadRawTextureData(raw); clone.Apply(false, false); break;
                        case "texture-format": Object.DestroyImmediate(clone); clone = new Texture2D(source.width, source.height, TextureFormat.RGBA32, spec.mipmaps, !spec.sRGB); clone.name = spec.name; break;
                        case "texture-mips": Object.DestroyImmediate(clone); clone = new Texture2D(source.width, source.height, source.format, !spec.mipmaps, !spec.sRGB); clone.name = spec.name; break;
                        case "texture-import-type": importer.textureType = spec.normalMap ? TextureImporterType.Default : TextureImporterType.NormalMap; break;
                        case "texture-import-srgb": importer.sRGBTexture = !spec.sRGB; break;
                        case "texture-height-conversion": importer.convertToNormalmap = true; break;
                        case "texture-import-mips": importer.mipmapEnabled = !spec.mipmaps; break;
                        case "texture-compression": importer.textureCompression = TextureImporterCompression.Uncompressed; break;
                        case "texture-crunch": importer.crunchedCompression = !spec.crunchedCompression; break;
                        case "texture-quality": importer.compressionQuality = (spec.compressionQuality ?? 50) == 100 ? 99 : 100; break;
                        case "texture-readable": importer.isReadable = false; break;
                    }
                    var contract = new MeshValidationContract { kind = kind };
                    try { ValidateTexture(clone, spec, path, expected); }
                    catch (InvalidOperationException exception) { contract.rejected = true; contract.rejection = exception.Message; }
                    contracts.Add(contract); Require(contract.rejected, "The texture validator accepted a mutated asset/import setting: " + kind);
                }
                finally
                {
                    if (clone != null) Object.DestroyImmediate(clone);
                    ConfigureTexture(path, spec);
                }
            }
            ValidateTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(path), spec, path, expected);
            return contracts.ToArray();
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
        private static void ValidateMeshSpec(MeshSpec spec) { MeshRecipeInput.ValidateSpec(spec); }
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
                if (spec.skin != null)
                {
                    var skin = spec.skin;
                    var bindPoses = new Matrix4x4[skin.bindPoses.Length / 16];
                    for (var bone = 0; bone < bindPoses.Length; bone++)
                        for (var element = 0; element < 16; element++) bindPoses[bone][element] = skin.bindPoses[bone * 16 + element];
                    mesh.bindposes = bindPoses;
                    mesh.boneWeights = Enumerable.Range(0, spec.vertices.Length / 3).Select(vertex => new BoneWeight {
                        weight0 = skin.boneWeights[vertex * 4], weight1 = skin.boneWeights[vertex * 4 + 1],
                        weight2 = skin.boneWeights[vertex * 4 + 2], weight3 = skin.boneWeights[vertex * 4 + 3],
                        boneIndex0 = skin.boneIndices[vertex * 4], boneIndex1 = skin.boneIndices[vertex * 4 + 1],
                        boneIndex2 = skin.boneIndices[vertex * 4 + 2], boneIndex3 = skin.boneIndices[vertex * 4 + 3] }).ToArray();
                }
                AddTemplateBlendShapes(mesh, spec.blendShapes);
                mesh.bounds = new Bounds(MeshVector3(spec.bounds.center), MeshVector3(spec.bounds.size));
                return mesh;
            }
            catch { Object.DestroyImmediate(mesh); throw; }
        }
        private static SerializedProperty MeshShapeChannels(SerializedObject serialized)
        {
            serialized.Update();
            var channels = serialized.FindProperty("m_Shapes.channels");
            Require(channels != null && channels.isArray, "The mesh serialized blend shape table is unavailable.");
            return channels;
        }
        private static long MeshShapeNameHash(string name)
        {
            var witness = new Mesh { name = "BlendShapeNameHashWitness" };
            try
            {
                witness.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                witness.triangles = new[] { 0, 1, 2 };
                witness.AddBlendShapeFrame(name, 100f, new Vector3[3], new Vector3[3], new Vector3[3]);
                var channels = MeshShapeChannels(new SerializedObject(witness));
                Require(channels.arraySize == 1, "The mesh name hash witness lost its physical channel.");
                var property = channels.GetArrayElementAtIndex(0).FindPropertyRelative("nameHash");
                Require(property != null && property.propertyType == SerializedPropertyType.Integer && property.type == "uint" &&
                    property.longValue >= 0 && property.longValue <= uint.MaxValue, "The mesh name hash is not an unsigned32 value.");
                return property.longValue;
            }
            finally { Object.DestroyImmediate(witness); }
        }
        private static void AddTemplateBlendShapes(Mesh mesh, MeshBlendShape[] shapes)
        {
            if (shapes == null || shapes.Length == 0) return;
            for (var index = 0; index < shapes.Length; index++)
                foreach (var frame in shapes[index].frames)
                    mesh.AddBlendShapeFrame("__UBBlendShapeSlot_" + index.ToString("D8"), frame.weight,
                        MeshVectors3(frame.vertices), MeshVectors3(frame.normals), MeshVectors3(frame.tangents));
            var serialized = new SerializedObject(mesh);
            var channels = MeshShapeChannels(serialized);
            Require(channels.arraySize == shapes.Length, "Temporary mesh blend shape physical slots were merged.");
            var hashes = new Dictionary<string, long>(StringComparer.Ordinal);
            var firstFrame = 0;
            for (var index = 0; index < shapes.Length; index++)
            {
                var channel = channels.GetArrayElementAtIndex(index);
                var name = channel.FindPropertyRelative("name"); var hash = channel.FindPropertyRelative("nameHash");
                var start = channel.FindPropertyRelative("frameIndex"); var count = channel.FindPropertyRelative("frameCount");
                Require(name != null && hash != null && start != null && count != null && hash.type == "uint" &&
                    start.intValue == firstFrame && count.intValue == shapes[index].frames.Length, "Temporary mesh blend shape frame ranges changed.");
                long expected;
                if (!hashes.TryGetValue(shapes[index].name, out expected)) { expected = MeshShapeNameHash(shapes[index].name); hashes.Add(shapes[index].name, expected); }
                name.stringValue = shapes[index].name;
                // A negative intValue is clamped to zero for the declared uint property.
                hash.longValue = expected;
                firstFrame += shapes[index].frames.Length;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var restored = MeshShapeChannels(new SerializedObject(mesh));
            Require(restored.arraySize == shapes.Length && Enumerable.Range(0, shapes.Length).All(index => {
                var channel = restored.GetArrayElementAtIndex(index);
                return channel.FindPropertyRelative("name").stringValue == shapes[index].name &&
                    channel.FindPropertyRelative("nameHash").longValue == hashes[shapes[index].name];
            }), "Mesh blend shape names/hashes were not restored.");
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
        private static string MeshFloatDifference(float[] expected, float[] actual)
        {
            var mismatch = 0; var zeroSign = 0; var dropped = 0; var introduced = 0; var first = -1;
            var maximum = 0d;
            var firstExpected = ""; var firstActual = ""; var firstExpectedBits = ""; var firstActualBits = "";
            for (var index = 0; index < Math.Min(expected.Length, actual.Length); index++)
            {
                var expectedBits = BitConverter.ToInt32(BitConverter.GetBytes(expected[index]), 0);
                var actualBits = BitConverter.ToInt32(BitConverter.GetBytes(actual[index]), 0);
                if (expectedBits == actualBits) continue;
                mismatch++;
                if (expected[index] == 0f && actual[index] == 0f) zeroSign++;
                if (expected[index] != 0f && actual[index] == 0f) dropped++;
                if (expected[index] == 0f && actual[index] != 0f) introduced++;
                maximum = Math.Max(maximum, Math.Abs((double)expected[index] - actual[index]));
                if (first >= 0) continue;
                first = index;
                firstExpected = expected[index].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                firstActual = actual[index].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                firstExpectedBits = unchecked((uint)expectedBits).ToString("X8");
                firstActualBits = unchecked((uint)actualBits).ToString("X8");
            }
            return " expectedElements=" + expected.Length + " actualElements=" + actual.Length + " mismatchCount=" + mismatch +
                " signedZeroOnly=" + zeroSign + " droppedNonzero=" + dropped + " introducedNonzero=" + introduced +
                " maxAbsoluteDifference=" + maximum.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                " firstIndex=" + first + " firstExpected=" + firstExpected + " firstActual=" + firstActual +
                " firstExpectedBits=" + firstExpectedBits + " firstActualBits=" + firstActualBits;
        }
        private static MeshValidation ValidateTemplateMesh(Mesh mesh, MeshSpec spec)
        {
            Require(mesh != null && mesh.isReadable && mesh.name == spec.name && mesh.vertexCount == spec.vertices.Length / 3 && mesh.subMeshCount == spec.subMeshes.Length && mesh.indexFormat.ToString() == spec.indexFormat, "The mesh identity/format changed.");
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
            var numericOwner = spec.name;
            Action<string, int, float[], float[], List<MeshChannelValidation>> numeric = (name, dimension, expected, actual, target) =>
            {
                var value = new MeshChannelValidation { attribute = name, dimension = dimension, elements = actual.Length,
                    expectedSha256 = MeshFloatHash(expected), reloadedSha256 = MeshFloatHash(actual) };
                value.exact = actual.Length == expected.Length && value.expectedSha256 == value.reloadedSha256;
                target.Add(value);
                if (!value.exact) throw new InvalidOperationException("The mesh numeric channel bits changed: " + name +
                    " owner=" + numericOwner + MeshFloatDifference(expected, actual));
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
            var bindPoses = mesh.bindposes;
            var weights = mesh.boneWeights;
            report.boneCount = bindPoses.Length;
            Require(report.boneCount == (spec.skin == null ? 0 : spec.skin.bindPoses.Length / 16) && weights.Length == (spec.skin == null ? 0 : mesh.vertexCount), "The mesh skin data count changed.");
            numeric("BindPoses", 16, spec.skin == null ? new float[0] : spec.skin.bindPoses,
                bindPoses.SelectMany(matrix => Enumerable.Range(0, 16).Select(element => matrix[element])).ToArray(), channels);
            numeric("BoneWeights", 4, spec.skin == null ? new float[0] : spec.skin.boneWeights,
                weights.SelectMany(weight => new[] { weight.weight0, weight.weight1, weight.weight2, weight.weight3 }).ToArray(), channels);
            var expectedIndices = spec.skin == null ? new int[0] : spec.skin.boneIndices;
            var actualIndices = weights.SelectMany(weight => new[] { weight.boneIndex0, weight.boneIndex1, weight.boneIndex2, weight.boneIndex3 }).ToArray();
            var boneIndices = new MeshChannelValidation { attribute = "BoneIndices", dimension = 4, elements = actualIndices.Length,
                expectedSha256 = MeshIndexHash(expectedIndices), reloadedSha256 = MeshIndexHash(actualIndices) };
            boneIndices.exact = expectedIndices.Length == actualIndices.Length && boneIndices.expectedSha256 == boneIndices.reloadedSha256;
            channels.Add(boneIndices); Require(boneIndices.exact, "The mesh bone index bits changed."); report.skinExact = true;
            report.blendShapeCount = mesh.blendShapeCount;
            Require(report.blendShapeCount == (spec.blendShapes == null ? 0 : spec.blendShapes.Length), "The mesh blend shape count changed.");
            report.blendShapes = new MeshBlendShapeValidation[report.blendShapeCount];
            var serializedShapes = MeshShapeChannels(new SerializedObject(mesh));
            Require(serializedShapes.arraySize == report.blendShapeCount, "The mesh serialized shape count changed.");
            var nameHashes = new Dictionary<string, long>(StringComparer.Ordinal);
            var firstFrame = 0;
            for (var shapeIndex = 0; shapeIndex < report.blendShapeCount; shapeIndex++)
            {
                var shape = spec.blendShapes[shapeIndex];
                Require(mesh.GetBlendShapeName(shapeIndex) == shape.name && mesh.GetBlendShapeFrameCount(shapeIndex) == shape.frames.Length,
                    "The mesh blend shape name/order/frame count changed.");
                var shapeReport = new MeshBlendShapeValidation { name = shape.name, frames = new MeshBlendFrameValidation[shape.frames.Length] };
                report.blendShapes[shapeIndex] = shapeReport;
                var serializedShape = serializedShapes.GetArrayElementAtIndex(shapeIndex);
                var serializedName = serializedShape.FindPropertyRelative("name");
                var serializedHash = serializedShape.FindPropertyRelative("nameHash");
                var serializedStart = serializedShape.FindPropertyRelative("frameIndex");
                var serializedCount = serializedShape.FindPropertyRelative("frameCount");
                Require(serializedName != null && serializedHash != null && serializedStart != null && serializedCount != null &&
                    serializedHash.propertyType == SerializedPropertyType.Integer && serializedHash.type == "uint", "The mesh serialized shape fields changed.");
                long expectedNameHash;
                if (!nameHashes.TryGetValue(shape.name, out expectedNameHash)) { expectedNameHash = MeshShapeNameHash(shape.name); nameHashes.Add(shape.name, expectedNameHash); }
                shapeReport.index = shapeIndex; shapeReport.frameIndex = serializedStart.intValue; shapeReport.frameCount = serializedCount.intValue;
                shapeReport.nameHash = serializedHash.longValue; shapeReport.expectedNameHash = expectedNameHash;
                Require(shapeReport.nameHash == expectedNameHash, "The mesh blend shape name hash changed.");
                shapeReport.channelIdentityExact = serializedName.stringValue == shape.name && shapeReport.frameIndex == firstFrame && shapeReport.frameCount == shape.frames.Length;
                Require(shapeReport.channelIdentityExact, "The mesh blend shape physical frame range changed.");
                shapeReport.expectedFirstIndex = Array.FindIndex(spec.blendShapes, value => string.Equals(value.name, shape.name, StringComparison.Ordinal));
                shapeReport.actualFirstIndex = mesh.GetBlendShapeIndex(shape.name);
                shapeReport.firstLookupExact = shapeReport.actualFirstIndex == shapeReport.expectedFirstIndex;
                Require(shapeReport.firstLookupExact, "The mesh blend shape first-name lookup changed.");
                firstFrame += shape.frames.Length;
                for (var frame = 0; frame < shape.frames.Length; frame++)
                {
                    numericOwner = spec.name + "/shape[" + shapeIndex + "]=" + shape.name + "/frame[" + frame + "]";
                    var expected = shape.frames[frame];
                    var value = new MeshBlendFrameValidation { weight = mesh.GetBlendShapeFrameWeight(shapeIndex, frame) };
                    shapeReport.frames[frame] = value;
                    value.weightExact = MeshFloatHash(new[] { value.weight }) == MeshFloatHash(new[] { expected.weight });
                    Require(value.weightExact, "The mesh blend shape frame weight bits changed.");
                    var vertices = new Vector3[mesh.vertexCount]; var normals = new Vector3[mesh.vertexCount]; var tangents = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(shapeIndex, frame, vertices, normals, tangents);
                    var frameChannels = new List<MeshChannelValidation>();
                    numeric("DeltaVertices", 3, expected.vertices, vertices.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray(), frameChannels);
                    numeric("DeltaNormals", 3, expected.normals, normals.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray(), frameChannels);
                    numeric("DeltaTangents", 3, expected.tangents, tangents.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray(), frameChannels);
                    value.channels = frameChannels.ToArray(); value.exact = true;
                }
                shapeReport.exact = true;
            }
            var missingShape = "__UBMissingBlendShape";
            while (nameHashes.ContainsKey(missingShape)) missingShape += "_";
            Require(mesh.GetBlendShapeIndex(missingShape) == -1, "The mesh missing-name lookup changed.");
            report.blendShapesExact = true;
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

        private static string MeshShapeDifference(MeshBlendShape expected, MeshBlendShape actual)
        {
            if (!string.Equals(expected.name, actual.name, StringComparison.Ordinal) || expected.frames.Length != actual.frames.Length)
                return "blend shape name/order/frame count changed";
            for (var index = 0; index < expected.frames.Length; index++)
            {
                var a = expected.frames[index]; var b = actual.frames[index];
                if (MeshFloatHash(new[] { a.weight }) != MeshFloatHash(new[] { b.weight })) return "blend shape frame weight bits changed";
                if (MeshFloatHash(a.vertices) != MeshFloatHash(b.vertices)) return "numeric channel bits changed: DeltaVertices";
                if (MeshFloatHash(a.normals) != MeshFloatHash(b.normals)) return "numeric channel bits changed: DeltaNormals";
                if (MeshFloatHash(a.tangents) != MeshFloatHash(b.tangents)) return "numeric channel bits changed: DeltaTangents";
            }
            return null;
        }
        private static MeshValidationContract[] VerifyMeshValidationContracts(Mesh source, MeshSpec spec)
        {
            var contracts = new List<MeshValidationContract>();
            foreach (var kind in new[] { "bind-pose", "bone-weight", "bone-index", "blend-shape-order", "blend-frame-weight", "blend-vertices", "blend-normals", "blend-tangents", "blend-name-hash", "blend-frame-range" })
            {
                var orderFirst = -1; var orderSecond = -1; string orderDifference = null;
                if (kind == "blend-shape-order")
                {
                    for (var first = 0; first < spec.blendShapes.Length && orderDifference == null; first++)
                        for (var second = first + 1; second < spec.blendShapes.Length; second++)
                        {
                            orderDifference = MeshShapeDifference(spec.blendShapes[first], spec.blendShapes[second]);
                            if (orderDifference != null) { orderFirst = first; orderSecond = second; break; }
                        }
                    if (orderDifference == null)
                    {
                        contracts.Add(new MeshValidationContract { kind = kind, applicable = false,
                            notApplicableReason = "All physical channels have identical names, frame weights and VNT; permutation is unobservable." });
                        continue;
                    }
                }
                var mesh = Object.Instantiate(source);
                mesh.name = spec.name;
                try
                {
                    if (kind == "bind-pose") { var matrices = mesh.bindposes; matrices[0][12] += 0.125f; mesh.bindposes = matrices; }
                    else if (kind == "bone-weight") { var weights = mesh.boneWeights; weights[0].weight0 = 0.625f; weights[0].weight1 = 0.375f; weights[0].weight2 = weights[0].weight3 = 0f; mesh.boneWeights = weights; }
                    else if (kind == "bone-index") { var weights = mesh.boneWeights; weights[0].boneIndex0 = (weights[0].boneIndex0 + 1) % mesh.bindposes.Length; mesh.boneWeights = weights; }
                    else if (kind == "blend-name-hash" || kind == "blend-frame-range")
                    {
                        var serialized = new SerializedObject(mesh); var channel = MeshShapeChannels(serialized).GetArrayElementAtIndex(0);
                        var field = channel.FindPropertyRelative(kind == "blend-name-hash" ? "nameHash" : "frameIndex");
                        if (kind == "blend-name-hash") field.longValue ^= 1L; else field.intValue += 1;
                        Require(serialized.ApplyModifiedPropertiesWithoutUndo(), "The blend shape contract mutation made no change.");
                    }
                    else
                    {
                        mesh.ClearBlendShapes();
                        var ordered = spec.blendShapes.ToArray();
                        if (kind == "blend-shape-order") { var other = ordered[orderFirst]; ordered[orderFirst] = ordered[orderSecond]; ordered[orderSecond] = other; }
                        var shapes = ordered.Select(shape => new MeshBlendShape {
                            name = shape.name, frames = shape.frames.Select((frame, index) => {
                                var value = new MeshBlendFrame { weight = frame.weight, vertices = (float[])frame.vertices.Clone(), normals = (float[])frame.normals.Clone(), tangents = (float[])frame.tangents.Clone() };
                                var first = ReferenceEquals(shape, spec.blendShapes[0]) && index == 0;
                                if (first && kind == "blend-frame-weight") value.weight += 0.125f;
                                if (first && kind == "blend-vertices") value.vertices[0] += 0.125f;
                                if (first && kind == "blend-normals") value.normals[1] += 0.125f;
                                if (first && kind == "blend-tangents") value.tangents[2] += 0.125f;
                                return value;
                            }).ToArray() }).ToArray();
                        AddTemplateBlendShapes(mesh, shapes);
                    }
                    var result = new MeshValidationContract { kind = kind };
                    try { ValidateTemplateMesh(mesh, spec); }
                    catch (InvalidOperationException exception) { result.rejected = true; result.rejection = exception.Message; }
                    var expected = kind == "bind-pose" ? "numeric channel bits changed: BindPoses" : kind == "bone-weight" ? "numeric channel bits changed: BoneWeights" :
                        kind == "bone-index" ? "bone index bits changed" : kind == "blend-shape-order" ? orderDifference :
                        kind == "blend-frame-weight" ? "blend shape frame weight bits changed" :
                        kind == "blend-name-hash" ? "blend shape name hash changed" : kind == "blend-frame-range" ? "blend shape physical frame range changed" :
                        "numeric channel bits changed: " + (kind == "blend-vertices" ? "DeltaVertices" : kind == "blend-normals" ? "DeltaNormals" : "DeltaTangents");
                    Require(result.rejected && result.rejection.Contains(expected), "The mesh validator accepted a mutation or rejected the wrong channel: " + kind);
                    contracts.Add(result);
                }
                finally { Object.DestroyImmediate(mesh); }
            }
            return contracts.ToArray();
        }

        private static bool BuildMaterials(string input, string destination, string jobId, string expectedHash,
            bool identityContracts = false, string colorSpace = "Linear")
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
                ApplyColorSpace(report, colorSpace);
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
            return InputPath(root, assetRoot, relative, ".shader", ".cginc", ".hlsl", ".png", ".jpg", ".jpeg", ".tga");
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
                    if (!Regex.IsMatch(jobId, "^[a-z0-9][a-z0-9_-]{0,63}$")) { continue; }
                    var requestPath = Path.Combine(jobDirectory, "request.json");
                    var claimedPath = Path.Combine(jobDirectory, "started.json");
                    if (!File.Exists(requestPath)) { continue; }
                    var reportPath = Path.Combine(jobDirectory, "Output/unity-report.json");
                    if (File.Exists(claimedPath))
                    {
                        if (!File.Exists(reportPath))
                        {
                            RequireJob(root, jobId);
                            WriteJobFailure(reportPath, jobId, "The previous job was interrupted; submit a new job.");
                        }
                        continue;
                    }
                    RequireJob(root, jobId);
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
                                job.kind == "material-identity-contracts" || job.kind == "mesh-templates" || job.kind == "mesh-template-contracts", "Unknown authoring job kind.");
                        if (job.kind == "mesh-templates" || job.kind == "mesh-template-contracts")
                        {
                            BuildMeshTemplates(Path.Combine(root, "Assets/AuthoringJobs", jobId, "Input/mesh-recipe.json"),
                                Path.Combine(jobDirectory, "Output"), jobId, job.inputSha256, job.kind == "mesh-template-contracts", job.colorSpace);
                        }
                        else if (job.kind == "materials" || job.kind == "material-identity-contracts")
                        {
                            BuildMaterials(Path.Combine(root, "Assets/AuthoringJobs", jobId, "Input/recipe.json"),
                                Path.Combine(jobDirectory, "Output"), jobId, job.inputSha256,
                                job.kind == "material-identity-contracts", job.colorSpace);
                        }
                        else
                        {
                            BuildValidated(Path.Combine(root, "Assets/AuthoringJobs", jobId, "Input/SampleRig.fbx"),
                                Path.Combine(jobDirectory, "Output"), jobId, job.inputSha256, job.colorSpace);
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
            Require(Regex.IsMatch(jobId, "^[a-z0-9][a-z0-9_-]{0,63}$"), "Invalid job identifier.");
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

#endif

namespace SolastaAssetAuthoring
{
    public static class MeshRecipeInput
    {
        [DataContract] public sealed class MeshRecipe
        {
            [DataMember(IsRequired = true)] public int schemaVersion;
            [DataMember(IsRequired = true)] public string bundleName;
            [DataMember] public MeshSpec[] meshes;
            [DataMember] public TextureSpec[] textures;
            [DataMember] public NumericPayload numericPayload;
            [DataMember] public SourceBundleSpec[] sourceBundles;
        }
        [DataContract] public sealed class SourceBundleSpec
        {
            [DataMember(IsRequired = true)] public string path;
            [DataMember(IsRequired = true)] public string sha256;
            [DataMember(IsRequired = true)] public SourceAssetSpec[] assets;
        }
        [DataContract] public sealed class SourceAssetSpec
        {
            [DataMember(IsRequired = true)] public string asset;
            [DataMember(IsRequired = true)] public string type;
            [DataMember(IsRequired = true)] public string name;
        }
        [DataContract] public sealed class TextureSpec
        {
            [DataMember(IsRequired = true)] public string name;
            [DataMember(IsRequired = true)] public string path;
            [DataMember(IsRequired = true)] public string sha256;
            [DataMember(IsRequired = true)] public bool normalMap;
            [DataMember(IsRequired = true)] public bool sRGB;
            [DataMember(IsRequired = true)] public bool mipmaps;
            [DataMember] public bool crunchedCompression;
            [DataMember(EmitDefaultValue = false)] public int? compressionQuality;
        }
        [DataContract] public sealed class MeshBounds
        {
            [DataMember(IsRequired = true)] public float[] center;
            [DataMember(IsRequired = true)] public float[] size;
        }
        [DataContract] public sealed class MeshUv
        {
            [DataMember(IsRequired = true)] public int channel;
            [DataMember(IsRequired = true)] public int dimension;
            [DataMember(IsRequired = true)] public float[] values;
        }
        [DataContract] public sealed class MeshSubmesh
        {
            [DataMember(IsRequired = true)] public string topology;
            [DataMember(IsRequired = true)] public int[] indices;
        }
        [DataContract] public sealed class MeshSkin
        {
            [DataMember(IsRequired = true)] public float[] bindPoses;
            [DataMember(IsRequired = true)] public float[] boneWeights;
            [DataMember(IsRequired = true)] public int[] boneIndices;
        }
        [DataContract] public sealed class MeshBlendFrame
        {
            [DataMember(IsRequired = true)] public float weight;
            [DataMember(IsRequired = true)] public float[] vertices;
            [DataMember(IsRequired = true)] public float[] normals;
            [DataMember(IsRequired = true)] public float[] tangents;
        }
        [DataContract] public sealed class MeshBlendShape
        {
            [DataMember(IsRequired = true)] public string name;
            [DataMember(IsRequired = true)] public MeshBlendFrame[] frames;
        }
        [DataContract] public sealed class MeshSpec
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
            [DataMember] public MeshSkin skin;
            [DataMember] public MeshBlendShape[] blendShapes;
        }
        public const int InputLimit = 67108864;
        public const string NumericFormat = "Solasta.MeshNumeric.1";

        [DataContract] public sealed class NumericPayload
        {
            [DataMember(IsRequired = true)] public string format;
            [DataMember(IsRequired = true)] public string path;
            [DataMember(IsRequired = true)] public string sha256;
            [DataMember(IsRequired = true)] public long byteLength;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static void MeshNumbers(float[] values, int count, string channel)
        {
            Require(values != null && values.Length == count && values.All(Finite), "Invalid mesh " + channel + " numeric data/length.");
        }

        public static void ValidateJson(byte[] bytes)
        {
            var document = new System.Xml.XmlDocument();
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, System.Xml.XmlDictionaryReaderQuotas.Max)) { document.Load(reader); }
            var root = document.DocumentElement;
            var fields = root.ChildNodes.Cast<System.Xml.XmlNode>().ToArray();
            Require(fields.All(node => new[] { "schemaVersion", "bundleName", "meshes", "textures", "numericPayload", "sourceBundles" }.Contains(node.Name)) &&
                    fields.Select(node => node.Name).Distinct().Count() == fields.Length, "Unknown or duplicate mesh recipe field.");
            var payload = root["numericPayload"];
            if (payload != null)
            {
                Require(root["meshes"] == null, "Inline meshes and an external numeric payload cannot be mixed.");
                var members = payload.ChildNodes.Cast<System.Xml.XmlNode>().ToArray();
                var names = new[] { "format", "path", "sha256", "byteLength" };
                Require(payload.GetAttribute("type") == "object" && members.Length == names.Length &&
                        names.All(name => members.Count(node => node.Name == name) == 1), "Unknown, duplicate or missing numeric payload field.");
                foreach (var name in names)
                    Require(payload[name].GetAttribute("type") == (name == "byteLength" ? "number" : "string"), "Invalid numeric payload JSON type.");
                long length;
                Require(long.TryParse(payload["byteLength"].InnerText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out length) && length >= 16 && length <= InputLimit,
                        "Invalid numeric payload byte length.");
            }
            else Require(root["meshes"] != null && root["meshes"].GetAttribute("type") == "array", "The inline mesh list must be a JSON array.");
            var sources = root["sourceBundles"];
            if (sources != null)
            {
                Require(sources.GetAttribute("type") == "array", "The source bundle list must be a JSON array.");
                foreach (System.Xml.XmlElement source in sources.ChildNodes)
                {
                    var members = source.ChildNodes.Cast<System.Xml.XmlNode>().ToArray();
                    var names = new[] { "path", "sha256", "assets" };
                    Require(source.GetAttribute("type") == "object" && members.Length == names.Length && names.All(name => members.Count(node => node.Name == name) == 1),
                        "Unknown, duplicate or missing source bundle field.");
                    Require(source["path"].GetAttribute("type") == "string" && source["sha256"].GetAttribute("type") == "string" && source["assets"].GetAttribute("type") == "array",
                        "Invalid source bundle JSON type.");
                    foreach (System.Xml.XmlElement asset in source["assets"].ChildNodes)
                    {
                        var values = asset.ChildNodes.Cast<System.Xml.XmlNode>().ToArray();
                        var keys = new[] { "asset", "type", "name" };
                        Require(asset.GetAttribute("type") == "object" && values.Length == keys.Length &&
                                keys.All(name => values.Count(node => node.Name == name && ((System.Xml.XmlElement)node).GetAttribute("type") == "string") == 1),
                            "Unknown, duplicate, missing or mistyped source asset field.");
                    }
                }
            }
            var list = root["textures"];
            if (list == null) return;
            Require(list.GetAttribute("type") == "array", "The mesh texture list must be a JSON array.");
            foreach (System.Xml.XmlElement texture in list.ChildNodes)
            {
                var members = texture.ChildNodes.Cast<System.Xml.XmlNode>().ToArray();
                var names = new[] { "name", "path", "sha256", "normalMap", "sRGB", "mipmaps" };
                var optional = new[] { "crunchedCompression", "compressionQuality" };
                Require(texture.GetAttribute("type") == "object" && names.All(name => members.Count(node => node.Name == name) == 1) &&
                        members.All(node => names.Contains(node.Name) || optional.Contains(node.Name)) && members.Select(node => node.Name).Distinct().Count() == members.Length,
                        "Unknown, duplicate or missing mesh texture field.");
                foreach (var name in names)
                    Require(texture[name].GetAttribute("type") == (new[] { "normalMap", "sRGB", "mipmaps" }.Contains(name) ? "boolean" : "string"),
                            "Invalid JSON type for texture " + name);
                if (texture["crunchedCompression"] != null)
                    Require(texture["crunchedCompression"].GetAttribute("type") == "boolean", "Invalid JSON type for texture crunchedCompression.");
                if (texture["compressionQuality"] != null)
                {
                    int quality;
                    Require(texture["compressionQuality"].GetAttribute("type") == "number" &&
                            int.TryParse(texture["compressionQuality"].InnerText, System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out quality) && quality >= 0 && quality <= 100,
                        "Texture compressionQuality must be an integer from 0 to 100.");
                }
            }
        }

        public static MeshRecipe ReadFile(string path)
        {
            RejectLinks(path);
            Require(new FileInfo(path).Length <= InputLimit, "The mesh recipe must be at most 64 MiB.");
            return Read(File.ReadAllBytes(path), path);
        }

        public static MeshRecipe Read(byte[] bytes, string path)
        {
            Require(bytes != null && bytes.Length <= InputLimit, "The mesh recipe must be at most 64 MiB.");
            ValidateJson(bytes);
            MeshRecipe recipe;
            using (var stream = new MemoryStream(bytes)) { recipe = (MeshRecipe)new DataContractJsonSerializer(typeof(MeshRecipe)).ReadObject(stream); }
            Require(recipe != null && recipe.schemaVersion == 1 && recipe.bundleName != null &&
                    Regex.IsMatch(recipe.bundleName, "^[a-z0-9][a-z0-9_.-]{0,127}$"), "Invalid mesh recipe version or bundle name.");
            if (recipe.numericPayload != null) recipe.meshes = ReadNumeric(PayloadPath(path, recipe.numericPayload), recipe.numericPayload);
            Require(recipe.meshes != null && recipe.meshes.Length <= 128, "Invalid mesh list.");
            Require(recipe.meshes.All(mesh => mesh != null && mesh.name != null && Regex.IsMatch(mesh.name, "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")) &&
                    recipe.meshes.Select(mesh => mesh.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == recipe.meshes.Length,
                    "Invalid or duplicate mesh name.");
            foreach (var mesh in recipe.meshes) ValidateSpec(mesh);
            recipe.textures = recipe.textures ?? new TextureSpec[0];
            Require(recipe.textures.Length <= 128 && recipe.textures.All(texture => texture != null && texture.name != null &&
                    Regex.IsMatch(texture.name, "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")) &&
                    recipe.textures.Select(texture => texture.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == recipe.textures.Length &&
                    recipe.textures.Select(texture => texture.path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == recipe.textures.Length,
                    "Invalid or duplicate mesh texture name/path.");
            foreach (var texture in recipe.textures)
                Require(texture.path != null && Regex.IsMatch(texture.path, "^[A-Za-z0-9_./-]+$") && !texture.path.StartsWith("/", StringComparison.Ordinal) &&
                        texture.path.Split('/').All(part => part.Length > 0 && part != "." && part != "..") &&
                        new[] { ".png", ".jpg", ".jpeg", ".tga" }.Contains(Path.GetExtension(texture.path)) &&
                        Path.GetFileNameWithoutExtension(texture.path) == texture.name && !(texture.normalMap && texture.sRGB) &&
                        texture.sha256 != null && Regex.IsMatch(texture.sha256, "^[a-f0-9]{64}$") &&
                        (!texture.compressionQuality.HasValue || texture.compressionQuality.Value >= 0 && texture.compressionQuality.Value <= 100),
                        "Invalid mesh texture path, hash or import flags.");
            recipe.sourceBundles = recipe.sourceBundles ?? new SourceBundleSpec[0];
            Require(recipe.sourceBundles.Length <= 128 && recipe.sourceBundles.All(source => source != null &&
                    SafeRelativePath(source.path) && source.path.EndsWith(".bundle", StringComparison.Ordinal) &&
                    source.sha256 != null && Regex.IsMatch(source.sha256, "^[a-f0-9]{64}$") && source.assets != null && source.assets.Length > 0 && source.assets.Length <= 128) &&
                    recipe.sourceBundles.Select(source => source.path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == recipe.sourceBundles.Length,
                "Invalid or duplicate source bundle path, hash or asset list.");
            var selected = recipe.sourceBundles.SelectMany(source => source.assets).ToArray();
            Require(selected.Length <= 128 && selected.All(asset => asset != null && (asset.type == "Mesh" || asset.type == "Texture2D") &&
                    SafeRelativePath(asset.asset) && asset.name != null && Regex.IsMatch(asset.name, "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")) &&
                    recipe.sourceBundles.All(source => source.assets.Select(asset => asset.asset).Distinct(StringComparer.OrdinalIgnoreCase).Count() == source.assets.Length),
                "Invalid, duplicate or unsupported source asset declaration.");
            var outputNames = recipe.meshes.Select(mesh => mesh.name).Concat(recipe.textures.Select(texture => texture.name)).Concat(selected.Select(asset => asset.name)).ToArray();
            Require(outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == outputNames.Length,
                "Mesh, texture and source asset output names must be unique.");
            var inputPaths = recipe.textures.Select(texture => texture.path).Concat(recipe.sourceBundles.Select(source => source.path)).ToArray();
            Require(inputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == inputPaths.Length, "Authoring input paths must be unique.");
            Require(recipe.meshes.Length > 0 || recipe.textures.Length > 0 || selected.Length > 0, "The mesh recipe must contain a mesh or texture.");
            return recipe;
        }

        private static bool SafeRelativePath(string path)
        {
            return path != null && Regex.IsMatch(path, "^[A-Za-z0-9_./-]+$") && !path.StartsWith("/", StringComparison.Ordinal) &&
                path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        }

        public static string InputPath(string root, string assetRoot, string relative, params string[] extensions)
        {
            Require(SafeRelativePath(relative), "Invalid authoring input path.");
            Require(extensions != null && extensions.Contains(Path.GetExtension(relative)), "Unsupported authoring input extension.");
            var project = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var owner = Path.GetFullPath(Path.Combine(project, assetRoot)).TrimEnd(Path.DirectorySeparatorChar);
            Require(owner == project || owner.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "The authoring input owner escaped its project.");
            var path = Path.GetFullPath(Path.Combine(owner, relative));
            Require(path.StartsWith(owner + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "The authoring input escaped its owner.");
            RejectLinks(path);
            Require(File.Exists(path), "The authoring input is missing: " + relative);
            return path;
        }

        public static string PayloadPath(string recipePath, NumericPayload payload)
        {
            Require(payload != null && payload.format == NumericFormat && payload.sha256 != null &&
                    Regex.IsMatch(payload.sha256, "^[a-f0-9]{64}$") && payload.byteLength >= 16 && payload.byteLength <= InputLimit,
                    "Invalid numeric payload declaration.");
            var relative = payload.path;
            Require(relative != null && Regex.IsMatch(relative, "^[A-Za-z0-9_./-]+$") && !relative.StartsWith("/", StringComparison.Ordinal) &&
                    relative.Split('/').All(part => part.Length > 0 && part != "." && part != "..") &&
                    relative.EndsWith(".bin", StringComparison.Ordinal), "Invalid numeric payload relative path.");
            var directory = Path.GetDirectoryName(Path.GetFullPath(recipePath));
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            Require(path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "The numeric payload escaped its recipe directory.");
            RejectLinks(path);
            return path;
        }

        private static void RejectLinks(string path)
        {
            var current = Path.GetFullPath(path);
            Require(!current.StartsWith("\\\\", StringComparison.Ordinal), "A network input path is not supported.");
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                    Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "A numeric input path contains a reparse point.");
                var parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        public static void VerifyPayload(string recipePath, NumericPayload payload)
        {
            var path = PayloadPath(recipePath, payload);
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(stream.Length == payload.byteLength && stream.Length <= InputLimit, "Numeric payload length differs.");
                using (var hash = SHA256.Create())
                    Require(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == payload.sha256,
                            "Numeric payload SHA256 differs.");
            }
        }

        private static int Count(BinaryReader reader, int minimumBytes, int maximum)
        {
            Require(reader.BaseStream.Length - reader.BaseStream.Position >= 4, "Truncated numeric payload count.");
            var count = reader.ReadInt32();
            Require(count >= 0 && count <= maximum && (long)count * minimumBytes <= reader.BaseStream.Length - reader.BaseStream.Position,
                    "Numeric payload count exceeds the remaining bounded file.");
            return count;
        }
        private static string Text(BinaryReader reader)
        {
            var count = Count(reader, 1, InputLimit);
            return new UTF8Encoding(false, true).GetString(reader.ReadBytes(count));
        }
        private static float[] Floats(BinaryReader reader)
        {
            var count = Count(reader, 4, InputLimit / 4);
            var bytes = reader.ReadBytes(checked(count * 4));
            Require(bytes.Length == count * 4, "Truncated numeric float32 array.");
            var values = new float[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
        private static int[] Integers(BinaryReader reader)
        {
            var count = Count(reader, 4, InputLimit / 4);
            var bytes = reader.ReadBytes(checked(count * 4));
            Require(bytes.Length == count * 4, "Truncated numeric int32 array.");
            var values = new int[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
        private static MeshSpec[] ReadNumeric(string path, NumericPayload payload)
        {
            Require(BitConverter.IsLittleEndian, "The numeric input host must use little-endian data.");
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(stream.Length == payload.byteLength && stream.Length >= 16 && stream.Length <= InputLimit, "Numeric payload length differs.");
                using (var hash = SHA256.Create())
                    Require(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == payload.sha256,
                            "Numeric payload SHA256 differs.");
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    Require(reader.ReadBytes(8).SequenceEqual(Encoding.ASCII.GetBytes("UBMSH001")) && reader.ReadInt32() == 1,
                            "Unknown numeric payload magic or version.");
                    var meshes = new MeshSpec[Count(reader, 4, 128)];
                    Require(meshes.Length > 0, "The numeric payload has no meshes.");
                    for (var index = 0; index < meshes.Length; index++)
                    {
                        var mesh = new MeshSpec { name = Text(reader), vertices = Floats(reader), normals = Floats(reader),
                            tangents = Floats(reader), colors = Floats(reader) };
                        mesh.uvChannels = new MeshUv[Count(reader, 12, 8)];
                        for (var uv = 0; uv < mesh.uvChannels.Length; uv++)
                            mesh.uvChannels[uv] = new MeshUv { channel = reader.ReadInt32(), dimension = reader.ReadInt32(), values = Floats(reader) };
                        mesh.subMeshes = new MeshSubmesh[Count(reader, 8, 256)];
                        for (var submesh = 0; submesh < mesh.subMeshes.Length; submesh++)
                            mesh.subMeshes[submesh] = new MeshSubmesh { topology = Text(reader), indices = Integers(reader) };
                        mesh.bounds = new MeshBounds { center = Floats(reader), size = Floats(reader) };
                        mesh.indexFormat = Text(reader);
                        var hasSkin = reader.ReadInt32();
                        Require(hasSkin == 0 || hasSkin == 1, "Invalid numeric skin presence flag.");
                        if (hasSkin == 1) mesh.skin = new MeshSkin { bindPoses = Floats(reader), boneWeights = Floats(reader), boneIndices = Integers(reader) };
                        mesh.blendShapes = new MeshBlendShape[Count(reader, 8, InputLimit / 8)];
                        for (var shape = 0; shape < mesh.blendShapes.Length; shape++)
                        {
                            var value = new MeshBlendShape { name = Text(reader) };
                            value.frames = new MeshBlendFrame[Count(reader, 20, InputLimit / 20)];
                            for (var frame = 0; frame < value.frames.Length; frame++)
                            {
                                var weight = Floats(reader);
                                Require(weight.Length == 1, "A numeric frame weight must contain one float32.");
                                value.frames[frame] = new MeshBlendFrame { weight = weight[0], vertices = Floats(reader),
                                    normals = Floats(reader), tangents = Floats(reader) };
                            }
                            mesh.blendShapes[shape] = value;
                        }
                        meshes[index] = mesh;
                    }
                    Require(stream.Position == stream.Length, "Trailing numeric payload data.");
                    return meshes;
                }
            }
        }

        public static void ValidateSpec(MeshSpec spec)
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
            Require(Enumerable.Range(0, 3).All(axis => Finite(spec.bounds.center[axis] - spec.bounds.size[axis] / 2f) &&
                    Finite(spec.bounds.center[axis] + spec.bounds.size[axis] / 2f)), "Mesh bounds overflow.");
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
            if (spec.skin != null)
            {
                var skin = spec.skin;
                Require(skin.bindPoses != null && skin.bindPoses.Length > 0 && skin.bindPoses.Length % 16 == 0, "Invalid mesh bind pose count.");
                MeshNumbers(skin.bindPoses, skin.bindPoses.Length, "bind poses");
                MeshNumbers(skin.boneWeights, count * 4, "bone weights");
                var bones = skin.bindPoses.Length / 16;
                Require(skin.boneIndices != null && skin.boneIndices.Length == count * 4 &&
                        skin.boneIndices.All(index => index >= 0 && index < bones), "Invalid mesh bone index range/count.");
                for (var vertex = 0; vertex < count; vertex++)
                {
                    double total = 0;
                    for (var influence = 0; influence < 4; influence++)
                    {
                        var weight = skin.boneWeights[vertex * 4 + influence];
                        Require(weight >= 0f && weight <= 1f && (influence == 0 || skin.boneWeights[vertex * 4 + influence - 1] >= weight),
                            "Mesh bone weights must be nonnegative, at most one and descending.");
                        total += weight;
                    }
                    Require(Math.Abs(total - 1d) <= 1e-5, "Mesh bone weights must sum to one.");
                }
            }
            if (spec.blendShapes != null)
            {
                Require(spec.blendShapes.All(shape => shape != null && !string.IsNullOrWhiteSpace(shape.name) && !shape.name.Any(char.IsControl)),
                    "Invalid mesh blend shape name.");
                foreach (var shape in spec.blendShapes)
                {
                    Require(shape.frames != null && shape.frames.Length > 0, "Missing mesh blend shape frames.");
                    for (var frame = 0; frame < shape.frames.Length; frame++)
                    {
                        var value = shape.frames[frame];
                        Require(value != null && Finite(value.weight) && (frame == 0 || value.weight > shape.frames[frame - 1].weight),
                            "Mesh blend shape frame weights must be finite and strictly increasing.");
                        MeshNumbers(value.vertices, count * 3, "blend shape vertices");
                        MeshNumbers(value.normals, count * 3, "blend shape normals");
                        MeshNumbers(value.tangents, count * 3, "blend shape tangents");
                    }
                }
            }
        }
    }
}
