using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using UnityEngine;

// Optional shared support compiled into a temporary probe, never into UB.
public static class PresentationCapture
{
    public static void FrameSubjects(Camera camera,IEnumerable<Renderer> subjects,float margin=1.1f)
    {
        if(!camera||!camera.orthographic||margin<1f)throw new ArgumentException("Orthographic framing requires a camera and margin >= 1.");
        var renderers=subjects.Where(renderer=>renderer&&renderer.enabled&&renderer.gameObject.activeInHierarchy&&!renderer.forceRenderingOff).ToArray();
        if(renderers.Length==0)throw new InvalidOperationException("No visible subjects to frame.");
        var bounds=renderers[0].bounds;foreach(var renderer in renderers.Skip(1))bounds.Encapsulate(renderer.bounds);
        camera.transform.position+=bounds.center-camera.transform.position-Vector3.Project(bounds.center-camera.transform.position,camera.transform.forward);
        var minimum=new Vector2(float.PositiveInfinity,float.PositiveInfinity);var maximum=new Vector2(float.NegativeInfinity,float.NegativeInfinity);
        for(var x=-1;x<=1;x+=2)for(var y=-1;y<=1;y+=2)for(var z=-1;z<=1;z+=2)
        {
            var local=camera.transform.InverseTransformPoint(bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z)));
            minimum=Vector2.Min(minimum,new Vector2(local.x,local.y));maximum=Vector2.Max(maximum,new Vector2(local.x,local.y));
        }
        var size=maximum-minimum;camera.orthographicSize=Mathf.Max(size.y*.5f,size.x*.5f/camera.aspect)*margin;
    }

    [Serializable] public sealed class ReviewDocument
    {
        public int schemaVersion=1;
        public string title;
        public List<ReviewFrame> frames=new List<ReviewFrame>();
        public List<ReviewCheck> checks=new List<ReviewCheck>();
        public List<ReviewSource> sources=new List<ReviewSource>();
    }
    [Serializable] public sealed class ReviewFrame
    {
        public string id,path,label,group,phase;
    }
    [Serializable] public sealed class ReviewCheck
    {
        public string id,title,status,expected,actual;
        public string[] evidence=new string[0];
    }
    [Serializable] public sealed class ReviewSource
    {
        public string path,sha256,role;
    }
    public static void WriteReviewEvidence(string name,ReviewDocument document)
    {
        if(string.IsNullOrEmpty(name)||name!=Path.GetFileName(name)||name.Any(c=>!char.IsLetterOrDigit(c)&&c!='-'&&c!='_'))
            throw new ArgumentException("Invalid review document name.","name");
        if(document==null||document.schemaVersion!=1||string.IsNullOrWhiteSpace(document.title))
            throw new ArgumentException("Invalid review document.","document");
        var ids=new HashSet<string>();
        foreach(var frame in document.frames)
            if(frame==null||string.IsNullOrWhiteSpace(frame.id)||string.IsNullOrWhiteSpace(frame.path)||!ids.Add(frame.id))
                throw new InvalidDataException("Invalid or duplicate review frame.");
        var checks=new HashSet<string>();
        foreach(var check in document.checks)
            if(check==null||string.IsNullOrWhiteSpace(check.id)||string.IsNullOrWhiteSpace(check.title)||!checks.Add(check.id)||
                !new[]{"PASS","FAIL","REVIEW","NOT_RECORDED"}.Contains(check.status)||
                check.evidence==null||check.evidence.Any(id=>!ids.Contains(id)))
                throw new InvalidDataException("Invalid review check.");
        var path=Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory,"PresentationEvidence-"+name+".json");
        using(var stream=File.Create(path))new DataContractJsonSerializer(typeof(ReviewDocument)).WriteObject(stream,document);
    }

    [Serializable]
    public sealed class Receipt
    {
        public string scope = "game-camera-capture", name, utc, sha256, error;
        public int requestFrame, capturedFrame, settleFrames, width, height, variedPixels;
        public int warmupFrames, warmedFrames, warmupStartFrame, warmupEndFrame, observationFrame;
        public bool accepted;
        public float[] cameraPosition, cameraRotation;
        public float nearClip, farClip, fieldOfView, orthographicSize;
        public bool orthographic;
        public int cullingMask;
        public Subject[] subjects;
    }

    [Serializable]
    public sealed class Subject
    {
        public string name;
        public bool active, enabled, forcedOff, inFrustum, includedLayer;
        public int layer;
        public float[] center, size;
        public MaterialState[] materials;
    }

    [Serializable]
    public sealed class MaterialState
    {
        public string name, shader, texture;
        public int textureWidth, textureHeight;
        public float[] color;
        public float[] emission;
        public float glossiness;
        public bool emissionEnabled;
        public int renderQueue;
    }

    [Serializable]
    public sealed class SphereSurfaceContact
    {
        public string scope = "baked-skinned-triangle-surface-distance";
        public int meshes, triangles, intersectingTriangles;
        public float radius, minimumSurfaceDistance, surfaceGap;
        public float[] center, closestPoint;
    }

    public static IEnumerator Capture(Camera camera, Renderer[] subjects, string name,
        int width, int height, Action<Receipt> completed = null, int settleFrames = 2,
        int warmupFrames = 0, Action onCaptured = null)
    {
        RuntimeVerificationHarness.AssertMainThread();
        if (!camera || subjects == null || subjects.Length == 0 || subjects.Any(subject => !subject))
            throw new ArgumentException("A camera and existing subject renderers are required.");
        if (width < 32 || height < 32 || width > 4096 || height > 4096 || settleFrames < 1 || settleFrames > 30 ||
            warmupFrames < 0 || warmupFrames > 30)
            throw new ArgumentOutOfRangeException("Capture dimensions or settling frames are out of range.");
        if (string.IsNullOrEmpty(name) || name.Length > 64 ||
            name.Any(character => !(char.IsLetterOrDigit(character) || character == '-' || character == '_')))
            throw new ArgumentException("Capture name must contain at most 64 letters, digits, underscores or hyphens.");

        var receipt = new Receipt { name = name, requestFrame = Time.frameCount,
            settleFrames = settleFrames, warmupFrames = warmupFrames, width = width, height = height };
        for (var frame = 0; frame < settleFrames; frame++) yield return null;
        yield return new WaitForEndOfFrame();
        if (warmupFrames > 0) yield return WarmCamera(camera, name, width, height, receipt);
        RuntimeVerificationHarness.AssertMainThread();
        receipt.capturedFrame = Time.frameCount;
        receipt.utc = DateTime.UtcNow.ToString("O");
        var previousActive = RenderTexture.active;
        var previousTarget = camera.targetTexture;
        var previousEnabled = camera.enabled;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        Texture2D image = null;
        Exception failure = null;
        try
        {
            camera.enabled = false;
            camera.targetTexture = target;
            receipt.cameraPosition = Vector(camera.transform.position);
            receipt.cameraRotation = new[] { camera.transform.rotation.x, camera.transform.rotation.y,
                camera.transform.rotation.z, camera.transform.rotation.w };
            receipt.nearClip = camera.nearClipPlane;
            receipt.farClip = camera.farClipPlane;
            receipt.fieldOfView = camera.fieldOfView;
            receipt.orthographicSize = camera.orthographicSize;
            receipt.orthographic = camera.orthographic;
            receipt.cullingMask = camera.cullingMask;
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            receipt.subjects = subjects.Select(renderer => new Subject {
                name = renderer.name, active = renderer.gameObject.activeInHierarchy, enabled = renderer.enabled,
                forcedOff = renderer.forceRenderingOff, layer = renderer.gameObject.layer,
                includedLayer = (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0,
                inFrustum = GeometryUtility.TestPlanesAABB(planes, renderer.bounds),
                center = Vector(renderer.bounds.center), size = Vector(renderer.bounds.size),
                materials = renderer.sharedMaterials.Select(material => {
                    if (!material) return new MaterialState { name = "<null>" };
                    var texture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
                    return new MaterialState { name = material.name, shader = material.shader ? material.shader.name : null,
                        texture = texture ? texture.name : null, textureWidth = texture ? texture.width : 0,
                        textureHeight = texture ? texture.height : 0,
                        color = material.HasProperty("_Color") ? ColorValues(material.GetColor("_Color")) : null,
                        emission = material.HasProperty("_EmissionColor") ? ColorValues(material.GetColor("_EmissionColor")) : null,
                        glossiness = material.HasProperty("_Glossiness") ? material.GetFloat("_Glossiness") : 0,
                        emissionEnabled = material.IsKeywordEnabled("_EMISSION"), renderQueue = material.renderQueue };
                }).ToArray()
            }).ToArray();
            if (!receipt.subjects.Any(subject => subject.active && subject.enabled && !subject.forcedOff &&
                                                subject.includedLayer && subject.inFrustum))
                throw new InvalidOperationException("No requested subject can be drawn by the capture camera.");
            camera.Render();
            if (onCaptured != null)
            {
                onCaptured();
                receipt.observationFrame = Time.frameCount;
            }
            RenderTexture.active = target;
            image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            var pixels = image.GetPixels32();
            var first = pixels[0];
            receipt.variedPixels = pixels.Count(pixel => Math.Abs(pixel.r - first.r) + Math.Abs(pixel.g - first.g) +
                                                        Math.Abs(pixel.b - first.b) > 6);
            var bytes = image.EncodeToPNG();
            File.WriteAllBytes(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, name + ".png"), bytes);
            using (var hash = SHA256.Create())
                receipt.sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
            receipt.accepted = receipt.variedPixels > 0 && receipt.capturedFrame > receipt.requestFrame;
            if (!receipt.accepted) throw new InvalidOperationException("The captured image is uniform or predates frame settling.");
        }
        catch (Exception error)
        {
            receipt.error = error.ToString();
            failure = error;
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (camera) { camera.targetTexture = previousTarget; camera.enabled = previousEnabled; }
            if (image) UnityEngine.Object.Destroy(image);
            target.Release();
            UnityEngine.Object.Destroy(target);
            using (var stream = File.Create(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, name + ".capture.json")))
                new DataContractJsonSerializer(typeof(Receipt)).WriteObject(stream, receipt);
        }
        completed?.Invoke(receipt);
        if (failure != null) throw new InvalidOperationException("Presentation capture failed: " + name, failure);
    }

    private static IEnumerator WarmCamera(Camera camera, string name, int width, int height, Receipt receipt)
    {
        var previousTarget = camera.targetTexture;
        var previousEnabled = camera.enabled;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var completed = false;
        try
        {
            if (!camera.gameObject.activeInHierarchy || !target.Create())
                throw new InvalidOperationException("Capture warmup requires an active camera and a RenderTexture.");
            camera.targetTexture = target;
            camera.enabled = true;
            receipt.warmupStartFrame = Time.frameCount;
            // A disabled manual camera does not keep off-screen skins visible during
            // the ordinary player loop. Let this owned camera render naturally.
            for (var frame = 0; frame < receipt.warmupFrames; frame++)
            {
                yield return null;
                yield return new WaitForEndOfFrame();
                receipt.warmedFrames++;
            }
            receipt.warmupEndFrame = Time.frameCount;
            completed = true;
        }
        finally
        {
            if (camera) { camera.targetTexture = previousTarget; camera.enabled = previousEnabled; }
            target.Release();
            UnityEngine.Object.Destroy(target);
            if (!completed)
            {
                receipt.error = "Capture camera warmup did not complete.";
                using (var stream = File.Create(Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, name + ".capture.json")))
                    new DataContractJsonSerializer(typeof(Receipt)).WriteObject(stream, receipt);
            }
        }
    }

    public static SphereSurfaceContact MeasureSphereSurface(SkinnedMeshRenderer[] renderers,
        Vector3 center, float radius, Transform[] influencingBones = null, float minimumWeight = 0.01f)
    {
        RuntimeVerificationHarness.AssertMainThread();
        if (renderers == null || renderers.Length == 0 || renderers.Any(renderer => !renderer) ||
            radius <= 0 || !Finite(radius) || !Finite(center.x) || !Finite(center.y) || !Finite(center.z) ||
            !Finite(minimumWeight) || minimumWeight < 0 || minimumWeight > 1)
            throw new ArgumentException("Existing skins, positive sphere radius and a valid bone weight are required.");
        var result = new SphereSurfaceContact { radius = radius, center = Vector(center),
            minimumSurfaceDistance = float.PositiveInfinity };
        foreach (var renderer in renderers)
        {
            var mesh = new Mesh();
            try
            {
                renderer.BakeMesh(mesh);
                // This Unity version includes the skin's scale in baked vertices.
                var world = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                var bones = renderer.bones;
                var selected = influencingBones == null ? null : new HashSet<int>(Enumerable.Range(0, bones.Length)
                    .Where(index => influencingBones.Any(bone => ReferenceEquals(bone, bones[index]))));
                if (selected != null && selected.Count == 0) continue;
                var weights = selected == null ? null : renderer.sharedMesh.boneWeights;
                if (selected != null && weights.Length != vertices.Length)
                    throw new InvalidOperationException("The skin does not expose matching vertex bone weights.");
                result.meshes++;
                for (var index = 0; index < triangles.Length; index += 3)
                {
                    if (selected != null && !Influenced(weights[triangles[index]], selected, minimumWeight) &&
                        !Influenced(weights[triangles[index + 1]], selected, minimumWeight) &&
                        !Influenced(weights[triangles[index + 2]], selected, minimumWeight)) continue;
                    var a = world.MultiplyPoint3x4(vertices[triangles[index]]);
                    var b = world.MultiplyPoint3x4(vertices[triangles[index + 1]]);
                    var c = world.MultiplyPoint3x4(vertices[triangles[index + 2]]);
                    var point = ClosestPoint(center, a, b, c);
                    var distance = Vector3.Distance(center, point);
                    result.triangles++;
                    if (distance < radius) result.intersectingTriangles++;
                    if (distance >= result.minimumSurfaceDistance) continue;
                    result.minimumSurfaceDistance = distance;
                    result.closestPoint = Vector(point);
                }
            }
            finally { UnityEngine.Object.Destroy(mesh); }
        }
        if (result.triangles == 0) throw new InvalidOperationException("No selected skin triangles were measured.");
        result.surfaceGap = result.minimumSurfaceDistance - radius;
        return result;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool Influenced(BoneWeight weight, HashSet<int> selected, float minimum) =>
        weight.weight0 > minimum && selected.Contains(weight.boneIndex0) ||
        weight.weight1 > minimum && selected.Contains(weight.boneIndex1) ||
        weight.weight2 > minimum && selected.Contains(weight.boneIndex2) ||
        weight.weight3 > minimum && selected.Contains(weight.boneIndex3);

    // Closest point on a triangle, including degenerate imported faces.
    public static Vector3 ClosestPoint(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a;
        if (Vector3.Cross(ab, ac).sqrMagnitude < 1e-20f)
        {
            var candidates = new[] { ClosestSegment(point, a, b), ClosestSegment(point, b, c), ClosestSegment(point, c, a) };
            return candidates.OrderBy(candidate => (candidate - point).sqrMagnitude).First();
        }
        var ap = point - a; var d1 = Vector3.Dot(ab, ap); var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = point - b; var d3 = Vector3.Dot(ab, bp); var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = point - c; var d5 = Vector3.Dot(ab, cp); var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        var denominator = 1f / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }

    private static Vector3 ClosestSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var edge = b - a;
        return a + edge * (edge.sqrMagnitude > 0 ? Mathf.Clamp01(Vector3.Dot(point - a, edge) / edge.sqrMagnitude) : 0);
    }

    private static float[] Vector(Vector3 vector) => new[] { vector.x, vector.y, vector.z };
    private static float[] ColorValues(Color color) => new[] { color.r, color.g, color.b, color.a };
}
