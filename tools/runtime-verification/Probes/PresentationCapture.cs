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
    const int NativePathBudget=259;
    public static void AssertPathBudget(string path,int maximum)
    {
        if(string.IsNullOrEmpty(path)||!Path.IsPathRooted(path)||maximum<1)
            throw new ArgumentException("A full output path and positive path budget are required.");
        if(path.Length>maximum)
            throw new PathTooLongException("The output needs "+path.Length+" UTF-16 characters; the path budget is "+maximum+": "+path);
    }

    public sealed class PayloadReceipt
    {
        public string path { get; private set; }
        public string sha256 { get; private set; }
        public long bytes { get; private set; }
        internal PayloadReceipt(string path,string sha256,long bytes) { this.path=path;this.sha256=sha256;this.bytes=bytes; }
    }

    public sealed class JsonPayloadStore
    {
        readonly string directory;
        readonly int maximumPathLength;
        readonly object gate=new object();
        readonly Dictionary<string,PayloadReceipt> files=new Dictionary<string,PayloadReceipt>(StringComparer.Ordinal);
        int sequence;

        public JsonPayloadStore(string directory,int maximumPathLength)
        {
            if(string.IsNullOrEmpty(directory)||!Path.IsPathRooted(directory)||maximumPathLength<1)
                throw new ArgumentException("A full payload directory and positive path budget are required.");
            AssertPathBudget(directory,maximumPathLength);
            this.directory=Path.GetFullPath(directory);this.maximumPathLength=maximumPathLength;
        }
        public PayloadReceipt Write(string category,byte[] bytes)
        {
            if(string.IsNullOrEmpty(category)||category.Any(c=>!char.IsLetterOrDigit(c)&&c!='-'&&c!='_')||bytes==null)
                throw new ArgumentException("A payload category and serialized JSON bytes are required.");
            string identity;
            using(var sha=SHA256.Create())identity=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
            lock(gate)
            {
                PayloadReceipt known;
                if(files.TryGetValue(identity,out known))
                {
                    if(!File.Exists(known.path)||new FileInfo(known.path).Length!=known.bytes)
                        throw new IOException("A published payload is missing or has changed: "+known.path);
                    return known;
                }
                string path,temporary;
                do
                {
                    sequence=checked(sequence+1);
                    path=Path.Combine(directory,category+"-"+sequence.ToString("D4",System.Globalization.CultureInfo.InvariantCulture)+".json");
                    temporary=path+".writing";
                    AssertPathBudget(path,maximumPathLength);AssertPathBudget(temporary,maximumPathLength);
                }while(File.Exists(path)||Directory.Exists(path)||File.Exists(temporary)||Directory.Exists(temporary));
                Directory.CreateDirectory(directory);
                var created=false;
                try
                {
                    using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {created=true;stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                    File.Move(temporary,path);created=false;
                }
                finally { if(created&&File.Exists(temporary))File.Delete(temporary); }
                var receipt=new PayloadReceipt(path,identity,bytes.LongLength);files.Add(identity,receipt);return receipt;
            }
        }
    }

    public static void FrameSubjects(Camera camera,IEnumerable<Renderer> subjects,float margin=1.1f)
    {
        RuntimeVerificationHarness.AssertMainThread();
        if(!camera||!camera.orthographic||subjects==null)throw new ArgumentException("Orthographic surface framing requires a camera and subjects.");
        var renderers=subjects.Where(renderer=>renderer&&(renderer is MeshRenderer||renderer is SkinnedMeshRenderer)&&
            renderer.enabled&&renderer.gameObject.activeInHierarchy&&!renderer.forceRenderingOff&&
            (camera.cullingMask&(1<<renderer.gameObject.layer))!=0).ToArray();
        if(renderers.Length==0)throw new InvalidOperationException("No visible subjects to frame.");
        var bounds=renderers[0].bounds;
        foreach(var renderer in renderers)
        {
            var value=renderer.bounds;var center=value.center;var extent=value.extents;
            if(!Finite(center.x)||!Finite(center.y)||!Finite(center.z)||!Finite(extent.x)||!Finite(extent.y)||!Finite(extent.z)||
                extent.x<0||extent.y<0||extent.z<0)throw new InvalidOperationException("A surface subject has invalid world bounds.");
            bounds.Encapsulate(value);
        }
        var minimum=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);
        var maximum=new Vector3(float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity);
        for(var x=-1;x<=1;x+=2)for(var y=-1;y<=1;y+=2)for(var z=-1;z<=1;z+=2)
        {
            var relative=bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z))-camera.transform.position;
            var local=new Vector3(Vector3.Dot(relative,camera.transform.right),Vector3.Dot(relative,camera.transform.up),Vector3.Dot(relative,camera.transform.forward));
            minimum=Vector3.Min(minimum,local);maximum=Vector3.Max(maximum,local);
        }
        var frame=CalculateOrthographicFrame(minimum,maximum,camera.aspect,camera.nearClipPlane,camera.farClipPlane,margin);
        camera.transform.position+=camera.transform.TransformDirection(frame.shift);
        camera.orthographicSize=frame.size;
    }
    private struct OrthographicFrame
    {
        public Vector3 shift;
        public float size;
    }
    private static OrthographicFrame CalculateOrthographicFrame(Vector3 minimum,Vector3 maximum,float aspect,float near,float far,float margin)
    {
        if(!Finite(minimum.x)||!Finite(minimum.y)||!Finite(minimum.z)||!Finite(maximum.x)||!Finite(maximum.y)||!Finite(maximum.z)||
            !Finite(aspect)||aspect<=0||!Finite(near)||near<=0||!Finite(far)||far<=near||!Finite(margin)||margin<1f)
            throw new ArgumentException("Surface framing requires finite bounds, a positive aspect and a valid clip interval.");
        var size=maximum-minimum;var center=minimum+size*.5f;
        var halfDepth=size.z*.5f*margin;
        var orthographicSize=Mathf.Max(size.y*.5f,size.x*.5f/aspect)*margin;
        if(size.x<0||size.y<0||size.z<0||!Finite(halfDepth)||halfDepth*2>=far-near||!Finite(orthographicSize)||orthographicSize<=0)
            throw new InvalidOperationException("The surface bounds cannot fit the requested orthographic clip interval.");
        var depth=center.z;
        if(depth-halfDepth<=near||depth+halfDepth>=far)depth=near+(far-near)*.5f;
        var shift=new Vector3(center.x,center.y,center.z-depth);
        if(!Finite(shift.x)||!Finite(shift.y)||!Finite(shift.z))throw new InvalidOperationException("The surface framing offset is not finite.");
        return new OrthographicFrame{shift=shift,size=orthographicSize};
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
        public string id,path,label,group,phase,sha256;
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
        foreach(var frame in document.frames)
        {
            var framePath=Path.IsPathRooted(frame.path)?frame.path:
                Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory,frame.path);
            if(string.IsNullOrEmpty(frame.sha256)&&File.Exists(framePath))
                using(var stream=File.OpenRead(framePath))
                using(var hash=SHA256.Create())
                    frame.sha256=BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
        }
        var path=Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory,"PresentationEvidence-"+name+".json");
        AssertPathBudget(path,NativePathBudget);
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
        int warmupFrames = 0, Action onCaptured = null, Action onBeforeRender = null)
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

        var imagePath = Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, name + ".png");
        var receiptPath = Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, name + ".capture.json");
        AssertPathBudget(imagePath,NativePathBudget);AssertPathBudget(receiptPath,NativePathBudget);
        if (File.Exists(imagePath) || Directory.Exists(imagePath) || File.Exists(receiptPath) || Directory.Exists(receiptPath))
            throw new InvalidOperationException("Capture evidence already exists: " + name);

        var receipt = new Receipt { name = name, requestFrame = Time.frameCount,
            settleFrames = settleFrames, warmupFrames = warmupFrames, width = width, height = height };
        FileStream receiptStream = null;
        var receiptWritten = false;
        try
        {
            try { receiptStream = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException error) { throw new InvalidOperationException("Capture evidence could not be reserved: " + name, error); }
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
                onBeforeRender?.Invoke();
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
                using (var stream = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes, 0, bytes.Length);
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
                new DataContractJsonSerializer(typeof(Receipt)).WriteObject(receiptStream, receipt);
                receiptStream.Flush();
                receiptWritten = true;
                receiptStream.Dispose();
                receiptStream = null;
            }
            completed?.Invoke(receipt);
            if (failure != null) throw new InvalidOperationException("Presentation capture failed: " + name, failure);
        }
        finally
        {
            if (receiptStream != null)
            {
                try
                {
                    if (!receiptWritten)
                    {
                        if (string.IsNullOrEmpty(receipt.error)) receipt.error = "Presentation capture did not complete.";
                        new DataContractJsonSerializer(typeof(Receipt)).WriteObject(receiptStream, receipt);
                    }
                }
                finally { receiptStream.Dispose(); }
            }
        }
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
