using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

// Read-only sampling in the temporary verification mod; never changes display settings.
public static class DisplayStateProbe
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static DisplayStateProbeObserver observer;
    private static Harmony harmony;
    private static Action<string> logger;
    private static int managedMainThread;
    private static float nextSample;
    private static string phase;
    private static int written;

    public static void InstallStartupHooks(Action<string> log)
    {
        RemoveStartupHooks();
        logger = log;
        managedMainThread = Thread.CurrentThread.ManagedThreadId;
        phase = "startup";
        written = 0;
        var method = typeof(RuntimeVerificationHarness).GetMethod("RecordExecution", Flags, null, new[] { typeof(string) }, null);
        if (method == null) throw new MissingMethodException(typeof(RuntimeVerificationHarness).FullName, "RecordExecution(string)");
        harmony = new Harmony("UBRuntimeVerification.ReadOnlyDisplayState");
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(DisplayStateProbe).GetMethod(nameof(AfterPhase), Flags)));
        var owner = new GameObject("Read-only display state observer");
        UnityEngine.Object.DontDestroyOnLoad(owner);
        observer = owner.AddComponent<DisplayStateProbeObserver>();
        Capture("startup-installed");
    }

    public static void RemoveStartupHooks()
    {
        if (observer) Capture("observer-removed");
        if (harmony != null) harmony.UnpatchAll(harmony.Id);
        harmony = null;
        if (observer) UnityEngine.Object.Destroy(observer.gameObject);
        observer = null;
        logger = null;
    }

    public static void Run(Action<string> log)
    {
        Capture("probe-entry");
        log((observer && written > 0 ? "PASS: " : "FAIL: ") +
            "Read-only display observer is installed on the main thread and wrote display evidence; resolution correctness is not asserted.");
    }

    private static void AfterPhase(string phase)
    {
        DisplayStateProbe.phase = phase;
        Capture("operation");
    }

    internal static void Heartbeat()
    {
        if (Time.realtimeSinceStartup < nextSample) return;
        nextSample = Time.realtimeSinceStartup + 1f;
        Capture("heartbeat");
    }

    private static void Capture(string eventName)
    {
        if (Thread.CurrentThread.ManagedThreadId != managedMainThread)
        {
            logger?.Invoke("FAIL: Display observation was requested outside its installed main thread; Unity access was skipped.");
            return;
        }
        try
        {
            var errors = new List<string>();
            int processId;
            using (var process = Process.GetCurrentProcess()) processId = process.Id;
            var resolution = Screen.currentResolution;
            var record = new DisplayRecord
            {
                utc = DateTime.UtcNow.ToString("O"), eventName = eventName, phase = phase,
                processId = processId, managedThreadId = Thread.CurrentThread.ManagedThreadId,
                nativeThreadId = UBVerification.DesktopReader.CurrentNativeThreadId(),
                frame = Time.frameCount, realtime = Time.realtimeSinceStartup,
                focused = Application.isFocused, screenWidth = Screen.width, screenHeight = Screen.height,
                fullScreen = Screen.fullScreen, fullScreenMode = Screen.fullScreenMode.ToString(), dpi = Screen.dpi,
                currentResolution = ResolutionRecord.FromNative(resolution),
                dynamicWidthScale = ScalableBufferManager.widthScaleFactor,
                dynamicHeightScale = ScalableBufferManager.heightScaleFactor,
                mapReady = ServiceRepository.GetService<IGameLocationService>()?.LocationIsReady == true,
                graphics = ReadGraphics(errors), cameras = ReadCameras(errors), canvases = ReadCanvases(errors),
                fidelityFx = ReadFidelityFx(errors), errors = errors
            };
            using (var output = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(DisplayRecord)).WriteObject(output, record);
                File.AppendAllText(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "Runtime-Display.jsonl"),
                    System.Text.Encoding.UTF8.GetString(output.ToArray()) + Environment.NewLine);
            }
            written++;
        }
        catch (Exception error)
        {
            logger?.Invoke("FAIL: Read-only display evidence could not be written: " + error);
        }
    }

    private static GraphicsRecord ReadGraphics(List<string> errors)
    {
        var value = new GraphicsRecord();
        try
        {
            var service = ServiceRepository.GetService<IGraphicsSettingsService>();
            if (service == null) return value;
            value.available = true;
            var manager = service as GraphicsResourceManager;
            if (manager == null) throw new InvalidOperationException("Graphics settings service is not GraphicsResourceManager.");
            value.settingsInitialized = (bool)Field(manager, "settingsInitialized");
            value.internalResolution = ResolutionRecord.FromNative((Resolution)Field(manager, "resolution"));
            value.internalFullScreenMode = (string)Field(manager, "fullScreenMode");
            value.resolutionChanged = (bool)Field(manager, "resolutionChanged");
            value.settingResolution = service.Resolution;
            value.settingFullScreenMode = service.FullScreenMode;
            value.settingFullScreenModeLatent = service.FullScreenModeLatentValue;
            value.settingRefreshRate = service.RefreshRate;
            var current = Field(manager, "currentSettings") as GraphicsSettingsDefinition;
            value.currentSettingsAvailable = current;
            if (current)
            {
                value.fsr = service.FSR;
                value.antiAliasing = service.AntiAliasingMode;
                value.qualityLevel = service.QualityLevel;
                value.customQuality = service.CustomQuality;
            }
        }
        catch (Exception error) { errors.Add("graphics: " + error.GetType().Name + ": " + error.Message); }
        return value;
    }

    private static List<CameraRecord> ReadCameras(List<string> errors)
    {
        var records = new List<CameraRecord>();
        foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>().Where(item => item && item.gameObject.scene.IsValid()).OrderBy(item => item.GetInstanceID()))
        {
            try { records.Add(CameraRecord.FromNative(camera)); }
            catch (Exception error) { errors.Add("camera " + camera.GetInstanceID() + ": " + error.GetType().Name + ": " + error.Message); }
        }
        return records;
    }

    private static List<CanvasRecord> ReadCanvases(List<string> errors)
    {
        var records = new List<CanvasRecord>();
        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>().Where(item => item && item.gameObject.scene.IsValid() && item.isRootCanvas && item.gameObject.activeInHierarchy).OrderBy(item => item.GetInstanceID()))
        {
            try
            {
                var scaler = canvas.GetComponent<CanvasScaler>();
                var rectangle = canvas.GetComponent<RectTransform>();
                records.Add(new CanvasRecord
                {
                    name = canvas.name, instanceId = canvas.GetInstanceID(), enabled = canvas.enabled,
                    renderMode = canvas.renderMode.ToString(), scaleFactor = canvas.scaleFactor,
                    pixelRect = RectRecord.FromNative(canvas.pixelRect),
                    rect = rectangle ? RectRecord.FromNative(rectangle.rect) : null,
                    scaler = scaler ? new ScalerRecord
                    {
                        enabled = scaler.enabled, uiScaleMode = scaler.uiScaleMode.ToString(),
                        scaleFactor = scaler.scaleFactor, referenceWidth = scaler.referenceResolution.x,
                        referenceHeight = scaler.referenceResolution.y, screenMatchMode = scaler.screenMatchMode.ToString(),
                        matchWidthOrHeight = scaler.matchWidthOrHeight, referencePixelsPerUnit = scaler.referencePixelsPerUnit
                    } : null
                });
            }
            catch (Exception error) { errors.Add("canvas " + canvas.GetInstanceID() + ": " + error.GetType().Name + ": " + error.Message); }
        }
        return records;
    }

    private static List<FsrRecord> ReadFidelityFx(List<string> errors)
    {
        var records = new List<FsrRecord>();
        foreach (var component in Resources.FindObjectsOfTypeAll<FidelityFX>().Where(item => item && item.gameObject.scene.IsValid()).OrderBy(item => item.GetInstanceID()))
        {
            try
            {
                var data = Field(component, "cameraData");
                var texture = Field(component, "FSRTexture");
                var source = Field(component, "sourceCamera") as Camera;
                var target = Field(component, "targetCamera") as Camera;
                var renderTexture = texture == null ? null : Field(texture, "renderTexture") as RenderTexture;
                var descriptor = (RenderTextureDescriptor)Field(component, "m_Descriptor");
                records.Add(new FsrRecord
                {
                    name = component.name, instanceId = component.GetInstanceID(), enabled = component.enabled,
                    activeInHierarchy = component.gameObject.activeInHierarchy,
                    quality = Field(component, "FSRQuality").ToString(), oldQuality = Field(component, "oldFSRQuality").ToString(),
                    sharpenScale = (float)Field(component, "sharpenScale"), beforePostProcessing = (bool)Field(component, "FSRBeforePP"),
                    sourceCameraId = source ? source.GetInstanceID() : 0, targetCameraId = target ? target.GetInstanceID() : 0,
                    cameraDataAvailable = data != null, renderScale = data == null ? (float?)null : (float)Field(data, "renderScale"),
                    dataPixelWidth = data == null ? (int?)null : (int)Field(data, "pixelWidth"),
                    dataPixelHeight = data == null ? (int?)null : (int)Field(data, "pixelHeight"),
                    descriptorWidth = descriptor.width, descriptorHeight = descriptor.height,
                    textureQuality = texture == null ? null : Field(texture, "textureQuality").ToString(),
                    texture = TextureRecord.FromNative(renderTexture)
                });
            }
            catch (Exception error) { errors.Add("FidelityFX " + component.GetInstanceID() + ": " + error.GetType().Name + ": " + error.Message); }
        }
        return records;
    }

    private static object Field(object owner, string name)
    {
        var field = owner.GetType().GetField(name, Flags);
        if (field == null) throw new MissingFieldException(owner.GetType().FullName, name);
        return field.GetValue(owner);
    }

    [DataContract] private sealed class DisplayRecord
    {
        [DataMember] public int schemaVersion = 1;
        [DataMember] public string utc, eventName, phase, fullScreenMode;
        [DataMember] public int processId, managedThreadId, frame, screenWidth, screenHeight;
        [DataMember] public uint nativeThreadId;
        [DataMember] public bool focused, fullScreen, mapReady;
        [DataMember] public float realtime, dpi, dynamicWidthScale, dynamicHeightScale;
        [DataMember] public ResolutionRecord currentResolution;
        [DataMember] public GraphicsRecord graphics;
        [DataMember] public List<CameraRecord> cameras;
        [DataMember] public List<CanvasRecord> canvases;
        [DataMember] public List<FsrRecord> fidelityFx;
        [DataMember] public List<string> errors;
    }

    [DataContract] private sealed class ResolutionRecord
    {
        [DataMember] public int width, height, refreshRate;
        public static ResolutionRecord FromNative(Resolution value) => new ResolutionRecord { width = value.width, height = value.height, refreshRate = value.refreshRate };
    }

    [DataContract] private sealed class GraphicsRecord
    {
        [DataMember] public bool available, settingsInitialized, resolutionChanged, currentSettingsAvailable, customQuality;
        [DataMember] public ResolutionRecord internalResolution;
        [DataMember] public string internalFullScreenMode, settingResolution, settingFullScreenMode, settingFullScreenModeLatent, fsr, antiAliasing, qualityLevel;
        [DataMember] public int settingRefreshRate;
    }

    [DataContract] private sealed class RectRecord
    {
        [DataMember] public float x, y, width, height;
        public static RectRecord FromNative(Rect value) => new RectRecord { x = value.x, y = value.y, width = value.width, height = value.height };
    }

    [DataContract] private sealed class TextureRecord
    {
        [DataMember] public int instanceId, width, height, antiAliasing;
        [DataMember] public bool created;
        [DataMember] public string name, format;
        public static TextureRecord FromNative(RenderTexture value) => value ? new TextureRecord
        {
            name = value.name, instanceId = value.GetInstanceID(), width = value.width, height = value.height,
            antiAliasing = value.antiAliasing, created = value.IsCreated(), format = value.format.ToString()
        } : null;
    }

    [DataContract] private sealed class CameraRecord
    {
        [DataMember] public string name, cameraType;
        [DataMember] public int instanceId, pixelWidth, pixelHeight, scaledPixelWidth, scaledPixelHeight, targetDisplay;
        [DataMember] public bool enabled, activeInHierarchy, allowDynamicResolution, orthographic;
        [DataMember] public float depth, fieldOfView, orthographicSize;
        [DataMember] public RectRecord pixelRect;
        [DataMember] public TextureRecord targetTexture;
        public static CameraRecord FromNative(Camera value) => new CameraRecord
        {
            name = value.name, instanceId = value.GetInstanceID(), enabled = value.enabled,
            activeInHierarchy = value.gameObject.activeInHierarchy, cameraType = value.cameraType.ToString(),
            pixelWidth = value.pixelWidth, pixelHeight = value.pixelHeight,
            scaledPixelWidth = value.scaledPixelWidth, scaledPixelHeight = value.scaledPixelHeight,
            targetDisplay = value.targetDisplay, allowDynamicResolution = value.allowDynamicResolution,
            orthographic = value.orthographic, orthographicSize = value.orthographicSize, fieldOfView = value.fieldOfView,
            depth = value.depth, pixelRect = RectRecord.FromNative(value.pixelRect), targetTexture = TextureRecord.FromNative(value.targetTexture)
        };
    }

    [DataContract] private sealed class ScalerRecord
    {
        [DataMember] public bool enabled;
        [DataMember] public string uiScaleMode, screenMatchMode;
        [DataMember] public float scaleFactor, referenceWidth, referenceHeight, matchWidthOrHeight, referencePixelsPerUnit;
    }

    [DataContract] private sealed class CanvasRecord
    {
        [DataMember] public string name, renderMode;
        [DataMember] public int instanceId;
        [DataMember] public bool enabled;
        [DataMember] public float scaleFactor;
        [DataMember] public RectRecord pixelRect, rect;
        [DataMember] public ScalerRecord scaler;
    }

    [DataContract] private sealed class FsrRecord
    {
        [DataMember] public string name, quality, oldQuality, textureQuality;
        [DataMember] public int instanceId, sourceCameraId, targetCameraId, descriptorWidth, descriptorHeight;
        [DataMember] public bool enabled, activeInHierarchy, beforePostProcessing, cameraDataAvailable;
        [DataMember] public float sharpenScale;
        [DataMember] public float? renderScale;
        [DataMember] public int? dataPixelWidth, dataPixelHeight;
        [DataMember] public TextureRecord texture;
    }
}

public sealed class DisplayStateProbeObserver : MonoBehaviour
{
    private void Update() => DisplayStateProbe.Heartbeat();
}
