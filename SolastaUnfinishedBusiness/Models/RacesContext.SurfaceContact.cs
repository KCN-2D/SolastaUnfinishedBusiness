using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using static GraphicsCharacterDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static partial class RacesContext
{
    private static int[] SurfaceTriangles(Mesh mesh, SkinnedMeshRenderer renderer, bool skin)
    {
        var materials = renderer.sharedMaterials;
        var selected = Enumerable.Range(0, Math.Min(mesh.subMeshCount, materials.Length))
            .Where(slot => IsJointSkinMaterial(materials[slot]) == skin && materials[slot] &&
                           materials[slot].renderQueue < (int)UnityEngine.Rendering.RenderQueue.Transparent)
            .ToArray();
        if (selected.Length == 0) return [];
        if (selected.Length == mesh.subMeshCount) return ReadBodyShapeRest(mesh).Triangles;
        return ReadNativeRestMesh(mesh, readable => selected
            .Where(slot => readable.GetTopology(slot) == MeshTopology.Triangles)
            .SelectMany(slot => readable.GetTriangles(slot)).ToArray());
    }

    private static bool SharesDetailLevel(LOD[] levels, SkinnedMeshRenderer first, SkinnedMeshRenderer second)
    {
        var hasFirst = false;
        var hasSecond = false;
        foreach (var level in levels)
        {
            var containsFirst = level.renderers.Contains(first);
            var containsSecond = level.renderers.Contains(second);
            if (containsFirst && containsSecond) return true;
            hasFirst |= containsFirst;
            hasSecond |= containsSecond;
        }
        return !hasFirst || !hasSecond;
    }

    private sealed class ContactSurface
    {
        private const float CellSize = .032f;
        private readonly float _reach;
        private readonly Dictionary<Vector3Int, List<int>> _cells = [];
        private readonly List<Triangle> _triangles = [];

        private readonly struct Triangle(Vector3 origin, Vector3 first, Vector3 second, Vector3 normal)
        {
            internal readonly Vector3 Origin = origin;
            internal readonly Vector3 First = first;
            internal readonly Vector3 Second = second;
            internal readonly Vector3 Normal = normal;
            internal readonly float FirstSquared = first.sqrMagnitude;
            internal readonly float SecondSquared = second.sqrMagnitude;
            internal readonly float Product = Vector3.Dot(first, second);
        }

        internal ContactSurface(Vector3[] points, int[] indices, float reach, Bounds? region = null)
        {
            _reach = reach;
            var margin = Vector3.one * reach;
            for (var index = 0; index < indices.Length; index += 3)
            {
                var a = points[indices[index]];
                var b = points[indices[index + 1]];
                var c = points[indices[index + 2]];
                var minimum = Vector3.Min(a, Vector3.Min(b, c)) - margin;
                var maximum = Vector3.Max(a, Vector3.Max(b, c)) + margin;
                if (region is { } bounds)
                {
                    minimum = Vector3.Max(minimum, bounds.min);
                    maximum = Vector3.Min(maximum, bounds.max);
                    if (minimum.x > maximum.x || minimum.y > maximum.y || minimum.z > maximum.z) continue;
                }
                var cross = Vector3.Cross(b - a, c - a);
                var area = cross.magnitude;
                if (area < 1e-9f) continue;
                var low = Cell(minimum);
                var high = Cell(maximum);
                var ordinal = _triangles.Count;
                _triangles.Add(new Triangle(a, b - a, c - a, cross / area));
                for (var x = low.x; x <= high.x; x++)
                for (var y = low.y; y <= high.y; y++)
                for (var z = low.z; z <= high.z; z++)
                {
                    var key = new Vector3Int(x, y, z);
                    if (!_cells.TryGetValue(key, out var triangles)) _cells.Add(key, triangles = []);
                    triangles.Add(ordinal);
                }
            }
        }

        private static Vector3Int Cell(Vector3 point) => Vector3Int.FloorToInt(point / CellSize);

        internal bool Near(Vector3 point) => _cells.ContainsKey(Cell(point));
        internal bool IsEmpty => _cells.Count == 0;

        internal bool Project(Vector3 point, Vector3 direction, out float depth, out Vector3 normal) =>
            ProjectSurface(point, direction, 0f, out depth, out normal);

        internal bool ProjectOutermost(Vector3 point, Vector3 direction, float reach, out float depth) =>
            ProjectSurface(point, direction, reach, out depth, out _);

        private bool ProjectSurface(Vector3 point, Vector3 direction, float outermostReach, out float depth, out Vector3 normal)
        {
            var outermost = outermostReach > 0f;
            depth = outermost ? Mathf.Min(outermostReach, _reach) : _reach;
            normal = Vector3.zero;
            if (!_cells.TryGetValue(Cell(point), out var candidates)) return false;
            var useDirection = direction.sqrMagnitude > 0f;
            foreach (var index in candidates)
            {
                var triangle = _triangles[index];
                var ray = useDirection ? direction : triangle.Normal;
                var alignment = Vector3.Dot(triangle.Normal, ray);
                if (alignment < (outermost ? .35f : .55f)) continue;
                var offset = point - triangle.Origin;
                var distance = Vector3.Dot(offset, triangle.Normal) / alignment;
                if (outermost ? distance < -outermostReach || distance >= depth : Mathf.Abs(distance) >= Mathf.Abs(depth))
                    continue;
                var plane = offset - ray * distance;
                var alongFirst = Vector3.Dot(triangle.First, plane);
                var alongSecond = Vector3.Dot(triangle.Second, plane);
                var u = alongFirst * triangle.SecondSquared - alongSecond * triangle.Product;
                var v = alongSecond * triangle.FirstSquared - alongFirst * triangle.Product;
                var determinant = triangle.FirstSquared * triangle.SecondSquared - triangle.Product * triangle.Product;
                if (determinant <= 0f) continue;
                var error = determinant * .00001f;
                if (u < -error || v < -error || u + v > determinant + error) continue;
                depth = distance;
                normal = triangle.Normal;
            }
            return normal != Vector3.zero;
        }
    }

    private const float WardrobeClearance = .0008f;
    private const float WardrobeReach = .024f;
    private const float CollarMotionClearance = .010f;

    private static (Vector3[] Positive, Vector3[] Negative) WardrobeClearanceFrames(
        BodyShapeRenderer skin, Mesh mesh, IReadOnlyList<(BodyShapeRenderer Row, Mesh Mesh)> wardrobe, float neutral)
    {
        var triangles = SurfaceTriangles(mesh, skin.Renderer, true);
        if (triangles.Length == 0) return default;
        var rest = ReadBodyShapeRest(mesh);
        var neutralSkin = BodySurfaceAt(mesh, rest, skin.UseNativeFrame, neutral, neutral);
        var region = new Bounds(neutralSkin[triangles[0]], Vector3.zero);
        foreach (var index in triangles) region.Encapsulate(neutralSkin[index]);
        region.Expand(WardrobeReach * 2f);
        var neutralNormals = rest.Normals;
        var upperSkin = neutral < 100f ? BodySurfaceAt(mesh, rest, skin.UseNativeFrame, 100f, neutral) : null;
        var lowerSkin = neutral > 0f ? BodySurfaceAt(mesh, rest, skin.UseNativeFrame, 0f, neutral) : null;
        var upperCorrection = new float[rest.Vertices.Length];
        var lowerCorrection = new float[rest.Vertices.Length];
        foreach (var (row, garment) in wardrobe)
        {
            var garmentTriangles = SurfaceTriangles(garment, row.Renderer, false);
            if (garmentTriangles.Length == 0) continue;
            var garmentRest = ReadBodyShapeRest(garment);
            if (!TryWardrobeFrame(row.Renderer, garmentRest, skin, rest, out var toSkin)) continue;
            Vector3[] GarmentAt(float value) => BodySurfaceAt(garment, garmentRest, row.UseNativeFrame, value, neutral)
                .Select(toSkin.MultiplyPoint3x4).ToArray();
            var neutralGarment = new ContactSurface(GarmentAt(neutral), garmentTriangles, WardrobeReach, region);
            if (neutralGarment.IsEmpty) continue;
            Check(upperSkin, 100f, upperCorrection);
            Check(lowerSkin, 0f, lowerCorrection);

            void Check(Vector3[] posed, float endpoint, float[] correction)
            {
                if (posed == null) return;
                var endpointGarment = new ContactSurface(GarmentAt(endpoint), garmentTriangles, WardrobeReach, region);
                if (endpointGarment.IsEmpty) return;
                var sampled = new bool[rest.Vertices.Length];
                for (var index = 0; index < triangles.Length; index += 3)
                {
                    var a = triangles[index];
                    var b = triangles[index + 1];
                    var c = triangles[index + 2];
                    if (!neutralGarment.Near(neutralSkin[a]) && !neutralGarment.Near(neutralSkin[b]) &&
                        !neutralGarment.Near(neutralSkin[c])) continue;
                    CheckVertex(a);
                    CheckVertex(b);
                    CheckVertex(c);
                    // Large collar triangles can cross beneath the armour without a visible vertex.
                    var middle = Clearance((neutralSkin[a] + neutralSkin[b] + neutralSkin[c]) / 3f,
                        (posed[a] + posed[b] + posed[c]) / 3f,
                        (neutralNormals[a] + neutralNormals[b] + neutralNormals[c]).normalized);
                    correction[a] = Mathf.Max(correction[a], middle);
                    correction[b] = Mathf.Max(correction[b], middle);
                    correction[c] = Mathf.Max(correction[c], middle);
                    CheckPatch(a, b, c);
                    CheckPatch(b, c, a);
                    CheckPatch(c, a, b);
                }

                void CheckPatch(int near, int first, int last)
                {
                    var previous = (neutralSkin[near] * 4f + neutralSkin[first] + neutralSkin[last]) / 6f;
                    var current = (posed[near] * 4f + posed[first] + posed[last]) / 6f;
                    var direction = (neutralNormals[near] * 4f + neutralNormals[first] + neutralNormals[last]).normalized;
                    var offset = Clearance(previous, current, direction);
                    correction[near] = Mathf.Max(correction[near], offset);
                    correction[first] = Mathf.Max(correction[first], offset);
                    correction[last] = Mathf.Max(correction[last], offset);
                }

                void CheckVertex(int vertex)
                {
                    if (sampled[vertex]) return;
                    sampled[vertex] = true;
                    correction[vertex] = Mathf.Max(correction[vertex],
                        Clearance(neutralSkin[vertex], posed[vertex], neutralNormals[vertex].normalized));
                }

                float Clearance(Vector3 from, Vector3 to, Vector3 direction)
                {
                    var clearance = WardrobeClearance + (skin.HeadAttachment ? CollarMotionClearance : 0f);
                    if (!endpointGarment.Project(to, direction, out var current, out _) ||
                        current < -clearance + .0003f ||
                        !neutralGarment.Project(from, direction, out var previous, out _) || previous > -.0003f ||
                        current - previous < .0003f)
                        return 0f;
                    // Head rotation consumes some of the neutral collar clearance.
                    // Limit the correction to the growth contributed by the slider.
                    return Mathf.Min(Mathf.Min(current + clearance, current - previous), .016f);
                }
            }
        }
        return (TurnInward(upperCorrection), TurnInward(lowerCorrection));

        Vector3[] TurnInward(float[] correction)
        {
            if (!correction.Any(value => value > 0f)) return null;
            var welded = new Dictionary<Vector3, int>();
            var groups = rest.Vertices.Select((point, index) =>
            {
                if (!welded.TryGetValue(point, out var primary)) welded.Add(point, primary = index);
                correction[primary] = Mathf.Max(correction[primary], correction[index]);
                return primary;
            }).ToArray();
            var eased = (float[])correction.Clone();
            foreach (var (first, last) in BodyShapeEdges(triangles, groups).Keys)
            {
                var fade = Vector3.Distance(rest.Vertices[first], rest.Vertices[last]) * .8f;
                eased[first] = Mathf.Max(eased[first], correction[last] - fade);
                eased[last] = Mathf.Max(eased[last], correction[first] - fade);
            }
            return rest.Vertices.Select((_, index) =>
                -neutralNormals[groups[index]].normalized * eased[groups[index]]).ToArray();
        }
    }

    private static bool TryWardrobeFrame(SkinnedMeshRenderer garment, BodyShapeRest garmentRest,
        BodyShapeRenderer skin, BodyShapeRest skinRest, out Matrix4x4 toSkin)
    {
        var garmentBones = garment.bones;
        var skinBones = skin.Renderer.bones;
        if (skin.HeadAttachment)
        {
            // A high collar can use a distinct head bind pose. The neck defines its shared torso frame.
            var neck = Array.FindIndex(skinBones, bone => bone && bone.name == "Bip001 Neck");
            var collar = neck < 0 ? -1 : Array.IndexOf(garmentBones, skinBones[neck]);
            if (collar >= 0 && collar < garmentRest.BindPoses.Length && neck < skinRest.BindPoses.Length)
            {
                toSkin = skinRest.BindPoses[neck].inverse * garmentRest.BindPoses[collar];
                return BodyShapeFinite(toSkin.determinant) && Mathf.Abs(toSkin.determinant) > 1e-8f;
            }
        }
        return TrySkinPaletteFrame(garmentBones, garmentRest.BindPoses, skinBones, skinRest.BindPoses,
            out toSkin, out _, out _);
    }

    private static Vector3[] BodySurfaceAt(Mesh mesh, BodyShapeRest rest, bool usesMuscle, float value, float neutral)
    {
        var points = (Vector3[])rest.Vertices.Clone();
        if (usesMuscle && rest.Muscular != null)
        {
            var amount = value * .01f;
            for (var index = 0; index < points.Length; index++) points[index] += rest.Muscular[index] * amount;
        }
        var difference = value - neutral;
        var channel = mesh.GetBlendShapeIndex(difference >= 0f ? BodyVolumePositive : BodyVolumeNegative);
        if (difference == 0f || channel < 0) return points;
        var delta = new Vector3[points.Length];
        mesh.GetBlendShapeFrameVertices(channel, 0, delta, null, null);
        var weight = Mathf.Abs(difference) / mesh.GetBlendShapeFrameWeight(channel, 0);
        for (var index = 0; index < points.Length; index++) points[index] += delta[index] * weight;
        return points;
    }

    private static bool CanCopyBlendShapeFrames(Mesh source)
    {
        // Imported meshes can have distinct channels with the same name. Unity's frame API cannot recreate them.
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var shape = 0; shape < source.blendShapeCount; shape++)
        {
            if (!names.Add(source.GetBlendShapeName(shape))) return false;
            var previous = float.NegativeInfinity;
            for (var frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
            {
                var weight = source.GetBlendShapeFrameWeight(shape, frame);
                if (!(weight > previous) || float.IsInfinity(weight)) return false;
                previous = weight;
            }
        }
        return true;
    }

    private static Mesh CopyReadableSkin(Mesh source,
        Action<int, float, Vector3[], Vector3[], Vector3[]> change = null)
    {
        var mesh = ReadNativeRestMesh(source, readable => UnityEngine.Object.Instantiate(readable));
        try
        {
            mesh.name = source.name;
            if (!source.isReadable)
            {
                mesh.bounds = source.bounds;
                mesh.bindposes = source.bindposes;
                mesh.boneWeights = source.boneWeights;
            }
            if (!source.isReadable || change != null) CopyFrames(source, mesh, change);
            return mesh;
        }
        catch
        {
            UnityEngine.Object.Destroy(mesh);
            throw;
        }
    }

    private static void CopyFrames(Mesh source, Mesh target,
        Action<int, float, Vector3[], Vector3[], Vector3[]> change)
    {
        target.ClearBlendShapes();
        var p = new Vector3[source.vertexCount];
        var n = new Vector3[source.vertexCount];
        var t = new Vector3[source.vertexCount];
        for (var shape = 0; shape < source.blendShapeCount; shape++)
        for (var frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
        {
            var weight = source.GetBlendShapeFrameWeight(shape, frame);
            source.GetBlendShapeFrameVertices(shape, frame, p, n, t);
            change?.Invoke(shape, weight, p, n, t);
            target.AddBlendShapeFrame(source.GetBlendShapeName(shape), weight, p, n, t);
        }
    }

    private static Mesh BuildWardrobeClearanceMesh(Mesh source, (Vector3[] Positive, Vector3[] Negative) frames, float neutral)
    {
        var positive = source.GetBlendShapeIndex(BodyVolumePositive);
        var negative = source.GetBlendShapeIndex(BodyVolumeNegative);
        var mesh = CopyReadableSkin(source, positive >= 0 || negative >= 0 ? ApplyClearance : null);
        try
        {
            if (positive < 0) AddChannel(BodyVolumePositive, frames.Positive, 100f - neutral);
            if (negative < 0) AddChannel(BodyVolumeNegative, frames.Negative, neutral);
            return mesh;
        }
        catch
        {
            UnityEngine.Object.Destroy(mesh);
            throw;
        }

        void ApplyClearance(int shape, float weight, Vector3[] positions, Vector3[] _, Vector3[] __)
        {
            if (shape == positive) Add(positions, frames.Positive, weight, 100f - neutral);
            if (shape == negative) Add(positions, frames.Negative, weight, neutral);
        }

        void AddChannel(string name, Vector3[] correction, float range)
        {
            var positions = new Vector3[mesh.vertexCount];
            Add(positions, correction, 100f, range);
            mesh.AddBlendShapeFrame(name, 100f, positions, null, null);
        }

        static void Add(Vector3[] positions, Vector3[] correction, float frame, float range)
        {
            if (correction == null) return;
            for (var i = 0; i < positions.Length; i++) positions[i] += correction[i] * (frame / range);
        }
    }

    internal static (int First, int Second, float Factor) BlendShapeFrameSample(Mesh mesh, int channel, float weight)
    {
        var count = mesh.GetBlendShapeFrameCount(channel);
        if (count == 0) return (-1, -1, float.NaN);
        var firstWeight = mesh.GetBlendShapeFrameWeight(channel, 0);
        var lastWeight = mesh.GetBlendShapeFrameWeight(channel, count - 1);
        if (lastWeight < 0f && weight >= lastWeight)
            return (count - 1, -1, 1f - weight / lastWeight);
        if (count == 1 || firstWeight > 0f && weight <= firstWeight)
            return (-1, 0, weight / firstWeight);
        var second = 1;
        while (second < count - 1 && weight > mesh.GetBlendShapeFrameWeight(channel, second)) second++;
        var lower = mesh.GetBlendShapeFrameWeight(channel, second - 1);
        var upper = mesh.GetBlendShapeFrameWeight(channel, second);
        return (second - 1, second, (weight - lower) / (upper - lower));
    }

    private static int[] WeldContactVertices(Vector3[] points, out Dictionary<Vector3Int, int> representatives)
    {
        var keys = new Dictionary<Vector3Int, int>();
        var groups = new int[points.Length];
        for (var index = 0; index < points.Length; index++)
        {
            var cell = Vector3Int.RoundToInt(points[index] / .00001f);
            if (!keys.TryGetValue(cell, out var representative)) keys.Add(cell, representative = index);
            groups[index] = representative;
        }
        representatives = keys;
        return groups;
    }

    private static Mesh RepairNoseSurface(Mesh source, int[] triangles, Matrix4x4 anatomy, float noseHeight, float noseWidth)
    {
        if (triangles.Length == 0 || !source.isReadable && !CanCopyBlendShapeFrames(source)) return null;
        var rest = ReadBodyShapeRest(source);
        var points = rest.Vertices;
        if (rest.Normals.Length != points.Length) return null;
        var groups = WeldContactVertices(points, out var representatives);
        var local = points.Select(anatomy.MultiplyPoint3x4).ToArray();
        var neighbours = new Dictionary<int, List<int>>();
        foreach (var (first, last) in BodyShapeEdges(triangles, groups).Keys)
        {
            if (first == last) continue;
            Add(first, last);
            Add(last, first);
        }
        var repairs = new Dictionary<int, (Vector3 Point, Vector3 Normal)>();
        foreach (var index in representatives.Values)
        {
            if (!neighbours.TryGetValue(index, out var ring) || ring.Count < 5) continue;
            var near = ring[0];
            var side = Mathf.Sign(local[near].x);
            if (Mathf.Abs(local[near].y - noseHeight) > .4f) continue;
            var center = Vector3.zero;
            var normal = Vector3.zero;
            var nearest = float.PositiveInfinity;
            var valid = true;
            foreach (var neighbour in ring)
            {
                var position = local[neighbour];
                if (side * position.x < .18f || Mathf.Abs(Mathf.Abs(position.x) - noseWidth) > .4f ||
                    Mathf.Abs(position.y - noseHeight) > .4f || position.z < .12f)
                {
                    valid = false;
                    break;
                }
                center += points[neighbour];
                normal += rest.Normals[neighbour];
                nearest = Mathf.Min(nearest, (points[neighbour] - points[index]).sqrMagnitude);
            }
            if (!valid || nearest < .016f * .016f) continue;
            center /= ring.Count;
            var radius = ring.Max(neighbour => (points[neighbour] - center).magnitude);
            // A displaced vertex can pull an otherwise intact nostril ring across the face.
            if (radius > .010f || nearest < 6.25f * radius * radius || normal.sqrMagnitude < .1f) continue;
            normal.Normalize();
            repairs.Add(index, (center - normal * Mathf.Min(.00065f, radius * .12f), normal));
        }
        if (repairs.Count == 0) return null;
        var mesh = CopyReadableSkin(source);
        try
        {
            var vertices = (Vector3[])points.Clone();
            var normals = (Vector3[])rest.Normals.Clone();
            var tangents = (Vector4[])rest.Tangents.Clone();
            for (var index = 0; index < vertices.Length; index++)
            {
                if (!repairs.TryGetValue(groups[index], out var repair)) continue;
                vertices[index] = repair.Point;
                normals[index] = Vector3.Lerp(normals[index], repair.Normal, .65f).normalized;
                if (tangents.Length != vertices.Length) continue;
                var tangent = Vector3.ProjectOnPlane(tangents[index], normals[index]).normalized;
                if (tangent != Vector3.zero) tangents[index] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[index].w);
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            if (tangents.Length == vertices.Length) mesh.tangents = tangents;
            mesh.bounds = source.bounds;
            return mesh;
        }
        catch
        {
            UnityEngine.Object.Destroy(mesh);
            throw;
        }

        void Add(int from, int to)
        {
            if (!neighbours.TryGetValue(from, out var ring)) neighbours.Add(from, ring = []);
            ring.Add(to);
        }
    }

    private sealed class GroomAlphaMask
    {
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _alpha;
        internal readonly string Fingerprint;

        internal GroomAlphaMask(Texture2D texture)
        {
            const int maximumSize = 256;
            var scale = Mathf.Min(1f, (float)maximumSize / Mathf.Max(texture.width, texture.height));
            _width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            _height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
            var pixels = ReadCharacterModelLinearPixels(texture, _width, _height);
            Fingerprint = CustomUI.CustomModels.TextureRgba32LinearFingerprint(_width, _height, pixels, pixels.Length);
            _alpha = new byte[_width * _height];
            for (var index = 0; index < _alpha.Length; index++) _alpha[index] = pixels[index * 4 + 3];
        }

        internal float Sample(Vector2 uv)
        {
            var x = Mathf.Clamp(Mathf.FloorToInt(uv.x * _width), 0, _width - 1);
            var y = Mathf.Clamp(Mathf.FloorToInt(uv.y * _height), 0, _height - 1);
            return _alpha[y * _width + x] / 255f;
        }

        internal float Coverage(Vector2 uv)
        {
            var across = new Vector2(0.65f / _width, 0f);
            var up = new Vector2(0f, 0.65f / _height);
            return (Sample(uv) + Sample(uv + across + up) + Sample(uv + across - up) +
                    Sample(uv - across + up) + Sample(uv - across - up)) * .2f;
        }
    }

    private readonly struct GroomCutout(GroomAlphaMask mask, float cutoff)
    {
        internal readonly GroomAlphaMask Mask = mask;
        internal readonly float Cutoff = cutoff;
        internal string Key => Mask == null ? string.Empty : FormattableString.Invariant($"{Mask.Fingerprint}/{Cutoff:R}");
    }

    private static readonly ConditionalWeakTable<Texture2D, GroomAlphaMask> GroomAlphaMasks = new();
    private static readonly int GroomMainTexture = Shader.PropertyToID("_MainTex");
    private static readonly int GroomAlphaCutoff = Shader.PropertyToID("_Cutoff");

    private static GroomCutout ReadGroomCutout(SkinnedMeshRenderer renderer)
    {
        var materials = renderer.sharedMaterials;
        if (renderer.sharedMesh.subMeshCount != 1 || materials.Length != 1 ||
            SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return default;
        var material = materials[0];
        if (!material ||
            !material.HasProperty(GroomMainTexture) || !material.HasProperty(GroomAlphaCutoff) ||
            material.GetTextureScale("_MainTex") != Vector2.one || material.GetTextureOffset("_MainTex") != Vector2.zero)
            return default;
        var cutoff = material.GetFloat(GroomAlphaCutoff);
        return cutoff is > 0f and < 1f &&
               CharacterModelMaterial.NativeTexture(material, GroomMainTexture) is Texture2D texture && texture
            ? new GroomCutout(GroomAlphaMasks.GetValue(texture, static source => new GroomAlphaMask(source)), cutoff)
            : default;
    }

    private static Vector2[] SeatUpperLipTexture(Mesh source, int[] triangles, Vector3[] points, int[] groups,
        float[] distance, Matrix4x4 anatomy, Vector4 landmarks, GroomCutout cutout)
    {
        const int subdivisions = 12;
        const float maximumUvShift = .07f;
        var uv = ReadNativeRestMesh(source, mesh => mesh.uv);
        if (uv.Length != points.Length) return null;
        var targets = (Vector2[])uv.Clone();
        var influence = new float[uv.Length];
        var attached = new bool[uv.Length];
        for (var index = 0; index < triangles.Length; index += 3)
        {
            var a = triangles[index];
            var b = triangles[index + 1];
            var c = triangles[index + 2];
            if (Mathf.Min(distance[groups[a]], Mathf.Min(distance[groups[b]], distance[groups[c]])) > .019f) continue;
            // A card can overhang the lip even where the face has no perpendicular projection.
            attached[a] = attached[b] = attached[c] = true;
        }
        var nearest = Enumerable.Repeat(maximumUvShift * maximumUvShift, uv.Length).ToArray();
        var targetCoverage = Mathf.Min(.95f, cutout.Cutoff + .28f);
        for (var index = 0; index < uv.Length; index++)
        {
            if (!attached[index] || cutout.Mask.Sample(uv[index]) >= cutout.Cutoff) continue;
            var local = anatomy.MultiplyPoint3x4(points[index]);
            var fromLip = Mathf.InverseLerp(landmarks.y - .22f, landmarks.y + .04f, local.y);
            var fromNose = Mathf.InverseLerp(landmarks.x + .16f, landmarks.x - .12f, local.y);
            var fromCorner = Mathf.InverseLerp(landmarks.w * 1.1f, landmarks.w * .65f, Mathf.Abs(local.x));
            var onFace = Mathf.InverseLerp(.03f, .24f, local.z);
            influence[index] = Mathf.SmoothStep(0f, 1f, fromLip) * Mathf.SmoothStep(0f, 1f, fromNose) *
                               Mathf.SmoothStep(0f, 1f, fromCorner) * Mathf.SmoothStep(0f, 1f, onFace);
        }
        if (!influence.Any(weight => weight > .01f)) return null;

        // Search the incident UV triangles so the root borrows opaque strands from its own card island.
        for (var index = 0; index < triangles.Length; index += 3)
        {
            var a = triangles[index];
            var b = triangles[index + 1];
            var c = triangles[index + 2];
            if (Mathf.Max(influence[a], Mathf.Max(influence[b], influence[c])) <= .01f) continue;
            for (var u = 0; u <= subdivisions; u++)
            for (var v = 0; v <= subdivisions - u; v++)
            {
                var sample = uv[a] + ((uv[b] - uv[a]) * u + (uv[c] - uv[a]) * v) / subdivisions;
                if (cutout.Mask.Coverage(sample) < targetCoverage) continue;
                Offer(a, sample);
                Offer(b, sample);
                Offer(c, sample);
            }
        }
        var offsets = new Vector3[uv.Length];
        for (var index = 0; index < uv.Length; index++)
            offsets[index] = (targets[index] - uv[index]) * influence[index];
        LimitSurfaceOffsets(uv.Select(value => (Vector3)value).ToArray(), triangles,
            Enumerable.Range(0, uv.Length).ToArray(), offsets, Matrix4x4.identity);
        if (!offsets.Any(offset => offset.sqrMagnitude > 1e-10f)) return null;
        for (var index = 0; index < uv.Length; index++) uv[index] += (Vector2)offsets[index];
        return uv;

        void Offer(int vertex, Vector2 sample)
        {
            if (influence[vertex] <= .01f) return;
            var change = (sample - uv[vertex]).sqrMagnitude;
            if (change >= nearest[vertex]) return;
            nearest[vertex] = change;
            targets[vertex] = sample;
        }
    }

    private static void SetGroomRootUv(Mesh target, Vector2[] rootUv, int dimension)
    {
        // Source groom shaders can pack strand direction into the remaining UV0 components.
        switch (dimension)
        {
            case 2:
                target.uv = rootUv;
                break;
            case 3:
            {
                var packed = new List<Vector3>(rootUv.Length);
                target.GetUVs(0, packed);
                for (var index = 0; index < rootUv.Length; index++)
                    packed[index] = new Vector3(rootUv[index].x, rootUv[index].y, packed[index].z);
                target.SetUVs(0, packed);
                break;
            }
            case 4:
            {
                var packed = new List<Vector4>(rootUv.Length);
                target.GetUVs(0, packed);
                for (var index = 0; index < rootUv.Length; index++)
                    packed[index] = new Vector4(rootUv[index].x, rootUv[index].y, packed[index].z, packed[index].w);
                target.SetUVs(0, packed);
                break;
            }
        }
    }

    private const float GroomContactReach = .024f;

    private sealed class GroomWardrobe : IDisposable
    {
        private const int Angles = 96, Heights = 64;
        private const float Bottom = -.16f, Top = .24f;
        private const float BackHalfWidth = .26f;
        private const float BackColumnWidth = 2f * BackHalfWidth / Angles;
        private const float CollarFloor = -.015f;
        private const float MaxCollarDepth = .08f;
        private static readonly int Enabled = Shader.PropertyToID("_UBGroomWardrobe");
        private static readonly int Profile = Shader.PropertyToID("_UBGroomWardrobeProfile");
        private static readonly int ProfileGrid = Shader.PropertyToID("_UBGroomWardrobeProfile_TexelSize");
        private static readonly int FromWorld = Shader.PropertyToID("_UBGroomWardrobeFromWorld");
        private static readonly int ToWorld = Shader.PropertyToID("_UBGroomWardrobeToWorld");
        private static readonly int HeadFromWorld = Shader.PropertyToID("_UBGroomWardrobeHeadFromWorld");
        private static readonly int Range = Shader.PropertyToID("_UBGroomWardrobeRange");
        private const float RowHeight = (Top - Bottom) / (Heights - 1);
        private static readonly int FirstCollarRow = Mathf.CeilToInt((CollarFloor - MaxCollarDepth - Bottom) / RowHeight);
        private static readonly Vector2[] Rays = Enumerable.Range(0, Angles).Select(index =>
        {
            var angle = 2f * Mathf.PI * ((index + .5f) / Angles - .5f);
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }).ToArray();

        private readonly MaterialPropertyBlock _properties = new();
        private readonly List<Material> _materials = [];
        private Texture2D _profile;
        private (SkinnedMeshRenderer Renderer, Mesh Mesh, int Level)[] _wardrobe = [];
        private (SkinnedMeshRenderer Renderer, Mesh Mesh)[] _hair = [];
        private Transform _chest;
        private Transform _head;
        private Matrix4x4 _chestToField;
        private Matrix4x4 _headToAnatomy;
        private Vector4 _profileRange;
        private float _hangingLength;

        internal void Refresh(GraphicsCharacter graphics, LOD[] levels, Matrix4x4 headToAnatomy)
        {
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.RFloat)) return;
            if (graphics.RulesetCharacter is RulesetCharacterHero hero &&
                hero.CharacterInventory?.InventorySlotsByName.Values.Any(slot => slot.EquipedItem?.ItemDefinition.SlotTypes
                    .Contains(EquipmentDefinitions.SlotTypeTorso) == true) != true)
            {
                // Native appearance puts a default shirt on a hero with an empty torso slot.
                Dispose();
                return;
            }
            var hair = CharacterPartRenderers(graphics.ShapeSkinnedMeshRenderersPerBodySlot, BodyPart.Hair)
                .Where(renderer => renderer && renderer.sharedMesh && SupportsClearance(renderer))
                .Select(renderer => (Renderer: renderer, Mesh: renderer.sharedMesh)).ToArray();
            var wardrobe = CharacterPartRenderers(graphics.ArmorSkinnedMeshRenderersPerBodySlot, BodyPart.Body)
                .Where(renderer => renderer && renderer.sharedMesh)
                .Select(renderer => (Renderer: renderer, Mesh: renderer.sharedMesh, Level: Detail(renderer))).ToArray();
            if (hair.Length == 0 || wardrobe.Length == 0)
            {
                Dispose();
                return;
            }
            var sameWardrobe = _wardrobe.SequenceEqual(wardrobe);
            var sameHair = _hair.SequenceEqual(hair) && _headToAnatomy == headToAnatomy;
            if (sameWardrobe && sameHair) return;
            ClearHair();
            _hair = hair;
            _headToAnatomy = headToAnatomy;
            var primary = hair.OrderBy(row => Detail(row.Renderer)).First();
            var bones = primary.Renderer.bones;
            var head = Array.FindIndex(bones, bone => bone && bone.name == "Bip001 Head");
            _head = head >= 0 ? bones[head] : null;
            if (!sameHair) _hangingLength = MeasureHangingLength(primary.Mesh, head);
            if (!sameWardrobe)
            {
                ReleaseProfile();
                _wardrobe = wardrobe;
                BuildWardrobeProfile(graphics);
            }
            Apply(true);

            int Detail(Renderer renderer) => Array.FindIndex(levels, level => level.renderers.Contains(renderer));
        }

        private float MeasureHangingLength(Mesh mesh, int head)
        {
            if (head < 0) return 0f;
            var rest = ReadBodyShapeRest(mesh);
            if (head >= rest.BindPoses.Length) return 0f;
            var frame = _headToAnatomy * rest.BindPoses[head];
            var points = rest.Vertices.Select(frame.MultiplyPoint3x4).ToArray();
            var triangles = rest.Triangles;
            var back = 0f;
            var belowNeck = 0f;
            for (var index = 0; index < triangles.Length; index += 3)
            {
                var a = points[triangles[index]];
                var b = points[triangles[index + 1]];
                var c = points[triangles[index + 2]];
                var center = (a + b + c) / 3f;
                if (center.z >= -2.8f) continue;
                var area = Vector3.Cross(b - a, c - a).magnitude;
                back += area;
                if (center.y < -6.7f) belowNeck += area;
            }
            return back > 0f ? ContactFade(.112f, .18f, belowNeck / back) : 0f;
        }

        private void BuildWardrobeProfile(GraphicsCharacter graphics)
        {
            var wardrobe = _wardrobe;
            var source = wardrobe[0].Renderer;
            var bones = source.bones;
            var bind = wardrobe[0].Mesh.bindposes;
            var neck = Array.FindIndex(bones, bone => bone && bone.name == "Bip001 Neck");
            var chest = Array.FindIndex(bones, bone => bone && bone.name == "Bip001 Spine2");
            if (chest < 0) chest = neck;
            if (chest < 0 || chest >= bind.Length) return;

            // Use the equipped mesh's bind pose to avoid baking the current idle pose into the contour.
            var fieldToMesh = source.transform.worldToLocalMatrix * graphics.transform.localToWorldMatrix;
            var neckInMesh = neck >= 0 && neck < bind.Length
                ? (Vector3)bind[neck].inverse.GetColumn(3)
                : BindNeckFromSkeleton(bones[chest], bind[chest]);
            fieldToMesh.SetColumn(3, new Vector4(neckInMesh.x, neckInMesh.y, neckInMesh.z, 1f));
            _chestToField = fieldToMesh.inverse * bind[chest].inverse;
            _chest = bones[chest];
            // The simplified equipment can lose the collar's side faces. All hair detail
            // levels use the same outline to keep the contact and silhouette continuous.
            var detail = wardrobe.Where(row => row.Level >= 0).Select(row => row.Level).DefaultIfEmpty(-1).Min();
            var sections = wardrobe.Where(row => row.Level == detail || row.Level < 0).ToArray();
            _profile = BuildProfile(sections, out _profileRange);

            static Vector3 BindNeckFromSkeleton(Transform chestBone, Matrix4x4 chestBind)
            {
                var neckBone = chestBone.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(bone => bone.name == "Bip001 Neck");
                var inChest = neckBone ? chestBone.InverseTransformPoint(neckBone.position) : Vector3.zero;
                return chestBind.inverse.MultiplyPoint3x4(inChest);
            }
        }

        private bool SupportsClearance(SkinnedMeshRenderer renderer)
        {
            renderer.GetSharedMaterials(_materials);
            return _materials.Any(material => material && material.HasProperty(Enabled));
        }

        private Texture2D BuildProfile((SkinnedMeshRenderer Renderer, Mesh Mesh, int Level)[] sections, out Vector4 range)
        {
            range = default;
            var radii = new float[Angles * Heights];
            var back = new float[radii.Length];
            foreach (var (renderer, mesh, _) in sections)
            {
                var bones = renderer.bones;
                var rest = ReadBodyShapeRest(mesh);
                var chest = Array.IndexOf(bones, _chest);
                if (chest < 0 || chest >= rest.BindPoses.Length) continue;
                var toField = _chestToField * rest.BindPoses[chest];
                var vertices = rest.Vertices.Select(toField.MultiplyPoint3x4).ToArray();
                var indices = SurfaceTriangles(mesh, renderer, false);
                for (var index = 0; index < indices.Length; index += 3)
                {
                    var a = vertices[indices[index]];
                    var b = vertices[indices[index + 1]];
                    var c = vertices[indices[index + 2]];
                    var first = Mathf.Max(0, Mathf.CeilToInt((Mathf.Min(a.y, b.y, c.y) - Bottom) / RowHeight));
                    var last = Mathf.Min(Heights - 1, Mathf.FloorToInt((Mathf.Max(a.y, b.y, c.y) - Bottom) / RowHeight));
                    for (var y = first; y <= last; y++)
                    {
                        var height = Bottom + y * RowHeight;
                        var start = Vector2.zero;
                        var end = Vector2.zero;
                        var intersections = 0;
                        Intersect(a, b, height, ref start, ref end, ref intersections);
                        Intersect(b, c, height, ref start, ref end, ref intersections);
                        Intersect(c, a, height, ref start, ref end, ref intersections);
                        if (intersections != 2) continue;
                        var edge = end - start;
                        if (Mathf.Abs(edge.x) > 1e-6f)
                        {
                            var left = Mathf.Max(0, Mathf.CeilToInt(Column(Mathf.Min(start.x, end.x))));
                            var right = Mathf.Min(Angles - 1, Mathf.FloorToInt(Column(Mathf.Max(start.x, end.x))));
                            for (var column = left; column <= right; column++)
                            {
                                var at = (column + .5f) * BackColumnWidth - BackHalfWidth;
                                var depth = -Mathf.LerpUnclamped(start.y, end.y, (at - start.x) / edge.x);
                                var pixel = y * Angles + column;
                                if (depth > back[pixel]) back[pixel] = depth;
                            }
                        }
                        var cross = Cross(start, edge);
                        for (var angle = 0; angle < Angles; angle++)
                        {
                            var direction = Rays[angle];
                            var determinant = Cross(direction, edge);
                            if (Mathf.Abs(determinant) < 1e-6f) continue;
                            var fraction = Cross(start, direction) / determinant;
                            if (fraction < 0f || fraction > 1f) continue;
                            var radius = cross / determinant;
                            var pixel = y * Angles + angle;
                            if (radius > radii[pixel]) radii[pixel] = radius;
                        }
                    }
                }
            }
            if (!radii.Any(radius => radius > .02f)) return null;
            range = new Vector4(Bottom, Top, BackHalfWidth, 0f);
            var result = new Texture2D(Angles, 2 * Heights + 1, TextureFormat.RFloat, false, true)
            {
                name = "Groom equipment contour",
                filterMode = FilterMode.Bilinear,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp
            };
            try
            {
                // The atlas stores the collar outline, radial slices, and the posterior profile.
                var values = new float[Angles * (2 * Heights + 1)];
                SmoothCollarContour(radii).CopyTo(values, 0);
                WriteContour(radii, values, 1, true);
                WriteContour(back, values, Heights + 1, false);
                result.SetPixelData(values, 0);
                result.Apply(false);
                return result;
            }
            catch
            {
                UnityEngine.Object.Destroy(result);
                throw;
            }

            static float Column(float side) => (side + BackHalfWidth) / BackColumnWidth - .5f;

            static float Cross(Vector2 first, Vector2 second) => first.x * second.y - first.y * second.x;

            static void Intersect(Vector3 a, Vector3 b, float height, ref Vector2 first, ref Vector2 last, ref int count)
            {
                if ((a.y > height) == (b.y > height)) return;
                var point = Vector3.LerpUnclamped(a, b, (height - a.y) / (b.y - a.y));
                var slice = new Vector2(point.x, point.z);
                if (count++ == 0) first = slice;
                else last = slice;
            }
        }

        private static void WriteContour(float[] contour, float[] target, int startRow, bool circular)
        {
            var expanded = new float[contour.Length];
            for (var row = 0; row < Heights; row++)
            for (var angle = 0; angle < Angles; angle++)
            {
                var furthest = 0f;
                for (var y = Mathf.Max(0, row - 2); y <= Mathf.Min(Heights - 1, row + 2); y++)
                for (var offset = -2; offset <= 2; offset++)
                {
                    var column = Index(angle + offset);
                    var radius = contour[y * Angles + column] - Mathf.Abs(y - row) * RowHeight * .8f -
                                 Mathf.Abs(offset) * .008f;
                    furthest = Mathf.Max(furthest, radius);
                }
                expanded[row * Angles + angle] = furthest;
            }
            for (var row = 0; row < Heights; row++)
            for (var angle = 0; angle < Angles; angle++)
            {
                var start = row * Angles;
                target[(startRow + row) * Angles + angle] = .25f * expanded[start + Index(angle - 1)] +
                                                            .5f * expanded[start + angle] +
                                                            .25f * expanded[start + Index(angle + 1)];
            }

            int Index(int value) => circular ? (value + Angles) % Angles : Mathf.Clamp(value, 0, Angles - 1);
        }

        private static float[] SmoothCollarContour(float[] contour)
        {
            var widths = new float[Angles];
            for (var angle = 0; angle < Angles; angle++)
            {
                for (var row = FirstCollarRow; row < Heights; row++)
                {
                    var y = Bottom + row * RowHeight;
                    var below = Mathf.Max(0f, CollarFloor - y);
                    for (var offset = -2; offset <= 2; offset++)
                    {
                        var column = (angle + offset + Angles) % Angles;
                        var radius = contour[row * Angles + column] - below * .70f - Mathf.Abs(offset) * .008f;
                        widths[angle] = Mathf.Max(widths[angle], radius);
                    }
                }
            }
            // A card's neighbouring vertices should see the same bend across a sharp pauldron edge.
            var result = new float[Angles];
            for (var angle = 0; angle < Angles; angle++)
            for (var offset = -3; offset <= 3; offset++)
                result[angle] += widths[(angle + offset + Angles) % Angles] / 7f;
            return result;
        }

        internal void Apply(bool initializing = false)
        {
            if (!_chest || !_head || !_profile) return;
            var fromWorld = _chestToField * _chest.worldToLocalMatrix;
            var toWorld = fromWorld.inverse;
            var anatomy = _headToAnatomy * _head.worldToLocalMatrix;
            foreach (var (renderer, _) in _hair)
            {
                if (!renderer || !renderer.enabled || !initializing && !renderer.isVisible) continue;
                Write(renderer, _profile, fromWorld, toWorld, anatomy);
            }
        }

        private void Write(SkinnedMeshRenderer renderer, Texture2D profile, Matrix4x4 fromWorld = default,
            Matrix4x4 toWorld = default, Matrix4x4 anatomy = default)
        {
            renderer.GetPropertyBlock(_properties);
            WriteProperties();
            renderer.SetPropertyBlock(_properties);
            renderer.GetSharedMaterials(_materials);
            for (var slot = 0; slot < _materials.Count; slot++)
            {
                var material = _materials[slot];
                if (!material || !material.HasProperty(Enabled)) continue;
                renderer.GetPropertyBlock(_properties, slot);
                if (_properties.isEmpty) continue;
                WriteProperties();
                renderer.SetPropertyBlock(_properties, slot);
            }

            void WriteProperties()
            {
                _properties.SetFloat(Enabled, profile ? 1f : 0f);
                _properties.SetTexture(Profile, profile ? profile : Texture2D.blackTexture);
                if (!profile) return;
                _properties.SetVector(ProfileGrid, new Vector4(1f / profile.width, 1f / profile.height, profile.width, profile.height));
                _properties.SetMatrix(FromWorld, fromWorld);
                _properties.SetMatrix(ToWorld, toWorld);
                _properties.SetMatrix(HeadFromWorld, anatomy);
                var range = _profileRange;
                range.w = _hangingLength;
                _properties.SetVector(Range, range);
            }
        }

        private void ClearHair()
        {
            if (_profile)
                foreach (var (renderer, _) in _hair)
                    if (renderer) Write(renderer, null);
            _hair = [];
            _head = null;
        }

        private void ReleaseProfile()
        {
            if (_profile) UnityEngine.Object.Destroy(_profile);
            _profile = null;
            _wardrobe = [];
            _chest = null;
        }

        public void Dispose()
        {
            ClearHair();
            ReleaseProfile();
            _materials.Clear();
            _properties.Clear();
        }
    }

    private static float ContactFade(float edge, float full, float value) =>
        Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(edge, full, value));

    private static float UpperLipContact(Vector3 point, Vector4 landmarks)
    {
        return ContactFade(landmarks.y - .24f, landmarks.y + .05f, point.y) *
               ContactFade(landmarks.x + .18f, landmarks.x - .04f, point.y) *
               ContactFade(landmarks.w * 1.32f, landmarks.w * .86f, Mathf.Abs(point.x)) *
               ContactFade(-.23f, .19f, point.z);
    }

    private static float ChinContact(Vector3 point, Vector4 landmarks) =>
        ContactFade(landmarks.y - .32f, landmarks.y - .67f, point.y) *
        ContactFade(landmarks.y - 2.5f, landmarks.y - 1.45f, point.y) *
        ContactFade(landmarks.w * 1.6f, landmarks.w * .9f, Mathf.Abs(point.x)) *
        ContactFade(.10f, .48f, point.z);

    private static Mesh FitGroomContact(Mesh source, (int Channel, float Weight, bool Bake)[] weights,
        Matrix4x4 toSkin, ContactSurface surface,
        Matrix4x4 anatomy, Vector4 landmarks, bool beard, HashSet<int> clothRoots, GroomCutout cutout)
    {
        if (surface.IsEmpty || !source.isReadable && !CanCopyBlendShapeFrames(source)) return null;
        var (vertices, normals, tangents, triangles) = ReadNativeRestMesh(source,
            mesh => (mesh.vertices, mesh.normals, mesh.tangents, mesh.triangles));
        if (triangles.Length == 0 || vertices.Length != normals.Length) return null;
        var contact = (Vector3[])vertices.Clone();
        var bakeFit = weights.Any(shape => shape.Bake);
        if (weights.Length != 0)
        {
            var delta = new Vector3[vertices.Length];
            var normalDelta = bakeFit ? new Vector3[vertices.Length] : null;
            var tangentDelta = bakeFit && tangents.Length == vertices.Length ? new Vector3[vertices.Length] : null;
            foreach (var (channel, weight, bake) in weights)
            {
                var (first, second, factor) = BlendShapeFrameSample(source, channel, weight);
                if (!BodyShapeFinite(factor)) return null;
                Accumulate(first, 1f - factor);
                Accumulate(second, factor);

                void Accumulate(int frame, float amount)
                {
                    if (frame < 0 || amount == 0f) return;
                    source.GetBlendShapeFrameVertices(channel, frame, delta, bake ? normalDelta : null,
                        bake ? tangentDelta : null);
                    for (var index = 0; index < vertices.Length; index++)
                    {
                        var shift = delta[index] * amount;
                        contact[index] += shift;
                        if (!bake) continue;
                        vertices[index] += shift;
                        normals[index] += normalDelta[index] * amount;
                        if (tangentDelta == null) continue;
                        var tangent = (Vector3)tangents[index] + tangentDelta[index] * amount;
                        tangents[index] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[index].w);
                    }
                }
            }
        }
        var points = contact.Select(toSkin.MultiplyPoint3x4).ToArray();
        // Keep duplicated vertices at texture seams together, including at nonzero morph weights.
        var groups = WeldContactVertices(contact, out var primary);
        var movable = clothRoots == null ? null : Enumerable.Repeat(true, points.Length).ToArray();
        if (movable != null)
        {
            for (var index = 0; index < points.Length; index++)
                if (!clothRoots.Contains(index)) movable[groups[index]] = false;
        }
        var edges = BodyShapeEdges(triangles, groups);
        var offsets = new Vector3[points.Length];
        var directions = new Vector3[points.Length];
        var skinNormals = new Vector3[points.Length];
        var gaps = new float[points.Length];
        var exterior = new float[points.Length];
        var upperLip = beard ? new float[points.Length] : null;
        var beardRim = beard ? new bool[points.Length] : null;
        var chinInset = beard ? new float[points.Length] : null;
        var chinRegion = beard ? new float[points.Length] : null;
        var forward = ((Vector3)anatomy.GetRow(2)).normalized;
        var inverse = toSkin.inverse;
        var toSkinNormals = inverse.transpose;
        var distance = Enumerable.Repeat(float.PositiveInfinity, points.Length).ToArray();
        var reach = beard ? .023f : .018f;
        foreach (var vertex in primary.Values)
        {
            if (movable != null && !movable[vertex]) continue;
            var anatomyPoint = anatomy.MultiplyPoint3x4(points[vertex]);
            // At the mouth, a perpendicular ray can reach a nostril or the back of a lip.
            // Use the forward skin surface for nearby upper-lip cards, including closed bands.
            var lip = beard ? UpperLipContact(anatomyPoint, landmarks) : 0f;
            if (lip > 0f && surface.Project(points[vertex], forward, out var frontGap, out var frontNormal) &&
                Mathf.Abs(frontGap) < .012f)
            {
                upperLip[vertex] = lip;
                directions[vertex] = forward;
                skinNormals[vertex] = frontNormal;
                gaps[vertex] = frontGap;
                continue;
            }
            if (!surface.Project(points[vertex], Vector3.zero, out var gap, out var normal)) continue;
            var local = anatomy.MultiplyPoint3x4(points[vertex] - normal * gap);
            if (!beard && local.y <= .32f && (local.y <= -.38f || local.z >= -.42f)) continue;
            directions[vertex] = normal;
            skinNormals[vertex] = normal;
            gaps[vertex] = gap;
            if (beard) chinRegion[vertex] = ChinContact(anatomyPoint, landmarks);
        }
        for (var index = 0; index < normals.Length; index++)
        {
            var group = groups[index];
            var groomNormal = toSkinNormals.MultiplyVector(normals[index]).normalized;
            var facing = Vector3.Dot(groomNormal, skinNormals[group]);
            exterior[group] = Mathf.Max(exterior[group], Mathf.InverseLerp(.12f, .72f, facing));
        }
        foreach (var edge in edges)
        {
            var (first, last) = edge.Key;
            if (beard && edge.Value == 1)
            {
                beardRim[first] = beardRim[last] = true;
                SeatChinRim(first);
                SeatChinRim(last);
            }
            if (first == last || !IsAttachment(first) || !IsAttachment(last) ||
                Vector3.Dot(skinNormals[first], skinNormals[last]) < .5f) continue;
            // Closed hair caps can use the near-scalp band of their shell as an attachment.
            var scalp = !beard && Mathf.Max(Mathf.Abs(gaps[first]), Mathf.Abs(gaps[last])) < .006f;
            if (edge.Value != 1 && !scalp ||
                Mathf.Min(Mathf.Abs(gaps[first]), Mathf.Abs(gaps[last])) > (beard ? .011f : .008f)) continue;
            distance[first] = distance[last] = 0f;
        }
        var attachedEdges = edges.Keys.Where(edge => directions[edge.Item1] != Vector3.zero &&
                                                    directions[edge.Item2] != Vector3.zero)
            .Select(edge => (edge.Item1, edge.Item2, Length: Vector3.Distance(points[edge.Item1], points[edge.Item2])))
            .ToArray();
        var previous = new float[distance.Length];
        var previousInset = beard ? new float[distance.Length] : null;
        var hasAttachment = distance.Any(value => value == 0f);
        var hasChinRim = beard && chinInset.Any(value => value != 0f);
        for (var pass = 0; (hasAttachment || hasChinRim) && pass < 6; pass++)
        {
            Array.Copy(distance, previous, distance.Length);
            if (hasChinRim) Array.Copy(chinInset, previousInset, chinInset.Length);
            foreach (var (first, last, length) in attachedEdges)
            {
                if (previous[last] + length < reach)
                    distance[first] = Mathf.Min(distance[first], previous[last] + length);
                if (previous[first] + length < reach)
                    distance[last] = Mathf.Min(distance[last], previous[first] + length);
                if (!hasChinRim) continue;
                // Move the nearest card vertices with the rim so a thin root cannot fold back.
                SpreadChinInset(first, last, length);
                SpreadChinInset(last, first, length);
            }
        }
        foreach (var index in primary.Values)
        {
            var lip = upperLip?[index] ?? 0f;
            var chin = chinInset?[index] ?? 0f;
            if (distance[index] >= reach && lip == 0f && chin == 0f) continue;
            var falloff = Mathf.SmoothStep(0f, 1f, distance[index] / reach);
            var outside = exterior[index];
            // Seat the inner edge while giving the outward-facing strands room above the skin.
            // Away from the attachment, the groom keeps its authored thickness.
            var minimum = Mathf.Lerp(-.0007f, beard ? .0013f : .0009f, outside);
            var maximum = Mathf.Lerp(.0004f, beard ? .0037f : .003f, outside) +
                          falloff * (beard ? .009f : .008f);
            var gap = gaps[index];
            var amount = gap < minimum
                ? Mathf.Min(minimum - gap, .006f) * outside
                : -Mathf.Clamp(gap - maximum, 0f, beard ? .009f : .006f);
            amount *= 1f - falloff;
            if (lip > 0f)
            {
                // Tuck the open rim into the upper-lip skin. Leave enough depth for
                // the visible strands and for the mouth to move behind the rim.
                var frontMinimum = beardRim[index] ? -.0012f : Mathf.Lerp(-.0004f, .0014f, outside);
                var frontMaximum = beardRim[index] ? .00015f : Mathf.Lerp(.0006f, .0022f, outside);
                var adjustment = Mathf.Clamp(Mathf.Clamp(gap, frontMinimum, frontMaximum) - gap, -.007f, .008f);
                amount = Mathf.Lerp(amount, adjustment, lip);
            }
            var offset = directions[index] * amount;
            if (chin > 0f) offset -= forward * Mathf.Max(0f, chin + Vector3.Dot(offset, forward));
            offsets[index] = inverse.MultiplyVector(offset);
        }
        LimitSurfaceOffsets(points, triangles, groups, offsets, toSkin);
        var repairedCards = beard
            ? LiftGroomCards(points, triangles, groups, offsets, movable, toSkin, forward,
                point => UpperLipCardClearance(point, surface, anatomy, landmarks, forward))
            : LiftHairCards();
        if (repairedCards)
            LimitSurfaceOffsets(points, triangles, groups, offsets, toSkin);
        var uvDimension = cutout.Mask != null && source.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)
            ? source.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0)
            : 0;
        var rootUv = uvDimension is >= 2 and <= 4
            ? SeatUpperLipTexture(source, triangles, points, groups, distance, anatomy, landmarks, cutout)
            : null;
        if (!bakeFit && rootUv == null && !offsets.Any(offset => offset.sqrMagnitude > 1e-10f)) return null;
        var target = CopyReadableSkin(source);
        try
        {
            var moved = vertices.Select((point, index) => point + offsets[groups[index]]).ToArray();
            var before = BodyShapeNormals(vertices, triangles);
            var after = BodyShapeNormals(moved, triangles);
            for (var index = 0; index < normals.Length; index++)
            {
                if (movable != null && !movable[groups[index]]) continue;
                if (before[index] == Vector3.zero || after[index] == Vector3.zero) continue;
                var turn = Quaternion.FromToRotation(before[index], after[index]);
                normals[index] = turn * normals[index];
                if (tangents.Length != vertices.Length) continue;
                var tangent = turn * (Vector3)tangents[index];
                tangents[index] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[index].w);
            }
            target.vertices = moved;
            target.normals = normals;
            if (tangents.Length == vertices.Length) target.tangents = tangents;
            if (rootUv != null) SetGroomRootUv(target, rootUv, uvDimension);
            var bounds = source.bounds;
            foreach (var point in moved) bounds.Encapsulate(point);
            target.bounds = bounds;
            return target;
        }
        catch
        {
            UnityEngine.Object.Destroy(target);
            throw;
        }

        bool IsAttachment(int index) => directions[index] != Vector3.zero &&
            gaps[index] >= -.010f && gaps[index] <= (beard ? .018f : .014f);

        void SeatChinRim(int index)
        {
            var gap = gaps[index];
            var alignment = Vector3.Dot(skinNormals[index], forward);
            if (chinRegion[index] <= 0f || gap >= .009f || alignment < .42f) return;
            // The visible ends of the open chin seam need some overlap with the moving jaw.
            var overlap = Mathf.Lerp(.0012f, .0057f, exterior[index]);
            chinInset[index] = Mathf.Clamp((gap + overlap) / alignment, 0f, .010f) * chinRegion[index];
        }

        void SpreadChinInset(int from, int to, float length)
        {
            var inset = Mathf.Max(0f, previousInset[from] - length * .25f) * chinRegion[to];
            chinInset[to] = Mathf.Max(chinInset[to], inset);
        }

        bool LiftHairCards()
        {
            var across = ((Vector3)anatomy.GetRow(0)).normalized;
            var up = ((Vector3)anatomy.GetRow(1)).normalized;
            return LiftSide(across) | LiftSide(-across) |
                   LiftGroomCards(points, triangles, groups, offsets, movable, toSkin, up,
                       point => ScalpCardClearance(point, surface, anatomy.MultiplyPoint3x4(point), up,
                           movable != null));

            bool LiftSide(Vector3 outward) => LiftGroomCards(points, triangles, groups, offsets, movable,
                toSkin, outward, point =>
                {
                    var local = anatomy.MultiplyPoint3x4(point);
                    return Mathf.Max(SideburnCardClearance(point, surface, local, outward),
                        ScalpCardClearance(point, surface, local, outward, movable != null));
                });
        }
    }

    private static float UpperLipCardClearance(Vector3 point, ContactSurface surface, Matrix4x4 anatomy,
        Vector4 landmarks, Vector3 outward)
    {
        var lip = UpperLipContact(anatomy.MultiplyPoint3x4(point), landmarks);
        if (lip <= .2f || !surface.ProjectOutermost(point, outward, .010f, out var depth) || depth >= -.0002f)
            return 0f;
        return Mathf.Min(.006f, .0008f - depth) * ContactFade(.2f, .75f, lip);
    }

    private static float ScalpCardClearance(Vector3 point, ContactSurface surface, Vector3 local, Vector3 outward,
        bool cloth)
    {
        var scalp = ContactFade(.65f, 1.5f, local.y);
        if (scalp == 0f || !surface.ProjectOutermost(point, outward, .010f, out var depth)) return 0f;
        // The cloth attachment shifts when the native hair morph is applied.
        var motionClearance = cloth ? .0055f * ContactFade(-1.65f, -3f, local.z) : 0f;
        return Mathf.Clamp(.0007f + motionClearance - depth, 0f, .006f) * scalp;
    }

    private static float SideburnCardClearance(Vector3 point, ContactSurface surface, Vector3 local, Vector3 outward)
    {
        var side = Mathf.Abs(local.x);
        var atTemple = ContactFade(1.8f, 2.35f, side) * ContactFade(3.6f, 3.15f, side) *
                      ContactFade(-2.85f, -2.32f, local.z) * ContactFade(-1.18f, -1.58f, local.z) *
                      ContactFade(-.55f, -.95f, local.y) * ContactFade(-2.3f, -2.05f, local.y);
        if (atTemple <= .12f || !surface.ProjectOutermost(point, outward, .014f, out var depth) || depth >= .0085f ||
            depth < -.006f) return 0f;

        // A side lock needs room for the cheek to move below its midsection.
        return Mathf.Min(.0065f, .0085f - depth) * ContactFade(.12f, .78f, atTemple);
    }

    private static bool LiftGroomCards(Vector3[] points, int[] triangles, int[] groups, Vector3[] offsets, bool[] movable,
        Matrix4x4 toSkin, Vector3 outward, Func<Vector3, float> clearance)
    {
        var fitted = points.Select((point, index) => point + toSkin.MultiplyVector(offsets[groups[index]])).ToArray();
        var correction = new float[points.Length];
        for (var index = 0; index < triangles.Length; index += 3)
        {
            var a = triangles[index];
            var b = triangles[index + 1];
            var c = triangles[index + 2];
            var first = fitted[a];
            var second = fitted[b];
            var third = fitted[c];
            var normal = Vector3.Cross(second - first, third - first).normalized;
            if (Vector3.Dot(normal, outward) <= .2f) continue;

            // Between the vertices of a coarse card, the face can still cut through the visible strands.
            var lift = clearance((first + second + third) / 3f);
            lift = Mathf.Max(lift, clearance((first + second) * .5f));
            lift = Mathf.Max(lift, clearance((second + third) * .5f));
            lift = Mathf.Max(lift, clearance((third + first) * .5f));
            if (lift == 0f) continue;
            correction[groups[a]] = Mathf.Max(correction[groups[a]], lift);
            correction[groups[b]] = Mathf.Max(correction[groups[b]], lift);
            correction[groups[c]] = Mathf.Max(correction[groups[c]], lift);
        }

        var outwardInGroom = toSkin.inverse.MultiplyVector(outward);
        var changed = false;
        for (var index = 0; index < correction.Length; index++)
        {
            if (correction[index] == 0f || movable != null && !movable[index]) continue;
            offsets[index] += outwardInGroom * correction[index];
            changed = true;
        }
        return changed;
    }

    private static void LimitSurfaceOffsets(Vector3[] points, int[] triangles, int[] groups, Vector3[] offsets, Matrix4x4 toSkin)
    {
        var unsafeGroups = new HashSet<int>();
        for (var pass = 0;; pass++)
        {
            unsafeGroups.Clear();
            for (var index = 0; index < triangles.Length; index += 3)
            {
                var a = triangles[index];
                var b = triangles[index + 1];
                var c = triangles[index + 2];
                var first = toSkin.MultiplyVector(offsets[groups[b]] - offsets[groups[a]]);
                var second = toSkin.MultiplyVector(offsets[groups[c]] - offsets[groups[a]]);
                if (first == Vector3.zero && second == Vector3.zero) continue;
                var u = points[b] - points[a];
                var v = points[c] - points[a];
                var before = Vector3.Cross(u, v);
                if (before.sqrMagnitude < 1e-18f ||
                    Vector3.Dot(before, Vector3.Cross(u + first, v + second)) >= before.sqrMagnitude * .18f) continue;
                unsafeGroups.Add(groups[a]);
                unsafeGroups.Add(groups[b]);
                unsafeGroups.Add(groups[c]);
            }
            if (unsafeGroups.Count == 0) return;
            foreach (var group in unsafeGroups) offsets[group] = pass < 8 ? offsets[group] * .5f : Vector3.zero;
        }
    }
}
