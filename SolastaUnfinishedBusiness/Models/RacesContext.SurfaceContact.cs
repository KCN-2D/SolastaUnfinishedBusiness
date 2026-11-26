using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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

        internal bool Project(Vector3 point, Vector3 direction, out float depth, out Vector3 normal)
        {
            depth = _reach;
            normal = Vector3.zero;
            if (!_cells.TryGetValue(Cell(point), out var candidates)) return false;
            var useDirection = direction.sqrMagnitude > 0f;
            foreach (var index in candidates)
            {
                var triangle = _triangles[index];
                var ray = useDirection ? direction : triangle.Normal;
                var alignment = Vector3.Dot(triangle.Normal, ray);
                if (alignment < .55f) continue;
                var offset = point - triangle.Origin;
                var distance = Vector3.Dot(offset, triangle.Normal) / alignment;
                if (Mathf.Abs(distance) >= Mathf.Abs(depth)) continue;
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

    private static int SelectedGroomFrame(Mesh source, string face) =>
        string.IsNullOrEmpty(face) ? -1 : Enumerable.Range(0, source.blendShapeCount)
            .Where(index => source.GetBlendShapeName(index).EndsWith("_" + face, StringComparison.Ordinal))
            .DefaultIfEmpty(-1).First();

    private static Mesh FitGroomContact(Mesh source, int shape, Matrix4x4 toSkin, Mesh head, int[] faceTriangles,
        Matrix4x4 anatomy, bool beard)
    {
        if (faceTriangles.Length == 0 || !source.isReadable && !CanCopyBlendShapeFrames(source)) return null;
        var (vertices, normals, tangents, triangles) = ReadNativeRestMesh(source,
            mesh => (mesh.vertices, mesh.normals, mesh.tangents, mesh.triangles));
        if (triangles.Length == 0 || vertices.Length != normals.Length) return null;
        var selected = new Vector3[vertices.Length];
        if (shape >= 0)
        {
            var last = source.GetBlendShapeFrameCount(shape) - 1;
            var weight = source.GetBlendShapeFrameWeight(shape, last);
            if (weight <= 0f) return null;
            source.GetBlendShapeFrameVertices(shape, last, selected, null, null);
            for (var index = 0; index < selected.Length; index++) selected[index] *= 100f / weight;
        }
        var points = vertices.Select((point, index) => toSkin.MultiplyPoint3x4(point + selected[index])).ToArray();
        var reach = beard ? .012f : .009f;
        var surface = new ContactSurface(ReadBodyShapeRest(head).Vertices, faceTriangles, reach);
        var primary = new Dictionary<Vector3Int, int>();
        var groups = points.Select((point, index) =>
        {
            var cell = Vector3Int.RoundToInt(point / .00001f);
            if (!primary.TryGetValue(cell, out var representative)) primary.Add(cell, representative = index);
            return representative;
        }).ToArray();
        var edges = BodyShapeEdges(triangles, groups);
        var offsets = new Vector3[points.Length];
        var directions = new Vector3[points.Length];
        var gaps = new float[points.Length];
        var eligible = new bool[points.Length];
        var distance = Enumerable.Repeat(float.PositiveInfinity, points.Length).ToArray();
        foreach (var vertex in primary.Values)
        {
            if (!surface.Project(points[vertex], Vector3.zero, out var gap, out var normal)) continue;
            var local = anatomy.MultiplyPoint3x4(points[vertex] - normal * gap);
            eligible[vertex] = gap >= -.002f && (beard || local.y > .38f || local.y > -.35f && local.z < -.42f);
            gaps[vertex] = gap;
            directions[vertex] = normal;
        }
        foreach (var edge in edges)
        {
            var (first, last) = edge.Key;
            // Open edges close to the same patch of skin identify the attachment.
            if (first == last || edge.Value != 1 || !eligible[first] || !eligible[last] ||
                Vector3.Dot(directions[first], directions[last]) < .5f ||
                !beard && Mathf.Min(gaps[first], gaps[last]) > .005f) continue;
            distance[first] = distance[last] = 0f;
        }
        if (!distance.Any(value => value == 0f)) return null;
        for (var pass = 0; pass < 3; pass++)
        {
            var previous = (float[])distance.Clone();
            foreach (var (first, last) in edges.Keys)
            {
                if (!eligible[first] || !eligible[last]) continue;
                var length = Vector3.Distance(points[first], points[last]);
                distance[first] = Mathf.Min(distance[first], previous[last] + length);
                distance[last] = Mathf.Min(distance[last], previous[first] + length);
            }
        }
        var inverse = toSkin.inverse;
        foreach (var index in primary.Values)
        {
            var fade = Mathf.SmoothStep(1f, 0f, Mathf.Clamp01(distance[index] / (reach * 1.7f)));
            var clearance = beard ? .0007f : .0012f;
            var gap = gaps[index] - clearance;
            if (gap < .00025f || fade == 0f) continue;
            offsets[index] = inverse.MultiplyVector(-directions[index] * (gap * fade * .85f));
        }
        LimitGroomOffsets(points, triangles, groups, offsets, toSkin);
        if (!offsets.Any(offset => offset.sqrMagnitude > 1e-10f)) return null;
        var target = CopyReadableSkin(source);
        try
        {
            var moved = vertices.Select((point, index) => point + offsets[groups[index]]).ToArray();
            var before = BodyShapeNormals(vertices, triangles);
            var after = BodyShapeNormals(moved, triangles);
            for (var index = 0; index < normals.Length; index++)
            {
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
    }

    private static void LimitGroomOffsets(Vector3[] points, int[] triangles, int[] groups, Vector3[] offsets, Matrix4x4 toSkin)
    {
        var unsafeGroups = new HashSet<int>();
        for (var pass = 0; pass < 8; pass++)
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
            foreach (var group in unsafeGroups) offsets[group] *= .5f;
        }
        // The source surface is the fallback when a thin card cannot take the requested correction.
        if (unsafeGroups.Count != 0) Array.Clear(offsets, 0, offsets.Length);
    }
}
